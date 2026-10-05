import fs from 'node:fs/promises';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
// Resolve the installed bundled package; no repository package installation is needed.
const [input, output, modules, previewDir] = process.argv.slice(2);
const require = createRequire(path.join(modules, 'trading-resolver.cjs'));
const {Workbook, SpreadsheetFile} = await import(pathToFileURL(require.resolve('@oai/artifact-tool')).href);
const data=JSON.parse(await fs.readFile(input,'utf8'));
if(!/^[a-f0-9]{12}$/.test(data.current_strategy_version??'') || typeof data.current_strategy_name!=='string')
  throw new Error('Current strategy version metadata is required for history export');
const wb=Workbook.create();
const summary=wb.worksheets.add('요약');
const trades=wb.worksheets.add('거래내역');
const fills=wb.worksheets.add('체결원장');
const funding=wb.worksheets.add('펀딩');
const safe=v=>typeof v==='string'&&/^[=+\-@]/.test(v)?"'"+v:v??null;
const excelDate=ms=>ms==null?null:new Date(ms+9*3600000); // Displayed wall clock is KST, labeled explicitly.
const money='#,##0.000000;[Red](#,##0.000000);0.000000';
function table(sheet,title,source,headers,rows,name) {
  sheet.showGridLines=false;
  sheet.getRangeByIndexes(0,0,Math.max(7,rows.length+5),headers.length).format.font={name:'맑은 고딕',size:10,color:'#202630'};
  sheet.getRange('A2').values=[[title]];
  sheet.getRange('A2').format.font={size:15,bold:true};
  sheet.getRange('A3').values=[[source]];
  sheet.getRange('A3').format.font={size:9,color:'#64748B'};
  sheet.getRangeByIndexes(4,0,1,headers.length).values=[headers];
  const head=sheet.getRangeByIndexes(4,0,1,headers.length);
  head.format={fill:'#263344',font:{bold:true,color:'#FFFFFF'},rowHeight:30,horizontalAlignment:'center',verticalAlignment:'center'};
  if(rows.length) sheet.getRangeByIndexes(5,0,rows.length,headers.length).values=rows.map(r=>r.map(safe));
  const area=sheet.getRangeByIndexes(4,0,Math.max(2,rows.length+1),headers.length);
  area.format.columnWidth=19;
  area.format.rowHeight=24;
  sheet.freezePanes.freezeRows(5);
  if(rows.length) sheet.tables.add(area,true,name);
}
const tradeRows=data.trades.map(r=>[
  r.id,r.symbol,r.direction,excelDate(r.opened_ms),excelDate(r.closed_ms),r.quantity,r.entry,r.exit,
  r.leverage,r.gross,r.fees,r.funding,null,r.margin_return,r.hours,r.status,r.strategy_version,
  r.minimum_override?'적용':'기본',r.stop==null?null:Number(r.stop),r.target==null?null:Number(r.target),r.exit_reason
]);
table(trades,'완료 거래 내역','프로그램 진입 확인 거래. 순손익은 USDT 비용·펀딩이 확인된 거래만 계산합니다.',
 ['거래 ID','종목','방향','진입 시각 (KST)','청산 시각 (KST)','체결 수량','진입 평균가','청산 평균가','레버리지','실현손익 (USDT)','수수료 (USDT)','펀딩 (USDT)','순손익 (USDT)','증거금 수익률','보유 시간','확인 상태','전략 버전','최소 주문 보정','초기 SL','초기 TP','종료 사유'],tradeRows,'ClosedTrades');
const n=Math.max(6,tradeRows.length+5);
trades.getRange('A:A').format.columnWidth=34;
trades.getRange('D:E').format.columnWidth=24;
trades.getRange('P:P').format.columnWidth=28;
trades.getRange('U:U').format.columnWidth=38;
trades.getRange('D6:E'+n).setNumberFormat('yyyy-mm-dd hh:mm:ss');
trades.getRange('F6:M'+n).setNumberFormat(money);
trades.getRange('I6:I'+n).setNumberFormat('0"x"');
trades.getRange('N6:N'+n).setNumberFormat('0.00%');
trades.getRange('O6:O'+n).setNumberFormat('0.00');
trades.getRange('S6:T'+n).setNumberFormat(money);
if(tradeRows.length) trades.getRange('M6:M'+n).formulas=data.trades.map((r,i)=>['=IF(P'+(i+6)+'="확인 완료",J'+(i+6)+'-K'+(i+6)+'+L'+(i+6)+',"")']);
table(fills,'체결 원장','Source: Binance /fapi/v1/userTrades. BTC·XRP·SOL·HYPE의 프로그램·수동·외부 체결을 모두 보관합니다.',
 ['종목','체결 ID','주문 ID','체결 시각 (KST)','매수/매도','포지션 모드','체결가','수량','거래대금 (USDT)','실현손익 (USDT)','수수료','수수료 자산','메이커'],data.fills.map(f=>[
 f.symbol,String(f.id),String(f.orderId),excelDate(f.time),f.side,f.positionSide,Number(f.price),Number(f.qty),
 Number(f.quoteQty??Number(f.price)*Number(f.qty)),Number(f.realizedPnl),Number(f.commission),f.commissionAsset,f.maker?'예':'아니오'
]),'RawFills');
const fn=Math.max(6,data.fills.length+5);
fills.getRange('D:D').format.columnWidth=24;
fills.getRange('D6:D'+fn).setNumberFormat('yyyy-mm-dd hh:mm:ss');
fills.getRange('G6:K'+fn).setNumberFormat(money);
table(funding,'펀딩 원장','Source: Binance /fapi/v1/income (FUNDING_FEE). 양수는 수령, 음수는 지급입니다.',
 ['종목','거래 ID','발생 시각 (KST)','금액','자산'],data.funding.map(f=>[f.symbol,String(f.tranId),excelDate(f.time),Number(f.income),f.asset]),'FundingRecords');
funding.getRange('C:C').format.columnWidth=24;
funding.getRange('C6:C'+Math.max(6,data.funding.length+5)).setNumberFormat('yyyy-mm-dd hh:mm:ss');
funding.getRange('D6:D'+Math.max(6,data.funding.length+5)).setNumberFormat(money);

summary.showGridLines=false;
summary.tabColor='#263344';
summary.getRange('A1:G36').format.font={name:'맑은 고딕',size:10,color:'#202630'};
summary.getRange('A:A').format.columnWidth=29;
summary.getRange('B:B').format.columnWidth=24;
summary.getRange('D:G').format.columnWidth=23;
summary.getRange('A2').values=[['거래 요약: 전체 버전과 현재 전략']];
summary.getRange('A2').format.font={size:15,bold:true};
summary.getRange('A4:B4').values=[['전체 버전 누적 통계','값']];
summary.getRange('A4:B4').format={fill:'#263344',font:{bold:true,color:'#FFFFFF'},rowHeight:28};
summary.getRange('A5:A14').values=[
 ['전체 버전 확인 거래 수'],['전체 버전 승률'],['전체 버전 순손익 (USDT)'],['수수료 합계 (USDT)'],['펀딩 합계 (USDT)'],
 ['평균 순손익 (USDT)'],['Profit factor'],['미확인 완료 기록 수'],['원시 체결 수'],['엑셀 저장 시각 (KST)']];
const nets="'거래내역'!M6:M"+n;
summary.getRange('B5').formulas=[['=COUNT('+nets+')']];
summary.getRange('B6').formulas=[['=IF(B5=0,"자료 없음",COUNTIF('+nets+',">0")/B5)']];
summary.getRange('B7').formulas=[['=IF(B5=0,"자료 없음",SUM('+nets+'))']];
summary.getRange('B8').formulas=[['=IF(B5=0,"자료 없음",SUMIF(\'거래내역\'!P6:P'+n+',"확인 완료",\'거래내역\'!K6:K'+n+'))']];
summary.getRange('B9').formulas=[['=IF(B5=0,"자료 없음",SUMIF(\'거래내역\'!P6:P'+n+',"확인 완료",\'거래내역\'!L6:L'+n+'))']];
summary.getRange('B10').formulas=[['=IF(B5=0,"자료 없음",B7/B5)']];
summary.getRange('B11').formulas=[['=IF(B5=0,"자료 없음",IF(SUMIF('+nets+',"<0")=0,"손실 거래 없음",SUMIF('+nets+',">0")/-SUMIF('+nets+',"<0")))']];
summary.getRange('B12').formulas=[['='+data.trades.length+'-B5']];
summary.getRange('B13').values=[[data.fills.length]];
summary.getRange('B14').values=[[excelDate(data.generated_ms)]];
summary.getRange('B6').setNumberFormat('0.00%');
summary.getRange('B7:B10').setNumberFormat(money);
summary.getRange('B11').setNumberFormat('0.00');
summary.getRange('B14').setNumberFormat('yyyy-mm-dd hh:mm:ss');
summary.getRange('D4:G4').values=[['기록 범위',null,null,null]];
summary.getRange('D4:G4').format={fill:'#EFF3F7',font:{bold:true}};
summary.getRange('D5').values=[['거래소 조회 시작: '+(data.meta.sync_start?new Date(data.meta.sync_start).toISOString():'아직 조회하지 않음')]];
summary.getRange('D6').values=[['마지막 조회 완료: '+(data.meta.sync_end?new Date(data.meta.sync_end).toISOString():'아직 조회하지 않음')]];
summary.getRange('D8').values=[[data.meta.sync_error?'최근 동기화 실패: 내역이 최신이 아닙니다.':'동기화 범위 안에서 수집한 내역입니다.']];
summary.getRange('D10').values=[['위 통계는 과거 전략 버전도 포함합니다. 현재 전략은 아래에서 구분합니다.']];
summary.getRange('D11').values=[['순손익 = 실현손익 - 진입/청산 수수료 + 보유기간 펀딩']];
summary.getRange('D12').values=[['수수료 환산이나 체결 확인이 필요한 거래는 통계에서 제외합니다.']];
summary.getRange('D14').values=[['최초 조회는 최근 89일. 이전에 저장한 내역은 계속 보관합니다.']];
summary.getRange('D15').values=[['엑셀 수동 편집은 다음 저장 시 덮어씁니다.']];
summary.getRange('D16').values=[['거래가 없으면 빈 원장이며, 예시 거래를 추가하지 않습니다.']];
summary.getRange('A18:B18').values=[['계좌 전체 원장 합계','USDT']];
summary.getRange('A18:B18').format={fill:'#263344',font:{bold:true,color:'#FFFFFF'},rowHeight:28};
summary.getRange('A19:A23').values=[['실현손익 합계'],['USDT 수수료'],['USDT 펀딩'],['USDT 현금흐름'],['비USDT 수수료 체결 수']];
summary.getRange('B19').formulas=[['=SUM(\'체결원장\'!J6:J'+fn+')']];
summary.getRange('B20').formulas=[['=SUMIF(\'체결원장\'!L6:L'+fn+',"USDT",\'체결원장\'!K6:K'+fn+')']];
const fundEnd=Math.max(6,data.funding.length+5);
summary.getRange('B21').formulas=[['=SUMIF(\'펀딩\'!E6:E'+fundEnd+',"USDT",\'펀딩\'!D6:D'+fundEnd+')']];
summary.getRange('B22').formulas=[['=B19-B20+B21']];
summary.getRange('B23').formulas=[['=COUNTIFS(\'체결원장\'!L6:L'+fn+',"< >USDT",\'체결원장\'!K6:K'+fn+',">0")'.replace('< >','<>')]];
summary.getRange('B19:B22').setNumberFormat(money);
summary.getRange('D19').values=[['계좌 합계에는 수동·외부·전략 귀속 미확인 거래가 포함됩니다.']];
summary.getRange('D20').values=[['현금흐름은 비USDT 비용 환산 전이며 포지션 전체 수익률이 아닙니다.']];
summary.getRange('D21').values=[['위의 전략 통계와 구분해서 확인하세요.']];
// Keep every archived row and its fingerprint. Current statistics filter the same ledger.
summary.getRange('A26:G26').merge();
summary.getRange('A26').values=[['현재 BASE 전략 성적']];
summary.getRange('A26:G26').format={fill:'#263344',font:{bold:true,color:'#FFFFFF'},rowHeight:28};
summary.getRange('A27:G27').merge();
summary.getRange('A27').values=[[safe(data.current_strategy_name)]];
summary.getRange('A27:G27').format.rowHeight=24;
summary.getRange('A28:B28').values=[['현재 설정과 같은 버전','값']];
summary.getRange('A28:B28').format={fill:'#EFF3F7',font:{bold:true},rowHeight:28};
summary.getRange('D28:E28').values=[['현재 버전 해시',data.current_strategy_version]];
summary.getRange('A29:A36').values=[['현재 확인 거래 수'],['현재 승률'],['현재 순손익 (USDT)'],
 ['현재 수수료 (USDT)'],['현재 펀딩 (USDT)'],['현재 평균 순손익 (USDT)'],['현재 Profit factor'],['현재 미확인 완료 기록 수']];
const versionRange="'거래내역'!Q6:Q"+n, statusRange="'거래내역'!P6:P"+n;
const currentCriteria=versionRange+',$E$28,'+statusRange+',"확인 완료"';
summary.getRange('B29').formulas=[['=COUNTIFS('+currentCriteria+')']];
summary.getRange('B30').formulas=[['=IF(B29=0,"자료 없음",COUNTIFS('+currentCriteria+','+nets+',">0")/B29)']];
summary.getRange('B31').formulas=[['=IF(B29=0,"자료 없음",SUMIFS('+nets+','+currentCriteria+'))']];
summary.getRange('B32').formulas=[['=IF(B29=0,"자료 없음",SUMIFS(\'거래내역\'!K6:K'+n+','+currentCriteria+'))']];
summary.getRange('B33').formulas=[['=IF(B29=0,"자료 없음",SUMIFS(\'거래내역\'!L6:L'+n+','+currentCriteria+'))']];
summary.getRange('B34').formulas=[['=IF(B29=0,"자료 없음",B31/B29)']];
const currentPositive='SUMIFS('+nets+','+currentCriteria+','+nets+',">0")';
const currentNegative='SUMIFS('+nets+','+currentCriteria+','+nets+',"<0")';
summary.getRange('B35').formulas=[['=IF(B29=0,"자료 없음",IF('+currentNegative+'=0,"손실 거래 없음",'+currentPositive+'/-'+currentNegative+'))']];
summary.getRange('B36').formulas=[['=COUNTIF('+versionRange+',$E$28)-B29']];
summary.getRange('B29').setNumberFormat('#,##0');
summary.getRange('B30').setNumberFormat('0.00%');
summary.getRange('B31:B34').setNumberFormat(money);
summary.getRange('B35').setNumberFormat('0.00');
summary.getRange('B36').setNumberFormat('#,##0');
for(const [range,text] of [
 ['D29:G30','현재 전략의 확인 거래가 0건이면 승률·손익은 자료 없음으로 표시합니다.'],
 ['D32:G33','과거 전략 거래와 버전 해시는 원장에 그대로 보관합니다. 현재 성과로 합산하지 않습니다.'],
 ['D35:G36','완료 포지션의 실제 체결·USDT 비용·펀딩을 확인한 성적이며 저장 연구 결과를 넣지 않습니다.']]) {
 summary.getRange(range).merge();summary.getRange(range.split(':')[0]).values=[[text]];
 summary.getRange(range).format.wrapText=true;summary.getRange(range).format.verticalAlignment='center';
 summary.getRange(range).format.rowHeight=24;
}
wb.recalculate();
const inspected=await wb.inspect({kind:'match',searchTerm:'#REF!|#DIV/0!|#VALUE!|#NAME\\?|#NUM!|#SPILL!|#CALC!',options:{useRegex:true,maxResults:20},maxChars:1500});
if(/"matchCount"\s*:\s*[1-9]/.test(inspected.ndjson)) throw new Error('Workbook formula errors');
if(previewDir) {
  await fs.mkdir(previewDir,{recursive:true});
  for(const sheet of [summary,trades,fills,funding]) {
    const preview=await wb.render({sheetName:sheet.name,range:sheet===summary?'A1:G36':sheet===funding?'A1:E9':'A1:H9',scale:1.5,format:'png'});
    await fs.writeFile(path.join(previewDir,sheet.name+'.png'),new Uint8Array(await preview.arrayBuffer()));
  }
  const check=await wb.inspect({kind:'table',range:'요약!A4:B14',include:'values,formulas',tableMaxRows:11,tableMaxCols:2,maxChars:3000});
  await fs.writeFile(path.join(previewDir,'checks.jsonl'),check.ndjson);
  const currentCheck=await wb.inspect({kind:'table',range:'요약!A28:B36',include:'values,formulas',tableMaxRows:9,tableMaxCols:2,maxChars:4000});
  await fs.writeFile(path.join(previewDir,'current-version-checks.jsonl'),currentCheck.ndjson);
}
const xlsx=await SpreadsheetFile.exportXlsx(wb);
await xlsx.save(output);
console.log(JSON.stringify({ok:true,rows:data.trades.length,fills:data.fills.length}));

