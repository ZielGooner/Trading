"""Durable, account-separated trade history. This module never sends orders."""
from contextlib import contextmanager
import hashlib
import json
import sqlite3
import threading
import time
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path

DAY = 86400000
SYMBOLS = ('BTCUSDT', 'XRPUSDT', 'SOLUSDT', 'HYPEUSDT')
LEGACY_UNKNOWN_STRATEGY = {'schema': 'legacy_unknown', 'strategy_version': '원전략 미확인'}


def number(value):
    v = Decimal(str(value))
    if not v.is_finite():
        raise ValueError('Nonfinite history value')
    return v


def encode(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, sort_keys=True)


def strategy_digest(config):
    """Keep the archived config fingerprint stable across displays and exports."""
    return hashlib.sha256(encode(config).encode()).hexdigest()[:12]


def identity(key):
    return hashlib.sha256(key.encode()).hexdigest()[:24]


def valid_account(account):
    if len(account) != 24 or any(c not in '0123456789abcdef' for c in account):
        raise ValueError('계정 식별자 오류')
    return account


class HistoryStore:
    def __init__(self, root, account):
        self.root, self.account = Path(root), valid_account(account)
        self.path = self.root / 'private_state' / ('history-' + account + '.sqlite3')
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS fills (
                    symbol TEXT NOT NULL, id TEXT NOT NULL, t INTEGER NOT NULL,
                    payload TEXT NOT NULL, PRIMARY KEY(symbol,id));
                CREATE TABLE IF NOT EXISTS funding (
                    id TEXT PRIMARY KEY, t INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS positions (
                    id TEXT PRIMARY KEY, symbol TEXT NOT NULL, payload TEXT NOT NULL,
                    strategy TEXT NOT NULL, active INTEGER NOT NULL, closed_ms INTEGER);
                CREATE INDEX IF NOT EXISTS fills_time ON fills(t);
            """)

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=5)
        try:
            db.execute('PRAGMA journal_mode=WAL')
            db.execute('PRAGMA synchronous=FULL')
            with db:
                yield db
        finally:
            db.close()

    def meta(self):
        with self.connect() as db:
            return {k: json.loads(v) for k, v in db.execute('SELECT key,value FROM meta')}

    def set_meta(self, **values):
        with self.connect() as db:
            db.executemany('INSERT OR REPLACE INTO meta VALUES (?,?)',
                           [(k, encode(v)) for k, v in values.items()])

    def remember(self, positions, now):
        # Called before orders are sent. Removed runtime records remain in this archive.
        with self.connect() as db:
            active = {row[0] for row in db.execute('SELECT id FROM positions WHERE active=1')}
            current = set()
            for symbol, record in positions.items():
                current.add(record['id'])
                snapshot = record.get('strategy_snapshot')
                # A recovered position without its entry-time settings cannot be
                # attributed to today's strategy. Existing archive hashes stay unchanged.
                archived_strategy = snapshot if isinstance(snapshot, dict) and snapshot else LEGACY_UNKNOWN_STRATEGY
                db.execute("""INSERT INTO positions VALUES (?,?,?,?,1,NULL)
                    ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,active=1,closed_ms=NULL""",
                    (record['id'], symbol, encode(record), encode(archived_strategy)))
            for record_id in active - current:
                db.execute('UPDATE positions SET active=0,closed_ms=? WHERE id=?', (int(now), record_id))

    def bind_entry(self, record_id, order_id):
        with self.connect() as db:
            row = db.execute('SELECT payload FROM positions WHERE id=?', (record_id,)).fetchone()
            if row:
                payload = json.loads(row[0])
                payload['entry_order_id'] = str(order_id)
                db.execute('UPDATE positions SET payload=? WHERE id=?', (encode(payload), record_id))

    def ingest_fills(self, rows, symbol):
        clean = []
        for row in rows:
            if row['symbol'] != symbol or symbol not in SYMBOLS or row['side'] not in ('BUY', 'SELL'):
                raise ValueError('체결 응답 종목/방향 오류')
            if number(row['qty']) <= 0 or number(row['price']) <= 0:
                raise ValueError('체결 수량/가격 오류')
            for key in ('realizedPnl', 'commission'):
                number(row[key])
            # Keep only account-trade fields, never request headers or credentials.
            payload = {k: row[k] for k in ('symbol','id','orderId','time','side','positionSide',
                        'price','qty','quoteQty','realizedPnl','commission','commissionAsset','maker')
                       if k in row}
            clean.append((symbol, str(row['id']), int(row['time']), encode(payload)))
        with self.connect() as db:
            db.executemany('INSERT OR REPLACE INTO fills VALUES (?,?,?,?)', clean)

    def ingest_funding(self, rows):
        clean = []
        for row in rows:
            if row['incomeType'] != 'FUNDING_FEE':
                raise ValueError('펀딩 응답 종류 오류')
            if row.get('symbol') not in SYMBOLS:
                continue
            number(row['income'])
            payload = {k: row[k] for k in ('symbol','incomeType','income','asset','time','tranId')}
            clean.append((str(row['tranId']), int(row['time']), encode(payload)))
        with self.connect() as db:
            db.executemany('INSERT OR REPLACE INTO funding VALUES (?,?,?)', clean)

    def snapshot(self):
        with self.connect() as db:
            db.execute('BEGIN')
            fills = [json.loads(x[0]) for x in db.execute('SELECT payload FROM fills ORDER BY t,CAST(id AS INTEGER)')]
            funding = [json.loads(x[0]) for x in db.execute('SELECT payload FROM funding ORDER BY t,id')]
            positions = [dict(id=x[0], symbol=x[1], record=json.loads(x[2]),
                              strategy=json.loads(x[3]), active=bool(x[4]), closed_ms=x[5])
                         for x in db.execute('SELECT * FROM positions ORDER BY closed_ms,id')]
            meta = {k: json.loads(v) for k, v in db.execute('SELECT key,value FROM meta')}
        return dict(fills=fills, funding=funding, positions=positions, meta=meta)


def cancelled(cancel):
    if cancel and cancel.is_set():
        raise InterruptedError('작업을 취소했습니다.')


def sync_history(client, store, cancel=None, emit=lambda message: None, full=False):
    """Fetch bounded windows; never advance coverage after an incomplete response."""
    cancelled(cancel)
    try:
        client.sync()
    except Exception as error:
        store.set_meta(sync_error=client.safe_error(str(error)))
        raise
    end = client.now() - 5000
    old = store.meta()
    earliest = end - 89 * DAY  # Exchange REST retention is three months.
    start = earliest if full or not old.get('sync_end') else max(earliest, int(old['sync_end']) - 3 * DAY)
    emit('Binance 체결·펀딩 내역을 조회하고 있습니다.')
    requests = 0

    def request(path, params):
        nonlocal requests
        cancelled(cancel)
        if cancel:
            if cancel.wait(.08):
                cancelled(cancel)
        requests += 1
        return client.call('GET', path, params)

    def trades(symbol, begin, finish):
        rows = request('/fapi/v1/userTrades', dict(symbol=symbol,startTime=begin,endTime=finish,limit=1000))
        if not isinstance(rows, list):
            raise ValueError('체결 내역 응답 형식 오류')
        if any(not begin <= int(r['time']) <= finish for r in rows):
            raise ValueError('체결 내역의 조회 기간이 일치하지 않습니다.')
        if len(rows) >= 1000:
            if begin == finish:
                raise ValueError('동일 밀리초에 체결이 1,000개 이상입니다. 전체 내역 확인이 필요합니다.')
            midpoint = (begin + finish) // 2
            trades(symbol, begin, midpoint)
            trades(symbol, midpoint + 1, finish)
        else:
            store.ingest_fills(rows, symbol)

    try:
        for symbol in SYMBOLS:
            cursor = start
            while cursor <= end:
                finish = min(cursor + 7 * DAY - 1, end)
                trades(symbol, cursor, finish)
                cursor = finish + 1
            emit(symbol + ' 체결 내역 저장 완료')
        cursor = start
        while cursor <= end:
            finish = min(cursor + 7 * DAY - 1, end)
            page, seen = 1, set()
            while True:
                rows = request('/fapi/v1/income', dict(incomeType='FUNDING_FEE',
                           startTime=cursor,endTime=finish,limit=1000,page=page))
                if not isinstance(rows, list) or any(not cursor <= int(r['time']) <= finish for r in rows):
                    raise ValueError('펀딩 내역 응답 형식/기간 오류')
                keys = {str(r['tranId']) for r in rows}
                if rows and keys.issubset(seen):
                    raise ValueError('펀딩 페이지가 반복되어 전체 수집을 확인할 수 없습니다.')
                seen.update(keys)
                store.ingest_funding(rows)
                if len(rows) < 1000:
                    break
                page += 1
                if page > 10000:
                    raise ValueError('펀딩 페이지 한도 초과')
            cursor = finish + 1
        # Recover entry order links for records created by an older program version.
        for p in store.snapshot()['positions']:
            if p['record'].get('entry_order_id'):
                continue
            try:
                order = request('/fapi/v1/order', dict(symbol=p['symbol'], origClientOrderId=p['id']+'-e'))
                if order.get('orderId') is not None:
                    store.bind_entry(p['id'], order['orderId'])
            except Exception as error:
                if getattr(error, 'code', None) != -2013:
                    raise
        gaps = old.get('coverage_gaps', [])
        if old.get('sync_end') is not None and start > int(old['sync_end'])+1:
            gaps.append([int(old['sync_end'])+1,start-1])
        gaps = [g for g in gaps if not (start <= g[0] and g[1] <= end)]
        store.set_meta(coverage_gaps=gaps, sync_start=min(int(old.get('sync_start', start)), start),
                       sync_end=end, synced_at=client.now(), sync_error=None)
        return {'requests': requests, 'through': end}
    except Exception as error:
        store.set_meta(sync_error=client.safe_error(str(error)))
        raise


def closed_trades(snapshot):
    """Match archived bot positions to actual entry fills and a balanced exit."""
    fills, funds, meta = snapshot['fills'], snapshot['funding'], snapshot['meta']
    result = []
    for position in snapshot['positions']:
        record, symbol = position['record'], position['symbol']
        order_id = record.get('entry_order_id')
        entries = [f for f in fills if f['symbol'] == symbol and str(f['orderId']) == str(order_id)] if order_id else []
        if not entries:
            if record.get('quantity') and not position['active']:
                result.append(dict(symbol=symbol, id=position['id'], status='진입 체결 미확인',
                                   closed_ms=position['closed_ms'], net=None))
            continue
        direction = record['direction']
        side = 'BUY' if direction == 1 else 'SELL'
        opened = min(int(f['time']) for f in entries)
        observed = int(position['closed_ms'] if not position['active'] else meta.get('sync_end', 0))
        if observed < opened:
            continue
        segment = [f for f in fills if f['symbol'] == symbol and opened <= int(f['time']) <= observed]
        exits = [f for f in segment if f['side'] != side and f.get('positionSide','BOTH') == 'BOTH']
        others = [f for f in segment if f['side'] == side and str(f['orderId']) != str(order_id)]
        quantity = sum((number(f['qty']) for f in entries), Decimal(0))
        exit_quantity = sum((number(f['qty']) for f in exits), Decimal(0))
        # A server TP/SL may fill while the app is closed. Balanced actual fills
        # can establish completion before live order management is restarted.
        if position['active'] and quantity != exit_quantity:
            continue
        closed = max((int(f['time']) for f in exits), default=observed)
        trade_funds = [f for f in funds if f['symbol'] == symbol and opened < int(f['time']) <= closed]
        chosen = entries + exits
        fees = sum((number(f['commission']) for f in chosen if f['commissionAsset'] == 'USDT'), Decimal(0))
        funding = sum((number(f['income']) for f in trade_funds if f['asset'] == 'USDT'), Decimal(0))
        gross = sum((number(f['realizedPnl']) for f in exits), Decimal(0))
        status = '확인 완료'
        if others or any(f.get('positionSide','BOTH') != 'BOTH' for f in segment):
            status = '외부 진입 혼합'
        elif any(f['side'] != side or number(f['realizedPnl']) != 0 for f in entries) or quantity != exit_quantity or quantity != number(record.get('quantity', quantity)):
            status = '진입/청산 수량 불일치'
        elif (int(meta.get('sync_start', observed+1)) > opened or int(meta.get('sync_end',0)) < observed
              or any(a <= observed and b >= opened for a,b in meta.get('coverage_gaps',[]))):
            status = '조회 범위/청산 확인 대기'
        elif meta.get('sync_error'):
            status = '최근 동기화 실패'
        elif any(f['commissionAsset'] != 'USDT' and number(f['commission']) != 0 for f in chosen) or any(f['asset'] != 'USDT' for f in trade_funds):
            status = '비USDT 비용 환산 필요'
        net = gross - fees + funding if status == '확인 완료' else None
        entry_value = sum((number(f['qty'])*number(f['price']) for f in entries), Decimal(0))
        exit_value = sum((number(f['qty'])*number(f['price']) for f in exits), Decimal(0))
        leverage = int(record['leverage'])
        version = strategy_digest(position['strategy'])
        result.append(dict(id=position['id'],symbol=symbol,direction='LONG' if direction==1 else 'SHORT',
            opened_ms=opened,closed_ms=closed,quantity=float(quantity),entry=float(entry_value/quantity),
            exit=float(exit_value/exit_quantity) if exit_quantity else None, leverage=leverage,
            gross=float(gross),fees=float(fees),funding=float(funding),net=float(net) if net is not None else None,
            margin_return=float(net/(entry_value/leverage)) if net is not None else None,
            hours=(closed-opened)/3600000, status=status, strategy_version=version,
            strategy_name=position['strategy'].get('strategy_version', position['strategy'].get('schema', '과거 전략')),
            minimum_override=bool(record.get('minimum_override')),stop=record.get('stop'),
            target=record.get('target'),exit_reason=record.get('close_reason','거래소/수동 종료 (원인 미확정)')))
    return sorted(result,key=lambda x:(x['closed_ms'],x['id']))


def statistics(rows):
    eligible = [r for r in rows if r.get('net') is not None and r.get('status') == '확인 완료']
    profits = [number(r['net']) for r in eligible]
    positive = sum((v for v in profits if v > 0), Decimal(0))
    negative = -sum((v for v in profits if v < 0), Decimal(0))
    equity = peak = drawdown = Decimal(0)
    for value in profits:
        equity += value
        peak = max(peak, equity)
        drawdown = max(drawdown, peak-equity)
    return dict(closed_records=len(rows),verified_trades=len(eligible),excluded_trades=len(rows)-len(eligible),
                wins=sum(v>0 for v in profits),losses=sum(v<0 for v in profits),break_even=sum(v==0 for v in profits),
                win_rate=float(sum(v>0 for v in profits)/len(profits)) if profits else None,
                net_usdt=float(sum(profits,Decimal(0))) if profits else None,
                profit_factor=float(positive/negative) if negative else None,
                profit_factor_note='손실 거래 없음' if profits and not negative else '자료 없음' if not profits else '',
                average_net_usdt=float(sum(profits,Decimal(0))/len(profits)) if profits else None,
                realized_drawdown_usdt=float(drawdown) if profits else None,
                fees_usdt=sum(r.get('fees',0) for r in eligible),
                funding_usdt=sum(r.get('funding',0) for r in eligible))



def account_activity(snapshot, start, end):
    """Period cash flows, not round-trip strategy returns. Includes manual/external fills."""
    selected=[f for f in snapshot['fills'] if start<=int(f['time'])<end]
    funding=[f for f in snapshot['funding'] if start<=int(f['time'])<end]
    def total(rows,key):
        return float(sum((number(r[key]) for r in rows),Decimal(0)))
    fees={asset:total([f for f in selected if f['commissionAsset']==asset],'commission')
          for asset in sorted({f['commissionAsset'] for f in selected})}
    funds={asset:total([f for f in funding if f['asset']==asset],'income')
           for asset in sorted({f['asset'] for f in funding})}
    by_symbol={}
    for symbol in SYMBOLS:
        sf=[f for f in selected if f['symbol']==symbol]
        funding_usdt=total([f for f in funding if f['symbol']==symbol and f['asset']=='USDT'],'income')
        fee_usdt=total([f for f in sf if f['commissionAsset']=='USDT'],'commission')
        gross=total(sf,'realizedPnl')
        by_symbol[symbol]=dict(fill_count=len(sf),realized_pnl_usdt=gross,usdt_commission=fee_usdt,
                               funding_usdt=funding_usdt,usdt_cash_flow=gross-fee_usdt+funding_usdt)
    recent=[{k:f.get(k) for k in ('symbol','time','side','positionSide','price','qty','realizedPnl','commission','commissionAsset')}
            for f in selected[-100:]]
    return dict(scope='BTC/XRP/SOL/HYPE 전체 계좌 체결. 수동/외부 및 전략 귀속 미확인 거래 포함.',
                fill_count=len(selected),funding_count=len(funding),realized_pnl_usdt=total(selected,'realizedPnl'),
                commission_by_asset=fees,funding_by_asset=funds,
                usdt_cash_flow=total(selected,'realizedPnl')-fees.get('USDT',0)+funds.get('USDT',0),
                cash_flow_definition='선택 기간 내 실현손익 - USDT 수수료 + USDT 펀딩. 비USDT 비용은 별도이며 포지션 전체 순손익/계좌 수익률이 아님.',
                strategy_attribution='프로그램 진입 기록과 연결되지 않은 거래는 현재 전략의 성과로 단정할 수 없음.',
                by_symbol=by_symbol,recent_fills=recent,omitted_fills=max(0,len(selected)-100))


def analysis_input(store, config, days, now=None):
    if not 1 <= int(days) <= 365:
        raise ValueError('분석 기간은 1~365일입니다.')
    now = int(now or time.time()*1000)
    snapshot = store.snapshot()
    all_trades = closed_trades(snapshot)
    start = now-int(days)*DAY
    chosen = [r for r in all_trades if start <= r['closed_ms'] < now]
    previous = [r for r in all_trades if start-int(days)*DAY <= r['closed_ms'] < start]
    current_version = strategy_digest(config)
    current_chosen = [r for r in chosen if r.get('strategy_version') == current_version]
    current_previous = [r for r in previous if r.get('strategy_version') == current_version]
    versions = sorted({r['strategy_version'] for r in chosen if r.get('strategy_version')})
    meta = snapshot['meta']
    public_rows = [{k:v for k,v in r.items() if k != 'id'} for r in chosen]
    return dict(period={'days':int(days),'start_utc':datetime.fromtimestamp(start/1000,timezone.utc).isoformat(),
                        'end_utc':datetime.fromtimestamp(now/1000,timezone.utc).isoformat(),'basis':'청산 시각 기준, 각 거래 전체 보유기간의 비용 포함'},
                coverage={'sync_start_ms':meta.get('sync_start'),'sync_end_ms':meta.get('sync_end'),
                          'sync_error':meta.get('sync_error'),'requested_period_covered':bool(meta.get('sync_start') is not None and meta['sync_start']<=start
                              and not any(a<now and b>=start for a,b in meta.get('coverage_gaps',[]))),
                          'snapshot_age_minutes':(now-meta['sync_end'])/60000 if meta.get('sync_end') else None},
                statistics=statistics(current_chosen),previous_period=statistics(current_previous),
                current_strategy_version=current_version,
                current_strategy_name=config.get('strategy_version', config.get('schema', '현재 전략')),
                statistics_scope='현재 설정과 같은 전략 버전의 귀속 확인 완료 거래',
                historical_strategy_statistics=statistics(chosen),
                by_strategy_version={version:statistics([r for r in chosen if r.get('strategy_version') == version]) for version in versions},
                account_activity=account_activity(snapshot,start,now),
                previous_account_activity=account_activity(snapshot,start-int(days)*DAY,start),
                by_symbol={s:statistics([r for r in current_chosen if r['symbol']==s]) for s in SYMBOLS},
                by_direction={s:statistics([r for r in current_chosen if r.get('direction')==s]) for s in ('LONG','SHORT')},
                current_strategy={k:v for k,v in config.items() if k not in ('orders_enabled','initial_equity')},
                live_order_switch='실계좌 실행 스위치 상태는 이 분석에 제공되지 않음',
                trades=public_rows[-500:],trades_omitted=max(0,len(public_rows)-500),
                definitions=['원시 체결은 모든 BTC/XRP/SOL/HYPE 거래를 보관한다. 현재 전략 통계에는 프로그램 진입과 정확히 연결되고 현재 설정 버전이 일치하는 완료 거래만 포함한다.',
                             '과거 전략 기록과 전략 버전 해시는 보존하며 버전별 통계와 전체 확인 거래 통계로 구분한다. 현재 전략 성과로 합산하지 않는다.',
                             '진입 당시 설정이 없는 과거 기록은 원전략 미확인으로 보관한다. 현재 설정으로 추정해 귀속하지 않는다.',
                             '순손익 = 거래소 실현손익 - 진입/청산 USDT 수수료 + 보유기간 펀딩. 불명확한 거래는 제외한다.',
                             '승률 분모는 순손익이 확인된 완료 거래 수이며 손익 0 거래도 분모에 포함한다.',
                             '낙폭은 완료 거래의 누적 실현손익 USDT 기준이다. 미실현손익 포함 계좌 최대낙폭/계좌 수익률은 계산하지 않는다.',
                             '초기 REST 복원은 최근 89일. 이전에 로컬에 저장한 내역은 계속 유지한다.',
                             '엑셀은 로컬 원장의 재생성 결과이며 수동 편집은 다음 저장 시 덮어쓴다.'])

