import copy
import unittest
from trading.config import HERE,load,validate,parameters_for
from tests.test_portfolio import fixture,signal
from trading.backtest import simulate
from trading.audit import verify


def triple():
    c,cfg=fixture()
    c['SOLUSDT']=copy.deepcopy(c['XRPUSDT'])
    c['SOLUSDT']['filters'].update(quantity_step=.01,min_quantity=.01,liquidation_fee=.015)
    cfg['leverage_by_symbol']={**cfg['leverage_by_symbol'],'SOLUSDT':5}
    cfg['entry_priority']=['BTCUSDT','XRPUSDT','SOLUSDT']
    return c,cfg


class SOLTests(unittest.TestCase):
    def test_three_entries_reserve_sequentially(self):
        c,cfg=triple(); r=simulate(c,{s:signal() for s in c},cfg,0,32400)
        self.assertTrue(verify(r,c)['passed'])
        p={t['symbol']:t for t in r['trades']}
        self.assertEqual([p[s]['isolated_margin'] for s in cfg['entry_priority']],[100,90,81])
        self.assertEqual(p['SOLUSDT']['entry_notional'],405)
        self.assertEqual(p['SOLUSDT']['available_margin_before'],810)
        self.assertEqual(r['equity_curve'][4]['available_margin'],729)
        self.assertEqual(r['stats']['maximum_concurrent_positions'],3)

    def test_sol_allocation_excludes_both_other_unrealized_profits(self):
        c,cfg=triple()
        for symbol in ('BTCUSDT','XRPUSDT'):
            for t in range(18000,32400,3600): c[symbol]['marks'][t].update(o=120,h=120,l=120,c=120)
        r=simulate(c,{'BTCUSDT':signal(),'XRPUSDT':signal(),'SOLUSDT':signal(21600)},cfg,0,32400)
        self.assertEqual(next(t for t in r['trades'] if t['symbol']=='SOLUSDT')['available_margin_before'],810)
        self.assertTrue(verify(r,c)['passed'])

    def test_third_entry_after_both_entry_fees(self):
        c,cfg=triple(); cfg['taker_fee']=.0005
        r=simulate(c,{s:signal() for s in c},cfg,0,32400)
        p={t['symbol']:t for t in r['trades']}
        expected=1000-sum(p[s]['isolated_margin']+p[s]['entry_fee'] for s in ('BTCUSDT','XRPUSDT'))
        self.assertAlmostEqual(p['SOLUSDT']['available_margin_before'],expected)
        self.assertAlmostEqual(p['SOLUSDT']['target_margin'],expected*.1)
        self.assertTrue(verify(r,c)['passed'])

    def test_sol_liquidation_does_not_debit_btc_or_xrp(self):
        c,cfg=triple(); c['SOLUSDT']['marks'][18000]['l']=50
        r=simulate(c,{s:signal() for s in c},cfg,0,32400)
        self.assertEqual(r['stats']['ending_equity'],919)
        self.assertEqual(r['stats']['liquidations'],1)
        self.assertEqual(r['stats']['by_symbol']['BTCUSDT']['net_pnl'],0)
        self.assertEqual(r['stats']['by_symbol']['XRPUSDT']['net_pnl'],0)
        self.assertTrue(verify(r,c)['passed'])

    def test_sol_cannot_change_locked_leverage(self):
        _,cfg=triple(); cfg['leverage_by_symbol']['SOLUSDT']=10
        with self.assertRaises(ValueError): validate(cfg)

    def test_priority_must_reserve_sol_exactly_once(self):
        for priority in (['BTCUSDT','XRPUSDT'],['BTCUSDT','XRPUSDT','SOLUSDT','SOLUSDT']):
            _,cfg=triple(); cfg['entry_priority']=priority
            with self.assertRaises(ValueError): validate(cfg)

    def test_sol_rule_does_not_change_btc_or_xrp(self):
        _,cfg=triple(); old={s:copy.deepcopy(parameters_for(cfg,s)) for s in ('BTCUSDT','XRPUSDT')}
        rule=copy.deepcopy(old['XRPUSDT']); rule['entry_parameters']['period']=25
        cfg.setdefault('symbol_overrides',{})['SOLUSDT']=rule
        validate(cfg)
        self.assertEqual({s:parameters_for(cfg,s) for s in old},old)
        self.assertEqual(parameters_for(cfg,'SOLUSDT')['entry_parameters']['period'],25)

    def test_two_asset_historical_config_remains_replayable(self):
        cfg=load(); cfg['leverage_by_symbol'].pop('SOLUSDT'); cfg['entry_priority'].remove('SOLUSDT'); cfg['symbol_overrides'].pop('SOLUSDT'); validate(cfg)
        self.assertEqual(set(cfg['leverage_by_symbol']),{'BTCUSDT','XRPUSDT'})
        with self.assertRaises(ValueError): parameters_for(cfg,'SOLUSDT')


if __name__=='__main__': unittest.main()
