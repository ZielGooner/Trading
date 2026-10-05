import copy
import json
import os
from pathlib import Path
import shutil
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile
from xml.etree import ElementTree
from trading.history import HistoryStore, closed_trades, statistics, analysis_input, sync_history, DAY, strategy_digest, LEGACY_UNKNOWN_STRATEGY
from trading.config import load, HERE
from trading.review import analyze, validate_response, SCHEMA

ACCOUNT='0123456789abcdef01234567'
NOW=1800000000000


def fill(trade_id, order, t, side, qty, pnl='0', fee='.1', asset='USDT', symbol='BTCUSDT'):
    return dict(symbol=symbol,id=trade_id,orderId=order,time=t,side=side,positionSide='BOTH',
                price='100' if side=='BUY' else '120',qty=str(qty),quoteQty=str(float(qty)*100),
                realizedPnl=str(pnl),commission=str(fee),commissionAsset=asset,maker=False)


class HistoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)
        self.store=HistoryStore(self.root,ACCOUNT)
        self.config=load()
        self.opened=NOW-20*DAY
        self.closed=NOW-3*DAY
        self.record=dict(id='tr-owned',phase='open',direction=1,entry_order_id='100',quantity='1',
                         leverage=10,stop='90',target='160',minimum_override=True,
                         strategy_snapshot=copy.deepcopy(self.config))
        self.rows=[fill(1,100,self.opened,'BUY','.4'),
                   fill(2,100,self.opened+1,'BUY','.6'),
                   fill(3,101,self.closed-1,'SELL','.5','10'),
                   {**fill(4,102,self.closed,'SELL','.5','20'),'price':'140','quoteQty':'70'}]

    def seeded(self):
        self.store.remember({'BTCUSDT':self.record},self.opened)
        self.store.remember({},self.closed+10000)
        self.store.ingest_fills(self.rows,'BTCUSDT')
        self.store.ingest_funding([dict(symbol='BTCUSDT',incomeType='FUNDING_FEE',income='-2',
                                       asset='USDT',time=self.opened+DAY,tranId=99)])
        self.store.set_meta(sync_start=NOW-89*DAY,sync_end=NOW-5000,sync_error=None)
        return closed_trades(self.store.snapshot())

    def test_completed_position_archive_survives_runtime_removal_and_restart(self):
        self.seeded()
        restored=HistoryStore(self.root,ACCOUNT).snapshot()
        self.assertEqual(len(restored['positions']),1)
        self.assertFalse(restored['positions'][0]['active'])
        self.assertEqual(restored['positions'][0]['record']['quantity'],'1')

    def test_partial_fills_fees_and_funding_reconcile_exactly(self):
        row=self.seeded()[0]
        self.assertEqual(row['status'],'확인 완료')
        self.assertAlmostEqual(row['net'],27.6)
        self.assertAlmostEqual(row['fees'],.4)
        self.assertAlmostEqual(row['funding'],-2)
        self.assertAlmostEqual(row['entry'],100)
        self.assertAlmostEqual(row['exit'],130)
        self.assertAlmostEqual(row['margin_return'],2.76)
        self.assertTrue(row['minimum_override'])

    def test_hype_fills_and_funding_are_retained_without_orders(self):
        rows=[fill(80,800,self.closed,'SELL','1','7',symbol='HYPEUSDT')]
        self.store.ingest_fills(rows,'HYPEUSDT')
        self.store.ingest_funding([dict(symbol='HYPEUSDT',incomeType='FUNDING_FEE',income='-.2',
                                       asset='USDT',time=self.closed,tranId=800)])
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertIn('HYPEUSDT',payload['by_symbol'])
        self.assertAlmostEqual(payload['account_activity']['by_symbol']['HYPEUSDT']['usdt_cash_flow'],6.7)
        self.assertEqual(payload['statistics']['verified_trades'],0)

    def test_archived_strategy_snapshot_survives_current_strategy_change(self):
        old=copy.deepcopy(self.config)
        old['strategy_version']='ARCHIVED_TEST_VERSION'
        self.record['strategy_snapshot']=old
        self.seeded()
        before=closed_trades(self.store.snapshot())[0]
        self.store.remember({'BTCUSDT':self.record},NOW)
        after=closed_trades(self.store.snapshot())[0]
        self.assertEqual(before['strategy_version'],after['strategy_version'])
        self.assertEqual(after['strategy_name'],'ARCHIVED_TEST_VERSION')
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(payload['statistics']['verified_trades'],0)
        self.assertEqual(payload['historical_strategy_statistics']['verified_trades'],1)
        self.assertEqual(payload['by_strategy_version'][before['strategy_version']]['verified_trades'],1)
        self.assertNotEqual(payload['current_strategy_version'],before['strategy_version'])

    def test_first_archive_of_snapshotless_legacy_record_is_not_current_strategy(self):
        self.record.pop('strategy_snapshot')
        row=self.seeded()[0]
        self.assertEqual(row['strategy_name'],'원전략 미확인')
        self.assertEqual(self.store.snapshot()['positions'][0]['strategy'],LEGACY_UNKNOWN_STRATEGY)
        self.assertNotEqual(row['strategy_version'],strategy_digest(self.config))
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(payload['statistics']['verified_trades'],0)
        self.assertEqual(payload['historical_strategy_statistics']['verified_trades'],1)
        self.assertAlmostEqual(payload['historical_strategy_statistics']['net_usdt'],27.6)
        self.assertEqual(len(self.store.snapshot()['fills']),4)

    def test_existing_archive_hash_survives_snapshotless_runtime_recovery(self):
        old=copy.deepcopy(self.config)
        old['strategy_version']='ORIGINAL_ARCHIVED_VERSION'
        self.record['strategy_snapshot']=old
        original=self.seeded()[0]['strategy_version']
        self.record.pop('strategy_snapshot')
        self.store.remember({'BTCUSDT':self.record},NOW)
        self.assertEqual(self.store.snapshot()['positions'][0]['strategy'],old)
        self.assertEqual(closed_trades(self.store.snapshot())[0]['strategy_version'],original)
        self.assertEqual(analysis_input(self.store,self.config,7,NOW)['statistics']['verified_trades'],0)

    def test_new_intent_snapshot_is_counted_as_current_strategy(self):
        row=self.seeded()[0]
        self.assertEqual(row['strategy_version'],strategy_digest(self.config))
        self.assertEqual(analysis_input(self.store,self.config,7,NOW)['statistics']['verified_trades'],1)

    def test_current_strategy_schema_is_present_without_order_switch(self):
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(payload['current_strategy']['schema'],self.config['schema'])
        self.assertNotIn('orders_enabled',payload['current_strategy'])

    def test_excel_preserves_old_rows_and_separates_current_version_in_runtime(self):
        from trading.review import export_excel
        self.record.pop('strategy_snapshot')
        self.seeded()
        row=closed_trades(self.store.snapshot())[0]
        self.assertEqual(row['strategy_name'],'원전략 미확인')
        old={**row,'id':'old-position'}
        current={**row,'id':'current-position','strategy_version':strategy_digest(self.config),'leverage':2,
                 'gross':-10,'fees':.4,'funding':-2,'margin_return':-.248}
        scripts=self.root/'app'
        scripts.mkdir()
        shutil.copyfile(HERE/'app/export_history.mjs',scripts/'export_history.mjs')
        ns={'m':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
        def cells(path,sheet):
            with zipfile.ZipFile(path) as package:
                strings=[]
                if 'xl/sharedStrings.xml' in package.namelist():
                    strings=[''.join(n.itertext()) for n in ElementTree.fromstring(package.read('xl/sharedStrings.xml'))]
                tree=ElementTree.fromstring(package.read('xl/worksheets/sheet'+str(sheet)+'.xml'))
                result={}
                for cell in tree.findall('.//m:sheetData/m:row/m:c',ns):
                    value=cell.find('m:v',ns)
                    if cell.get('t')=='inlineStr': result[cell.get('r')]=''.join(cell.find('m:is',ns).itertext())
                    elif value is not None: result[cell.get('r')]=strings[int(value.text)] if cell.get('t')=='s' else value.text
                return result
        with patch('trading.review.closed_trades',return_value=[old,current]):
            output=export_excel(self.store)
        summary,ledger=cells(output,1),cells(output,2)
        self.assertEqual(float(summary['B5']),2)
        self.assertEqual(float(summary['B6']),.5)
        self.assertAlmostEqual(float(summary['B7']),15.2)
        self.assertEqual(float(summary['B29']),1)
        self.assertEqual(float(summary['B30']),0)
        self.assertAlmostEqual(float(summary['B31']),-12.4)
        self.assertAlmostEqual(float(summary['B32']),.4)
        self.assertAlmostEqual(float(summary['B33']),-2)
        self.assertEqual(ledger['Q6'],old['strategy_version'])
        self.assertEqual(ledger['Q7'],current['strategy_version'])
        self.assertEqual(ledger['A6'],'old-position')
        self.assertEqual(ledger['A7'],'current-position')
        with patch('trading.review.closed_trades',return_value=[old]):
            output=export_excel(self.store,preview=os.environ.get('TRADING_TEST_PREVIEW_DIR'))
        summary,ledger=cells(output,1),cells(output,2)
        self.assertEqual(float(summary['B5']),1)
        self.assertAlmostEqual(float(summary['B7']),27.6)
        self.assertEqual(float(summary['B29']),0)
        self.assertEqual(summary['B30'],'자료 없음')
        self.assertEqual(summary['B31'],'자료 없음')
        self.assertEqual(ledger['Q6'],old['strategy_version'])
        with zipfile.ZipFile(output) as package:
            workbook=ElementTree.fromstring(package.read('xl/workbook.xml'))
            self.assertEqual([s.get('name') for s in workbook.find('m:sheets',ns)],['요약','거래내역','체결원장','펀딩'])

    def test_short_actual_fills_have_correct_net_after_costs(self):
        self.record['direction']=-1
        self.rows=[fill(1,100,self.opened,'SELL','1'),fill(2,101,self.closed,'BUY','1','20')]
        row=self.seeded()[0]
        self.assertEqual(row['direction'],'SHORT')
        self.assertEqual(row['status'],'확인 완료')
        self.assertAlmostEqual(row['net'],17.8)

    def test_fill_id_deduplication_survives_overlap(self):
        self.seeded()
        self.store.ingest_fills(self.rows,'BTCUSDT')
        self.assertEqual(len(self.store.snapshot()['fills']),4)
        self.assertAlmostEqual(closed_trades(self.store.snapshot())[0]['net'],27.6)

    def test_server_exit_is_visible_before_live_management_restart(self):
        self.seeded()
        self.store.remember({'BTCUSDT':self.record},NOW)
        row=closed_trades(self.store.snapshot())[0]
        self.assertEqual(row['status'],'확인 완료')
        self.assertAlmostEqual(row['net'],27.6)
        self.assertTrue(self.store.snapshot()['positions'][0]['active'])

    def test_raw_manual_trades_do_not_enter_strategy_statistics(self):
        self.seeded()
        self.store.ingest_fills([fill(10,300,self.closed+DAY,'BUY',2),
                                 fill(11,301,self.closed+2*DAY,'SELL',2,100)],'BTCUSDT')
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(payload['statistics']['verified_trades'],1)
        self.assertAlmostEqual(payload['statistics']['net_usdt'],27.6)
        self.assertEqual(len(self.store.snapshot()['fills']),6)

    def test_mixed_manual_entry_is_flagged_not_assumed_to_be_bot_profit(self):
        self.seeded()
        self.store.ingest_fills([fill(20,500,self.opened+DAY,'BUY','.1')],'BTCUSDT')
        result=closed_trades(self.store.snapshot())[0]
        self.assertEqual(result['status'],'외부 진입 혼합')
        self.assertIsNone(result['net'])

    def test_missing_exit_or_foreign_fee_excludes_net_and_win_rate(self):
        self.rows.pop()
        result=self.seeded()[0]
        self.assertEqual(result['status'],'진입/청산 수량 불일치')
        self.assertIsNone(result['net'])
        self.assertIsNone(statistics([result])['win_rate'])
        self.rows.append(fill(4,102,self.closed,'SELL','.5','20',asset='BNB'))
        self.store.ingest_fills(self.rows,'BTCUSDT')
        result=closed_trades(self.store.snapshot())[0]
        self.assertEqual(result['status'],'비USDT 비용 환산 필요')
        self.assertIsNone(result['net'])

    def test_funding_coverage_gap_and_lag_are_explicit(self):
        self.seeded()
        self.store.set_meta(coverage_gaps=[[self.opened+1,self.opened+DAY]])
        self.assertIsNone(closed_trades(self.store.snapshot())[0]['net'])
        self.store.set_meta(coverage_gaps=[],sync_end=self.closed)
        self.assertIsNone(closed_trades(self.store.snapshot())[0]['net'])

    def test_period_is_by_close_time_but_includes_entire_trade_cost(self):
        self.seeded()
        seven=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(seven['statistics']['verified_trades'],1)
        self.assertAlmostEqual(seven['statistics']['net_usdt'],27.6)
        self.assertEqual(analysis_input(self.store,self.config,2,NOW)['statistics']['verified_trades'],0)
        self.assertNotIn('tr-owned',json.dumps(seven))
        self.assertNotIn('orders_enabled',seven['current_strategy'])
        self.assertNotIn('initial_equity',seven['current_strategy'])

    def test_account_cash_flow_does_not_turn_into_strategy_performance(self):
        self.store.ingest_fills(self.rows,'BTCUSDT')
        payload=analysis_input(self.store,self.config,7,NOW)
        self.assertEqual(payload['statistics']['verified_trades'],0)
        self.assertIsNone(payload['statistics']['win_rate'])
        self.assertEqual(payload['account_activity']['fill_count'],2)
        self.assertAlmostEqual(payload['account_activity']['usdt_cash_flow'],29.8)
        self.assertNotIn('orderId',json.dumps(payload['account_activity']))
        self.assertNotIn('tr-owned',json.dumps(payload))

    def test_account_isolation_and_path_validation(self):
        self.seeded()
        other=HistoryStore(self.root,'fedcba9876543210fedcba98')
        self.assertEqual(other.snapshot()['fills'],[])
        with self.assertRaises(ValueError):
            HistoryStore(self.root,'../private')

    def test_zero_denominator_and_realized_drawdown(self):
        self.assertIsNone(statistics([])['net_usdt'])
        rows=[dict(net=n,status='확인 완료') for n in (10,-3,0,-8,5)]
        s=statistics(rows)
        self.assertAlmostEqual(s['win_rate'],.4)
        self.assertAlmostEqual(s['realized_drawdown_usdt'],11)
        self.assertEqual(s['break_even'],1)

    def test_analyzer_does_not_call_codex_without_verified_trades(self):
        with patch('trading.review.codex_status',side_effect=AssertionError('must not call')):
            result=analyze(self.store,self.config,30)
        self.assertFalse(result['ai_called'])
        self.assertIsNone(result['path'])

    def test_active_positions_and_unfilled_intents_are_not_completed_trades(self):
        self.store.remember({'BTCUSDT':self.record},self.opened)
        self.store.ingest_fills(self.rows[:2],'BTCUSDT')
        self.assertEqual(closed_trades(self.store.snapshot()),[])
        self.store.remember({},self.closed)
        self.store.bind_entry('tr-owned',100)
        self.assertEqual(self.store.snapshot()['positions'][0]['record']['entry_order_id'],'100')

    def test_cli_job_cancellation_terminates_only_its_child(self):
        import sys,time
        from trading.review import run_process
        event=threading.Event()
        timer=threading.Timer(.3,event.set)
        start=time.monotonic()
        timer.start()
        try:
            with self.assertRaises(InterruptedError):
                run_process([sys.executable,'-c','import time; time.sleep(20)'],self.root,event,30)
        finally:
            timer.cancel()
        self.assertLess(time.monotonic()-start,10)

    def test_schema_rejects_wrong_types_or_execution_fields(self):
        value=dict(summary='ok',observations=[],symbol_feedback=[],recommendations=[],limitations=[])
        validate_response(value)
        with self.assertRaises(ValueError):
            validate_response({**value,'execute':'buy'})
        with self.assertRaises(ValueError):
            validate_response({**value,'summary':123})


class HistoryClient:
    def __init__(self, rows=None):
        self.calls=[]
        self.rows=rows or []
        self.fail=False
    def now(self): return NOW
    def sync(self): pass
    def safe_error(self,message): return str(message)
    def call(self,method,path,params):
        assert method=='GET'
        self.calls.append((path,params))
        if self.fail: raise RuntimeError('offline')
        if path.endswith('userTrades'):
            rows=[r for r in self.rows if r['symbol']==params['symbol'] and params['startTime']<=r['time']<=params['endTime']]
            return rows[-1000:]
        if path.endswith('income'): return []
        if path.endswith('/order'): return {'orderId':100}
        raise AssertionError(path)


class SyncTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store=HistoryStore(self.tmp.name,ACCOUNT)

    def test_history_sync_is_get_only_and_seven_day_windows(self):
        c=HistoryClient([fill(1,100,NOW-DAY,'BUY',1)])
        sync_history(c,self.store)
        self.assertEqual(len(self.store.snapshot()['fills']),1)
        self.assertEqual(self.store.meta()['sync_end'],NOW-5000)
        self.assertTrue(all(p['endTime']-p['startTime']<7*DAY for _,p in c.calls))
        count=len(c.calls); sync_history(c,self.store)
        self.assertLess(len(c.calls)-count,count)
        self.assertEqual(len(self.store.snapshot()['fills']),1)

    def test_full_trade_page_is_bisected_without_losing_same_time_rows(self):
        c=HistoryClient([fill(i,100,NOW-DAY+i,'BUY','.001') for i in range(1200)])
        self.store.set_meta(sync_start=NOW-10*DAY,sync_end=NOW-DAY)
        sync_history(c,self.store)
        self.assertEqual(len(self.store.snapshot()['fills']),1200)

    def test_failure_preserves_cursor_and_records_error(self):
        self.store.set_meta(sync_start=NOW-10*DAY,sync_end=NOW-DAY)
        c=HistoryClient(); c.fail=True
        with self.assertRaises(RuntimeError):
            sync_history(c,self.store)
        self.assertEqual(self.store.meta()['sync_end'],NOW-DAY)
        self.assertEqual(self.store.meta()['sync_error'],'offline')

    def test_impossible_millisecond_page_does_not_claim_complete(self):
        c=HistoryClient([fill(i,100,NOW-DAY,'BUY','.001') for i in range(1001)])
        self.store.set_meta(sync_start=NOW-10*DAY,sync_end=NOW-DAY)
        with self.assertRaisesRegex(ValueError,'1,000'):
            sync_history(c,self.store)
        self.assertEqual(self.store.meta()['sync_end'],NOW-DAY)

    def test_retention_gap_is_reported(self):
        self.store.set_meta(sync_start=NOW-200*DAY,sync_end=NOW-150*DAY)
        sync_history(HistoryClient(),self.store)
        self.assertEqual(len(self.store.meta()['coverage_gaps']),1)
        payload=analysis_input(self.store,load(),180,NOW)
        self.assertFalse(payload['coverage']['requested_period_covered'])

    def test_clock_sync_failure_is_saved_as_incomplete(self):
        c=HistoryClient()
        with patch.object(c,'sync',side_effect=RuntimeError('clock offline')):
            with self.assertRaises(RuntimeError): sync_history(c,self.store)
        self.assertEqual(self.store.meta()['sync_error'],'clock offline')

    def test_cancel_stops_before_private_read(self):
        event=threading.Event(); event.set()
        c=HistoryClient()
        with self.assertRaises(InterruptedError): sync_history(c,self.store,event)
        self.assertEqual(c.calls,[])


if __name__=='__main__':
    unittest.main()

