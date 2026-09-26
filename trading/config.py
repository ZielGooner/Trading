"""One global available-margin allocation policy for every supported symbol."""
import json
import math
from pathlib import Path
from trading.signals import validate_entry
from trading.signals import validate_exit

HERE=Path(__file__).resolve().parent.parent
LEVERAGE={'BTCUSDT':10,'XRPUSDT':5,'SOLUSDT':5}


def validate(config):
    required={'schema','orders_enabled','initial_equity','sizing_basis','margin_fraction','margin_mode',
              'leverage_by_symbol','entry_priority','reserved_order_margin','taker_fee','slippage',
              'cap_notional','maintenance_margin_rate','daily_loss_limit','timeframe',
              'entry_parameters','exit_parameters'}
    if not isinstance(config,dict) or not required<=set(config) or set(config)-required-{'symbol_overrides'}:
        raise ValueError('unknown/missing portfolio config fields')
    json.dumps(config,allow_nan=False)
    if config['schema']!='available_margin_portfolio_v1' or config['orders_enabled'] is not False:
        raise ValueError('research-only portfolio config required')
    if config['sizing_basis']!='available_margin' or config['margin_fraction']!=.1 or config['margin_mode']!='isolated':
        raise ValueError('ALL entries must use 10% of available margin, isolated')
    allocation=config['leverage_by_symbol']
    if (not isinstance(allocation,dict) or set(allocation) not in ({'BTCUSDT','XRPUSDT'},set(LEVERAGE))
            or any(value!=LEVERAGE[symbol] for symbol,value in allocation.items())):
        raise ValueError('BTC requires 10x; XRP and optional SOL require 5x')
    priority=config['entry_priority']
    if not isinstance(priority,list) or len(priority)!=len(allocation) or set(priority)!=set(allocation):
        raise ValueError('priority must list every active symbol exactly once')
    for name in ('initial_equity','reserved_order_margin','taker_fee','slippage','cap_notional','maintenance_margin_rate','daily_loss_limit'):
        v=config[name]
        if isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v): raise ValueError('invalid '+name)
    if config['initial_equity']<=0 or not 0<=config['reserved_order_margin']<=config['initial_equity']:
        raise ValueError('invalid capital/reservation')
    if not 0<=config['taker_fee']<.1 or not 0<=config['slippage']<.1 or config['cap_notional']<=0:
        raise ValueError('invalid costs/notional cap')
    if not 0<config['maintenance_margin_rate']<.1 or not 0<config['daily_loss_limit']<=1:
        raise ValueError('invalid maintenance/daily gate')
    if config['timeframe']!='4h': raise ValueError('4h signal timeframe required')
    validate_entry(config['entry_parameters']); validate_exit(config['exit_parameters'])
    if config['exit_parameters']['management']!='fixed': raise ValueError('portfolio currently supports fixed SL/TP only')
    overrides=config.get('symbol_overrides',{})
    if not isinstance(overrides,dict) or not set(overrides)<=set(allocation):
        raise ValueError('invalid symbol overrides')
    for override in overrides.values():
        if not isinstance(override,dict) or set(override)!={'entry_parameters','exit_parameters'}:
            raise ValueError('symbol override may change entry/exit parameters only')
        validate_entry(override['entry_parameters']); validate_exit(override['exit_parameters'])
        if override['exit_parameters']['management']!='fixed':
            raise ValueError('portfolio currently supports fixed SL/TP only')
    return config


def load(path=HERE/'strategy.json'):
    return validate(json.loads(Path(path).read_text(encoding='utf-8-sig')))


def parameters_for(config,symbol):
    if symbol not in config['leverage_by_symbol']: raise ValueError('unsupported symbol')
    return config.get('symbol_overrides',{}).get(symbol,
        {key:config[key] for key in ('entry_parameters','exit_parameters')})
