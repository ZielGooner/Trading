import copy
import unittest

from trading.config import SYMBOLS, VERSION, load, parameters_for, validate
from trading.signals import validate_parameters


class BaseConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.config = load()

    def test_explicit_start_and_shared_margin_settings(self):
        self.assertIs(self.config['orders_enabled'], False)
        self.assertEqual(self.config['strategy_version'], VERSION)
        self.assertEqual(self.config['margin_fraction'], .5)
        self.assertEqual(self.config['entry_priority'], 'previous_closed_4h_turnover_desc')
        self.assertEqual(self.config['leverage_by_symbol'], dict.fromkeys(SYMBOLS, 2))
        self.assertEqual(self.config['taker_fee'], .0005)
        self.assertEqual(self.config['slippage_ticks'], 2)
        self.assertNotIn('daily_loss_limit', self.config)

    def test_four_approved_profile_rules(self):
        expected = {
            'BTCUSDT': ('BTC_R4M_Final', 'bb_breakout', 14, 50, 1.5, 28800, 'adx_range', 20, 1.25, 1, 3, True),
            'XRPUSDT': ('XRP_T100_Channel', 'donchian', 2, 50, 2, 86400, 'price', 10, 1.25, 1.5, 3, True),
            'SOLUSDT': ('SOL_T100_EMA', 'ema_cross', 2, 8, 2, 28800, 'ma_stack', 20, 3, 1, 6, False),
            'HYPEUSDT': ('HYPE_T100_BB', 'bb_reentry', 8, 50, 1.5, 43200, 'ma_stack', 20, 1.5, .5, 0, False),
        }
        fields = ('profile', 'family', 'length', 'slow_length', 'bb_deviation', 'higher_seconds',
                  'filter', 'higher_ema_length', 'stop_atr', 'take_profit_atr', 'max_hold_bars', 'flip_exit')
        for symbol, values in expected.items():
            with self.subTest(symbol=symbol):
                p = parameters_for(self.config, symbol)
                self.assertEqual(tuple(p[k] for k in fields), values)
                self.assertEqual((p['atr_length'], p['adx_length'], p['adx_max'], p['higher_fast'], p['higher_slow']),
                                 (14, 14, 25, 5, 20))

    def test_missing_extra_or_old_configuration_rejected(self):
        for field in ('orders_enabled', 'strategy_by_symbol', 'entry_priority'):
            changed = copy.deepcopy(self.config)
            del changed[field]
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate(changed)
        for field in ('backtest', 'entry_parameters', 'initial_equity', 'start_date', 'daily_loss_limit'):
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate({**self.config, field: {}})

    def test_unapproved_activation_sizing_and_leverage_rejected(self):
        for key, value in (('orders_enabled', True), ('timeframe', '1h'), ('margin_fraction', .1),
                           ('entry_priority', ['BTCUSDT']), ('slippage_ticks', .0002)):
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate({**self.config, key: value})
        for value in (True, 5, 10):
            changed = copy.deepcopy(self.config)
            changed['leverage_by_symbol']['BTCUSDT'] = value
            with self.subTest(leverage=value), self.assertRaises(ValueError):
                validate(changed)

    def test_exact_symbol_set_required(self):
        for field in ('strategy_by_symbol', 'leverage_by_symbol'):
            changed = copy.deepcopy(self.config)
            del changed[field]['HYPEUSDT']
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate(changed)
        with self.assertRaises(ValueError):
            parameters_for(self.config, 'ETHUSDT')

    def test_no_old_families_or_partial_runner_fields(self):
        for family in ('supertrend', 'ema_reclaim', 'rsi', 'conditional_runner'):
            p = {**self.config['strategy_by_symbol']['BTCUSDT'], 'family': family}
            with self.subTest(family=family), self.assertRaises(ValueError):
                validate_parameters(p)
        p = {**self.config['strategy_by_symbol']['SOLUSDT'], 'partial_fraction': .5}
        with self.assertRaises(ValueError):
            validate_parameters(p)

    def test_hype_zero_limit_allowed_but_negative_rejected(self):
        p = copy.deepcopy(self.config['strategy_by_symbol']['HYPEUSDT'])
        self.assertEqual(validate_parameters(p)['max_hold_bars'], 0)
        for value in (-1, True, 1.5):
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_parameters({**p, 'max_hold_bars': value})

    def test_nonfinite_and_boolean_parameters_rejected(self):
        p = self.config['strategy_by_symbol']['BTCUSDT']
        for field, value in (('length', True), ('stop_atr', float('nan')), ('take_profit_atr', float('inf')),
                             ('adx_max', False), ('higher_seconds', 14400), ('flip_exit', 1)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate_parameters({**p, field: value})


if __name__ == '__main__':
    unittest.main()
