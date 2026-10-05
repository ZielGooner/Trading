using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TradingLauncher
{
    public enum ChartFeedState { Connecting, Live, Stale, Reconnecting, Stopped }

    public sealed class ChartCandle
    {
        public long Time { get; set; } // Exchange candle open, Unix seconds.
        public long CloseTime { get; set; } // Exchange candle close, Unix milliseconds.
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }
        public bool Closed { get; set; }

        internal ChartCandle Copy() { return (ChartCandle)MemberwiseClone(); }
    }

    public sealed class ChartFeedEventArgs : EventArgs
    {
        public string Symbol { get; set; }
        public string Interval { get; set; }
        public long Generation { get; set; }
        public ChartFeedState State { get; set; }
        public IList<ChartCandle> Candles { get; set; }
        public long LastEventTime { get; set; } // Last valid WS exchange event, Unix ms; zero before WS.
        public DateTime ReceivedUtc { get; set; }
        public string Message { get; set; }
        public bool IsSnapshot { get; set; }
    }

    public interface IChartFeed : IDisposable
    {
        event EventHandler<ChartFeedEventArgs> Updated;
        long Select(string symbol, string interval);
    }

    // Injected by source-level tests; the public UI contract contains no HTTP/authentication API.
    internal interface IBinanceChartTransport
    {
        Task<string> GetAsync(Uri address, CancellationToken token);
        Task RunStreamAsync(Uri address, Action connected, Action<string> message, CancellationToken token);
    }

    // Only public market data. This component has no dependency on the trading/account worker.
    public sealed class BinanceChartFeed : IChartFeed
    {
        private static readonly string[] Symbols = { "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT" };
        private static readonly string[] Intervals = { "15m", "1h", "4h", "1d", "1w" };
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal const int CandleLimit = 200;
        private readonly object sync = new object();
        private readonly IBinanceChartTransport transport;
        private Selection selected;
        private long generation;
        private bool disposed;

        private sealed class Selection
        {
            internal string Symbol, Interval;
            internal long Generation, LastEventTime;
            internal int StreamCycle;
            internal DateTime LastReceivedUtc;
            internal readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            internal readonly object Sync = new object();
            internal readonly SortedDictionary<long, ChartCandle> Candles = new SortedDictionary<long, ChartCandle>();
        }

        public event EventHandler<ChartFeedEventArgs> Updated;
        public BinanceChartFeed() : this(new BinanceChartTransport()) { }
        internal BinanceChartFeed(IBinanceChartTransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            this.transport = transport;
        }

        public long Select(string symbol, string interval)
        {
            ValidatePair(symbol, interval);
            Selection old, next;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("BinanceChartFeed");
                old = selected;
                next = new Selection { Symbol = symbol, Interval = interval, Generation = ++generation };
                selected = next;
            }
            Cancel(old); // Aborts the previous request/socket immediately, without blocking the UI.
            Task.Run(delegate { return RunAsync(next); });
            return next.Generation;
        }

        public void Dispose()
        {
            Selection old;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                old = selected;
                selected = null;
            }
            Cancel(old);
        }

        private static void Cancel(Selection value)
        {
            if (value == null) return;
            try { value.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        }

        private bool Current(Selection value)
        {
            lock (sync) return !disposed && selected == value && !value.Cancel.IsCancellationRequested;
        }

        internal static void ValidatePair(string symbol, string interval)
        {
            if (Array.IndexOf(Symbols, symbol) < 0 || Array.IndexOf(Intervals, interval) < 0)
                throw new ArgumentException("지원하지 않는 차트 종목 또는 시간봉입니다.");
        }

        internal static Uri RestAddress(string symbol, string interval)
        {
            ValidatePair(symbol, interval);
            return new Uri("https://fapi.binance.com/fapi/v1/klines?symbol=" + symbol + "&interval=" + interval + "&limit=200");
        }

        internal static Uri StreamAddress(string symbol, string interval)
        {
            ValidatePair(symbol, interval);
            // Routed /market is required for klines by Binance's current stream mapping.
            return new Uri("wss://fstream.binance.com/market/ws/" + symbol.ToLowerInvariant() + "@kline_" + interval);
        }

        private async Task RunAsync(Selection value)
        {
            int failures = 0, cycleNumber = 0;
            CancellationToken token = value.Cancel.Token;
            try
            {
                while (Current(value))
                {
                    int currentCycle = ++cycleNumber;
                    lock (value.Sync) value.StreamCycle = currentCycle;
                    Publish(value, failures == 0 ? ChartFeedState.Connecting : ChartFeedState.Reconnecting,
                        false, failures == 0 ? "공개 시세 연결 중" : "시세 다시 연결 중");
                    try
                    {
                        string json = await transport.GetAsync(RestAddress(value.Symbol, value.Interval), token).ConfigureAwait(false);
                        if (!Current(value)) return;
                        IList<ChartCandle> snapshot = ParseSnapshot(json, value.Interval, DateTime.UtcNow);
                        lock (value.Sync)
                        {
                            value.Candles.Clear();
                            foreach (ChartCandle candle in snapshot) value.Candles.Add(candle.Time, candle);
                            value.LastEventTime = 0;
                            value.LastReceivedUtc = DateTime.UtcNow;
                        }
                        Publish(value, failures == 0 ? ChartFeedState.Connecting : ChartFeedState.Reconnecting,
                            true, "최신 봉 수신 · 실시간 연결 중");
                        using (var cycle = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            Task stream = transport.RunStreamAsync(StreamAddress(value.Symbol, value.Interval),
                                delegate { }, delegate(string payload) { Receive(value, currentCycle, payload); }, cycle.Token);
                            try
                            {
                                while (!stream.IsCompleted)
                                {
                                    await Task.WhenAny(stream, Task.Delay(500, token)).ConfigureAwait(false);
                                    token.ThrowIfCancellationRequested();
                                    DateTime last;
                                    lock (value.Sync) last = value.LastReceivedUtc;
                                    if (!stream.IsCompleted && DateTime.UtcNow - last >= TimeSpan.FromSeconds(20))
                                    {
                                        lock (value.Sync) value.StreamCycle = 0;
                                        Publish(value, ChartFeedState.Stale, false, "시세 수신 지연 · 다시 연결합니다");
                                        cycle.Cancel();
                                        throw new IOException("market data idle");
                                    }
                                }
                                await stream.ConfigureAwait(false);
                                throw new IOException("market stream ended");
                            }
                            finally
                            {
                                lock (value.Sync) if (value.StreamCycle == currentCycle) value.StreamCycle = 0;
                                cycle.Cancel();
                                // Observe transport failures even when cancellation/idle wins first.
                                Task observe = stream.ContinueWith(delegate(Task t) { var ignored = t.Exception; },
                                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                            }
                        }
                    }
                    catch (OperationCanceledException) { if (token.IsCancellationRequested) return; }
                    catch (Exception) { if (!Current(value)) return; }
                    if (!Current(value)) return;
                    lock (value.Sync) if (value.LastEventTime > 0) failures = 0;
                    failures = Math.Min(failures + 1, 6);
                    Publish(value, ChartFeedState.Reconnecting, false, "시세 연결 끊김 · 다시 연결 중");
                    int seconds = Math.Min(30, 1 << (failures - 1));
                    await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { value.Cancel.Dispose(); }
        }

        private void Receive(Selection value, int cycle, string json)
        {
            if (!Current(value)) return;
            lock (value.Sync) if (value.StreamCycle != cycle) return;
            long eventTime;
            ChartCandle candle = ParseUpdate(json, value.Symbol, value.Interval, out eventTime);
            if (candle == null) return;
            DateTime receivedUtc = DateTime.UtcNow;
            double age = (receivedUtc - Epoch).TotalMilliseconds - eventTime;
            if (age > 20000 || age < -5000)
            {
                Publish(value, ChartFeedState.Stale, false, "시세 시간 불일치 · 다시 연결합니다");
                throw new InvalidDataException("stale or future market event");
            }
            lock (value.Sync)
            {
                if (value.StreamCycle != cycle) return;
                if (eventTime < value.LastEventTime) return;
                ChartCandle old;
                if (value.Candles.TryGetValue(candle.Time, out old) && old.Closed && !candle.Closed) return;
                if (value.Candles.Count > 0)
                {
                    ChartCandle latest = null;
                    foreach (ChartCandle item in value.Candles.Values) latest = item;
                    if (candle.Time < latest.Time) return;
                    if (candle.Time > latest.Time && (candle.Time != latest.Time + IntervalSeconds(value.Interval) || !latest.Closed))
                        throw new InvalidDataException("missed candle close; REST backfill required");
                }
                value.Candles[candle.Time] = candle;
                while (value.Candles.Count > CandleLimit)
                {
                    long first = 0;
                    foreach (long key in value.Candles.Keys) { first = key; break; }
                    value.Candles.Remove(first);
                }
                value.LastEventTime = eventTime;
                value.LastReceivedUtc = receivedUtc;
            }
            Publish(value, ChartFeedState.Live, false, "Binance 실시간", cycle);
        }

        private void Publish(Selection value, ChartFeedState state, bool snapshot, string message, int streamCycle = 0)
        {
            if (!Current(value)) return;
            var candles = new List<ChartCandle>();
            var args = new ChartFeedEventArgs { Symbol = value.Symbol, Interval = value.Interval,
                Generation = value.Generation, State = state, Message = message, IsSnapshot = snapshot };
            lock (value.Sync)
            {
                if (streamCycle != 0 && value.StreamCycle != streamCycle) return;
                foreach (ChartCandle candle in value.Candles.Values) candles.Add(candle.Copy());
                args.LastEventTime = value.LastEventTime;
                args.ReceivedUtc = value.LastReceivedUtc;
            }
            args.Candles = candles.AsReadOnly();
            EventHandler<ChartFeedEventArgs> handler = Updated;
            if (handler != null && Current(value))
            {
                // Subscribers dispatch to the UI and reject any already-queued older generation.
                lock (value.Sync)
                {
                    if (streamCycle != 0 && value.StreamCycle != streamCycle) return;
                    try { handler(this, args); } catch (Exception) { }
                }
            }
        }

        internal static IList<ChartCandle> ParseSnapshot(string json, string interval, DateTime utcNow)
        {
            object parsed = Serializer().DeserializeObject(json);
            IEnumerable rows = parsed as IEnumerable;
            if (rows == null || parsed is string || parsed is IDictionary) throw new InvalidDataException("invalid klines");
            var result = new List<ChartCandle>();
            long previous = -1, now = (long)(utcNow.ToUniversalTime() - Epoch).TotalMilliseconds;
            foreach (object value in rows)
            {
                object[] row = value as object[];
                if (row == null || row.Length < 7) throw new InvalidDataException("invalid kline row");
                long openTime = Integer(row[0]);
                if (openTime % 1000 != 0) throw new InvalidDataException("invalid open timestamp");
                var candle = new ChartCandle { Time = openTime / 1000, Open = Number(row[1]), High = Number(row[2]),
                    Low = Number(row[3]), Close = Number(row[4]), Volume = Number(row[5]), CloseTime = Integer(row[6]) };
                candle.Closed = candle.CloseTime < now;
                ValidateCandle(candle, interval);
                if (candle.Time <= previous || (previous >= 0 && candle.Time != previous + IntervalSeconds(interval)))
                    throw new InvalidDataException("unordered or missing klines");
                previous = candle.Time;
                result.Add(candle);
                if (result.Count > CandleLimit) throw new InvalidDataException("too many klines");
            }
            if (result.Count == 0) throw new InvalidDataException("empty klines");
            return result;
        }

        internal static ChartCandle ParseUpdate(string json, string symbol, string interval, out long eventTime)
        {
            eventTime = 0;
            IDictionary<string, object> row = Map(Serializer().DeserializeObject(json));
            if (row.ContainsKey("data")) row = Map(row["data"]);
            if (!row.ContainsKey("e") || Convert.ToString(row["e"], CultureInfo.InvariantCulture) != "kline") return null;
            if (Convert.ToString(row["s"], CultureInfo.InvariantCulture) != symbol) return null;
            var kline = Map(row["k"]);
            if (Convert.ToString(kline["s"], CultureInfo.InvariantCulture) != symbol || Convert.ToString(kline["i"], CultureInfo.InvariantCulture) != interval)
                return null;
            long openTime = Integer(kline["t"]);
            if (openTime % 1000 != 0) throw new InvalidDataException("invalid open timestamp");
            var candle = new ChartCandle { Time = openTime / 1000, CloseTime = Integer(kline["T"]),
                Open = Number(kline["o"]), High = Number(kline["h"]), Low = Number(kline["l"]),
                Close = Number(kline["c"]), Volume = Number(kline["v"]), Closed = Flag(kline["x"]) };
            eventTime = Integer(row["E"]);
            if (eventTime <= 0) throw new InvalidDataException("invalid event time");
            ValidateCandle(candle, interval);
            return candle;
        }

        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 512 * 1024, RecursionLimit = 32 }; }
        private static IDictionary<string, object> Map(object value)
        {
            var row = value as IDictionary<string, object>;
            if (row == null) throw new InvalidDataException("invalid market object");
            return row;
        }
        private static long Integer(object value)
        {
            if (value == null || value is bool) throw new InvalidDataException("invalid timestamp type");
            decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (number != decimal.Truncate(number)) throw new InvalidDataException("fractional timestamp");
            return decimal.ToInt64(number);
        }
        private static double Number(object value)
        {
            if (value == null || value is bool) throw new InvalidDataException("invalid market value type");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("nonfinite market value");
            return number;
        }
        private static bool Flag(object value)
        {
            if (!(value is bool)) throw new InvalidDataException("invalid candle close flag");
            return (bool)value;
        }
        internal static long IntervalSeconds(string interval)
        {
            switch (interval)
            {
                case "15m": return 900;
                case "1h": return 3600;
                case "4h": return 14400;
                case "1d": return 86400;
                case "1w": return 604800;
                default: throw new ArgumentException("unsupported interval");
            }
        }
        private static void ValidateCandle(ChartCandle candle, string interval)
        {
            long seconds = IntervalSeconds(interval);
            bool aligned = interval == "1w" ? (candle.Time - 345600) % seconds == 0 : candle.Time % seconds == 0;
            if (candle.Time <= 0 || candle.CloseTime != candle.Time * 1000 + IntervalSeconds(interval) * 1000 - 1 ||
                !aligned ||
                candle.Open <= 0 || candle.Close <= 0 || candle.Low <= 0 || candle.High < Math.Max(candle.Open, candle.Close) ||
                candle.Low > Math.Min(candle.Open, candle.Close) || candle.High < candle.Low || candle.Volume < 0)
                throw new InvalidDataException("invalid OHLCV or interval");
            // Server weekly opens are Monday UTC (1970-01-05 offset), never epoch Thursday.
        }
    }

    internal sealed class BinanceChartTransport : IBinanceChartTransport
    {
        internal static void ValidateAddress(Uri address, bool stream)
        {
            if (address == null || !address.IsAbsoluteUri || !string.IsNullOrEmpty(address.UserInfo) ||
                !string.IsNullOrEmpty(address.Fragment) || !address.IsDefaultPort) throw new ArgumentException("invalid market address");
            if (stream)
            {
                if (address.Scheme != "wss" || address.Host != "fstream.binance.com" || !string.IsNullOrEmpty(address.Query))
                    throw new ArgumentException("market stream address denied");
                foreach (string symbol in new[] { "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT" })
                    foreach (string interval in new[] { "15m", "1h", "4h", "1d", "1w" })
                        if (address.AbsolutePath == BinanceChartFeed.StreamAddress(symbol, interval).AbsolutePath) return;
            }
            else
            {
                if (address.Scheme != "https" || address.Host != "fapi.binance.com" || address.AbsolutePath != "/fapi/v1/klines")
                    throw new ArgumentException("market REST address denied");
                foreach (string symbol in new[] { "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT" })
                    foreach (string interval in new[] { "15m", "1h", "4h", "1d", "1w" })
                        if (address.Query == BinanceChartFeed.RestAddress(symbol, interval).Query) return;
            }
            throw new ArgumentException("market pair address denied");
        }

        public async Task<string> GetAsync(Uri address, CancellationToken token)
        {
            ValidateAddress(address, false);
            token.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, normal certificate verification.
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 15000;
            request.UserAgent = "TradingMarketChart/1.0";
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using (timeout.Token.Register(delegate { request.Abort(); }))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 512 * 1024)
                        throw new IOException("invalid market response");
                    using (Stream input = response.GetResponseStream())
                    using (var content = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (content.Length + count > 512 * 1024) throw new IOException("market response too large");
                            content.Write(buffer, 0, count);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
                        return new UTF8Encoding(false, true).GetString(content.ToArray());
                    }
                }
            }
        }

        public async Task RunStreamAsync(Uri address, Action connected, Action<string> message, CancellationToken token)
        {
            ValidateAddress(address, true);
            token.ThrowIfCancellationRequested();
            using (var socket = new ClientWebSocket())
            using (token.Register(delegate { socket.Abort(); }))
            {
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await socket.ConnectAsync(address, token).ConfigureAwait(false);
                connected();
                var buffer = new byte[8192];
                using (var payload = new MemoryStream())
                {
                    while (!token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (result.MessageType != WebSocketMessageType.Text || payload.Length + result.Count > 64 * 1024)
                            throw new IOException("invalid market stream frame");
                        payload.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            message(new UTF8Encoding(false, true).GetString(payload.ToArray()));
                            payload.SetLength(0);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
