"""Validate retained public sources and reviewed cross-feed discrepancies."""
from datetime import datetime, timezone
import hashlib
import json
import math
from numbers import Real
from pathlib import Path
DATA=Path(__file__).resolve().parent.parent/'data'
RAW_PATH=DATA/'binance_execution_1h_long.json'
DISCREPANCY_PROOF_PATH=DATA/'binance_4h_discrepancy_check.json'
DISCREPANCY_PROOF_SHA256='fc78e3e304bcf97921cde7b7b95b9f49a48ebc4ea9f549fea763447f11c62b27'
SIGNAL_INTERVALS={'1h':3600,'4h':14400}
INTERVAL_SECONDS={'4h':14400}
SYMBOL='BINANCE:BTCUSDT.P'
def utc(timestamp: int | float) -> str:
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat().replace('+00:00', 'Z')

def load_snapshot(path: Path, safety_delay_seconds: int=1800):
    """Exclude the last supplied bar plus bars not closed 30 min before retrieval.

    Provider delay is 15+ min, not a guaranteed upper bound. The buffer and tail
    removal reduce provisional-bar risk; historical feed revisions remain possible.
    Raw responses are preserved verbatim inside the envelope.
    """
    if safety_delay_seconds < 0:
        raise ValueError('safety delay must be nonnegative')
    raw_bytes = path.read_bytes()
    envelope = json.loads(raw_bytes)
    requested = envelope['requested']
    response = envelope['response']
    if response.get('success') is not True or requested['symbol'] != SYMBOL:
        raise ValueError('snapshot source failed or requested symbol differs')
    if response.get('symbol') != SYMBOL:
        raise ValueError('response symbol differs')
    interval = requested['interval']
    if response.get('interval') != interval:
        raise ValueError('response interval differs')
    step = INTERVAL_SECONDS[interval]
    fetched = datetime.fromisoformat(envelope['retrieved_at_utc'].replace('Z', '+00:00'))
    if fetched.tzinfo is None:
        raise ValueError('retrieval timestamp requires timezone')
    cutoff = fetched.timestamp() - safety_delay_seconds
    source_bars = response['bars']
    if len(source_bars) != response['count'] or not source_bars:
        raise ValueError('bar count mismatch or empty snapshot')
    bars, excluded, gaps = ([], [], [])
    previous_t = None
    for index, source in enumerate(source_bars):
        if any((isinstance(source.get(k), bool) or not isinstance(source.get(k), (int, float)) or (not math.isfinite(source[k])) for k in ('t', 'o', 'h', 'l', 'c', 'v'))):
            raise ValueError(f'non-finite/non-numeric OHLCV at {index}')
        t = source['t']
        if int(t) != t or t % step:
            raise ValueError(f'unaligned timestamp at {index}')
        if previous_t is not None:
            if t <= previous_t:
                raise ValueError('timestamps must be strictly increasing and unique')
            if t - previous_t != step:
                gaps.append({'after': int(previous_t), 'before': int(t), 'missing': int((t - previous_t) / step) - 1})
        previous_t = t
        if min((source[k] for k in ('o', 'h', 'l', 'c'))) <= 0 or source['v'] < 0:
            raise ValueError(f'nonpositive price/negative volume at {index}')
        if not source['l'] <= min(source['o'], source['c']) <= max(source['o'], source['c']) <= source['h']:
            raise ValueError(f'invalid candle geometry at {index}')
        if index == len(source_bars) - 1 or t + step > cutoff:
            excluded.append({'t': int(t), 'reason': 'last_returned_bar' if index == len(source_bars) - 1 else 'closure_buffer'})
        else:
            bars.append({'t': int(t), **{k: float(source[k]) for k in ('o', 'h', 'l', 'c', 'v')}})
    if gaps:
        raise ValueError(f'missing candles: {gaps[:5]}; do not bridge gaps silently')
    if len(bars) < 200:
        raise ValueError('too few completed bars for research')
    audit = {'symbol': SYMBOL, 'interval': interval, 'interval_seconds': step, 'source': envelope['source'], 'tool': envelope['tool'], 'retrieved_at_utc': envelope['retrieved_at_utc'], 'raw_file': path.name, 'raw_sha256': hashlib.sha256(raw_bytes).hexdigest(), 'requested_count': requested['count'], 'returned_count': len(source_bars), 'completed_count': len(bars), 'excluded': excluded, 'first_open_utc': utc(bars[0]['t']), 'last_open_utc': utc(bars[-1]['t']), 'last_close_utc': utc(bars[-1]['t'] + step), 'safety_delay_seconds': safety_delay_seconds, 'gaps': gaps, 'provider_notice': response.get('notice'), 'price_unit': 'USDT per BTC', 'volume_unit': 'BTC (provider base units)', 'time_semantics': 't is bar open; indicator known after t + interval_seconds'}
    return (bars, audit)
def _intervals(signal_interval_seconds, execution_interval_seconds):
    for name, value in (('signal', signal_interval_seconds), ('execution', execution_interval_seconds)):
        if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
            raise ValueError(f'{name} interval must be a positive integer number of seconds')
    if signal_interval_seconds % execution_interval_seconds:
        raise ValueError('signal interval must be an integer multiple of execution interval')
    return signal_interval_seconds // execution_interval_seconds

def _timestamps(bars, step, name):
    if not bars:
        raise ValueError(f'{name} bars must be nonempty')
    times = []
    for index, bar in enumerate(bars):
        timestamp = bar.get('t')
        if isinstance(timestamp, bool) or not isinstance(timestamp, Real) or (not math.isfinite(timestamp)) or (int(timestamp) != timestamp) or timestamp % step:
            raise ValueError(f'{name} candle {index} has an invalid or unaligned open timestamp')
        timestamp = int(timestamp)
        if times and timestamp - times[-1] != step:
            raise ValueError(f'{name} candles must be unique, increasing and contiguous at index {index}')
        times.append(timestamp)
    return times

def _layout(signal_bars, execution_bars, signal_interval_seconds, execution_interval_seconds):
    ratio = _intervals(signal_interval_seconds, execution_interval_seconds)
    signal_times = _timestamps(signal_bars, signal_interval_seconds, 'signal')
    execution_times = _timestamps(execution_bars, execution_interval_seconds, 'execution')
    source_start, source_end = (signal_times[0], signal_times[-1] + signal_interval_seconds)
    execution_start, execution_end = (execution_times[0], execution_times[-1] + execution_interval_seconds)
    if execution_start > source_start or execution_end < source_end:
        raise ValueError('execution candles must fully cover every signal candle, including both boundary candles')
    return (ratio, signal_times, execution_times)

def _ohlc(bar, name):
    for key in ('o', 'h', 'l', 'c'):
        value = bar.get(key)
        if isinstance(value, bool) or not isinstance(value, Real) or (not math.isfinite(value)):
            raise ValueError(f'{name} has invalid {key}')
    if bar['l'] <= 0 or not bar['l'] <= min(bar['o'], bar['c']) <= max(bar['o'], bar['c']) <= bar['h']:
        raise ValueError(f'{name} has invalid OHLC geometry')

def aggregate_audit(signal_bars, execution_bars, signal_interval_seconds, execution_interval_seconds=3600, *, price_tolerance=0.1, fail_on_price_mismatch=True):
    """Compare complete contract-price aggregation against source signal OHLC.

    The default tolerance is one observed BTCUSDT tick (0.1 USDT). A price
    difference above it fails by default. Volume is separately reported because
    provider volume conventions/rounding can differ; it never relaxes OHLC checks.
    Execution bars may include padding outside the source coverage; only source
    windows are compared, while all supplied timestamps must remain contiguous.
    """
    ratio, signal_times, execution_times = _layout(signal_bars, execution_bars, signal_interval_seconds, execution_interval_seconds)
    if isinstance(price_tolerance, bool) or not isinstance(price_tolerance, Real) or (not math.isfinite(price_tolerance)) or (price_tolerance < 0):
        raise ValueError('price_tolerance must be finite and nonnegative')
    if not isinstance(fail_on_price_mismatch, bool):
        raise ValueError('fail_on_price_mismatch must be boolean')
    differences = {key: 0.0 for key in ('o', 'h', 'l', 'c')}
    price_mismatches = []
    volume_missing = volume_different = volume_compared = 0
    max_volume_absolute = max_volume_relative = 0.0
    for source_index, source in enumerate(signal_bars):
        _ohlc(source, f'signal candle {source_index}')
        first = (signal_times[source_index] - execution_times[0]) // execution_interval_seconds
        group = execution_bars[first:first + ratio]
        if len(group) != ratio:
            raise ValueError('incomplete execution aggregation window')
        for offset, bar in enumerate(group):
            _ohlc(bar, f'execution candle {first + offset}')
        aggregate = {'o': group[0]['o'], 'h': max((b['h'] for b in group)), 'l': min((b['l'] for b in group)), 'c': group[-1]['c']}
        failures = {}
        for key in aggregate:
            delta = float(aggregate[key] - source[key])
            differences[key] = max(differences[key], abs(delta))
            if abs(delta) > price_tolerance and (not math.isclose(abs(delta), price_tolerance, rel_tol=1e-12, abs_tol=1e-08)):
                failures[key] = delta
        if failures:
            price_mismatches.append({'source_index': source_index, 'open_t': signal_times[source_index], 'execution_minus_signal': failures})
        if 'v' not in source or any(('v' not in bar for bar in group)):
            volume_missing += 1
        else:
            volumes = [source['v'], *(b['v'] for b in group)]
            if any((isinstance(v, bool) or not isinstance(v, Real) or (not math.isfinite(v)) or (v < 0) for v in volumes)):
                raise ValueError('volume, when supplied, must be finite and nonnegative')
            volume_compared += 1
            delta = abs(sum(volumes[1:]) - volumes[0])
            max_volume_absolute = max(max_volume_absolute, delta)
            if volumes[0] > 0:
                max_volume_relative = max(max_volume_relative, delta / volumes[0])
            volume_different += int(not math.isclose(sum(volumes[1:]), volumes[0], rel_tol=1e-08, abs_tol=1e-08))
    result = {'signal_interval_seconds': signal_interval_seconds, 'execution_interval_seconds': execution_interval_seconds, 'execution_bars_per_signal_bar': ratio, 'source_bars_checked': len(signal_bars), 'execution_bars_in_coverage': len(signal_bars) * ratio, 'coverage_start_t': signal_times[0], 'coverage_end_t': signal_times[-1] + signal_interval_seconds, 'price_tolerance_usdt': price_tolerance, 'price_mismatch_count': len(price_mismatches), 'maximum_absolute_price_difference': differences, 'price_mismatches': price_mismatches, 'volume_windows_compared': volume_compared, 'volume_windows_missing': volume_missing, 'volume_mismatch_count': volume_different, 'maximum_absolute_volume_difference': max_volume_absolute, 'maximum_relative_volume_difference_nonzero_source': max_volume_relative, 'volume_policy': 'reported separately; not used to excuse a price mismatch or to generate indicator signals', 'execution_management_policy': 'signals known at original candle close; stops and optional management evaluate every execution candle; hold duration preserved', 'orders_enabled': False}
    if price_mismatches and fail_on_price_mismatch:
        first = price_mismatches[0]
        raise ValueError(f"contract aggregation price mismatch in {len(price_mismatches)} of {len(signal_bars)} source bars; first source_index={first['source_index']}, differences={first['execution_minus_signal']}")
    return result
def _file_sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def _read_raw():
    payload = RAW_PATH.read_bytes()
    return (json.loads(payload.decode('utf-8')), hashlib.sha256(payload).hexdigest())

def _read_discrepancy_proof():
    payload = DISCREPANCY_PROOF_PATH.read_bytes()
    return (json.loads(payload.decode('utf-8-sig')), hashlib.sha256(payload).hexdigest())

def _reviewed_aggregation(signal_bars, execution, signal_step):
    """Retain one exact, independently corroborated cross-feed difference.

    This does not change a TradingView signal candle or the default one-tick
    tolerance. Every other timestamp, field, or value still fails closed.
    """
    audit = aggregate_audit(signal_bars, execution, signal_step, fail_on_price_mismatch=False)
    reviewed = []
    for mismatch in audit['price_mismatches']:
        source = signal_bars[mismatch['source_index']]
        delta = mismatch['execution_minus_signal']
        if signal_step != 14400 or mismatch['open_t'] != 1736856000 or set(delta) != {'h'} or (not math.isclose(source['h'], 97232.3, rel_tol=0, abs_tol=1e-08)) or (not math.isclose(delta['h'], 17.7, rel_tol=0, abs_tol=1e-08)):
            raise ValueError(f'unapproved contract aggregation price mismatch: {mismatch}')
        proof, proof_digest = _read_discrepancy_proof()
        if proof_digest != DISCREPANCY_PROOF_SHA256:
            raise ValueError('known-discrepancy corroboration file hash mismatch')
        row = proof.get('response')
        expected_url = 'https://fapi.binance.com/fapi/v1/klines?symbol=BTCUSDT&interval=4h&startTime=1736856000000&endTime=1736870399999&limit=1'
        if proof.get('source') != 'Binance public USD-M REST' or proof.get('url') != expected_url:
            raise ValueError('known-discrepancy proof must identify the exact official Binance 4h request')
        if not isinstance(row, list) or len(row) != 12 or row[0] != 1736856000000 or (row[6] != 1736870399999):
            raise ValueError('known-discrepancy proof must contain the exact completed 4h kline')
        proof_values = {key: float(row[column]) for column, key in enumerate(('o', 'h', 'l', 'c', 'v'), 1)}
        group = [b for b in execution if source['t'] <= b['t'] < source['t'] + 14400]
        aggregate = {'o': group[0]['o'], 'h': max((b['h'] for b in group)), 'l': min((b['l'] for b in group)), 'c': group[-1]['c'], 'v': sum((b['v'] for b in group))}
        if not math.isclose(aggregate['h'], 97250, rel_tol=0, abs_tol=1e-08) or any((not math.isclose(aggregate[key], proof_values[key], rel_tol=1e-12, abs_tol=1e-06) for key in aggregate)):
            raise ValueError('hourly execution aggregation differs from independently corroborated Binance 4h OHLCV')
        reviewed.append({'open_t': source['t'], 'source_index': mismatch['source_index'], 'field': 'h', 'tradingview_value': source['h'], 'execution_aggregate_value': aggregate['h'], 'difference_usdt': delta['h'], 'severity': 'documented_cross_feed_discrepancy', 'evidence_file': DISCREPANCY_PROOF_PATH.name, 'evidence_sha256': proof_digest, 'evidence_url': proof['url'], 'evidence_retrieved_at_utc': proof.get('retrieved_at_utc'), 'independent_binance_4h_ohlcv': proof_values, 'disposition': 'TradingView signal candle unchanged; original public hourly execution retained; no global tolerance relaxation'})
    audit.update(approved_discrepancy_count=len(reviewed), approved_discrepancies=reviewed, unapproved_discrepancy_count=0, status='pass_with_documented_discrepancy' if reviewed else 'pass_within_one_tick')
    return audit

def _validate_source_hashes(raw, interval, audit):
    snapshots = raw.get('tradingview_source_hashes')
    supplements = raw.get('funding_source_hashes')
    expected_supplements = {'binance_public_execution.json', 'binance_public_execution_4h.json'}
    if not isinstance(snapshots, dict) or set(snapshots) != set(SIGNAL_INTERVALS):
        raise ValueError('execution supplement must identify both TradingView source hashes')
    if not isinstance(supplements, dict) or set(supplements) != expected_supplements:
        raise ValueError('execution supplement funding source hashes do not identify the fixed public files')
    for timeframe, digest in snapshots.items():
        if digest != _file_sha256(DATA / f'tradingview_btcusdt_p_{timeframe}.json'):
            raise ValueError(f'TradingView source hash mismatch for {timeframe}')
    for filename, digest in supplements.items():
        if digest != _file_sha256(DATA / filename):
            raise ValueError(f'funding source hash mismatch for {filename}')
    if snapshots[interval] != audit['raw_sha256']:
        raise ValueError('loaded signal audit does not match execution supplement provenance')

def _normalize_klines(rows, label, with_volume):
    if not isinstance(rows, list) or not rows:
        raise ValueError(f'{label} klines must be a nonempty list')
    bars = []
    for index, row in enumerate(rows):
        if not isinstance(row, list) or len(row) < 7 or isinstance(row[0], bool):
            raise ValueError(f'malformed {label} kline {index}')
        try:
            milliseconds = float(row[0])
            values = {key: float(row[column]) for column, key in enumerate(('o', 'h', 'l', 'c'), 1)}
            close_ms = float(row[6])
            volume = float(row[5]) if with_volume else None
        except (TypeError, ValueError) as exc:
            raise ValueError(f'nonnumeric {label} kline {index}') from exc
        if not math.isfinite(milliseconds) or milliseconds % 3600000 or close_ms != milliseconds + 3599999:
            raise ValueError(f'invalid hourly boundary in {label} kline {index}')
        timestamp = int(milliseconds) // 1000
        if bars and timestamp - bars[-1]['t'] != 3600:
            raise ValueError(f'{label} klines must be contiguous, unique and ordered')
        if not all((math.isfinite(v) for v in values.values())) or values['l'] <= 0 or (not values['l'] <= min(values['o'], values['c']) <= max(values['o'], values['c']) <= values['h']):
            raise ValueError(f'invalid {label} OHLC at kline {index}')
        bar = {'t': timestamp, **values}
        if with_volume:
            if not math.isfinite(volume) or volume < 0:
                raise ValueError(f'invalid {label} volume at kline {index}')
            bar['v'] = volume
        bars.append(bar)
    return bars

def _funding_for_window(events, start_t, end_t):
    if not isinstance(events, list):
        raise ValueError('funding ledger must be a list')
    result, times = ([], [])
    for event in events:
        try:
            timestamp = float(event['fundingTime'])
            rate, price = (float(event['fundingRate']), float(event['markPrice']))
        except (KeyError, TypeError, ValueError) as exc:
            raise ValueError('malformed execution funding record') from exc
        if event.get('symbol') != 'BTCUSDT' or not all((math.isfinite(v) for v in (timestamp, rate, price))) or int(timestamp) != timestamp or (price <= 0) or (timestamp % 28800000 > 1000):
            raise ValueError('invalid execution funding symbol, timestamp, rate or mark price')
        boundary = int(timestamp) // 28800000 * 28800
        if start_t <= boundary < end_t:
            times.append(boundary)
            result.append(dict(event))
    expected = list(range((start_t + 28799) // 28800 * 28800, end_t, 28800))
    if times != expected:
        raise ValueError('execution funding must cover every 8h boundary exactly once in chronological order')
    return result

def load_btc_context(interval='4h'):
    if interval!='4h': raise ValueError('only the current 4h strategy is supported')
    source,audit=load_snapshot(DATA/'tradingview_btcusdt_p_4h.json')
    raw,raw_hash=_read_raw()
    if raw.get('symbol')!='BTCUSDT' or raw.get('interval')!='1h': raise ValueError('BTC execution symbol/interval mismatch')
    _validate_source_hashes(raw,interval,audit)
    bars=_normalize_klines(raw.get('contract_klines'),'contract',True)
    marks=_normalize_klines(raw.get('mark_klines'),'mark',False)
    if [b['t'] for b in bars]!=[b['t'] for b in marks]: raise ValueError('BTC contract/Mark coverage mismatch')
    if raw.get('start_t')!=bars[0]['t'] or raw.get('end_t')!=bars[-1]['t']+3600: raise ValueError('BTC execution window mismatch')
    start,end=source[0]['t'],source[-1]['t']+14400
    bars=[b for b in bars if start<=b['t']<end]; marks=[b for b in marks if start<=b['t']<end]
    funding=_funding_for_window(raw.get('funding'),start,end)
    audit.update(execution_raw_file=RAW_PATH.name,execution_raw_sha256=raw_hash,
                 execution_source=raw.get('source'),execution_retrieved_at_utc=raw.get('retrieved_at_utc'),
                 execution_aggregation=_reviewed_aggregation(source,bars,14400),execution_funding_coverage_verified=True)
    return {'signal_bars':source,'execution_bars':bars,'execution_marks':{b['t']:b for b in marks},'funding':funding,'audit':audit}
