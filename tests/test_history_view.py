import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from trading.history import HistoryStore, DAY
from trading.history_view import history_page, read_history_page

ACCOUNT = 'a' * 24
START = 1790780400000  # 2026-10-01 00:00 KST

class HistoryViewTests(unittest.TestCase):
    def snapshot(self):
        return dict(fills=[], funding=[], positions=[], meta={})

    def trades(self, count=45):
        return [dict(id=str(i), closed_ms=START + i * 1000, symbol='BTCUSDT' if i % 2 == 0 else 'XRPUSDT',
                     direction='LONG' if i % 2 == 0 else 'SHORT', entry=100, exit=110, quantity=1,
                     gross=10, fees=.2, net=9.8, status='확인 완료') for i in range(count)]

    def test_stable_descending_pages_and_partial_last_page(self):
        with patch('trading.history_view.closed_trades', return_value=self.trades()):
            pages = [history_page(self.snapshot(), START, START + DAY, page=page) for page in (1, 2, 3)]
        self.assertEqual([len(page['rows']) for page in pages], [20, 20, 5])
        self.assertEqual([row['time'] for page in pages for row in page['rows']], [START + i * 1000 for i in reversed(range(45))])
        self.assertEqual(pages[0]['total'], 45)
        self.assertEqual(pages[2]['pages'], 3)
        self.assertNotIn('id', pages[0]['rows'][0])
        self.assertEqual(pages[0]['rows'][0]['direction'], 'long')

    def test_period_is_inclusive_start_exclusive_end_and_symbol_before_paging(self):
        with patch('trading.history_view.closed_trades', return_value=self.trades()):
            page = history_page(self.snapshot(), START + 10000, START + 30000, 'XRPUSDT')
        self.assertEqual(page['total'], 10)
        self.assertEqual([row['time'] for row in page['rows']], [START + i * 1000 for i in reversed(range(11, 30, 2))])
        self.assertTrue(all(row['direction'] == 'short' for row in page['rows']))

    def test_changed_filters_clamp_page_and_missing_price_remains_unknown(self):
        rows = [dict(id='pending', closed_ms=START, symbol='BTCUSDT', status='진입 체결 미확인', net=None)]
        with patch('trading.history_view.closed_trades', return_value=rows):
            page = history_page(self.snapshot(), START, START + DAY, page=100)
        self.assertEqual(page['page'], 1)
        self.assertIsNone(page['rows'][0]['entry'])
        self.assertIsNone(page['rows'][0]['gross'])
        self.assertEqual(page['rows'][0]['direction'], '')

    def test_whole_account_fills_do_not_guess_position_direction(self):
        snapshot = self.snapshot()
        for i, side in enumerate(('BUY', 'SELL')):
            snapshot['fills'].append(dict(id=i, orderId=i + 100, time=START + i, symbol='BTCUSDT', side=side,
                                          positionSide='BOTH', price='100', qty='1', commission='.01', commissionAsset='BNB', realizedPnl='0'))
        result = history_page(snapshot, START, START + DAY, view='fills')
        self.assertEqual(result['total'], 2)
        self.assertEqual([row['side'] for row in result['rows']], ['sell', 'buy'])
        self.assertTrue(all(row['direction'] == '' for row in result['rows']))
        self.assertEqual(result['rows'][0]['fee_asset'], 'BNB')

    def test_empty_read_does_not_create_database_or_directories(self):
        with tempfile.TemporaryDirectory() as root:
            result = read_history_page(root, ACCOUNT, START, START + DAY)
            self.assertEqual(result['rows'], [])
            self.assertEqual(result['page_size'], 20)
            self.assertFalse((Path(root) / 'private_state').exists())

    def test_existing_ledger_is_read_without_modifying_records(self):
        with tempfile.TemporaryDirectory() as root:
            store = HistoryStore(root, ACCOUNT)
            rows = [dict(symbol='BTCUSDT', id=i, orderId=i + 1, time=START + i * 1000, side='BUY', positionSide='BOTH',
                         price='100', qty='1', quoteQty='100', realizedPnl='0', commission='.01', commissionAsset='USDT', maker=False) for i in range(23)]
            store.ingest_fills(rows, 'BTCUSDT')
            before = store.snapshot()
            result = read_history_page(root, ACCOUNT, START, START + DAY, view='fills', page=2)
            self.assertEqual(result['total'], 23)
            self.assertEqual(len(result['rows']), 3)
            self.assertEqual(store.snapshot(), before)

    def test_invalid_filters_are_rejected(self):
        for kwargs in (dict(symbol='INVALID'), dict(page=0), dict(page=1.5), dict(page=True), dict(view='invalid')):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                history_page(self.snapshot(), START, START + DAY, **kwargs)
        for end in (START, START - DAY, START + 366 * DAY):
            with self.subTest(end=end), self.assertRaises(ValueError):
                history_page(self.snapshot(), START, end)

if __name__ == '__main__':
    unittest.main()
