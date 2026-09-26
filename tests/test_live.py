import copy
from decimal import Decimal
import io
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlsplit

from trading.binance_live import Binance, BinanceError
from trading.config import load
from trading.live import Engine, INTERVAL, decimal, quantize, entry_size

class FakeBinance:
    def __init__(self):
        self.key = 'FAKE-KEY-NOT-A-CREDENTIAL'
        self.secret = 'FAKE-SECRET-NOT-A-CREDENTIAL'
        self.authorized = False
        self.synced = time.monotonic()
        self.ms = (1800000000 // INTERVAL * INTERVAL + 10) * 1000
        self.calls, self.held, self.normal, self.algos = [], {}, {}, {}
        self.leverage = {'BTCUSDT': 10, 'XRPUSDT': 5, 'SOLUSDT': 5}
        self.wallet = Decimal('1000')
        self.fill_ratio = Decimal('1')
        self.timeout_entry = False
        self.reject_entry = False
        self.reject_tp = False
        self.unknown_entry = False
        self.hedge = False
        self.multi = False
        self.auto_margin = False
        self.book_age = 0
        self.external_orders = []
        self.available_seen = []
        self.filter_overrides = {}

    def safe_error(self, message):
        return str(message).replace(self.key, '[redacted]').replace(self.secret, '[redacted]')

    def now(self):
        return self.ms

    def sync(self):
        self.synced = time.monotonic()

    def info(self, symbol):
        item = {'symbol': symbol, 'status': 'TRADING', 'contractType': 'PERPETUAL', 'marginAsset': 'USDT',
                'filters': [
                    {'filterType': 'LOT_SIZE', 'minQty': '0.001', 'maxQty': '100000', 'stepSize': '0.001'},
                    {'filterType': 'MARKET_LOT_SIZE', 'minQty': '0.001', 'maxQty': '100000', 'stepSize': '0.001'},
                    {'filterType': 'PRICE_FILTER', 'minPrice': '0.01', 'maxPrice': '1000000', 'tickSize': '0.01'},
                    {'filterType': 'MIN_NOTIONAL', 'notional': '5'}]}
        for f in item['filters']:
            f.update(self.filter_overrides.get(symbol, {}).get(f['filterType'], {}))
        return item

    def call(self, method, path, params=None):
        p = dict(params or {})
        self.calls.append((method, path, p))
        if method != 'GET' and not self.authorized:
            raise PermissionError('writes disabled')
        symbol = p.get('symbol')
        if path == '/fapi/v3/account':
            used = sum(abs(decimal(x['positionAmt'])) * decimal(x['entryPrice']) / self.leverage[s] for s, x in self.held.items())
            available = self.wallet - used
            self.available_seen.append(available)
            return {'assets': [{'asset': 'USDT', 'walletBalance': str(self.wallet),
                                'availableBalance': str(available), 'unrealizedProfit': '0'}]}
        if path == '/fapi/v3/positionRisk':
            return list(copy.deepcopy(self.held).values())
        if path == '/fapi/v1/exchangeInfo':
            return {'symbols': [self.info(s) for s in self.leverage]}
        if path == '/fapi/v1/accountConfig':
            return {'canTrade': True, 'dualSidePosition': self.hedge, 'multiAssetsMargin': self.multi}
        if path == '/fapi/v1/symbolConfig':
            return [{'symbol': symbol, 'marginType': 'ISOLATED', 'leverage': self.leverage[symbol],
                     'isAutoAddMargin': self.auto_margin, 'maxNotionalValue': '1000000'}]
        if path == '/fapi/v1/commissionRate':
            return {'takerCommissionRate': '0.0005'}
        if path == '/fapi/v1/ticker/bookTicker':
            return {'bidPrice': '99.99', 'askPrice': '100.01', 'time': self.ms - self.book_age}
        if path == '/fapi/v1/openOrders':
            return self.external_orders
        if path == '/fapi/v1/openAlgoOrders':
            return [copy.deepcopy(x) for x in self.algos.values() if x['symbol'] == symbol]
        if path == '/fapi/v1/klines':
            end = self.ms // (INTERVAL * 1000) * INTERVAL
            first = int(p.get('startTime', (end - 600 * INTERVAL) * 1000)) // 1000
            return [[t * 1000, '100', '102', '98', '100', '1', (t + INTERVAL) * 1000 - 1]
                    for t in range(first, end + INTERVAL, INTERVAL)][:1500]
        if path == '/fapi/v1/order':
            if method == 'GET':
                if self.unknown_entry or p['origClientOrderId'] not in self.normal:
                    raise BinanceError('order absent', -2013)
                return copy.deepcopy(self.normal[p['origClientOrderId']])
            if method == 'POST':
                cid = p['newClientOrderId']
                if cid in self.normal:
                    raise AssertionError('duplicate order transmission')
                if p['type'] == 'LIMIT':
                    if self.reject_entry:
                        raise BinanceError('margin insufficient', -2019, False)
                    q = quantize(decimal(p['quantity']) * self.fill_ratio, '.001')
                    direction = 1 if p['side'] == 'BUY' else -1
                    if q:
                        self.held[symbol] = {'symbol': symbol, 'positionSide': 'BOTH', 'positionAmt': str(direction * q),
                                             'entryPrice': '100', 'liquidationPrice': '80' if direction == 1 else '120',
                                             'unRealizedProfit': '0'}
                    result = {'status': 'FILLED' if self.fill_ratio == 1 else 'EXPIRED',
                              'executedQty': str(q), 'avgPrice': '100', 'clientOrderId': cid}
                    self.normal[cid] = result
                    if self.timeout_entry:
                        self.timeout_entry = False
                        raise BinanceError('timeout unknown', uncertain=True)
                    return result
                self.assert_reduce_only(p)
                amount = decimal(self.held[symbol]['positionAmt'])
                q = min(abs(amount), decimal(p['quantity']))
                remaining = abs(amount) - q
                if remaining:
                    self.held[symbol]['positionAmt'] = str(remaining * (1 if amount > 0 else -1))
                else:
                    del self.held[symbol]
                result = {'status': 'FILLED', 'executedQty': str(q), 'avgPrice': '100', 'clientOrderId': cid}
                self.normal[cid] = result
                return result
            if method == 'DELETE':
                self.normal[p['origClientOrderId']]['status'] = 'CANCELED'
                return self.normal[p['origClientOrderId']]
        if path == '/fapi/v1/algoOrder':
            if method == 'GET':
                if p['clientAlgoId'] not in self.algos:
                    raise BinanceError('algo absent', -2013)
                return copy.deepcopy(self.algos[p['clientAlgoId']])
            if method == 'DELETE':
                for cid, item in list(self.algos.items()):
                    if cid == p['clientAlgoId']:
                        del self.algos[cid]
                        return {'code': 200}
                raise BinanceError('algo absent', -2011)
            if method == 'POST':
                if self.reject_tp and p['type'] == 'TAKE_PROFIT_MARKET':
                    raise BinanceError('TP refused', -2021)
                cid = p['clientAlgoId']
                if cid in self.algos:
                    raise AssertionError('duplicate protection')
                item = {**p, 'algoId': len(self.algos) + 1, 'orderType': p['type'], 'algoStatus': 'NEW', 'reduceOnly': True}
                self.algos[cid] = item
                return copy.deepcopy(item)
        raise AssertionError((method, path, p))

    @staticmethod
    def assert_reduce_only(p):
        assert p['reduceOnly'] == 'true'
        assert p['type'] == 'MARKET'


class SizingTests(unittest.TestCase):
    def setUp(self):
        self.filters = {f['filterType']: f for f in FakeBinance().info('BTCUSDT')['filters']}
        self.filters['MIN_NOTIONAL']['notional'] = '50'

    def size(self, available='20', cap='50000', fee='.0005'):
        return entry_size(available, 10, fee, '100', '100', cap, self.filters)

    def test_minimum_is_rounded_up_once_and_not_doubled(self):
        self.filters['MIN_NOTIONAL']['notional'] = '50.00001'
        result = self.size()
        self.assertEqual(result['quantity'], Decimal('.501'))
        self.assertTrue(result['minimum_override'])
        self.assertLess((result['quantity'] - Decimal('.001')) * 100, Decimal('50.00001'))

    def test_exact_minimum_does_not_add_an_extra_lot(self):
        self.assertEqual(self.size()['quantity'], Decimal('.5'))

    def test_both_lot_grids_are_satisfied(self):
        self.filters['LOT_SIZE'].update(stepSize='.002', minQty='.002')
        self.filters['MARKET_LOT_SIZE'].update(stepSize='.003', minQty='.003')
        q = self.size()['quantity']
        self.assertEqual(q, Decimal('.504'))
        self.assertEqual(q % Decimal('.002'), 0)
        self.assertEqual(q % Decimal('.003'), 0)

    def test_market_step_disabled_still_honors_market_minimum(self):
        self.filters['MARKET_LOT_SIZE'].update(stepSize='0', minQty='.7')
        self.assertEqual(self.size()['quantity'], Decimal('.7'))

    def test_fee_must_fit_and_exact_available_funds_are_allowed(self):
        with self.assertRaisesRegex(ValueError, '수수료'):
            self.size(available='5.024999')
        result = self.size(available='5.025')
        self.assertEqual(result['estimated_cost'], Decimal('5.025'))

    def test_cannot_override_maximum_notional_or_market_quantity(self):
        with self.assertRaisesRegex(ValueError, '한도'):
            self.size(cap='49.99')
        self.filters['MARKET_LOT_SIZE']['maxQty'] = '.499'
        with self.assertRaisesRegex(ValueError, '한도'):
            self.size()

    def test_normal_allocation_stays_within_ten_percent_with_fees(self):
        result = self.size(available='1000')
        self.assertFalse(result['minimum_override'])
        self.assertEqual(result['budget'], Decimal('100'))
        self.assertEqual(result['quantity'], Decimal('9.950'))
        self.assertLessEqual(result['estimated_cost'], Decimal('100'))

    def test_invalid_or_empty_funds_cannot_place_minimum_order(self):
        for value in ('0', '-1', 'NaN', 'Infinity'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.size(available=value)


class LiveTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.client = FakeBinance()
        self.events = []
        self.engine = Engine(self.client, Path(self.directory.name), self.events.append, load())
        self.engine.connect()

    def begin(self):
        self.engine.start()
        return self.client.ms // (INTERVAL * 1000) * INTERVAL

    def signal(self, direction=1):
        return {'direction': direction, 'stop_distance': 2, 'take_profit_r': 8, 'max_hold_bars': 768}

    def enter(self, symbol='BTCUSDT', direction=1):
        boundary = self.begin()
        self.engine.enter(symbol, self.signal(direction), boundary)

    def finish_flat(self):
        for _ in range(3):
            self.client.ms += 5000
            self.engine.reconcile()

    def writes(self):
        return [c for c in self.client.calls if c[0] != 'GET']

    def test_incomplete_market_setup_cannot_start(self):
        self.engine.ready = False
        with self.assertRaises(ValueError):
            self.engine.start()
        self.assertEqual(self.writes(), [])

    def test_auto_margin_is_checked_before_start_completes(self):
        self.client.auto_margin = True
        with self.assertRaises(ValueError):
            self.engine.start()
        self.assertFalse(self.engine.entries)
        self.assertEqual(self.writes(), [])

    def test_worker_shutdown_without_credentials_does_not_connect(self):
        import subprocess, sys
        from trading.config import HERE
        result = subprocess.run([sys.executable, '-B', '-u', '-m', 'trading.live'],
                                input='{"action":"shutdown"}\n',
                                text=True, capture_output=True, cwd=HERE, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, '')

    def test_connect_is_read_only_and_does_not_leak_keys(self):
        self.assertFalse(self.engine.entries)
        self.assertFalse(self.client.authorized)
        self.assertEqual(self.writes(), [])
        serialized = json.dumps(self.events)
        self.assertNotIn(self.client.key, serialized)
        self.assertNotIn(self.client.secret, serialized)

    def test_cannot_enter_before_user_start(self):
        with self.assertRaises(PermissionError):
            self.engine.enter('BTCUSDT', self.signal(), self.client.ms // 1000)
        self.assertEqual(self.writes(), [])

    def test_next_closed_bar_runs_full_indicator_pipeline(self):
        boundary = self.begin()
        self.client.ms += INTERVAL * 1000
        self.engine.tick()
        self.assertEqual(set(self.engine.state['last_bar'].values()), {boundary + INTERVAL})
        self.assertTrue(all('v' in b for b in self.engine.candles['BTCUSDT']))

    def test_start_skips_preexisting_closed_bar(self):
        boundary = self.begin()
        self.assertEqual(set(self.engine.state['last_bar'].values()), {boundary})
        self.engine.tick()
        self.assertEqual(self.writes(), [])

    def test_connection_restart_never_enables_orders(self):
        self.enter()
        restarted = Engine(FakeBinance(), Path(self.directory.name), config=load())
        self.assertFalse(restarted.entries)
        self.assertFalse(restarted.client.authorized)
        self.assertIn('BTCUSDT', restarted.state['positions'])

    def test_actual_fill_gets_exchange_stop_and_target(self):
        self.enter()
        record = self.engine.state['positions']['BTCUSDT']
        self.assertEqual(record['stop'], '98')
        self.assertEqual(record['target'], '116')
        self.assertEqual(len(self.client.algos), 2)
        for algo in self.client.algos.values():
            self.assertTrue(algo['reduceOnly'])
            self.assertEqual(algo['workingType'], 'CONTRACT_PRICE')
        self.assertLessEqual(decimal(record['quantity']) * 100 / 10, Decimal('100'))

    def test_short_stop_and_target_are_on_correct_sides(self):
        self.enter(direction=-1)
        record = self.engine.state['positions']['BTCUSDT']
        self.assertEqual(record['stop'], '102')
        self.assertEqual(record['target'], '84')
        self.assertTrue(all(x['side'] == 'BUY' for x in self.client.algos.values()))

    def test_expired_ioc_partial_fill_is_protected(self):
        self.client.fill_ratio = Decimal('.4')
        self.enter()
        record = self.engine.state['positions']['BTCUSDT']
        self.assertLess(decimal(record['quantity']), decimal(record['requested_quantity']))
        self.assertEqual(len(self.client.algos), 2)

    def test_zero_fill_creates_no_protection(self):
        self.client.fill_ratio = Decimal('0')
        self.enter()
        self.assertEqual(self.engine.state['positions'], {})
        self.assertEqual(self.client.algos, {})

    def test_ambiguous_entry_is_queried_not_resubmitted(self):
        boundary = self.begin()
        self.client.timeout_entry = True
        with self.assertRaises(BinanceError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.engine.reconcile()
        entries = [p for m, path, p in self.writes() if path == '/fapi/v1/order' and m == 'POST' and p['type'] == 'LIMIT']
        self.assertEqual(len(entries), 1)
        self.assertEqual(len(self.client.algos), 2)

    def test_unknown_entry_stays_blocked_without_duplicate(self):
        boundary = self.begin()
        self.client.timeout_entry = True
        with self.assertRaises(BinanceError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.client.unknown_entry = True
        with self.assertRaises(BinanceError):
            self.engine.reconcile()
        self.assertEqual(self.engine.state['positions']['BTCUSDT']['phase'], 'intent')
        self.assertEqual(len([1 for m, path, p in self.writes() if m == 'POST' and path == '/fapi/v1/order']), 1)

    def test_definitive_entry_rejection_does_not_leave_intent(self):
        boundary = self.begin()
        self.client.reject_entry = True
        with self.assertRaises(BinanceError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertEqual(self.engine.state['positions'], {})

    def test_protection_failure_reduces_position_and_stops_entries(self):
        boundary = self.begin()
        self.client.reject_tp = True
        with self.assertRaises(BinanceError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertFalse(self.engine.entries)
        self.assertNotIn('BTCUSDT', self.client.held)
        self.finish_flat()
        self.assertEqual(self.engine.state['positions'], {})
        self.assertEqual(self.client.algos, {})

    def test_missing_exchange_protection_triggers_reduce_only_exit(self):
        self.enter()
        self.client.algos.clear()
        with self.assertRaises(ValueError):
            self.engine.reconcile()
        self.assertFalse(self.engine.entries)
        self.assertEqual(self.client.held, {})

    def test_stop_keeps_exchange_protection_and_never_opens_more(self):
        self.enter()
        before = len(self.writes())
        self.engine.stop()
        self.engine.tick()
        self.assertEqual(len(self.client.algos), 2)
        self.assertEqual(len(self.writes()), before)
        self.assertFalse(self.engine.entries)

    def test_max_hold_exit_is_owned_and_reduce_only(self):
        self.enter()
        self.client.ms += 769 * 3600 * 1000
        self.engine.reconcile()
        self.assertEqual(self.client.held, {})
        self.finish_flat()
        self.assertEqual(self.client.algos, {})

    def test_each_entry_uses_new_available_margin(self):
        boundary = self.begin()
        self.engine.enter('BTCUSDT', self.signal(), boundary)
        first = self.engine.state['positions']['BTCUSDT']
        self.engine.enter('XRPUSDT', self.signal(), boundary)
        second = self.engine.state['positions']['XRPUSDT']
        self.assertEqual(decimal(first['budget']), Decimal('100'))
        self.assertLess(decimal(second['budget']), Decimal('100'))
        self.assertLessEqual(decimal(second['quantity']) * 100 / 5, decimal(second['budget']))

    def minimum_wallet(self, wallet='20', symbol='BTCUSDT'):
        self.client.wallet = Decimal(wallet)
        self.engine.state['day'] = None
        self.client.filter_overrides[symbol] = {'MIN_NOTIONAL': {'notional': '50'}}

    def test_minimum_exception_keeps_long_and_short_protected(self):
        self.minimum_wallet()
        self.client.filter_overrides['XRPUSDT'] = {'MIN_NOTIONAL': {'notional': '50'}}
        boundary = self.begin()
        self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.engine.enter('XRPUSDT', self.signal(-1), boundary)
        first = self.engine.state['positions']['BTCUSDT']
        second = self.engine.state['positions']['XRPUSDT']
        self.assertEqual(first['requested_quantity'], '0.501')
        self.assertEqual(second['requested_quantity'], '0.501')
        self.assertEqual(decimal(first['base_budget']), Decimal('2'))
        self.assertLess(decimal(second['base_budget']), Decimal('2'))
        for record in (first, second):
            self.assertTrue(record['minimum_override'])
            self.assertEqual(record['phase'], 'open')
            self.assertGreater(decimal(record['budget']), decimal(record['base_budget']))
        self.assertEqual(len(self.client.algos), 4)
        self.assertEqual(len(self.client.held), 2)
        self.assertFalse(any(c[2].get('type') == 'MARKET' for c in self.writes()))
        self.assertTrue(any('최소 주문 조건' in e.get('message', '') for e in self.events))

    def test_minimum_quantity_alone_can_exceed_ten_percent(self):
        self.minimum_wallet(symbol='SOLUSDT')
        self.client.filter_overrides['SOLUSDT'] = {
            'LOT_SIZE': {'minQty': '.700'}, 'MIN_NOTIONAL': {'notional': '5'}}
        self.enter(symbol='SOLUSDT')
        record = self.engine.state['positions']['SOLUSDT']
        self.assertEqual(decimal(record['quantity']), Decimal('.700'))
        self.assertTrue(record['minimum_override'])
        self.assertEqual(record['leverage'], 5)
        self.assertEqual(record['phase'], 'open')

    def test_too_little_balance_for_minimum_plus_fee_skips_all_orders(self):
        self.minimum_wallet('5.02')
        self.enter()
        self.assertEqual(self.writes(), [])
        self.assertEqual(self.engine.state['positions'], {})
        self.assertTrue(any('수수료' in e.get('message', '') for e in self.events))

    def test_minimum_cannot_bypass_configured_notional_cap(self):
        self.minimum_wallet()
        self.engine.config['cap_notional'] = 49
        self.enter()
        self.assertEqual(self.writes(), [])

    def test_minimum_exception_survives_restart_without_new_order(self):
        self.minimum_wallet()
        self.enter()
        count = len(self.writes())
        self.client.authorized = False
        restored = Engine(self.client, Path(self.directory.name), config=load())
        restored.connect()
        self.assertFalse(restored.entries)
        restored.start()
        self.assertTrue(restored.state['positions']['BTCUSDT']['minimum_override'])
        self.assertEqual(restored.state['positions']['BTCUSDT']['phase'], 'open')
        self.assertEqual(len(self.writes()), count)
        self.assertEqual(len(self.client.algos), 2)

    def test_partial_fill_of_minimum_is_protected_without_top_up(self):
        self.minimum_wallet()
        self.client.fill_ratio = Decimal('.4')
        self.enter()
        self.engine.reconcile()
        record = self.engine.state['positions']['BTCUSDT']
        self.assertLess(decimal(record['quantity']) * 100, Decimal('50'))
        self.assertEqual(len([c for c in self.writes() if c[1] == '/fapi/v1/order']), 1)
        self.assertEqual(len(self.client.algos), 2)
        for order in self.client.algos.values():
            self.assertEqual(decimal(order['quantity']), decimal(record['quantity']))

    def test_excess_fill_cost_still_closes_minimum_exception(self):
        self.minimum_wallet()
        self.enter(direction=-1)
        self.client.held['BTCUSDT']['entryPrice'] = '101'
        self.engine.reconcile()
        self.assertEqual(self.client.held, {})
        closes = [c for c in self.writes() if c[2].get('type') == 'MARKET']
        self.assertEqual(len(closes), 1)
        self.assertEqual(closes[0][2]['reduceOnly'], 'true')

    def test_minimum_filter_is_refreshed_for_each_entry(self):
        boundary = self.begin()
        self.client.filter_overrides['BTCUSDT'] = {'MIN_NOTIONAL': {'notional': '1200'}}
        self.engine.enter('BTCUSDT', self.signal(), boundary)
        record = self.engine.state['positions']['BTCUSDT']
        self.assertGreaterEqual(decimal(record['requested_quantity']) * Decimal('99.99'), 1200)
        self.assertTrue(record['minimum_override'])
        self.assertEqual(len(self.client.algos), 2)

    def test_single_missing_position_snapshot_does_not_discard_protection(self):
        self.enter()
        held = self.client.held.pop('BTCUSDT')
        self.engine.reconcile()
        self.assertIn('BTCUSDT', self.engine.state['positions'])
        self.assertEqual(len(self.client.algos), 2)
        self.client.held['BTCUSDT'] = held
        self.engine.reconcile()
        self.assertNotIn('flat_since', self.engine.state['positions']['BTCUSDT'])

    def test_filled_order_waits_for_position_visibility(self):
        boundary = self.begin()
        self.client.timeout_entry = True
        with self.assertRaises(BinanceError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        held = self.client.held.pop('BTCUSDT')
        self.engine.reconcile()
        self.assertIn('BTCUSDT', self.engine.state['positions'])
        self.client.held['BTCUSDT'] = held
        self.engine.reconcile()
        self.assertEqual(len(self.client.algos), 2)

    def test_invalid_short_target_is_skipped_before_order(self):
        boundary = self.begin()
        signal = {**self.signal(-1), 'stop_distance': 13}
        self.engine.enter('XRPUSDT', signal, boundary)
        self.assertEqual(self.writes(), [])

    def test_daily_loss_gate_persists_across_restart(self):
        boundary = self.begin()
        self.client.wallet = Decimal('960')
        self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertTrue(self.engine.state['day']['blocked'])
        self.assertEqual(self.writes(), [])
        restarted = Engine(FakeBinance(), Path(self.directory.name), config=load())
        self.assertTrue(restarted.state['day']['blocked'])

    def test_foreign_position_and_orders_are_not_touched(self):
        self.client.held['BTCUSDT'] = {'symbol': 'BTCUSDT', 'positionAmt': '1', 'positionSide': 'BOTH',
                                      'entryPrice': '100', 'unRealizedProfit': '0'}
        with self.assertRaises(ValueError):
            self.engine.start()
        self.assertEqual(self.writes(), [])

    def test_hedge_and_multi_asset_modes_fail_closed(self):
        for flag in ('hedge', 'multi'):
            setattr(self.client, flag, True)
            with self.assertRaises(ValueError):
                self.engine.start()
            setattr(self.client, flag, False)
        self.assertEqual(self.writes(), [])

    def test_auto_add_margin_is_not_silently_enabled(self):
        boundary = self.begin()
        self.client.auto_margin = True
        with self.assertRaises(ValueError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertEqual(self.writes(), [])

    def test_stale_book_and_late_signal_never_trade(self):
        boundary = self.begin()
        self.client.book_age = 6000
        with self.assertRaises(ValueError):
            self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.client.book_age = 0
        self.client.ms += 100000
        self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertEqual(self.writes(), [])

    def test_only_closed_bars_and_gap_detection(self):
        self.begin()
        bars = self.engine.candles['BTCUSDT']
        self.assertEqual(bars[-1]['t'] + INTERVAL, self.client.ms // (INTERVAL * 1000) * INTERVAL)
        self.engine.candles['BTCUSDT'].pop(100)
        with self.assertRaises(ValueError):
            self.engine.bars('BTCUSDT')

    def test_manual_position_increase_is_not_closed_by_bot(self):
        self.enter()
        before = len(self.writes())
        self.client.held['BTCUSDT']['positionAmt'] = '1000'
        with self.assertRaises(ValueError):
            self.engine.reconcile()
        self.assertEqual(len(self.writes()), before)

    def test_disk_failure_before_intent_prevents_order(self):
        boundary = self.begin()
        with patch.object(self.engine, 'save', side_effect=OSError('disk full')):
            with self.assertRaises(OSError):
                self.engine.enter('BTCUSDT', self.signal(), boundary)
        self.assertEqual(self.writes(), [])


class Reply:
    def __init__(self, payload): self.payload = payload
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self, limit): return json.dumps(self.payload).encode()

class Capture:
    def __init__(self, payload=None, error=None):
        self.payload, self.error, self.requests = payload or {}, error, []
    def open(self, request, timeout):
        self.requests.append(request)
        if self.error: raise self.error
        return Reply(self.payload)

class ClientTests(unittest.TestCase):
    def test_signature_matches_exact_encoded_query_and_key_stays_in_header(self):
        import hashlib, hmac
        capture = Capture()
        client = Binance('fake-api-key', 'fake-api-secret', capture)
        client.call('GET', '/fapi/v3/account')
        request = capture.requests[0]
        query, signature = urlsplit(request.full_url).query.rsplit('&signature=', 1)
        self.assertEqual(signature, hmac.new(b'fake-api-secret', query.encode(), hashlib.sha256).hexdigest())
        self.assertEqual(request.get_header('X-mbx-apikey'), 'fake-api-key')
        self.assertNotIn('fake-api-secret', request.full_url)

    def test_mutations_and_other_hosts_endpoints_are_denied(self):
        capture = Capture()
        client = Binance('key', 'secret', capture)
        with self.assertRaises(PermissionError): client.call('POST', '/fapi/v1/order', {})
        client.authorized = True
        with self.assertRaises(PermissionError): client.call('POST', '/sapi/v1/capital/withdraw/apply', {})
        with self.assertRaises(ValueError): client.call('GET', 'https://other.example/')
        self.assertEqual(capture.requests, [])

    def test_post_uses_signed_body_and_never_retries(self):
        capture = Capture(error=URLError('private signed URL'))
        client = Binance('fake-key', 'fake-secret', capture)
        client.authorized = True
        with self.assertRaises(BinanceError) as raised:
            client.call('POST', '/fapi/v1/order', {'symbol': 'BTCUSDT'})
        self.assertTrue(raised.exception.uncertain)
        self.assertEqual(len(capture.requests), 1)
        self.assertNotIn('?', capture.requests[0].full_url)
        self.assertIn(b'signature=', capture.requests[0].data)
        self.assertNotIn('private signed URL', str(raised.exception))

    def test_rate_limit_sets_backoff(self):
        error = HTTPError('hidden', 429, 'Too many', {'Retry-After': '120'}, io.BytesIO(b'{"code":-1003,"msg":"slow"}'))
        capture = Capture(error=error)
        client = Binance('fake-key', 'fake-secret', capture)
        with self.assertRaises(BinanceError): client.call('GET', '/fapi/v3/account')
        with self.assertRaises(BinanceError): client.call('GET', '/fapi/v3/account')
        self.assertEqual(len(capture.requests), 1)

    def test_api_error_redacts_credentials(self):
        body = b'{"code":-2015,"msg":"fake-api-key fake-api-secret"}'
        capture = Capture(error=HTTPError('hidden', 401, 'bad key', {}, io.BytesIO(body)))
        client = Binance('fake-api-key', 'fake-api-secret', capture)
        with self.assertRaises(BinanceError) as raised: client.call('GET', '/fapi/v3/account')
        self.assertNotIn('fake-api-key', str(raised.exception))
        self.assertNotIn('fake-api-secret', str(raised.exception))

if __name__ == '__main__':
    unittest.main()
