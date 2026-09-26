import copy
import unittest
from trading.config import load,validate,parameters_for
from trading.backtest import simulate
from trading.audit import verify


def fixture(count=9):
    contexts={}
    for symbol in ('BTCUSDT','XRPUSDT'):
        bars={t:{'t':t,'o':100.,'h':100.,'l':100.,'c':100.} for t in range(0,count*3600,3600)}
        contexts[symbol]={'by_time':bars,'marks':copy.deepcopy(bars),'funding_by_time':{},
            'filters':{'quantity_step':.001,'min_quantity':.001,'max_quantity':100000.,'min_notional':5.,'liquidation_fee':.0125}}
    cfg=load(); cfg.update(taker_fee=0.,slippage=0.)
    return contexts,cfg


def signal(t=14400,hold=20,distance=5,rr=8,direction=1):
    return {t:{'signal_open_t':t-14400,'direction':direction,'stop_distance':distance,'take_profit_r':rr,'max_hold_bars':hold}}


class PortfolioTests(unittest.TestCase):
    def run_case(self,c,s,cfg,end=32400):
        result=simulate(c,s,cfg,0,end)
        self.assertTrue(verify(result,c)['passed'])
        return result

    def test_sequential_available_margin_and_leverage(self):
        c,cfg=fixture(); r=self.run_case(c,{s:signal() for s in c},cfg)
        p={p['symbol']:p for p in r['trades']}
        self.assertEqual(p['BTCUSDT']['isolated_margin'],100)
        self.assertEqual(p['XRPUSDT']['available_margin_before'],900)
        self.assertEqual(p['XRPUSDT']['isolated_margin'],90)
        self.assertEqual(p['XRPUSDT']['entry_notional'],450)
        self.assertEqual(r['equity_curve'][4]['available_margin'],810)

    def test_entry_fee_reduces_next_available_margin(self):
        c,cfg=fixture(); cfg['taker_fee']=.0005
        r=self.run_case(c,{s:signal() for s in c},cfg)
        x=next(p for p in r['trades'] if p['symbol']=='XRPUSDT')
        self.assertAlmostEqual(x['available_margin_before'],899.5)
        self.assertAlmostEqual(x['target_margin'],89.95)

    def test_unrealized_profit_not_spendable(self):
        c,cfg=fixture()
        for t in range(18000,32400,3600): c['BTCUSDT']['marks'][t].update(o=120.,h=120.,l=120.,c=120.)
        r=self.run_case(c,{'BTCUSDT':signal(),'XRPUSDT':signal(21600)},cfg)
        x=next(p for p in r['trades'] if p['symbol']=='XRPUSDT')
        self.assertEqual(x['available_margin_before'],900)
        self.assertEqual(x['isolated_margin'],90)

    def test_funding_uses_isolated_wallet_and_new_entry_exempt(self):
        c,cfg=fixture(); c['BTCUSDT']['funding_by_time']={14400:{'rate':.01,'price':100.},21600:{'rate':.001,'price':100.}}
        r=self.run_case(c,{'BTCUSDT':signal(),'XRPUSDT':signal(21600)},cfg)
        self.assertEqual(r['stats']['funding_paid'],1)
        self.assertEqual(next(p for p in r['trades'] if p['symbol']=='XRPUSDT')['available_margin_before'],900)

    def test_pending_order_reserve(self):
        c,cfg=fixture(); cfg['reserved_order_margin']=100
        r=self.run_case(c,{'BTCUSDT':signal()},cfg)
        self.assertEqual(r['trades'][0]['isolated_margin'],90)
        self.assertEqual(r['stats']['ending_equity'],1000)

    def test_intrabar_proceeds_unavailable_at_open(self):
        c,cfg=fixture(); c['BTCUSDT']['by_time'][18000]['h']=110
        r=self.run_case(c,{'BTCUSDT':signal(distance=5,rr=2),'XRPUSDT':signal(18000)},cfg)
        x=next(p for p in r['trades'] if p['symbol']=='XRPUSDT')
        self.assertEqual(x['available_margin_before'],900)

    def test_known_open_exit_releases_cash(self):
        c,cfg=fixture(); c['BTCUSDT']['by_time'][18000].update(o=110,h=110,c=110)
        r=self.run_case(c,{'BTCUSDT':signal(distance=5,rr=2),'XRPUSDT':signal(18000)},cfg)
        x=next(p for p in r['trades'] if p['symbol']=='XRPUSDT')
        self.assertEqual(x['available_margin_before'],1100)

    def test_no_same_bar_reentry_or_adding(self):
        c,cfg=fixture(); cfg['daily_loss_limit']=1; c['BTCUSDT']['by_time'][18000]['l']=95
        r=self.run_case(c,{'BTCUSDT':{**signal(),**signal(18000),**signal(21600)}},cfg)
        self.assertEqual([p['entry_time'] for p in r['trades']],[14400,21600])

    def test_daily_loss_gate_blocks_later_entries(self):
        c,cfg=fixture(); c['BTCUSDT']['by_time'][18000]['l']=95
        r=self.run_case(c,{'BTCUSDT':{**signal(),**signal(21600)}},cfg)
        self.assertEqual(r['stats']['trades'],1)
        self.assertEqual(r['stats']['skipped_entries']['daily_loss_limit'],1)

    def test_ambiguous_bar_stop_before_target(self):
        c,cfg=fixture(); c['BTCUSDT']['by_time'][14400].update(l=95,h=110)
        r=self.run_case(c,{'BTCUSDT':signal(rr=2)},cfg)
        self.assertEqual(r['trades'][0]['exit_reason'],'stop')
        self.assertEqual(r['stats']['stop_target_ambiguous_bars'],1)

    def test_liquidation_cannot_use_other_wallet(self):
        c,cfg=fixture(); c['BTCUSDT']['marks'][18000].update(o=50,h=50,l=50,c=50)
        r=self.run_case(c,{s:signal() for s in c},cfg)
        self.assertEqual(r['stats']['ending_equity'],900)
        self.assertEqual(r['stats']['liquidations'],1)
        x=next(p for p in r['trades'] if p['symbol']=='XRPUSDT')
        self.assertEqual(x['net_pnl'],0)

    def test_quantity_filters(self):
        c,cfg=fixture(); cfg['initial_equity']=10
        c['BTCUSDT']['filters']['min_notional']=50
        c['XRPUSDT']['filters']['quantity_step']=.1
        r=self.run_case(c,{s:signal() for s in c},cfg)
        self.assertEqual(r['stats']['trades'],0)
        self.assertEqual(r['stats']['skipped_entries']['minimum_quantity_or_notional'],2)

    def test_global_policy_locked(self):
        for key,value in [('sizing_basis','total_equity'),('margin_fraction',.2),('orders_enabled',True),
                          ('leverage_by_symbol',{'BTCUSDT':10,'XRPUSDT':10})]:
            cfg=load(); cfg[key]=value
            with self.assertRaises(ValueError): validate(cfg)

    def test_xrp_override_leaves_btc_unchanged(self):
        cfg=load(); original=copy.deepcopy(parameters_for(cfg,'BTCUSDT'))
        variant=copy.deepcopy(original); variant['entry_parameters']['period']=21
        cfg['symbol_overrides']={'XRPUSDT':variant}
        validate(cfg)
        self.assertEqual(parameters_for(cfg,'BTCUSDT'),original)
        self.assertEqual(parameters_for(cfg,'XRPUSDT')['entry_parameters']['period'],21)

    def test_symbol_override_cannot_change_global_allocation(self):
        for extra in ('margin_fraction','leverage','orders_enabled','sizing_basis'):
            cfg=load(); override=copy.deepcopy(parameters_for(cfg,'BTCUSDT')); override[extra]=1
            cfg['symbol_overrides']={'XRPUSDT':override}
            with self.assertRaises(ValueError): validate(cfg)

    def test_invalid_symbol_or_exit_override_rejected(self):
        cfg=load(); override=copy.deepcopy(parameters_for(cfg,'BTCUSDT'))
        cfg['symbol_overrides']={'ETHUSDT':override}
        with self.assertRaises(ValueError): validate(cfg)
        override['exit_parameters']['management']='trail2_2'; cfg['symbol_overrides']={'XRPUSDT':override}
        with self.assertRaises(ValueError): validate(cfg)

    def test_unsupported_exit_controls_rejected(self):
        c,cfg=fixture(); s=signal(); s[14400]['trail_activation_r']=2
        with self.assertRaises(ValueError): simulate(c,{'BTCUSDT':s},cfg,0,32400)

    def test_future_candle_changes_do_not_change_past_allocations(self):
        c,cfg=fixture(); signals={s:signal() for s in c}
        first=self.run_case(c,signals,cfg)
        c['XRPUSDT']['marks'][28800].update(o=130,h=130,l=130,c=130)
        second=self.run_case(c,signals,cfg)
        self.assertEqual(first['equity_curve'][:-1],second['equity_curve'][:-1])
        self.assertEqual([e for e in first['events'] if e['type']=='entry'],[e for e in second['events'] if e['type']=='entry'])

    def test_source_signal_before_start_excluded(self):
        c,cfg=fixture(); r=simulate(c,{'BTCUSDT':signal()},cfg,3600,32400)
        self.assertEqual(r['stats']['trades'],0)

    def test_win_rate_counts_net_profit_after_fees(self):
        c,cfg=fixture(); cfg['taker_fee']=.0005
        c['BTCUSDT']['by_time'][28800].update(h=100.01,c=100.01)
        r=self.run_case(c,{s:signal() for s in c},cfg)
        self.assertGreater(next(t for t in r['trades'] if t['symbol']=='BTCUSDT')['gross_pnl'],0)
        self.assertEqual(r['stats']['wins'],0)
        self.assertEqual(r['stats']['win_rate'],0)
        self.assertEqual(r['stats']['by_symbol']['BTCUSDT']['win_rate'],0)

    def test_symbol_win_rate_in_shared_account(self):
        c,cfg=fixture(); cfg['taker_fee']=.0005
        c['BTCUSDT']['by_time'][18000]['h']=110
        r=self.run_case(c,{'BTCUSDT':signal(rr=2),'XRPUSDT':signal()},cfg)
        self.assertEqual(r['stats']['win_rate'],.5)
        self.assertEqual(r['stats']['by_symbol']['BTCUSDT']['win_rate'],1)
        self.assertEqual(r['stats']['by_symbol']['XRPUSDT']['win_rate'],0)


if __name__=='__main__': unittest.main()
