"""Causal OHLCV indicators required by the four approved BASE profiles."""
from math import isfinite, sqrt
from numbers import Real


def validate_bars(bars, interval=14400):
    previous = None
    for bar in bars:
        if not isinstance(bar, dict) or not {'t', 'o', 'h', 'l', 'c', 'v'} <= bar.keys():
            raise ValueError('bar needs t/o/h/l/c/v')
        if any(isinstance(bar[k], bool) or not isinstance(bar[k], Real) or not isfinite(bar[k])
               for k in ('t', 'o', 'h', 'l', 'c', 'v')):
            raise ValueError('bar values must be finite numeric')
        if (int(bar['t']) != bar['t'] or bar['t'] % interval or bar['v'] < 0 or bar['l'] <= 0
                or not bar['l'] <= min(bar['o'], bar['c']) <= max(bar['o'], bar['c']) <= bar['h']):
            raise ValueError('invalid bar bounds or boundary')
        if previous is not None and bar['t'] != previous + interval:
            raise ValueError('bar gap/duplicate')
        previous = bar['t']
    return bars


def ema(values, period):
    """Pine ta.ema seeds with the first valid value, not a period-length SMA."""
    out, average = [], None
    alpha = 2 / (period + 1)
    for value in values:
        if value is not None:
            average = value if average is None else alpha * value + (1 - alpha) * average
        out.append(average)
    return out


def rma(values, period):
    out, seed, average = [], [], None
    for value in values:
        if value is not None:
            if average is None:
                seed.append(value)
                if len(seed) == period:
                    average = sum(seed) / period
            else:
                average = value / period + (1 - 1 / period) * average
        out.append(average)
    return out


def true_range(bars):
    return [max(b['h'] - b['l'], abs(b['h'] - bars[i - 1]['c']), abs(b['l'] - bars[i - 1]['c']))
            if i else b['h'] - b['l'] for i, b in enumerate(bars)]


def atr(bars, period=14):
    return rma(true_range(bars), period)


def adx(bars, period=14):
    plus, minus = ([None], [None]) if bars else ([], [])
    for b, old in zip(bars[1:], bars):
        up, down = b['h'] - old['h'], old['l'] - b['l']
        plus.append(up if up > down and up > 0 else 0.)
        minus.append(down if down > up and down > 0 else 0.)
    positive, negative = rma(plus, period), rma(minus, period)
    dx = [None if p is None or m is None else 100 * abs(p - m) / (p + m if p + m else 1)
          for p, m in zip(positive, negative)]
    return rma(dx, period)


def bollinger(values, period, deviation):
    lower, upper = [None] * len(values), [None] * len(values)
    for i in range(period - 1, len(values)):
        window = values[i - period + 1:i + 1]
        mean = sum(window) / period
        spread = deviation * sqrt(sum((x - mean) ** 2 for x in window) / period)
        lower[i], upper[i] = mean - spread, mean + spread
    return lower, upper


def aggregate(bars, seconds):
    """UTC aligned complete groups only; no incomplete first/last HTF candle."""
    if seconds <= 14400 or seconds % 14400:
        raise ValueError('higher timeframe must be a multiple of 4h')
    groups, current, bucket = [], [], None
    def complete():
        if len(current) == seconds // 14400 and current[0]['t'] == bucket:
            groups.append(dict(t=bucket, o=current[0]['o'], h=max(b['h'] for b in current),
                               l=min(b['l'] for b in current), c=current[-1]['c'], v=sum(b['v'] for b in current)))
    for bar in bars:
        start = bar['t'] // seconds * seconds
        if bucket is not None and start != bucket:
            complete()
            current = []
        bucket = start
        current.append(bar)
    if current:
        complete()
    return groups
