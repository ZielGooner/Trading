"""The approved four-asset TradingView BASE strategy for the live worker."""
import json
import math
from pathlib import Path
from trading.signals import validate_parameters

HERE = Path(__file__).resolve().parent.parent
SYMBOLS = ('BTCUSDT', 'XRPUSDT', 'SOLUSDT', 'HYPEUSDT')
VERSION = 'TV_BASE_4ASSETS_2X_20261005'


def validate(config):
    required = {'schema', 'strategy_version', 'orders_enabled', 'sizing_basis', 'margin_fraction',
                'margin_mode', 'leverage_by_symbol', 'entry_priority', 'taker_fee',
                'slippage_ticks', 'maintenance_margin_rate',
                'timeframe', 'strategy_by_symbol'}
    if not isinstance(config, dict) or set(config) != required:
        raise ValueError('unknown/missing live strategy config fields')
    json.dumps(config, allow_nan=False)
    if (config['schema'] != 'tradingview_base_mtf_v1' or config['strategy_version'] != VERSION
            or config['orders_enabled'] is not False):
        raise ValueError('user-started BASE configuration required')
    if (config['sizing_basis'] != 'available_margin' or config['margin_fraction'] != .5
            or config['margin_mode'] != 'isolated' or config['timeframe'] != '4h'
            or config['entry_priority'] != 'previous_closed_4h_turnover_desc'):
        raise ValueError('BASE requires 4h, isolated, sequential free margin 50%, turnover priority')
    if (not isinstance(config['leverage_by_symbol'], dict)
            or set(config['leverage_by_symbol']) != set(SYMBOLS)
            or any(isinstance(x, bool) or x != 2 for x in config['leverage_by_symbol'].values())):
        raise ValueError('all four symbols require 2x leverage')
    for key in ('taker_fee', 'maintenance_margin_rate', 'slippage_ticks'):
        value = config[key]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError('invalid ' + key)
    if not 0 <= config['taker_fee'] < .1 or not 0 < config['maintenance_margin_rate'] < .1:
        raise ValueError('invalid fee/maintenance estimate')
    if config['slippage_ticks'] != 2:
        raise ValueError('BASE uses two-tick IOC allowance')
    if not isinstance(config['strategy_by_symbol'], dict) or set(config['strategy_by_symbol']) != set(SYMBOLS):
        raise ValueError('exactly four strategy profiles required')
    for parameters in config['strategy_by_symbol'].values():
        validate_parameters(parameters)
    return config


def load(path=HERE / 'strategy.json'):
    return validate(json.loads(Path(path).read_text(encoding='utf-8-sig')))


def parameters_for(config, symbol):
    if symbol not in config['strategy_by_symbol']:
        raise ValueError('unsupported symbol')
    return config['strategy_by_symbol'][symbol]
