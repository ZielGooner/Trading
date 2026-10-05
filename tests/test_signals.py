import copy
import math
import unittest
from unittest.mock import patch

from trading.config import load
from trading.indicators import aggregate, atr, bollinger, ema, rma, true_range, validate_bars
from trading.signals import build


def fixture(count=480, offset=0):
    bars, previous = [], 100.
    for i in range(count):
        close = 100 + .04 * i + 5 * math.sin(.83 * i) + .5 * math.sin(.12 * i)
        if i % 37 == 0:
            close -= 9  # A real band excursion and recovery for the reentry family.
        bars.append(dict(t=(offset + i) * 14400, o=previous, h=max(previous, close) + 1,
                         l=min(previous, close) - 1, c=close, v=1000 + i))
        previous = close
    return bars


class IndicatorTests(unittest.TestCase):
    def test_ema_first_valid_seed_and_missing_value(self):
        self.assertEqual(ema([None, 10, 20, None, 30], 3), [None, 10, 15, 15, 22.5])

    def test_rma_sma_seed_not_ema_seed(self):
        self.assertEqual(rma([None, 3, 6, None, 9, 12], 3), [None, None, None, None, 6, 8])

    def test_population_bollinger_deviation(self):
        lower, upper = bollinger([1, 2, 3], 3, 2)
        self.assertEqual(lower[:2], [None, None])
        self.assertAlmostEqual(lower[2], 2 - 2 * math.sqrt(2 / 3))
        self.assertAlmostEqual(upper[2], 2 + 2 * math.sqrt(2 / 3))

    def test_true_range_gap_and_atr_seed(self):
        bars = [dict(t=0, o=10, h=12, l=9, c=11, v=1),
                dict(t=14400, o=15, h=16, l=14, c=15, v=1),
                dict(t=28800, o=15, h=17, l=13, c=14, v=1)]
        self.assertEqual(true_range(bars), [3, 5, 4])
        self.assertEqual(atr(bars, 2), [None, 4, 4])

    def test_utc_aggregation_drops_incomplete_first_and_last_groups(self):
        bars = fixture(6, offset=1)
        grouped = aggregate(bars, 28800)
        self.assertEqual([b['t'] for b in grouped], [28800, 57600])
        self.assertEqual(grouped[0]['o'], bars[1]['o'])
        self.assertEqual(grouped[0]['c'], bars[2]['c'])
        self.assertEqual(grouped[0]['v'], bars[1]['v'] + bars[2]['v'])
        self.assertEqual(grouped[0]['h'], max(bars[1]['h'], bars[2]['h']))

    def test_12h_and_daily_groups_are_epoch_aligned_complete(self):
        bars = fixture(13)
        self.assertEqual([b['t'] for b in aggregate(bars, 43200)], [0, 43200, 86400, 129600])
        self.assertEqual([b['t'] for b in aggregate(bars, 86400)], [0, 86400])

    def test_invalid_gap_duplicate_boundary_and_candle_rejected(self):
        for mutation in ('gap', 'duplicate', 'boundary', 'bounds', 'nan'):
            bars = fixture(5)
            if mutation == 'gap': bars.pop(2)
            elif mutation == 'duplicate': bars[2]['t'] = bars[1]['t']
            elif mutation == 'boundary': bars[2]['t'] += 1
            elif mutation == 'bounds': bars[2]['h'] = bars[2]['l'] - 1
            else: bars[2]['v'] = float('nan')
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_bars(bars)


class ClosedBarSignalTests(unittest.TestCase):
    def setUp(self):
        self.config = load()

    def test_signal_action_is_next_open_with_frozen_independent_atr_distances(self):
        bars = fixture()
        for symbol, p in self.config['strategy_by_symbol'].items():
            events = build(bars, p)
            self.assertTrue(events, symbol)
            av = atr(bars, p['atr_length'])
            lookup = {b['t']: (i, b) for i, b in enumerate(bars)}
            for action, event in events.items():
                i, signal = lookup[event['signal_open_t']]
                self.assertEqual(action, signal['t'] + 14400)
                self.assertLessEqual(event['higher_close_t'], signal['t'])
                self.assertAlmostEqual(event['stop_distance'], av[i] * p['stop_atr'])
                self.assertAlmostEqual(event['take_profit_distance'], av[i] * p['take_profit_atr'])
                self.assertEqual(event['turnover'], signal['v'] * signal['c'])
                self.assertEqual(event['max_hold_bars'], p['max_hold_bars'])
                self.assertEqual(event['flip_exit'], p['flip_exit'])

    def test_confirmed_higher_timeframe_is_asof_signal_open_not_close(self):
        bars = fixture(110)
        p = self.config['strategy_by_symbol']['BTCUSDT']
        # First fifty complete 8h candles become ready at execution index100.
        # ADX of HTF index50 is deliberately disqualifying; 100/101 must still use49.
        def adx_values(h, period):
            return [10 if i < 50 else 100 for i in range(len(h))]
        bands = ([0.] * len(bars), [1.] * len(bars))
        with patch('trading.signals.adx', side_effect=adx_values), patch('trading.signals.bollinger', return_value=bands):
            events = build(bars, p)
        self.assertNotIn(100 * 14400, events)
        self.assertIn(101 * 14400, events)
        self.assertIn(102 * 14400, events)
        self.assertNotIn(103 * 14400, events)
        self.assertEqual(events[101 * 14400]['higher_close_t'], 100 * 14400)

    def test_adx_upper_bound_is_strict(self):
        bars = fixture(110)
        p = self.config['strategy_by_symbol']['BTCUSDT']
        bands = ([0.] * len(bars), [1.] * len(bars))
        with patch('trading.signals.adx', side_effect=lambda h, period: [25.] * len(h)), \
             patch('trading.signals.bollinger', return_value=bands):
            self.assertEqual(build(bars, p), {})

    def test_donchian_uses_previous_bars_excluding_signal_high(self):
        bars = [dict(t=i * 14400, o=100., h=101., l=99., c=100., v=1.) for i in range(360)]
        bars[-1].update(c=110., h=111.)
        p = self.config['strategy_by_symbol']['XRPUSDT']
        with patch('trading.signals.ema', side_effect=lambda values, period: [50.] * len(values)):
            events = build(bars, p)
        self.assertEqual(events[bars[-1]['t'] + 14400]['direction'], 1)

    def test_prefix_and_future_price_volume_changes_cannot_rewrite_signals(self):
        bars = fixture()
        cutoff = 380
        future = copy.deepcopy(bars)
        for b in future[cutoff:]:
            for key in ('o', 'h', 'l', 'c'): b[key] *= 7
            b['v'] *= 100
        last_action = bars[cutoff - 1]['t'] + 14400
        for symbol, p in self.config['strategy_by_symbol'].items():
            prefix = build(bars[:cutoff], p)
            complete = {t: e for t, e in build(bars, p).items() if t <= last_action}
            perturbed = {t: e for t, e in build(future, p).items() if t <= last_action}
            self.assertEqual(prefix, complete, symbol)
            self.assertEqual(prefix, perturbed, symbol)

    def test_calendar_shift_does_not_disable_live_strategy(self):
        bars = fixture()
        shifted = copy.deepcopy(bars)
        shift = 86400 * 365 * 50  # Multiple of every configured HTF; far beyond research window.
        for b in shifted: b['t'] += shift
        for p in self.config['strategy_by_symbol'].values():
            before, after = build(bars, p), build(shifted, p)
            self.assertEqual([e['direction'] for e in before.values()], [e['direction'] for e in after.values()])
            self.assertEqual([t + shift for t in before], list(after))


if __name__ == '__main__':
    unittest.main()
