"""Forward-only EMA, ATR, ADX and Supertrend used by the active strategies."""
from math import isfinite
from numbers import Real

def _smooth(values, period, alpha):
    result = [None] * len(values)
    seed, average = ([], None)
    for i, value in enumerate(values):
        if value is None:
            seed, average = ([], None)
            continue
        if average is None:
            seed.append(value)
            if len(seed) == period:
                average = sum(seed) / period
        else:
            average += alpha * (value - average)
        result[i] = average
    return result

class IndicatorCache:

    def __init__(self, bars):
        self.bars = list(bars)
        previous = None
        for i, bar in enumerate(self.bars):
            if not isinstance(bar, dict) or not {'t', 'o', 'h', 'l', 'c', 'v'} <= bar.keys():
                raise ValueError(f'bar {i} needs t/o/h/l/c/v')
            for key in ('t', 'o', 'h', 'l', 'c', 'v'):
                value = bar[key]
                if isinstance(value, bool) or not isinstance(value, Real) or (not isfinite(value)):
                    raise ValueError(f'bar {i} {key} must be finite numeric')
            if previous is not None and bar['t'] <= previous:
                raise ValueError('bar times must increase strictly')
            previous = bar['t']
            if bar['l'] <= 0 or bar['v'] < 0 or (not bar['l'] <= min(bar['o'], bar['c']) <= max(bar['o'], bar['c']) <= bar['h']):
                raise ValueError(f'bar {i} has invalid OHLCV bounds')
        self.close = [b['c'] for b in self.bars]
        self.values = {}

    def _get(self, key, factory):
        if key not in self.values:
            self.values[key] = factory()
        return self.values[key]

    def ema(self, period):
        return self._get(('ema', period), lambda: _smooth(self.close, period, 2 / (period + 1)))

    def atr(self, period):

        def calc():
            tr = [max(b['h'] - b['l'], abs(b['h'] - self.close[i - 1]), abs(b['l'] - self.close[i - 1])) if i else b['h'] - b['l'] for i, b in enumerate(self.bars)]
            return _smooth(tr, period, 1 / period)
        return self._get(('atr', period), calc)

    def adx(self, period=14):

        def calc():
            tr, plus, minus = ([None], [None], [None]) if self.bars else ([], [], [])
            for i in range(1, len(self.bars)):
                b, old = (self.bars[i], self.bars[i - 1])
                up, down = (b['h'] - old['h'], old['l'] - b['l'])
                plus.append(up if up > down and up > 0 else 0.0)
                minus.append(down if down > up and down > 0 else 0.0)
                tr.append(max(b['h'] - b['l'], abs(b['h'] - old['c']), abs(b['l'] - old['c'])))
            atr, positive, negative = [_smooth(v, period, 1 / period) for v in (tr, plus, minus)]
            dx = [None if a is None else 0.0 if p + m == 0 or a == 0 else 100 * abs(p - m) / (p + m) for a, p, m in zip(atr, positive, negative)]
            return _smooth(dx, period, 1 / period)
        return self._get(('adx', period), calc)

    def supertrend(self, period, multiple):

        def compute():
            atr = self.atr(period)
            result = [None] * len(self.bars)
            upper = lower = None
            direction = -1
            for i, b in enumerate(self.bars):
                if atr[i] is None:
                    continue
                mid = (b['h'] + b['l']) / 2
                basic_upper, basic_lower = (mid + multiple * atr[i], mid - multiple * atr[i])
                if upper is None:
                    upper, lower = (basic_upper, basic_lower)
                    direction = -1
                else:
                    upper = basic_upper if basic_upper < upper or self.close[i - 1] > upper else upper
                    lower = basic_lower if basic_lower > lower or self.close[i - 1] < lower else lower
                    direction = (1 if b['c'] > upper else -1) if direction == -1 else -1 if b['c'] < lower else 1
                result[i] = {'direction': direction, 'upper': upper, 'lower': lower, 'line': lower if direction == 1 else upper}
            return result
        return self._get(('supertrend', period, multiple), compute)
