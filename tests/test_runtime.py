import copy
import json
import unittest
from trading.config import HERE,load,parameters_for,validate
from trading.data import load_contexts,klines
from trading.signals import signals_for,build
from trading.run import fingerprint,verify_inputs,verify_result


class RuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contexts,cls.start,cls.end=load_contexts(True)
        cls.config=load()
        cls.reference=json.loads((HERE/'tests/reference.json').read_text(encoding='utf-8'))

    def test_inputs_and_signals_match_pre_cleanup(self):
        sig={s:signals_for(c,self.config,s) for s,c in self.contexts.items()}
        verify_inputs(self.contexts,sig,self.config,self.start,self.end,self.reference)

    def test_indicators_do_not_use_future_bars(self):
        for symbol,c in self.contexts.items():
            p=parameters_for(self.config,symbol); short=c['source'][:900]
            full=build(c['source'],p['entry_parameters'],p['exit_parameters'])
            prefix=build(short,p['entry_parameters'],p['exit_parameters'])
            self.assertEqual(prefix,{t:s for t,s in full.items() if t<=short[-1]['t']+14400},symbol)

    def test_reference_rejects_changed_strategy(self):
        changed=copy.deepcopy(self.config); changed['exit_parameters']['stop_atr']=3.4
        with self.assertRaises(ValueError): verify_inputs(self.contexts,{},changed,self.start,self.end,self.reference)

    def test_rejected_cooldown_is_not_available(self):
        with self.assertRaises(ValueError): validate({**self.config,'risk_controls':{'loss_cooldown_hours':72}})

    def test_ledger_fingerprint_detects_changes(self):
        expected={'stats':{},**{k:fingerprint([]) for k in ('trades','events','equity_curve','daily')}}
        changed={'stats':{},'trades':[],'events':[{'t':1}],'equity_curve':[],'daily':[]}
        with self.assertRaises(AssertionError): verify_result('case',changed,{'cases':{'case':expected}})

    def test_execution_data_rejects_gaps(self):
        rows=[[0,'100','101','99','100','1',3599999],[7200000,'100','101','99','100','1',10799999]]
        with self.assertRaises(ValueError): klines(rows)


if __name__=='__main__': unittest.main()
