"""Run the current offline strategy; retain only compact, current outputs."""
import argparse
from datetime import datetime,timezone
import gzip
import hashlib
import json
from pathlib import Path
import time
from trading.config import HERE,load
from trading.data import DATA,load_contexts,digest
from trading.signals import signals_for
from trading.backtest import simulate
from trading.audit import verify


def fingerprint(value):
    payload=json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=False,allow_nan=False).encode()
    return hashlib.sha256(payload).hexdigest()


def save_json(path,value):
    payload=json.dumps(value,ensure_ascii=False,indent=2,allow_nan=False)+'\n'
    temporary=path.with_name(path.name+'.tmp')
    temporary.write_text(payload,encoding='utf-8'); temporary.replace(path)


def scenarios(contexts,config,all_cases=False):
    if not all_cases:
        yield 'portfolio',contexts,config
        return
    for symbol,c in contexts.items(): yield symbol[:-4].lower()+'_only',{symbol:c},config
    if 'SOLUSDT' in contexts: yield 'btc_xrp',{s:contexts[s] for s in ('BTCUSDT','XRPUSDT')},config
    yield 'portfolio',contexts,config
    yield 'cost_stress',contexts,{**config,'taker_fee':.0006,'slippage':.0005}
    yield 'reverse_priority',contexts,{**config,'entry_priority':list(reversed(config['entry_priority']))}
    # Copy only the modified contract map; other symbols/immutable inputs are shared.
    envelope=dict(contexts); xrp=dict(contexts['XRPUSDT']); xrp['by_time']=dict(xrp['by_time']); envelope['XRPUSDT']=xrp
    proof=json.loads((DATA/'xrp_4h_discrepancy_check.json').read_text(encoding='utf-8'))['response']
    first=int(proof[0])//1000
    for t in range(first,first+14400,3600):
        b=dict(xrp['by_time'][t]); xrp['by_time'][t]=b
        b['l']=min(b['l'],float(proof[3])); b['h']=max(b['h'],float(proof[2]))
    xrp['by_time'][first]['o']=float(proof[1])
    yield 'data_discrepancy_stress',envelope,config


def verify_inputs(contexts,signals,config,start,end,reference):
    if config!=reference['config']: raise ValueError('strategy differs from the verified cleanup reference; use normal replay for edited parameters')
    if (start,end)!=(reference['start'],reference['end']): raise AssertionError('data bounds changed')
    for symbol,c in contexts.items():
        if fingerprint(signals[symbol])!=reference['signals'][symbol]: raise AssertionError(symbol+' signals changed')
        for field,expected in reference['contexts'][symbol].items():
            if fingerprint(c[field])!=expected: raise AssertionError(symbol+' input changed: '+field)


def verify_result(name,result,reference):
    expected=reference['cases'][name]
    if result['stats']!=expected['stats']: raise AssertionError(name+' statistics differ from pre-cleanup execution')
    for field in ('trades','events','equity_curve','daily'):
        if fingerprint(result[field])!=expected[field]: raise AssertionError(name+' changed: '+field)


def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',type=Path,default=HERE/'strategy.json')
    parser.add_argument('--verify',action='store_true',help='replay all 8 reference scenarios and verify exact pre-cleanup results')
    parser.add_argument('--all',action='store_true',help='show standalone and stress cases without fixed-reference comparison')
    parser.add_argument('--details',action='store_true',help='save the current portfolio ledger as compressed JSON')
    args=parser.parse_args(argv); begin=time.perf_counter(); cfg=load(args.config)
    contexts,start,end=load_contexts(include_sol='SOLUSDT' in cfg['leverage_by_symbol'])
    signals={s:signals_for(c,cfg,s) for s,c in contexts.items()}
    reference=json.loads((HERE/'tests/reference.json').read_text(encoding='utf-8')) if args.verify else None
    if reference: verify_inputs(contexts,signals,cfg,start,end,reference)
    output=HERE/'reports'; output.mkdir(exist_ok=True); stats={}; audits={}; portfolio=None
    for name,ctx,config in scenarios(contexts,cfg,args.all or args.verify):
        result=simulate(ctx,signals,config,start,end); audits[name]=verify(result,ctx)
        if reference: verify_result(name,result,reference)
        s=result['stats']; stats[name]=s
        if name=='portfolio': portfolio=result
        print(f"{name}: return={s['total_return']:+.2%}, win={s['win_rate']:.2%}, drawdown={s['max_drawdown']:.2%}, trades={s['trades']}",flush=True)
    inputs=[args.config,*sorted(Path(__file__).parent.glob('*.py')),*sorted(DATA.glob('*.json'))]
    summary={'generated_at_utc':datetime.now(timezone.utc).isoformat(),'start_t':start,'end_exclusive_t':end,
        'duration_days':(end-start)/86400,'config':cfg,'results':stats,'audit':audits,
        'source_audit':{s:c['audit'] for s,c in contexts.items()},
        'source_manifest':{str(p.resolve()):digest(p) for p in inputs},
        'regression':{'passed':True,'cases':list(stats),'fields':['stats','trades','events','equity_curve','daily']} if reference else None,
        'elapsed_seconds':time.perf_counter()-begin,'orders_enabled':False,'research_only':True}
    save_json(output/'summary.json',summary)
    if args.details:
        target=output/'portfolio.json.gz'; temporary=target.with_name(target.name+'.tmp')
        with gzip.open(temporary,'wt',encoding='utf-8',compresslevel=6) as stream:
            json.dump(portfolio,stream,ensure_ascii=False,separators=(',',':'),allow_nan=False)
        temporary.replace(target)
    else:
        # Avoid leaving the detailed ledger of an older configuration.
        (output/'portfolio.json.gz').unlink(missing_ok=True)
    lines=['# 현재 BTC · XRP · SOL 전략','',
        f"초기 {cfg['initial_equity']:,.0f} USDT / {(end-start)/86400:g}일 / 수수료·슬리피지·펀딩 차감 후 모의 결과.",
        '모든 진입은 그 순간 가용증거금10%, BTC10배·XRP/SOL5배. 이 백테스트 실행은 실계좌 주문을 전송하지 않음.','',
        '| 경우 | 종료 USDT | 수익률 | 승률 | 최대낙폭 | 거래 |','|---|---:|---:|---:|---:|---:|']
    for name,s in stats.items():
        lines.append(f"| {name} | {s['ending_equity']:,.2f} | {s['total_return']:+.2%} | {s['win_rate']:.2%} | {s['max_drawdown']:.2%} | {s['trades']} |")
    lines+=['','승률은 비용 차감 후 양의 순손익 거래 비율, 낙폭은 시간별 Mark 종가 기준이다.','',
        '재진입 대기는 없다. 일중 손실3% 이후 신규진입 차단 등 기존 계좌규칙은 유지한다. 완료4h 신호→다음1h 시가, 봉내 Mark청산→SL→TP 보수순서로 계산한다.','',
        '현재 과거 최적화 설정이며 미래 수익성 검증이 아니다. 유지증거금1%·현재필터 과거적용·봉내경로·실제체결 차이가 있다. BTC/XRP 한 봉의 교차자료 불일치는 보존하고 별도검증한다.','',
        '[전략 설정](../strategy.json) · [전체 요약/출처](summary.json) · [현재 전략의 후반·민감도 검증](strategy_validation.json).','',
        ('정리 전후8시나리오의 모든 거래·이벤트·시간별자산·일별원장·통계가 정확히 일치했다.' if reference else '정리 전후 동일성 확인: `./run.ps1 --verify`'), '']
    (output/'REPORT.md').write_text('\n'.join(lines),encoding='utf-8')
    print(f"Saved reports/REPORT.md ({summary['elapsed_seconds']:.2f}s)",flush=True)
    return summary
