"""Price validation, quantity rounding and isolated liquidation arithmetic."""
import math
from decimal import Decimal, ROUND_FLOOR

def _bar(bar, label):
    for key in ('t', 'o', 'h', 'l', 'c'):
        value = bar.get(key)
        if isinstance(value, bool) or not isinstance(value, (int, float)) or (not math.isfinite(value)):
            raise ValueError(f'invalid {label}.{key}')
    if int(bar['t']) != bar['t'] or bar['l'] <= 0 or (not bar['l'] <= min(bar['o'], bar['c']) <= max(bar['o'], bar['c']) <= bar['h']):
        raise ValueError(f'invalid OHLC geometry or timestamp: {label}')

def _rounded_quantity(quantity, step):
    return float((Decimal(str(quantity)) / Decimal(str(step))).to_integral_value(rounding=ROUND_FLOOR) * Decimal(str(step)))

def _liquidation_price(position, cfg):
    q, entry = (position['quantity'], position['entry_price'])
    remaining = position['isolated_margin'] - position['funding_paid']
    if position['direction'] == 1:
        return (q * entry - remaining) / (q * (1 - cfg.maintenance_margin_rate))
    return (q * entry + remaining) / (q * (1 + cfg.maintenance_margin_rate))
