"""Read-only, paged views of the saved account ledger. No exchange requests."""
import json
import sqlite3
from contextlib import closing
from pathlib import Path

from trading.history import DAY, SYMBOLS, closed_trades, valid_account


def history_page(snapshot, start_ms, end_ms, symbol='', page=1, view='trades'):
    start_ms, end_ms = int(start_ms), int(end_ms)
    if not 0 < end_ms - start_ms <= 365 * DAY:
        raise ValueError('조회 기간은 1~365일입니다.')
    if symbol and symbol not in SYMBOLS:
        raise ValueError('지원하지 않는 심볼입니다.')
    if view not in ('trades', 'fills'):
        raise ValueError('지원하지 않는 거래 보기입니다.')
    if isinstance(page, bool) or int(page) != page or page < 1:
        raise ValueError('페이지 번호 오류')
    rows = []
    if view == 'trades':
        for trade in reversed(closed_trades(snapshot)):
            if start_ms <= trade['closed_ms'] < end_ms and (not symbol or trade['symbol'] == symbol):
                rows.append(dict(time=trade['closed_ms'], symbol=trade['symbol'],
                                 direction=trade.get('direction', '').lower(), entry=trade.get('entry'),
                                 exit=trade.get('exit'), quantity=trade.get('quantity'), fees=trade.get('fees'),
                                 fee_asset='USDT', gross=trade.get('gross'), status=trade['status']))
    else:
        # BUY/SELL is not a position direction for one-way (BOTH) account fills.
        # Keep it separate; never infer long/short from the order side alone.
        for fill in reversed(snapshot['fills']):
            if start_ms <= int(fill['time']) < end_ms and (not symbol or fill['symbol'] == symbol):
                position_side = fill.get('positionSide', 'BOTH')
                rows.append(dict(time=int(fill['time']), symbol=fill['symbol'],
                                 direction=position_side.lower() if position_side in ('LONG', 'SHORT') else '',
                                 side=fill['side'].lower(), entry=fill['price'], exit=None,
                                 quantity=fill['qty'], fees=fill['commission'], fee_asset=fill['commissionAsset'],
                                 gross=fill['realizedPnl'], status='계좌 체결'))
    total = len(rows)
    pages = max(1, (total + 19) // 20)
    page = min(int(page), pages)
    offset = (page - 1) * 20
    return dict(rows=rows[offset:offset + 20], total=total, page=page, pages=pages,
                page_size=20, view=view, synced_at=snapshot['meta'].get('synced_at'),
                sync_error=snapshot['meta'].get('sync_error'))


def read_history_page(root, account, start_ms, end_ms, symbol='', page=1, view='trades'):
    account = valid_account(account)
    path = Path(root) / 'private_state' / ('history-' + account + '.sqlite3')
    snapshot = dict(fills=[], funding=[], positions=[], meta={})
    if path.exists():
        # A tab switch must not create an account database or run migrations.
        with closing(sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=5)) as db:
            db.execute('BEGIN')
            snapshot['fills'] = [json.loads(row[0]) for row in db.execute('SELECT payload FROM fills ORDER BY t,CAST(id AS INTEGER)')]
            snapshot['funding'] = [json.loads(row[0]) for row in db.execute('SELECT payload FROM funding ORDER BY t,id')]
            snapshot['positions'] = [dict(id=row[0], symbol=row[1], record=json.loads(row[2]),
                                          strategy=json.loads(row[3]), active=bool(row[4]), closed_ms=row[5])
                                     for row in db.execute('SELECT * FROM positions ORDER BY closed_ms,id')]
            snapshot['meta'] = {key: json.loads(value) for key, value in db.execute('SELECT key,value FROM meta')}
    return history_page(snapshot, start_ms, end_ms, symbol, page, view)
