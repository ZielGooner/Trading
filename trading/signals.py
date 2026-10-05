"""Approved BASE: four closed-4h profiles and confirmed higher-timeframe filters."""
from math import isfinite
from trading.indicators import validate_bars, aggregate, ema, atr, adx, bollinger


def validate_parameters(p):
    fields = {'profile', 'family', 'length', 'slow_length', 'bb_deviation', 'higher_seconds',
              'filter', 'higher_ema_length', 'higher_fast', 'higher_slow', 'adx_length',
              'adx_max', 'atr_length', 'stop_atr', 'take_profit_atr', 'max_hold_bars', 'flip_exit'}
    if not isinstance(p, dict) or set(p) != fields:
        raise ValueError('unknown/missing BASE profile fields')
    if p['family'] not in ('bb_breakout', 'donchian', 'ema_cross', 'bb_reentry') or p['filter'] not in ('adx_range', 'price', 'ma_stack'):
        raise ValueError('unsupported BASE family/filter')
    for key in ('length', 'slow_length', 'higher_seconds', 'higher_ema_length', 'higher_fast', 'higher_slow', 'adx_length', 'atr_length'):
        if isinstance(p[key], bool) or not isinstance(p[key], int) or p[key] <= 0:
            raise ValueError('positive integer required: ' + key)
    if p['higher_seconds'] not in (28800, 43200, 86400):
        raise ValueError('BASE confirmed higher timeframe must be 8h, 12h or 1d')
    if isinstance(p['max_hold_bars'], bool) or not isinstance(p['max_hold_bars'], int) or p['max_hold_bars'] < 0:
        raise ValueError('holding limit must be nonnegative; zero disables it')
    if not isinstance(p['flip_exit'], bool) or not isinstance(p['profile'], str) or not p['profile']:
        raise ValueError('invalid profile/exit switch')
    for key in ('bb_deviation', 'adx_max', 'stop_atr', 'take_profit_atr'):
        x = p[key]
        if isinstance(x, bool) or not isinstance(x, (int, float)) or not isfinite(x) or x <= 0:
            raise ValueError('positive numeric required: ' + key)
    return p


def build(bars, parameters):
    """Action keys are next 4h open. HTF as-of is the SIGNAL BAR OPEN ([1] Pine)."""
    p = validate_parameters(parameters)
    bars = list(bars)
    validate_bars(bars)
    h = aggregate(bars, p['higher_seconds'])
    close, hc = [b['c'] for b in bars], [b['c'] for b in h]
    fixed_atr = atr(bars, p['atr_length'])
    if p['filter'] == 'adx_range':
        ha = adx(h, p['adx_length'])
    elif p['filter'] == 'price':
        hp = ema(hc, p['higher_ema_length'])
    else:
        hf, hs = ema(hc, p['higher_fast']), ema(hc, p['higher_slow'])
    if p['family'] == 'ema_cross':
        fast, slow = ema(close, p['length']), ema(close, p['slow_length'])
    elif p['family'] in ('bb_breakout', 'bb_reentry'):
        lower, upper = bollinger(close, p['length'], p['bb_deviation'])
    events, j = {}, -1
    for i, bar in enumerate(bars):
        while j + 1 < len(h) and h[j + 1]['t'] + p['higher_seconds'] <= bar['t']:
            j += 1
        if j < 49 or fixed_atr[i] is None or fixed_atr[i] <= 0 or not i:
            continue
        if p['filter'] == 'adx_range':
            up = down = ha[j] is not None and ha[j] < p['adx_max']
        elif p['filter'] == 'price':
            up, down = hc[j] > hp[j], hc[j] < hp[j]
        else:
            up, down = hf[j] > hs[j], hf[j] < hs[j]
        if p['family'] == 'ema_cross':
            long = fast[i] > slow[i] and fast[i - 1] <= slow[i - 1]
            short = fast[i] < slow[i] and fast[i - 1] >= slow[i - 1]
        elif p['family'] == 'donchian':
            if i < p['length']:
                continue
            previous = bars[i - p['length']:i]
            long, short = close[i] > max(b['h'] for b in previous), close[i] < min(b['l'] for b in previous)
        else:
            if lower[i] is None or lower[i - 1] is None:
                continue
            if p['family'] == 'bb_breakout':
                long, short = close[i] > upper[i], close[i] < lower[i]
            else:
                long = close[i] > lower[i] and close[i - 1] <= lower[i - 1]
                short = close[i] < upper[i] and close[i - 1] >= upper[i - 1]
        direction = 1 if up and long else -1 if down and short else 0
        if direction:
            events[bar['t'] + 14400] = {
                'direction': direction, 'stop_distance': fixed_atr[i] * p['stop_atr'],
                'take_profit_distance': fixed_atr[i] * p['take_profit_atr'],
                'max_hold_bars': p['max_hold_bars'], 'flip_exit': p['flip_exit'],
                'turnover': bar['v'] * bar['c'], 'signal_open_t': bar['t'],
                'higher_close_t': h[j]['t'] + p['higher_seconds'], 'profile': p['profile']}
    return events
