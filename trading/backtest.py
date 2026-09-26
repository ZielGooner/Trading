"""Shared isolated-margin account; hourly causal execution; no exchange orders.

Signals are keyed by entry time and carry the original closed 4h candle time.
All opening allocations precede any unresolved intrabar liquidation/SL/TP.
"""
from collections import Counter
import math
from types import SimpleNamespace
from trading.config import validate
from trading.primitives import _bar,_rounded_quantity,_liquidation_price


def simulate(contexts,signals,config,start,end):
    cfg=validate(config)
    if not contexts or not set(contexts)<=set(cfg['leverage_by_symbol']): raise ValueError('unsupported symbols')
    if any(isinstance(t,bool) or not isinstance(t,int) or t%3600 for t in (start,end)) or end<=start:
        raise ValueError('invalid hourly bounds')
    times=range(start,end,3600)
    for symbol,c in contexts.items():
        f=c['filters']
        if any(not math.isfinite(f[k]) or f[k]<=0 for k in ('quantity_step','min_quantity','max_quantity','min_notional')):
            raise ValueError('invalid order filters')
        if not 0<=f['liquidation_fee']<1: raise ValueError('invalid liquidation fee')
        for t in times:
            for name in ('by_time','marks'):
                if t not in c[name] or c[name][t]['t']!=t: raise ValueError('missing execution/mark coverage')
                _bar(c[name][t],symbol+' '+name)
        for t,s in signals.get(symbol,{}).items():
            if set(s)!={'signal_open_t','direction','stop_distance','take_profit_r','max_hold_bars'}:
                raise ValueError('unknown or unsupported signal fields')
            if t%3600 or t!=s['signal_open_t']+14400: raise ValueError('signal must be known only after 4h close')
            if isinstance(s['direction'],bool) or s['direction'] not in (-1,1): raise ValueError('invalid direction')
            if any(isinstance(s[k],bool) or not isinstance(s[k],(int,float)) or not math.isfinite(s[k]) or s[k]<=0
                   for k in ('stop_distance','take_profit_r','max_hold_bars')):
                raise ValueError('invalid signal')
            if int(s['max_hold_bars'])!=s['max_hold_bars']: raise ValueError('fractional holding time')
    free=float(cfg['initial_equity']); reserve=cfg['reserved_order_margin']
    positions={}; trades=[]; events=[]; curve=[]; daily=[]; skips=Counter()
    peak=previous_nav=free; mdd=0.; maximum_concurrent=0; day=None
    mmr=SimpleNamespace(maintenance_margin_rate=cfg['maintenance_margin_rate'])
    ambiguous_stop=ambiguous_liq=0

    def available(): return max(0.,free-reserve)

    def event(kind,symbol,t,**values):
        events.append({'seq':len(events),'type':kind,'symbol':symbol,'t':t,'free_after':free,
                       'available_margin_after':available(),**values})

    def close(symbol,raw,reason,t,liquidated=False):
        nonlocal free
        p=positions.pop(symbol)
        price=raw if liquidated else raw*(1-p['direction']*cfg['slippage'])
        natural=p['quantity']*(price-p['entry_price'])*p['direction']
        floor=-p['isolated_margin']+p['funding_paid']
        modeled_fee=p['quantity']*price*(contexts[symbol]['filters']['liquidation_fee'] if liquidated else cfg['taker_fee'])
        if liquidated:
            fee=min(modeled_fee,max(0.,natural-floor)); gross=floor+fee
        elif natural-modeled_fee<floor:
            gross=max(natural,floor); fee=min(modeled_fee,max(0.,gross-floor))
            reason='isolated_margin_depletion'; liquidated=True
        else:
            gross=natural; fee=modeled_fee
        released=max(0.,p['isolated_margin']-p['funding_paid']+gross-fee)
        free+=released
        trade={**p,'exit_time':t,'exit_price':price,'exit_reason':reason,'gross_pnl':gross,
               'natural_exit_price_pnl':natural,'exit_fee':fee,'modeled_exit_fee':modeled_fee,
               'liquidated':liquidated,'released_cash':released,
               'net_pnl':gross-p['entry_fee']-fee-p['funding_paid'],'free_after_exit':free}
        trades.append(trade)
        event('exit',symbol,t,trade_id=p['trade_id'],released_cash=released,gross_pnl=gross,fee=fee)
        day['exits']+=1

    def open_exit(symbol,t):
        p=positions[symbol]; b=contexts[symbol]['by_time'][t]; m=contexts[symbol]['marks'][t]
        liq=_liquidation_price(p,mmr); d=p['direction']
        if d*(m['o']-liq)<=0: close(symbol,m['o'],'liquidation',t,True)
        elif d*(b['o']-p['stop_price'])<=0: close(symbol,b['o'],'stop_gap',t)
        elif d*(b['o']-p['target_price'])>=0: close(symbol,p['target_price'],'target_gap',t)

    for t in times:
        if day is None or day['day_start_t']!=t//86400*86400:
            day={'day_start_t':t//86400*86400,'start_equity':previous_nav,'entries':0,'exits':0,
                 'entry_gate_reason':None,'first_open_t':t,'exposure_bars':0}
            daily.append(day)
        held_at_open=set(positions); entries_before=day['entries']
        for symbol in sorted(held_at_open):
            f=contexts[symbol]['funding_by_time'].get(t)
            if f is not None:
                p=positions[symbol]
                payment=p['quantity']*f['price']*f['rate']*p['direction']
                if not math.isfinite(payment): raise ValueError('invalid funding payment')
                p['funding_paid']+=payment
                event('funding',symbol,t,trade_id=p['trade_id'],payment=payment,rate=f['rate'],price=f['price'])
        for symbol in sorted(held_at_open): open_exit(symbol,t)
        for symbol in cfg['entry_priority']:
            if symbol not in contexts or symbol in held_at_open: continue
            s=signals.get(symbol,{}).get(t)
            if s is None or s['signal_open_t']<start: continue
            if day['entry_gate_reason']:
                skips['daily_loss_limit']+=1; continue
            # Flat-account boundary costs are known before entry (legacy parity).
            if not positions and free/day['start_equity']-1<=-cfg['daily_loss_limit']:
                day['entry_gate_reason']='daily_loss_limit'; skips['daily_loss_limit']+=1; continue
            b=contexts[symbol]['by_time'][t]; spec=contexts[symbol]['filters']; d=s['direction']
            entry=b['o']*(1+d*cfg['slippage']); distance=s['stop_distance']
            stop=entry-d*distance; target=entry+d*distance*s['take_profit_r']
            if min(stop,target)<=0:
                skips['nonpositive_stop_or_target']+=1; continue
            before=available(); lev=cfg['leverage_by_symbol'][symbol]
            raw=min(before*cfg['margin_fraction']*lev/entry,cfg['cap_notional']/entry,
                    spec['max_quantity'],before/(entry/lev+entry*cfg['taker_fee']))
            q=_rounded_quantity(raw,spec['quantity_step'])
            if q<spec['min_quantity'] or q*entry<spec['min_notional']:
                skips['minimum_quantity_or_notional']+=1; continue
            margin=q*entry/lev; fee=q*entry*cfg['taker_fee']
            p={'trade_id':len(trades)+len(positions),'symbol':symbol,'entry_time':t,
               'signal_open_t':s['signal_open_t'],'direction':d,'quantity':q,'entry_price':entry,
               'stop_price':stop,'target_price':target,'max_hold_bars':s['max_hold_bars'],
               'leverage':lev,'entry_notional':q*entry,'isolated_margin':margin,'entry_fee':fee,
               'funding_paid':0.,'available_margin_before':before,'free_before':free,
               'target_margin':before*cfg['margin_fraction'],'actual_margin_fraction':margin/before}
            p['initial_liquidation_price']=_liquidation_price(p,mmr)
            p['stop_beyond_buffer']=distance>.7*abs(entry-p['initial_liquidation_price'])
            free-=margin+fee; positions[symbol]=p
            event('entry',symbol,t,position=dict(p),trade_id=p['trade_id'])
            day['entries']+=1
        maximum_concurrent=max(maximum_concurrent,len(positions))
        # No intrabar proceeds can be allocated to any opening entry above.
        for symbol in list(positions):
            open_exit(symbol,t)  # all new-position Mark gaps precede intrabar fills
        for symbol in list(positions):
            p=positions[symbol]; d=p['direction']; b=contexts[symbol]['by_time'][t]; m=contexts[symbol]['marks'][t]
            liq=_liquidation_price(p,mmr)
            liquidated=m['l']<=liq if d==1 else m['h']>=liq
            stop_hit=b['l']<=p['stop_price'] if d==1 else b['h']>=p['stop_price']
            target_hit=b['h']>=p['target_price'] if d==1 else b['l']<=p['target_price']
            close_t=t+3599.999
            if liquidated:
                ambiguous_liq+=int(stop_hit); close(symbol,liq,'liquidation',close_t,True)
            elif stop_hit:
                ambiguous_stop+=int(target_hit); close(symbol,p['stop_price'],'stop',close_t)
            elif target_hit: close(symbol,p['target_price'],'target',close_t)
            elif (t-p['entry_time'])//3600+1>=p['max_hold_bars']: close(symbol,b['c'],'max_hold',close_t)
            elif t+3600==end: close(symbol,b['c'],'split_end',close_t)
        isolated=sum(p['isolated_margin']-p['funding_paid'] for p in positions.values())
        posted=sum(p['isolated_margin'] for p in positions.values())
        unrealized=sum(p['quantity']*(contexts[s]['marks'][t]['c']-p['entry_price'])*p['direction'] for s,p in positions.items())
        nav=free+isolated+unrealized; peak=max(peak,nav); drawdown=1-nav/peak; mdd=max(mdd,drawdown)
        if free<-1e-8 or nav<-1e-8: raise AssertionError('isolated account became negative')
        curve.append({'t':t+3600,'equity':nav,'free_cash':free,'available_margin':available(),
                      'posted_margin':posted,'isolated_wallet':isolated,'unrealized_pnl':unrealized,
                      'drawdown':drawdown,'positions':{s:p['trade_id'] for s,p in positions.items()}})
        day.update(end_equity=nav,return_=nav/day['start_equity']-1,last_close_t=t+3600)
        day['exposure_bars']+=int(bool(held_at_open) or day['entries']>entries_before)
        day['execution_day']=bool(day['entries'] or day['exits'])
        if day['return_']<=-cfg['daily_loss_limit']: day['entry_gate_reason']='daily_loss_limit'
        previous_nav=nav
    wins=[p for p in trades if p['net_pnl']>0]; losses=[p for p in trades if p['net_pnl']<0]
    execution_days=[d for d in daily if d['execution_day']]
    stats={'initial_equity':cfg['initial_equity'],'ending_equity':free,'total_return':free/cfg['initial_equity']-1,
           'max_drawdown':mdd,'trades':len(trades),'wins':len(wins),'win_rate':len(wins)/len(trades) if trades else 0.,
           'profit_factor':sum(p['net_pnl'] for p in wins)/-sum(p['net_pnl'] for p in losses) if losses else None,
           'fees_paid':sum(p['entry_fee']+p['exit_fee'] for p in trades),'funding_paid':sum(p['funding_paid'] for p in trades),
           'liquidations':sum(p['exit_reason']=='liquidation' for p in trades),
           'margin_depletions':sum(p['exit_reason']=='isolated_margin_depletion' for p in trades),
           'unsafe_stop_entries':sum(p['stop_beyond_buffer'] for p in trades),
           'maximum_concurrent_positions':maximum_concurrent,'stop_target_ambiguous_bars':ambiguous_stop,
           'liquidation_stop_ambiguous_bars':ambiguous_liq,'skipped_entries':dict(skips),
           'execution_days':len(execution_days),
           'arithmetic_execution_day_return':sum(d['return_'] for d in execution_days)/len(execution_days) if execution_days else None,
           'maximum_margin_fraction':max((p['actual_margin_fraction'] for p in trades),default=0),
           'ledger_reconciliation_error':free-cfg['initial_equity']-sum(p['net_pnl'] for p in trades),
           'by_symbol':{s:{'trades':len(group),'wins':sum(p['net_pnl']>0 for p in group),
                            'win_rate':sum(p['net_pnl']>0 for p in group)/len(group) if group else 0.,
                            'net_pnl':sum(p['net_pnl'] for p in group)}
                        for s in contexts for group in [[p for p in trades if p['symbol']==s]]}}
    return {'stats':stats,'trades':trades,'events':events,'equity_curve':curve,'daily':daily,
            'config':cfg,'start_t':start,'end_t':end,'orders_enabled':False}
