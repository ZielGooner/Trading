"""Reconstruct free cash, isolated wallets and Mark NAV from the event ledger."""
import math
from decimal import Decimal


def verify(result,contexts):
    cfg=result['config']; checks=0
    def check(condition,message):
        nonlocal checks
        checks+=1
        if not condition: raise AssertionError(message)
    def same(a,b,label): check(math.isclose(a,b,rel_tol=1e-10,abs_tol=1e-7),label)
    free=cfg['initial_equity']; positions={}; cursor=0; events=result['events']
    trades={p['trade_id']:p for p in result['trades']}
    check(len(trades)==len(result['trades']),'unique trade ids')
    check(all(a['t']<=b['t'] for a,b in zip(events,events[1:])),'chronological events')
    for row in result['equity_curve']:
        while cursor<len(events) and events[cursor]['t']<row['t']:
            e=events[cursor]; cursor+=1; symbol=e['symbol']
            check(e['seq']==cursor-1,'event sequence')
            if e['type']=='entry':
                p=dict(e['position']); f=contexts[symbol]['filters']; lev=cfg['leverage_by_symbol'][symbol]
                before=max(0.,free-cfg['reserved_order_margin'])
                check(symbol not in positions,'one position per symbol')
                check(p['entry_time']==p['signal_open_t']+14400 and p['signal_open_t']>=result['start_t'],'closed signal')
                same(p['available_margin_before'],before,'available margin before entry')
                same(p['target_margin'],before*.1,'target exactly 10 percent available margin')
                same(p['isolated_margin'],p['quantity']*p['entry_price']/lev,'symbol leverage')
                same(p['entry_fee'],p['quantity']*p['entry_price']*cfg['taker_fee'],'entry fee')
                raw=min(before*.1*lev/p['entry_price'],cfg['cap_notional']/p['entry_price'],f['max_quantity'],
                        before/(p['entry_price']/lev+p['entry_price']*cfg['taker_fee']))
                expected=float((Decimal(str(raw))//Decimal(str(f['quantity_step'])))*Decimal(str(f['quantity_step'])))
                same(p['quantity'],expected,'rounded affordable quantity')
                check(p['isolated_margin']<=before*.1+1e-7,'global margin limit')
                check(f['min_quantity']<=p['quantity']<=f['max_quantity'],'quantity bounds')
                check(p['quantity']*p['entry_price']>=f['min_notional'],'minimum notional')
                free-=p['isolated_margin']+p['entry_fee']; positions[symbol]=p
            elif e['type']=='funding':
                p=positions[symbol]; f=contexts[symbol]['funding_by_time'][e['t']]
                expected=p['quantity']*f['price']*f['rate']*p['direction']
                same(e['payment'],expected,'public funding amount')
                check(e['t']>p['entry_time'],'new boundary entry pays no prior funding')
                p['funding_paid']+=expected
            elif e['type']=='exit':
                p=positions.pop(symbol); trade=trades[e['trade_id']]
                same(p['funding_paid'],trade['funding_paid'],'funding ledger')
                expected_funding=sum(p['quantity']*f['price']*f['rate']*p['direction']
                    for t,f in contexts[symbol]['funding_by_time'].items() if p['entry_time']<t<=trade['exit_time'])
                same(trade['funding_paid'],expected_funding,'complete funding window')
                natural=p['quantity']*(trade['exit_price']-p['entry_price'])*p['direction']
                floor=-p['isolated_margin']+p['funding_paid']
                modeled=p['quantity']*trade['exit_price']*(contexts[symbol]['filters']['liquidation_fee']
                    if trade['exit_reason']=='liquidation' else cfg['taker_fee'])
                if trade['exit_reason']=='liquidation': fee=min(modeled,max(0.,natural-floor)); gross=floor+fee
                elif natural-modeled<floor: gross=max(natural,floor); fee=min(modeled,max(0.,gross-floor))
                else: gross=natural; fee=modeled
                same(gross,trade['gross_pnl'],'gross trade pnl'); same(fee,trade['exit_fee'],'exit fee')
                released=max(0.,p['isolated_margin']-p['funding_paid']+gross-fee)
                same(released,e['released_cash'],'released collateral')
                same(gross-fee-p['entry_fee']-p['funding_paid'],trade['net_pnl'],'net trade pnl')
                free+=released
            else: raise AssertionError('unknown ledger event')
            same(free,e['free_after'],'event free cash')
            same(max(0.,free-cfg['reserved_order_margin']),e['available_margin_after'],'event available margin')
        t=row['t']-3600
        isolated=sum(p['isolated_margin']-p['funding_paid'] for p in positions.values())
        unrealized=sum(p['quantity']*(contexts[s]['marks'][t]['c']-p['entry_price'])*p['direction'] for s,p in positions.items())
        same(free,row['free_cash'],'curve cash'); same(isolated,row['isolated_wallet'],'curve isolated')
        same(unrealized,row['unrealized_pnl'],'curve unrealized')
        same(sum(p['isolated_margin'] for p in positions.values()),row['posted_margin'],'curve posted margin')
        same(free+isolated+unrealized,row['equity'],'Mark account NAV')
        same(max(0.,free-cfg['reserved_order_margin']),row['available_margin'],'curve available margin')
        check({s:p['trade_id'] for s,p in positions.items()}==row['positions'],'curve positions')
    check(not positions and cursor==len(events),'all positions and events settled')
    same(free,result['stats']['ending_equity'],'ending equity')
    same(free,cfg['initial_equity']+sum(t['net_pnl'] for t in trades.values()),'full account reconciliation')
    return {'passed':True,'checks':checks,'events':len(events),'trades':len(trades),'curve_rows':len(result['equity_curve'])}
