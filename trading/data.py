"""Strict symbol-specific snapshots and common portfolio execution clocks."""
from datetime import datetime,timezone
import hashlib
import json
import math
from pathlib import Path
from trading.primitives import _bar
from trading.market_validation import aggregate_audit
from trading.market_validation import load_btc_context as btc_context

DATA=Path(__file__).resolve().parents[1]/'data'


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def load_tv_snapshot(symbol):
    if symbol not in ('XRPUSDT','SOLUSDT'): raise ValueError('unsupported TradingView snapshot')
    tv_symbol='BINANCE:'+symbol+'.P'
    path=DATA/f'tradingview_{symbol.lower()}_p_4h.json'
    envelope=json.loads(path.read_text(encoding='utf-8'))
    requested,response=envelope['requested'],envelope['response']
    if requested['symbol']!=tv_symbol or response.get('symbol')!=requested['symbol'] or response.get('success') is not True:
        raise ValueError(symbol+' TradingView perpetual source mismatch')
    if requested['interval']!='4h' or response.get('interval')!='4h' or response.get('count')!=len(response['bars']):
        raise ValueError(symbol+' invalid interval/count')
    fetched=datetime.fromisoformat(envelope['retrieved_at_utc'].replace('Z','+00:00'))
    if fetched.tzinfo is None: raise ValueError('retrieval time must include timezone')
    cutoff=fetched.timestamp()-1800
    bars=[]; excluded=[]; previous=None
    for i,b in enumerate(response['bars']):
        _bar(b,'XRP signal')
        if b['t']%14400 or (previous is not None and b['t']-previous!=14400):
            raise ValueError('noncontiguous XRP TradingView candles')
        if isinstance(b['v'],bool) or not isinstance(b['v'],(int,float)) or not math.isfinite(b['v']) or b['v']<0:
            raise ValueError('invalid XRP volume')
        previous=b['t']
        if i==len(response['bars'])-1 or b['t']+14400>cutoff:
            excluded.append(b['t'])
        else: bars.append(dict(b))
    if len(bars)<=450: raise ValueError('insufficient XRP history')
    return bars,{'source':'TradingView official MCP','symbol':tv_symbol,'raw_sha256':digest(path),
                 'retrieved_at_utc':envelope['retrieved_at_utc'],'completed_count':len(bars),
                 'excluded_timestamps':excluded,'notice':response.get('notice')}


def load_xrp_snapshot(): return load_tv_snapshot('XRPUSDT')


def klines(rows,volume=True):
    result=[]
    for r in rows:
        if int(r[0])%3600000 or int(r[6])!=int(r[0])+3599999: raise ValueError('invalid execution boundaries')
        b={'t':int(r[0])//1000,**{k:float(r[j]) for j,k in enumerate(('o','h','l','c'),1)}}
        if volume:
            b['v']=float(r[5])
            if not math.isfinite(b['v']) or b['v']<0: raise ValueError('invalid execution volume')
        _bar(b,'execution')
        if result and b['t']-result[-1]['t']!=3600: raise ValueError('execution gaps')
        result.append(b)
    return result


def filters(symbol):
    if symbol.get('contractType')!='PERPETUAL' or symbol.get('marginAsset')!='USDT' or symbol.get('status')!='TRADING':
        raise ValueError('not a trading USDT perpetual')
    f={x['filterType']:x for x in symbol['filters']}
    lot,market=f['LOT_SIZE'],f['MARKET_LOT_SIZE']
    return {'quantity_step':max(float(lot['stepSize']),float(market['stepSize'])),
            'min_quantity':max(float(lot['minQty']),float(market['minQty'])),
            'max_quantity':min(float(lot['maxQty']),float(market['maxQty'])),
            'min_notional':float(f['MIN_NOTIONAL']['notional']),
            'price_tick':float(f['PRICE_FILTER']['tickSize']),
            'liquidation_fee':float(symbol['liquidationFee'])}


def load_contexts(include_sol=False):
    btc=btc_context('4h')
    xrp,xa=load_xrp_snapshot()
    path=DATA/'xrp_public_execution_1h.json'
    raw=json.loads(path.read_text(encoding='utf-8'))
    if raw['symbol']!='XRPUSDT' or raw['interval']!='1h' or raw['tradingview_sha256']!=xa['raw_sha256']:
        raise ValueError('XRP execution provenance mismatch')
    xb,xm=klines(raw['contract_klines']),klines(raw['mark_klines'],False)
    if raw['start_t']!=xb[0]['t'] or raw['end_t']!=xb[-1]['t']+3600:
        raise ValueError('XRP execution window mismatch')
    if [b['t'] for b in xb]!=[b['t'] for b in xm]: raise ValueError('XRP Mark coverage mismatch')
    info=json.loads((DATA/'portfolio_exchange_info.json').read_text(encoding='utf-8'))
    symbols=info['symbols']
    aggregation=aggregate_audit(xrp,xb,14400,price_tolerance=filters(symbols['XRPUSDT'])['price_tick'],
                                fail_on_price_mismatch=False)
    for mismatch in aggregation['price_mismatches']:
        if (mismatch['open_t']!=1730145600 or set(mismatch['execution_minus_signal'])!={'l'}
                or xrp[mismatch['source_index']]['l']!=.516
                or not math.isclose(mismatch['execution_minus_signal']['l'],.001,abs_tol=1e-12)):
            raise ValueError(f'unreviewed XRP cross-feed mismatch: {mismatch}')
        for name,expected_hash in {
            'xrp_4h_discrepancy_check.json':'975656c8621e5062f0ddce4aaac5878a394f78993f72ee26a80a7524c91d06ab',
            'xrp_1h_discrepancy_check.json':'9610e2281c6a6a8a25f8f45a12efec830708b11e7f6a8906761e64954101ed23'}.items():
            if digest(DATA/name)!=expected_hash: raise ValueError('XRP discrepancy proof hash mismatch')
        proof=json.loads((DATA/'xrp_4h_discrepancy_check.json').read_text(encoding='utf-8'))
        repeat=json.loads((DATA/'xrp_1h_discrepancy_check.json').read_text(encoding='utf-8'))
        expected='https://fapi.binance.com/fapi/v1/klines?symbol=XRPUSDT&interval='
        if proof['url']!=expected+'4h&startTime=1730145600000&endTime=1730159999999&limit=1':
            raise ValueError('incorrect 4h discrepancy proof')
        if repeat['url']!=expected+'1h&startTime=1730145600000&endTime=1730159999999&limit=4':
            raise ValueError('incorrect repeated 1h discrepancy proof')
        group=[b for b in xb if 1730145600<=b['t']<1730160000]
        if klines(repeat['response'])!=group or float(proof['response'][3])!=.516:
            raise ValueError('public XRP discrepancy changed')
    aggregation.update(status='unresolved_public_1h_vs_4h_discrepancy' if aggregation['price_mismatches'] else 'pass',
        policy='Raw hourly execution retained as a scenario. No repaired candles. Separate conservative 4h-extrema sensitivity required.',
        proof_sha256={name:digest(DATA/name) for name in ('xrp_4h_discrepancy_check.json','xrp_1h_discrepancy_check.json')})
    xa.update(execution_sha256=digest(path),execution_source='Binance public USD-M REST',aggregation=aggregation)
    contexts={'BTCUSDT':{'source':btc['signal_bars'],'bars':btc['execution_bars'],'marks':btc['execution_marks'],
                         'funding':btc['funding'],'audit':btc['audit']},
              'XRPUSDT':{'source':xrp,'bars':xb,'marks':{b['t']:b for b in xm},'funding':raw['funding'],'audit':xa}}
    if include_sol:
        sol,sa=load_tv_snapshot('SOLUSDT')
        path=DATA/'sol_public_execution_1h.json'
        raw=json.loads(path.read_text(encoding='utf-8'))
        if raw['symbol']!='SOLUSDT' or raw['interval']!='1h' or raw['tradingview_sha256']!=sa['raw_sha256']:
            raise ValueError('SOL execution provenance mismatch')
        sb,sm=klines(raw['contract_klines']),klines(raw['mark_klines'],False)
        if raw['start_t']!=sb[0]['t'] or raw['end_t']!=sb[-1]['t']+3600 or [b['t'] for b in sb]!=[b['t'] for b in sm]:
            raise ValueError('SOL execution/Mark window mismatch')
        sol_info=json.loads((DATA/'sol_exchange_info.json').read_text(encoding='utf-8'))
        symbols['SOLUSDT']=sol_info['symbols']['SOLUSDT']
        sa.update(execution_sha256=digest(path),execution_source='Binance public USD-M REST',
                  aggregation=aggregate_audit(sol,sb,14400,price_tolerance=filters(symbols['SOLUSDT'])['price_tick']))
        contexts['SOLUSDT']={'source':sol,'bars':sb,'marks':{b['t']:b for b in sm},'funding':raw['funding'],'audit':sa}
    for symbol,c in contexts.items():
        c['filters']=filters(symbols[symbol])
        c['audit']['filters_snapshot_sha256']=digest(DATA/('sol_exchange_info.json' if symbol=='SOLUSDT' else 'portfolio_exchange_info.json'))
        c['audit']['allocation_policy']='10% of available margin before each entry; BTC 10x, XRP/SOL 5x; shared isolated account'
        c['by_time']={b['t']:b for b in c['bars']}
        c['funding_by_time']={}
        for event in c['funding']:
            milliseconds=float(event['fundingTime'])
            boundary=int(milliseconds)//3600000*3600
            rate,price=float(event['fundingRate']),float(event['markPrice'])
            if event.get('symbol')!=symbol or not math.isfinite(rate) or not math.isfinite(price) or price<=0:
                raise ValueError('invalid public funding')
            if milliseconds%3600000>1000 or boundary in c['funding_by_time']:
                raise ValueError('non-hourly/duplicate funding event')
            c['funding_by_time'][boundary]={'rate':rate,'price':price}
        # This captured history is regular 8h funding; changes must be explicitly handled.
        first,end=c['bars'][0]['t'],c['bars'][-1]['t']+3600
        expected=list(range(((first+28799)//28800)*28800,end,28800))
        observed=sorted(t for t in c['funding_by_time'] if first<=t<end)
        if observed!=expected: raise ValueError(f'{symbol} funding history does not cover every expected 8h boundary')
    start=max(c['source'][450]['t'] for c in contexts.values())
    end=min(c['source'][-1]['t']+14400 for c in contexts.values())
    return contexts,start,end
