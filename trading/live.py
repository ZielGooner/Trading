"""User-started live execution. No trading starts on launch or account connection."""
import ctypes
from decimal import Decimal, ROUND_FLOOR, ROUND_CEILING, ROUND_HALF_UP
import hashlib
import json
from math import lcm
import os
from pathlib import Path
import queue
import sys
import threading
import time

from trading.binance_live import Binance, BinanceError
from trading.config import HERE, load, parameters_for
from trading.signals import build
from trading.indicators import validate_bars
from trading.history import HistoryStore

INTERVAL = 14400
ENTRY_WINDOW = 90
TERMINAL = {'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED', 'EXPIRED_IN_MATCH'}


def decimal(value):
    result = Decimal(str(value))
    if not result.is_finite():
        raise ValueError('nonfinite exchange value')
    return result


def text(value):
    return format(decimal(value), 'f')


def quantize(value, step, up=False):
    step = decimal(step)
    if step <= 0:
        raise ValueError('invalid exchange increment')
    return (decimal(value) / step).to_integral_value(rounding=ROUND_CEILING if up else ROUND_FLOOR) * step



def entry_size(available, leverage, fee, reference, minimum_price, cap, filters, *, equity=None, gross=0):
    """Request 50% margin at 2x; fees are separate, lots round DOWN, no minimum top-up."""
    available, leverage, fee, reference, minimum_price, cap = map(
        decimal, (available, leverage, fee, reference, minimum_price, cap))
    if available <= 0:
        raise ValueError('가용 증거금이 없습니다.')
    if leverage != 2 or min(reference, minimum_price, cap) <= 0 or fee < 0:
        raise ValueError('주문 금액 계산 조건이 올바르지 않습니다.')
    lot, market = filters['LOT_SIZE'], filters['MARKET_LOT_SIZE']
    lot_step, market_step = decimal(lot['stepSize']), decimal(market['stepSize'])
    if lot_step <= 0 or market_step < 0:
        raise ValueError('거래소 수량 단위가 올바르지 않습니다.')
    steps = [v for v in (lot_step, market_step) if v > 0]
    scale = Decimal(10) ** max(0, *(-v.as_tuple().exponent for v in steps))
    step = Decimal(lcm(*(int(v * scale) for v in steps))) / scale
    # Current contracts use zero-aligned lots. Reject unfamiliar offset grids.
    if any(decimal(f['minQty']) % decimal(f['stepSize'])
           for f in (lot, market) if decimal(f['stepSize']) > 0):
        raise ValueError('거래소 최소 수량과 수량 단위가 호환되지 않습니다.')
    minimum = quantize(max(decimal(lot['minQty']), decimal(market['minQty']),
                           decimal(filters['MIN_NOTIONAL']['notional']) / minimum_price), step, True)
    if minimum <= 0:
        raise ValueError('거래소 최소 주문 조건이 올바르지 않습니다.')
    maximum = min(decimal(lot['maxQty']), decimal(market['maxQty']), cap / reference)
    base_budget = available * Decimal('0.5')
    unit_cost = reference / leverage + reference * fee
    maximum = min(maximum, available / unit_cost)
    if equity is not None:
        headroom = max(Decimal(0), 2 * decimal(equity) - decimal(gross))
        maximum = min(maximum, headroom / (reference * (1 + 2 * fee)))
    quantity = quantize(min(base_budget * leverage / reference, maximum), step)
    if quantity < minimum:
        raise ValueError('50% 증거금 또는 2배 총노출 한도 안에서 최소 주문 수량/금액을 충족하지 못합니다.')
    required = quantity * unit_cost
    if required > available:
        raise ValueError('최소 주문에 필요한 증거금과 진입 수수료가 가용 잔고를 초과합니다.')
    return {'quantity': quantity, 'budget': required,
            'base_budget': base_budget, 'minimum_override': False,
            'estimated_cost': required, 'entry_fee_rate': fee, 'notional_cap': cap}


def tick_distance(value, tick):
    """Pine's positive-distance round-half-up, independently for SL and TP."""
    value, tick = decimal(value), decimal(tick)
    if value <= 0 or tick <= 0:
        raise ValueError('TP/SL 거리는 양수여야 합니다.')
    return max(Decimal(1), (value / tick).to_integral_value(rounding=ROUND_HALF_UP)) * tick


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    with temporary.open('w', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, allow_nan=False, separators=(',', ':'))
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


class AccountLock:
    """Only one live controller for this API key in the Windows login session."""
    def __init__(self, account):
        self.dll = ctypes.WinDLL('kernel32', use_last_error=True)
        self.dll.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
        self.dll.CreateMutexW.restype = ctypes.c_void_p
        self.dll.CloseHandle.argtypes = [ctypes.c_void_p]
        self.handle = self.dll.CreateMutexW(None, False, 'Local\\TradingLive_' + account)
        if not self.handle or ctypes.get_last_error() == 183:
            self.close()
            raise RuntimeError('이 API 키를 사용하는 다른 Trading 연결이 실행 중입니다.')

    def close(self):
        if getattr(self, 'handle', None):
            self.dll.CloseHandle(self.handle)
            self.handle = None


class Engine:
    def __init__(self, client, root=HERE, emit=lambda value: None, config=None):
        self.client, self.root, self.emit = client, Path(root), emit
        self.config = config or load(self.root / 'strategy.json')
        self.symbols = tuple(self.config['strategy_by_symbol'])
        self.account_id = hashlib.sha256(client.key.encode()).hexdigest()[:24]
        self.path = self.root / 'private_state' / ('live-' + self.account_id + '.json')
        self.state = json.loads(self.path.read_text(encoding='utf-8')) if self.path.exists() else {
            'version': 1, 'account_id': self.account_id, 'positions': {}, 'last_bar': {}}
        if self.state.get('version') != 1 or self.state.get('account_id') != self.account_id:
            raise ValueError('실계좌 상태 파일을 확인해야 합니다.')
        self.history = HistoryStore(self.root, self.account_id)
        self.entries = False
        self.connected = False
        self.ready = False
        self.account = None
        self.positions = {}
        self.filters = {}
        self.candles = {}
        self._batch_available = None
        self.last_message = '연결 전'

    def save(self):
        self.history.remember(self.state['positions'], self.client.now())
        write_json(self.path, self.state)

    def log(self, message):
        self.last_message = self.client.safe_error(message)
        self.emit({'type': 'log', 'message': self.last_message})

    def fault(self, error):
        self.entries = False
        self.connected = False
        self.log(error)
        self.publish()

    def publish(self):
        account = self.account if self.connected else None
        self.emit({'type': 'status', 'connected': self.connected, 'running': self.entries,
                   'management_enabled': self.client.authorized, 'account_id': self.account_id,
                   'wallet': str(account['walletBalance']) if account else None,
                   'available': str(account['availableBalance']) if account else None,
                   'unrealized': str(account['unrealizedProfit']) if account else None,
                   'server_time': self.client.now(), 'message': self.last_message,
                   'positions': [dict(symbol=s, quantity=p['positionAmt'], entry=p['entryPrice'],
                                      unrealized=p.get('unRealizedProfit', '0'),
                                      managed=s in self.state['positions'])
                                 for s, p in self.positions.items()] if self.connected else [],
                   'managed_count': len(self.state['positions'])})

    def read_account(self):
        result = self.client.call('GET', '/fapi/v3/account')
        self.account = next((a for a in result['assets'] if a['asset'] == 'USDT'), None)
        if self.account is None:
            raise ValueError('USDT 선물 잔고 항목이 없습니다.')
        rows = self.client.call('GET', '/fapi/v3/positionRisk')
        positions = {}
        for row in rows:
            if decimal(row['positionAmt']) != 0:
                if row.get('positionSide') != 'BOTH':
                    # Connection can display hedge positions, but automatic trading cannot start.
                    positions[row['symbol'] + ':' + row.get('positionSide', '?')] = row
                else:
                    positions[row['symbol']] = row
        self.positions = positions
        self.connected = True

    def check_mode(self):
        cfg = self.client.call('GET', '/fapi/v1/accountConfig')
        if cfg.get('dualSidePosition') is not False or cfg.get('multiAssetsMargin') is not False:
            raise ValueError('자동매매는 단방향(One-way) · 단일 자산 USDT 선물 계정에서 실행하세요.')
        if cfg.get('canTrade') is not True:
            raise ValueError('선물 거래가 허용되지 않은 계정입니다.')

    def connect(self):
        self.client.sync()
        self.read_account()
        self.refresh_filters()
        self.ready = True
        self.log('실계좌 조회 연결 완료. 자동매매 시작 전입니다.' + (' 기존 포지션 관리는 실거래 시작으로 복구하세요.' if self.state['positions'] else ''))
        self.publish()

    def refresh_filters(self):
        info = self.client.call('GET', '/fapi/v1/exchangeInfo')
        for symbol in self.symbols:
            item = next((x for x in info['symbols'] if x['symbol'] == symbol), None)
            if not item or item['status'] != 'TRADING' or item['contractType'] != 'PERPETUAL' or item['marginAsset'] != 'USDT':
                raise ValueError(symbol + ' USDT 무기한 거래 조건을 확인하지 못했습니다.')
            self.filters[symbol] = {f['filterType']: f for f in item['filters']}

    def orders(self, symbol):
        return (self.client.call('GET', '/fapi/v1/openOrders', {'symbol': symbol}),
                self.client.call('GET', '/fapi/v1/openAlgoOrders', {'symbol': symbol}))

    def start(self):
        if not self.connected or not self.ready or self.entries:
            raise ValueError('계정 연결 상태를 먼저 확인하세요.')
        self.check_mode()
        self.read_account()
        for symbol in self.symbols:
            standard, algos = self.orders(symbol)
            record = self.state['positions'].get(symbol)
            if not record and (symbol in self.positions or standard or algos):
                raise ValueError(symbol + ': 이 프로그램 소유가 아닌 포지션/주문이 있어 시작할 수 없습니다.')
            if record:
                allowed = {record['id'] + '-e', *record.get('close_ids', [])}
                if any(o['clientOrderId'] not in allowed for o in standard) or any(o['clientAlgoId'] not in {record['id'] + '-s', record['id'] + '-t'} for o in algos):
                    raise ValueError(symbol + ': 외부 주문이 섞여 있습니다.')
        for symbol in self.symbols:
            self.bars(symbol)
        # A click authorizes this process only; persisted state never starts trading automatically.
        self.client.authorized = True
        self.reconcile()
        for symbol in self.symbols:
            if symbol not in self.state['positions']:
                self.read_account()
                if symbol in self.positions:
                    raise ValueError(symbol + ': 시작 준비 중 외부 포지션 감지')
                self.configure_symbol(symbol)
        boundary = self.client.now() // (INTERVAL * 1000) * INTERVAL
        for symbol in self.symbols:
            self.state['last_bar'][symbol] = boundary
        self.save()
        self.entries = True
        self.log('자동매매 시작. 시작 이후 새로 완료되는 4시간봉부터 판단합니다.')
        self.publish()

    def stop(self):
        self.entries = False
        self.log('신규 진입 중지. 기존 포지션의 TP/SL과 청산 관리는 유지합니다.')
        self.publish()

    def bars(self, symbol):
        path = self.root / 'private_state' / ('candles-' + symbol + '.json')
        bars = self.candles.get(symbol)
        if bars is None:
            bars = json.loads(path.read_text(encoding='utf-8')) if path.exists() else []
        if bars and any('v' not in bar for bar in bars):
            bars = []  # Refresh incomplete public caches; never invent missing volume.
        now = self.client.now()
        cutoff = now // (INTERVAL * 1000) * INTERVAL
        params = {'symbol': symbol, 'interval': '4h', 'limit': 1500}
        if bars:
            params['startTime'] = (bars[-1]['t'] + INTERVAL) * 1000
        for _ in range(20):
            if bars and bars[-1]['t'] + INTERVAL >= cutoff:
                break
            rows = self.client.call('GET', '/fapi/v1/klines', params)
            added = []
            for row in rows:
                if int(row[6]) >= now:
                    continue
                b = dict(t=int(row[0]) // 1000, o=float(row[1]), h=float(row[2]), l=float(row[3]), c=float(row[4]), v=float(row[5]))
                if decimal(row[5]) < 0:
                    raise ValueError(symbol + ': 잘못된 거래량')
                validate_bars([b])
                if b['t'] % INTERVAL or int(row[6]) != (b['t'] + INTERVAL) * 1000 - 1:
                    raise ValueError(symbol + ': 잘못된 봉 경계')
                if bars and b['t'] != bars[-1]['t'] + INTERVAL:
                    raise ValueError(symbol + ': 실시간 봉 누락/중복')
                bars.append(b)
                added.append(b)
            if not added:
                break
            params['startTime'] = (bars[-1]['t'] + INTERVAL) * 1000
        if len(bars) < 500 or bars[-1]['t'] + INTERVAL != cutoff:
            raise ValueError(symbol + ': 최신 완료봉/지표 준비 데이터가 부족합니다.')
        if any(b['t'] != a['t'] + INTERVAL for a, b in zip(bars, bars[1:])):
            raise ValueError(symbol + ': 저장된 봉의 시간 간격 오류')
        self.candles[symbol] = bars
        write_json(path, bars)
        return bars

    def configure_symbol(self, symbol):
        lev = self.config['leverage_by_symbol'][symbol]
        cfg = self.client.call('GET', '/fapi/v1/symbolConfig', {'symbol': symbol})
        cfg = cfg[0] if isinstance(cfg, list) and len(cfg) == 1 else cfg
        if cfg.get('isAutoAddMargin') is not False:
            raise ValueError(symbol + ': 자동 증거금 추가를 꺼야 합니다.')
        if cfg['marginType'].upper() != 'ISOLATED':
            self.client.call('POST', '/fapi/v1/marginType', {'symbol': symbol, 'marginType': 'ISOLATED'})
        if int(cfg['leverage']) != lev:
            self.client.call('POST', '/fapi/v1/leverage', {'symbol': symbol, 'leverage': lev})
        cfg = self.client.call('GET', '/fapi/v1/symbolConfig', {'symbol': symbol})
        cfg = cfg[0] if isinstance(cfg, list) and len(cfg) == 1 else cfg
        if cfg['marginType'].upper() != 'ISOLATED' or int(cfg['leverage']) != lev or cfg.get('isAutoAddMargin') is not False:
            raise ValueError(symbol + ': 레버리지/격리 설정 확인 실패')
        return decimal(cfg['maxNotionalValue'])

    def enter(self, symbol, signal, boundary):
        if not self.entries or not self.client.authorized:
            raise PermissionError('신규 진입이 승인되지 않았습니다.')
        self.check_mode()
        self.read_account()
        if symbol in self.positions or symbol in self.state['positions']:
            return
        if any(self.orders(symbol)):
            raise ValueError(symbol + ': 기존 주문이 있어 진입을 차단했습니다.')
        self.refresh_filters()  # Minimums can change while the program is running.
        cap = self.configure_symbol(symbol)
        book = self.client.call('GET', '/fapi/v1/ticker/bookTicker', {'symbol': symbol})
        if abs(self.client.now() - int(book['time'])) > 5000:
            raise ValueError(symbol + ': 호가가 오래되었습니다.')
        bid, ask = decimal(book['bidPrice']), decimal(book['askPrice'])
        if not 0 < bid <= ask or (ask / bid - 1) > decimal('0.003'):
            raise ValueError(symbol + ': 호가/스프레드 확인 실패')
        direction = int(signal['direction'])
        filters = self.filters[symbol]
        tick = decimal(filters['PRICE_FILTER']['tickSize'])
        slip = decimal(self.config['slippage_ticks']) * tick
        price = quantize((ask if direction == 1 else bid) + direction * slip, tick, direction == 1)
        if not decimal(filters['PRICE_FILTER']['minPrice']) <= price <= decimal(filters['PRICE_FILTER']['maxPrice']):
            raise ValueError('진입 가격 필터 오류')
        lev = self.config['leverage_by_symbol'][symbol]
        available = decimal(self.account['availableBalance'])
        if self._batch_available is not None:
            available = min(available, self._batch_available)
        commission = self.client.call('GET', '/fapi/v1/commissionRate', {'symbol': symbol})
        fee = max(decimal(commission['takerCommissionRate']), decimal(self.config['taker_fee']))
        reference = max(price, ask)
        try:
            equity = decimal(self.account['walletBalance']) + decimal(self.account['unrealizedProfit'])
            gross = sum(abs(decimal(p['positionAmt'])) * decimal(p.get('markPrice', p['entryPrice']))
                        for p in self.positions.values())
            sizing = entry_size(available, lev, fee, reference, min(bid, price), cap, filters,
                                equity=equity, gross=gross)
        except ValueError as error:
            self.log(symbol + ': ' + str(error) + ' 진입을 건너뜁니다.')
            return
        quantity, budget = sizing['quantity'], sizing['budget']
        distance = tick_distance(signal['stop_distance'], tick)
        target_distance = tick_distance(signal['take_profit_distance'], tick)
        # Do not open an isolated position whose planned stop lies near/beyond liquidation.
        margin = reference / lev
        mmr = decimal(self.config['maintenance_margin_rate'])
        liq = (reference - direction * margin) / (1 - direction * mmr)
        if distance < tick or reference + direction * target_distance <= 0:
            self.log(symbol + ': 유효한 TP/SL 가격을 만들 수 없어 건너뜁니다.')
            return
        if distance <= 0 or distance >= abs(reference - liq) * decimal('0.7'):
            self.log(symbol + ': 손절가가 청산가에 너무 가까워 진입을 건너뜁니다.')
            return
        if self.client.now() / 1000 - boundary > ENTRY_WINDOW:
            self.log(symbol + ': 완료봉 진입 허용 시간(90초)이 지나 건너뜁니다.')
            return
        token = hashlib.sha256((self.account_id + symbol + str(boundary)).encode()).hexdigest()[:22]
        record = {'id': 'tr-' + token, 'phase': 'intent', 'direction': direction,
                  'requested_quantity': text(quantity), 'budget': text(budget), 'leverage': lev,
                  'base_budget': text(sizing['base_budget']), 'minimum_override': sizing['minimum_override'],
                  'entry_fee_rate': text(fee), 'notional_cap': text(cap),
                  'distance': text(distance), 'target_distance': text(target_distance),
                  'opened_t': boundary, 'deadline': boundary + int(signal['max_hold_bars']) * INTERVAL if signal['max_hold_bars'] else None,
                  'flip_exit': bool(signal.get('flip_exit', False)), 'profile': signal.get('profile'),
                  'strategy_version': self.config['strategy_version'],
                  'strategy_snapshot': json.loads(json.dumps(self.config)),
                  'close_ids': [], 'protection_submitted': {}}
        self.state['positions'][symbol] = record
        self.save()  # Intent must be durable before a request can reach the exchange.
        if self._batch_available is not None:
            self._batch_available = max(Decimal(0), available - budget)
        try:
            self.client.call('POST', '/fapi/v1/order', {
                'symbol': symbol, 'side': 'BUY' if direction == 1 else 'SELL', 'positionSide': 'BOTH',
                'type': 'LIMIT', 'timeInForce': 'IOC', 'quantity': text(quantity), 'price': text(price),
                'newClientOrderId': record['id'] + '-e', 'newOrderRespType': 'RESULT'})
        except BinanceError as error:
            self.entries = False
            if not error.uncertain:
                self.state['positions'].pop(symbol)
                self.save()
            raise
        self.reconcile_symbol(symbol)
        if self._batch_available is not None:
            current = self.state['positions'].get(symbol)
            if current is None:
                spent = Decimal(0)  # An IOC that did not fill consumes no margin.
            elif 'quantity' in current and 'entry' in current:
                notional = decimal(current['quantity']) * decimal(current['entry'])
                spent = notional / lev + notional * fee
            else:
                spent = budget  # Unconfirmed fills keep their full reservation.
            self._batch_available = max(Decimal(0), available - spent)

    def query_entry(self, symbol, record):
        result = self.client.call('GET', '/fapi/v1/order', {'symbol': symbol, 'origClientOrderId': record['id'] + '-e'})
        if result['status'] not in TERMINAL:
            self.client.call('DELETE', '/fapi/v1/order', {'symbol': symbol, 'origClientOrderId': record['id'] + '-e'})
            result = self.client.call('GET', '/fapi/v1/order', {'symbol': symbol, 'origClientOrderId': record['id'] + '-e'})
        if result['status'] not in TERMINAL:
            raise ValueError(symbol + ': 진입 주문 상태가 확정되지 않아 신규 진입을 중지합니다.')
        quantity = decimal(result['executedQty'])
        if quantity == 0:
            self.state['positions'].pop(symbol)
            self.save()
            return False
        price = decimal(result['avgPrice'])
        if price <= 0 or quantity > decimal(record['requested_quantity']):
            raise ValueError(symbol + ': 체결 수량/가격 불일치')
        direction = record['direction']
        tick = self.filters[symbol]['PRICE_FILTER']['tickSize']
        # Round both triggers towards entry so discretization cannot widen initial risk.
        target_distance = decimal(record['target_distance']) if 'target_distance' in record else decimal(record['distance']) * decimal(record['reward'])
        record.update(phase='open', quantity=text(quantity), entry=text(price), entry_order_id=result.get('orderId'),
                      stop=text(quantize(price - direction * decimal(record['distance']), tick, direction == 1)),
                      target=text(quantize(price + direction * target_distance, tick, direction == -1)))
        self.save()
        return True

    def cancel_protection(self, symbol, record):
        _, orders = self.orders(symbol)
        for order in orders:
            if order['clientAlgoId'] in (record['id'] + '-s', record['id'] + '-t'):
                try:
                    self.client.call('DELETE', '/fapi/v1/algoOrder', {'clientAlgoId': order['clientAlgoId']})
                except BinanceError as error:
                    if error.code not in (-2011, -2013):
                        raise

    def flat_confirmed(self, symbol, record):
        now = self.client.now()
        if 'flat_since' not in record:
            record['flat_since'] = now
            record['flat_checks'] = 0
            self.log(symbol + ': 거래소 포지션 없음 상태를 재확인합니다.')
        record['flat_checks'] += 1
        self.save()
        return record['flat_checks'] >= 3 and now - record['flat_since'] >= 10000

    def close_position(self, symbol, record, reason):
        record['phase'] = 'closing'
        record['close_reason'] = reason
        self.save()
        self.read_account()
        position = self.positions.get(symbol)
        if not position:
            if not self.flat_confirmed(symbol, record):
                if not record.get('position_seen'):
                    self.entries = False
                return
            self.cancel_protection(symbol, record)
            self.state['positions'].pop(symbol)
            self.save()
            self.log(symbol + ': 포지션 종료 확인 (' + reason + ')')
            return
        amount = decimal(position['positionAmt'])
        if amount * record['direction'] <= 0 or abs(amount) > decimal(record['quantity']):
            raise ValueError(symbol + ': 외부 포지션 변경 감지. 자동 청산을 중단합니다.')
        if record.get('close_pending'):
            old = self.client.call('GET', '/fapi/v1/order', {'symbol': symbol, 'origClientOrderId': record['close_pending']})
            if old['status'] not in TERMINAL:
                raise ValueError(symbol + ': 청산 주문 상태 확인 대기')
            record.pop('close_pending')
            self.save()
            self.read_account()
            position = self.positions.get(symbol)
            if not position:
                return self.close_position(symbol, record, reason)
            amount = decimal(position['positionAmt'])
            if amount * record['direction'] <= 0 or abs(amount) > decimal(record['quantity']):
                raise ValueError(symbol + ': 청산 중 외부 포지션 변경')
        client_id = record['id'] + '-c' + str(len(record['close_ids']))
        record['close_ids'].append(client_id)
        record['close_pending'] = client_id
        self.save()
        try:
            self.client.call('POST', '/fapi/v1/order', {'symbol': symbol, 'positionSide': 'BOTH',
                'side': 'SELL' if amount > 0 else 'BUY', 'type': 'MARKET', 'reduceOnly': 'true',
                'quantity': text(abs(amount)), 'newClientOrderId': client_id, 'newOrderRespType': 'RESULT'})
        except BinanceError as error:
            if not error.uncertain:
                record.pop('close_pending', None)
                self.save()
            raise
        self.log(symbol + ': 보호/보유기한 청산 요청. 체결 확인 중입니다.')

    def protect(self, symbol, record):
        _, orders = self.orders(symbol)
        existing = {order['clientAlgoId']: order for order in orders}
        for suffix, kind, price in (('s', 'STOP_MARKET', record['stop']), ('t', 'TAKE_PROFIT_MARKET', record['target'])):
            cid = record['id'] + '-' + suffix
            if cid not in existing:
                if record['protection_submitted'].get(suffix):
                    raise ValueError(symbol + ': 거래소 TP/SL 누락 감지')
                record['protection_submitted'][suffix] = True
                self.save()
                self.client.call('POST', '/fapi/v1/algoOrder', {
                    'algoType': 'CONDITIONAL', 'symbol': symbol,
                    'side': 'SELL' if record['direction'] == 1 else 'BUY', 'positionSide': 'BOTH',
                    'type': kind, 'triggerPrice': price, 'quantity': record['quantity'], 'reduceOnly': 'true',
                    'workingType': 'CONTRACT_PRICE', 'priceProtect': 'false', 'clientAlgoId': cid})
            order = self.client.call('GET', '/fapi/v1/algoOrder', {'clientAlgoId': cid})
            if (order['algoStatus'] != 'NEW' or order['symbol'] != symbol or order['orderType'] != kind
                    or order['side'] != ('SELL' if record['direction'] == 1 else 'BUY')
                    or decimal(order['triggerPrice']) != decimal(price)
                    or decimal(order['quantity']) != decimal(record['quantity'])
                    or str(order.get('reduceOnly')).lower() != 'true'
                    or order.get('workingType') != 'CONTRACT_PRICE'):
                raise ValueError(symbol + ': 거래소 TP/SL 확인 실패')

    def reconcile_symbol(self, symbol):
        record = self.state['positions'][symbol]
        if record['phase'] == 'intent' and not self.query_entry(symbol, record):
            return
        self.read_account()
        position = self.positions.get(symbol)
        if not position:
            if not self.flat_confirmed(symbol, record):
                if not record.get('position_seen'):
                    self.entries = False
                return
            self.cancel_protection(symbol, record)
            self.state['positions'].pop(symbol)
            self.save()
            self.log(symbol + ': 거래소 포지션 종료 확인')
            return
        amount = decimal(position['positionAmt'])
        if amount * record['direction'] <= 0 or abs(amount) > decimal(record['quantity']):
            raise ValueError(symbol + ': 수동/외부 포지션 변경 감지. 신규 진입 중지')
        first_seen = not record.get('position_seen')
        record['position_seen'] = True
        record.pop('flat_since', None)
        record.pop('flat_checks', None)
        self.save()
        if record['phase'] == 'closing':
            return self.close_position(symbol, record, record['close_reason'])
        entry = decimal(position['entryPrice'])
        liq = decimal(position.get('liquidationPrice', '0'))
        stop, target = decimal(record['stop']), decimal(record['target'])
        notional = abs(amount) * entry
        # Honor the persisted minimum-order exception after fills and restarts.
        # Old records have no fee allowance field, so keep their original margin-only guard.
        cost = notional / record['leverage'] + notional * decimal(record.get('entry_fee_rate', '0'))
        if (min(stop, target) <= 0 or cost > decimal(record['budget'])
                or ('notional_cap' in record and notional > decimal(record['notional_cap']))):
            return self.close_position(symbol, record, '증거금/가격 범위 초과')
        if liq <= 0 or (stop - liq) * record['direction'] <= 0:
            return self.close_position(symbol, record, '청산가보다 늦은 손절')
        if record.get('deadline') and self.client.now() / 1000 >= record['deadline']:
            return self.close_position(symbol, record, '최대 보유 기간')
        try:
            self.protect(symbol, record)
            if first_seen:
                self.log(symbol + ': 체결과 거래소 TP/SL 등록을 확인했습니다.')
        except Exception:
            self.entries = False
            self.close_position(symbol, record, 'TP/SL 등록 또는 확인 실패')
            raise

    def reconcile(self):
        if not self.client.authorized:
            return
        for symbol in list(self.state['positions']):
            try:
                self.reconcile_symbol(symbol)
            except Exception:
                self.entries = False
                raise

    def tick(self):
        if not self.ready:
            self.connect()
            return
        if time.monotonic() - self.client.synced > 120:
            self.client.sync()
        self.read_account()
        held_before_management = set(self.state['positions'])
        self.reconcile()
        boundary = self.client.now() // (INTERVAL * 1000) * INTERVAL
        # Full management first; collect the entire same-boundary entry batch before ordering.
        # A position closed by time/opposite here cannot reverse from the same signal candle.
        candidates = []
        if self.entries or self.client.authorized:
            for symbol in self.symbols:
                if self.state['last_bar'].get(symbol, 0) >= boundary:
                    continue
                p = parameters_for(self.config, symbol)
                signal = build(self.bars(symbol), p).get(boundary)
                self.state['last_bar'][symbol] = boundary
                self.save()
                record = self.state['positions'].get(symbol)
                if record:
                    if (self.client.authorized and record.get('flip_exit', False) and signal
                            and signal['direction'] == -record['direction'] and record['phase'] == 'open'):
                        self.close_position(symbol, record, '확정 반대 신호')
                    continue
                if signal and self.entries and symbol not in held_before_management:
                    candidates.append((symbol, signal))
                else:
                    self.log(symbol + ': 완료 4시간봉 진입 신호 없음')
        # Balance and position REST snapshots can arrive at different times. Retain
        # the confirmed cost locally so a stale balance cannot reuse the same funds.
        self._batch_available = decimal(self.account['availableBalance'])
        try:
            for symbol, signal in sorted(candidates, key=lambda row: (-decimal(row[1]['turnover']), row[0])):
                if not self.entries:
                    break
                if self.client.now() / 1000 - boundary > ENTRY_WINDOW:
                    self.log(symbol + ': 완료봉 진입 허용 시간(90초)이 지나 건너뜁니다.')
                    continue
                self.enter(symbol, signal, boundary)
        finally:
            self._batch_available = None
        self.publish()


def main():
    commands = queue.Queue()
    def read_commands():
        for line in sys.stdin:
            try:
                command = json.loads(line)
                if isinstance(command, dict):
                    commands.put(command)
            except ValueError:
                pass
        commands.put({'action': 'shutdown'})
    def emit(value):
        try:
            print(json.dumps(value, ensure_ascii=True, allow_nan=False), flush=True)
        except (BrokenPipeError, OSError):
            pass  # Finish protection of any in-flight entry even if the parent has gone away.
    threading.Thread(target=read_commands, daemon=True).start()
    engine, lock, history_service = None, None, None
    deadline = 0
    try:
        while True:
            try:
                command = commands.get(timeout=1)
            except queue.Empty:
                command = None
            if command:
                action = command.get('action')
                if action == 'shutdown':
                    if engine:
                        engine.entries = False
                        try:
                            engine.reconcile()
                        except Exception as error:
                            engine.fault(error)
                    break
                try:
                    if action == 'connect':
                        if engine:
                            raise ValueError('이미 연결 프로세스가 실행 중입니다.')
                        client = Binance(str(command.pop('api_key', '')).strip(), str(command.pop('api_secret', '')).strip())
                        identity = hashlib.sha256(client.key.encode()).hexdigest()[:24]
                        lock = AccountLock(identity)
                        engine = Engine(client, emit=emit)
                        engine.connect()
                        from trading.review import HistoryService
                        history_service = HistoryService(client.key, client.secret, engine.root, emit)
                        history_service.start()
                    elif engine and action == 'start':
                        engine.start()
                    elif engine and action == 'stop':
                        engine.stop()
                    elif engine and action == 'refresh':
                        engine.tick()
                except Exception as error:
                    if engine:
                        engine.fault(error)
                    else:
                        emit({'type': 'status', 'connected': False, 'running': False, 'message': '연결 준비 실패: ' + type(error).__name__})
                command.clear()
            if engine and time.monotonic() >= deadline:
                try:
                    engine.tick()
                except Exception as error:
                    engine.fault(error)
                deadline = time.monotonic() + 5
    finally:
        if history_service:
            history_service.stop()
        if lock:
            lock.close()


if __name__ == '__main__':
    main()
