"""Closed 4h Supertrend/EMA signals and fixed ATR exits for the current strategy."""
from math import isfinite
from trading.indicators import IndicatorCache

FILTERS={
    'open':{'trend':0,'min_adx':0,'max_adx':100},
    'fast':{'trend':50,'min_adx':10,'max_adx':100},
    'range':{'trend':0,'min_adx':0,'max_adx':20},
}


def validate_entry(p):
    fields={'family','period','filter','direction','atr_period','warmup'}
    extra={'supertrend':{'multiplier'},'ema_reclaim':{'mode'}}
    if not isinstance(p,dict) or p.get('family') not in extra or set(p)!=fields|extra[p['family']]:
        raise ValueError('unsupported entry fields or strategy family')
    if p['filter'] not in FILTERS or p['direction'] not in ('long','short','both'):
        raise ValueError('unknown filter/direction')
    for name in ('period','atr_period','warmup'):
        if isinstance(p[name],bool) or not isinstance(p[name],int) or p[name]<=0:
            raise ValueError('positive integer required: '+name)
    if p['warmup']<max(450,p['period'],p['atr_period']): raise ValueError('insufficient indicator warmup')
    if p['family']=='supertrend':
        value=p['multiplier']
        if isinstance(value,bool) or not isinstance(value,(int,float)) or not isfinite(value) or value<=0:
            raise ValueError('positive Supertrend multiplier required')
    elif p['mode'] not in ('wick','cross'): raise ValueError('unknown EMA entry mode')
    return p


def validate_exit(p):
    if not isinstance(p,dict) or set(p)!={'stop_atr','take_profit_r','max_hold_bars','management'}:
        raise ValueError('invalid exit fields')
    for key in ('stop_atr','take_profit_r','max_hold_bars'):
        value=p[key]
        if isinstance(value,bool) or not isinstance(value,(int,float)) or not isfinite(value) or value<=0:
            raise ValueError('positive exit parameter required: '+key)
    if int(p['max_hold_bars'])!=p['max_hold_bars']: raise ValueError('holding duration must be integral')
    if p['management']!='fixed': raise ValueError('only the current fixed SL/TP is supported')
    return p


def build(bars,entry,exit):
    """Original indicator arithmetic; each event is actionable only after 4h close."""
    p=validate_entry(entry); xp=validate_exit(exit); c=IndicatorCache(bars)
    bars=c.bars; atr=c.atr(p['atr_period']); adx=c.adx(14); gate=FILTERS[p['filter']]
    trend=c.ema(gate['trend']) if gate['trend'] else None
    st=c.supertrend(p['period'],p['multiplier']) if p['family']=='supertrend' else None
    fast=c.ema(p['period']) if p['family']=='ema_reclaim' else None
    signals={}
    for i in range(p['warmup'],len(bars)):
        if atr[i] is None or atr[i]<=0: continue
        b=bars[i]
        if st is not None:
            long,short=st[i]['direction']==1,st[i]['direction']==-1
        else:
            if p['mode']=='wick':
                long=c.close[i-1]>fast[i-1] and b['l']<=fast[i]<b['c']
                short=c.close[i-1]<fast[i-1] and b['h']>=fast[i]>b['c']
            else:
                long=c.close[i-1]<=fast[i-1] and b['c']>fast[i]
                short=c.close[i-1]>=fast[i-1] and b['c']<fast[i]
            long=long and b['c']>b['o']; short=short and b['c']<b['o']
        if long==short: continue
        direction=1 if long else -1
        if p['direction']!='both' and (p['direction']=='long')!=(direction==1): continue
        if trend is not None and (direction*(b['c']-trend[i])<=0 or direction*(trend[i]-trend[i-5])<=0): continue
        if adx[i] is None or not gate['min_adx']<=adx[i]<=gate['max_adx']: continue
        signals[b['t']+14400]={'direction':direction,'stop_distance':atr[i]*xp['stop_atr'],
            'take_profit_r':xp['take_profit_r'],'max_hold_bars':int(xp['max_hold_bars'])*4,'signal_open_t':b['t']}
    return signals


def signals_for(context,config,symbol):
    from trading.config import parameters_for
    p=parameters_for(config,symbol)
    return build(context['source'],p['entry_parameters'],p['exit_parameters'])
