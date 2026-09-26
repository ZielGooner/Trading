using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TradingLauncher
{
    // This component reads historical snapshots only. It never starts an account worker.
    public sealed class MarketChart : UserControl
    {
        private readonly string root;
        private readonly string[] symbols = { "BTCUSDT", "XRPUSDT", "SOLUSDT" };
        private readonly Button[] tabs = new Button[3];
        private readonly Label quote = new Label(), market = new Label(), source = new Label(), timestamps = new Label();
        private readonly CandleCanvas canvas = new CandleCanvas();
        private string selectedSymbol;
        public string SourceText { get { return source.Text + " · " + timestamps.Text; } }
        public int CandleCount { get { return canvas.CandleCount; } }
        public string SelectedSymbol { get { return selectedSymbol; } }

        public MarketChart(string projectRoot)
        {
            root = projectRoot;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Surface;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Font(9F);
            Padding = new Padding(16, 8, 12, 6);
            Margin = Padding.Empty;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 187));
            navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabStrip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            for (int i = 0; i < symbols.Length; i++)
            {
                string symbol = symbols[i];
                var button = new Button { Tag = symbol, TabStop = true };
                UiTheme.StyleButton(button, symbol.Replace("USDT", "/USDT"), 92, false);
                button.Height = 29;
                button.Font = UiTheme.Font(8F, FontStyle.Bold);
                button.Margin = new Padding(0, 0, 4, 0);
                button.FlatAppearance.BorderSize = 0;
                button.Click += delegate { SelectSymbol(symbol); };
                tabs[i] = button;
                tabStrip.Controls.Add(button);
            }
            source.Text = "저장 데이터 · 실시간 아님";
            source.Dock = DockStyle.Fill;
            source.Font = UiTheme.Font(8F);
            source.ForeColor = UiTheme.Warning;
            source.TextAlign = ContentAlignment.MiddleRight;
            source.Margin = Padding.Empty;
            navigation.Controls.Add(tabStrip, 0, 0);
            navigation.Controls.Add(source, 1, 0);
            layout.Controls.Add(navigation, 0, 0);

            var quoteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            quoteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            quoteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            quoteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            quoteRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            market.Dock = quote.Dock = DockStyle.Fill;
            market.Font = UiTheme.Font(12F, FontStyle.Bold);
            quote.Font = UiTheme.Font(19F, FontStyle.Bold);
            market.TextAlign = quote.TextAlign = ContentAlignment.MiddleLeft;
            market.Margin = quote.Margin = Padding.Empty;
            var interval = new Label { Dock = DockStyle.Fill, Text = "저장 종가 · 4시간", ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleRight, Font = UiTheme.Font(8F), Margin = Padding.Empty };
            quoteRow.Controls.Add(market, 0, 0);
            quoteRow.Controls.Add(quote, 1, 0);
            quoteRow.Controls.Add(interval, 2, 0);
            layout.Controls.Add(quoteRow, 0, 1);
            timestamps.Dock = DockStyle.Fill;
            timestamps.Font = UiTheme.Font(7.5F);
            timestamps.ForeColor = UiTheme.Muted;
            timestamps.Margin = Padding.Empty;
            timestamps.AutoEllipsis = true;
            layout.Controls.Add(timestamps, 0, 2);
            canvas.Dock = DockStyle.Fill;
            canvas.Margin = Padding.Empty;
            layout.Controls.Add(canvas, 0, 3);
            SelectSymbol("BTCUSDT");
        }

        public void SelectSymbol(string symbol)
        {
            if (Array.IndexOf(symbols, symbol) < 0) return;
            selectedSymbol = symbol;
            market.Text = symbol.Replace("USDT", "/USDT");
            foreach (Button tab in tabs)
            {
                bool selected = (string)tab.Tag == symbol;
                tab.ForeColor = selected ? UiTheme.Accent : UiTheme.Muted;
                tab.BackColor = selected ? UiTheme.Elevated : UiTheme.Surface;
            }
            quote.Text = "—";
            quote.ForeColor = UiTheme.Text;
            timestamps.Text = "저장된 완료봉을 불러오는 중입니다.";
            canvas.SetData(null, symbol, "저장 데이터를 불러오는 중입니다.");
            try
            {
                string path = Path.Combine(root, "data", "tradingview_" + symbol.ToLowerInvariant() + "_p_4h.json");
                if (!File.Exists(path))
                {
                    timestamps.Text = "이 종목의 저장된 시세 파일이 없습니다.";
                    canvas.SetData(null, symbol, "저장 데이터 없음\n시세 파일을 확인하세요.");
                    return;
                }
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("snapshot too large");
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
                var envelope = Map(serializer.DeserializeObject(File.ReadAllText(path)));
                var response = Map(envelope["response"]);
                var request = Map(envelope["requested"]);
                string expected = "BINANCE:" + symbol + ".P";
                if (Convert.ToString(response["symbol"]) != expected || Convert.ToString(request["symbol"]) != expected ||
                    Convert.ToString(response["interval"]) != "4h" || Convert.ToString(request["interval"]) != "4h" ||
                    !Convert.ToBoolean(response["success"])) throw new InvalidDataException("snapshot identity mismatch");
                DateTimeOffset retrieved = DateTimeOffset.Parse(Convert.ToString(envelope["retrieved_at_utc"]), CultureInfo.InvariantCulture).ToUniversalTime();
                var all = new List<StoredCandle>();
                long previous = 0;
                foreach (object item in (IEnumerable)response["bars"])
                {
                    var row = Map(item);
                    var candle = new StoredCandle();
                    candle.Time = Convert.ToInt64(row["t"], CultureInfo.InvariantCulture);
                    candle.Open = Number(row, "o"); candle.High = Number(row, "h");
                    candle.Low = Number(row, "l"); candle.Close = Number(row, "c");
                    candle.Volume = row.ContainsKey("v") ? Number(row, "v") : -1;
                    if (candle.Time <= previous || (previous > 0 && candle.Time - previous != 14400) || candle.Time % 14400 != 0 || candle.Low <= 0 ||
                        candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) ||
                        (row.ContainsKey("v") && candle.Volume < 0)) throw new InvalidDataException("invalid candle");
                    previous = candle.Time;
                    all.Add(candle);
                }
                if (all.Count != Convert.ToInt32(response["count"]) || all.Count < 2) throw new InvalidDataException("invalid candle count");
                var closed = new List<StoredCandle>();
                // Match the snapshot replay boundary: the final source candle may be incomplete.
                DateTime cutoff = retrieved.UtcDateTime.AddMinutes(-30);
                for (int i = 0; i < all.Count - 1; i++)
                {
                    StoredCandle candle = all[i];
                    if (Epoch.AddSeconds(candle.Time + 14400) <= cutoff) closed.Add(candle);
                }
                if (closed.Count == 0) throw new InvalidDataException("no completed candles");
                double sum = 0;
                for (int i = 0; i < closed.Count; i++)
                {
                    sum += closed[i].Close;
                    if (i >= 20) sum -= closed[i - 20].Close;
                    closed[i].MovingAverage = i >= 19 ? sum / 20 : double.NaN;
                }
                int start = Math.Max(0, closed.Count - 72);
                var visible = closed.GetRange(start, closed.Count - start);
                StoredCandle last = visible[visible.Count - 1];
                quote.Text = FormatPrice(last.Close, symbol);
                quote.ForeColor = last.Close >= last.Open ? UiTheme.Positive : UiTheme.Negative;
                timestamps.Text = "봉 마감 " + Epoch.AddSeconds(last.Time + 14400).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  ·  저장 " + retrieved.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC  ·  MA(20)";
                canvas.SetData(visible, symbol, null);
            }
            catch (Exception)
            {
                quote.Text = "—";
                timestamps.Text = "저장 데이터 형식 또는 종목 정보를 확인할 수 없습니다.";
                canvas.SetData(null, symbol, "차트를 표시할 수 없습니다.\n저장된 시세 파일을 확인하세요.");
            }
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        private static double Number(Dictionary<string, object> value, string key)
        {
            double number = Convert.ToDouble(value[key], CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("non-finite candle");
            return number;
        }
        private static string FormatPrice(double value, string symbol) { return value.ToString(symbol == "XRPUSDT" ? "N4" : "N2", CultureInfo.InvariantCulture); }

        private sealed class StoredCandle
        {
            public long Time;
            public double Open, High, Low, Close, Volume, MovingAverage;
        }

        private sealed class CandleCanvas : Control
        {
            private List<StoredCandle> candles;
            private string symbol, emptyText;
            public int CandleCount { get { return candles == null ? 0 : candles.Count; } }
            public CandleCanvas()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = UiTheme.Surface;
                Font = UiTheme.Font(7.5F);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            }
            public void SetData(List<StoredCandle> data, string selected, string message)
            {
                candles = data; symbol = selected; emptyText = message;
                Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (candles == null || candles.Count == 0)
                {
                    TextRenderer.DrawText(g, emptyText ?? "저장 데이터 없음", Font, ClientRectangle, UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                if (Width < 180 || Height < 100) return;
                bool hasVolume = true;
                double low = double.MaxValue, high = double.MinValue, maximumVolume = 0;
                foreach (StoredCandle candle in candles)
                {
                    low = Math.Min(low, candle.Low); high = Math.Max(high, candle.High);
                    if (!double.IsNaN(candle.MovingAverage)) { low = Math.Min(low, candle.MovingAverage); high = Math.Max(high, candle.MovingAverage); }
                    if (candle.Volume < 0) hasVolume = false;
                    maximumVolume = Math.Max(maximumVolume, candle.Volume);
                }
                double spread = Math.Max(high - low, high * 0.001);
                high += spread * 0.08; low -= spread * 0.08;
                int volumeHeight = hasVolume ? Math.Max(20, Math.Min(49, Height / 6)) : 0;
                var plot = new RectangleF(4, 7, Width - 91, Height - 34 - volumeHeight);
                float cell = plot.Width / candles.Count;
                Func<double, float> y = delegate(double price) { return plot.Bottom - (float)((price - low) / (high - low)) * plot.Height; };
                using (var gridPen = new Pen(Color.FromArgb(100, UiTheme.Border)))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        float lineY = plot.Top + plot.Height * i / 4F;
                        g.DrawLine(gridPen, plot.Left, lineY, plot.Right, lineY);
                        double price = high - (high - low) * i / 4D;
                        TextRenderer.DrawText(g, FormatPrice(price, symbol), Font, new Rectangle((int)plot.Right + 8, (int)lineY - 8, 77, 18), UiTheme.Muted, TextFormatFlags.VerticalCenter);
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        int index = (candles.Count - 1) * i / 4;
                        float x = plot.Left + cell * (index + 0.5F);
                        g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom + volumeHeight);
                        string date = Epoch.AddSeconds(candles[index].Time).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                        int labelX = Math.Max(0, Math.Min((int)plot.Right - 68, (int)x - 34));
                        TextRenderer.DrawText(g, date, Font, new Rectangle(labelX, Height - 22, 74, 20), UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                var average = new List<PointF>();
                for (int i = 0; i < candles.Count; i++)
                {
                    StoredCandle candle = candles[i];
                    float x = plot.Left + cell * (i + 0.5F);
                    Color color = candle.Close >= candle.Open ? UiTheme.Positive : UiTheme.Negative;
                    float bodyWidth = Math.Max(2F, cell * 0.64F);
                    using (var wick = new Pen(color, 1F))
                    using (var body = new SolidBrush(color))
                    {
                        g.DrawLine(wick, x, y(candle.High), x, y(candle.Low));
                        float top = y(Math.Max(candle.Open, candle.Close));
                        g.FillRectangle(body, x - bodyWidth / 2F, top, bodyWidth, Math.Max(1.5F, y(Math.Min(candle.Open, candle.Close)) - top));
                    }
                    if (!double.IsNaN(candle.MovingAverage)) average.Add(new PointF(x, y(candle.MovingAverage)));
                    if (hasVolume && maximumVolume > 0)
                    {
                        float height = (float)(candle.Volume / maximumVolume) * Math.Max(1, volumeHeight - 9);
                        using (var volume = new SolidBrush(Color.FromArgb(100, color)))
                            g.FillRectangle(volume, x - bodyWidth / 2F, plot.Bottom + volumeHeight - height, bodyWidth, height);
                    }
                }
                if (average.Count > 1) using (var ma = new Pen(UiTheme.Accent, 1.4F)) g.DrawLines(ma, average.ToArray());
                StoredCandle last = candles[candles.Count - 1];
                float lastY = y(last.Close);
                Color lastColor = last.Close >= last.Open ? UiTheme.Positive : UiTheme.Negative;
                using (var line = new Pen(Color.FromArgb(165, lastColor), 1F))
                { line.DashStyle = DashStyle.Dash; g.DrawLine(line, plot.Left, lastY, plot.Right, lastY); }
                var priceBox = new Rectangle((int)plot.Right + 2, (int)lastY - 10, 82, 20);
                using (var fill = new SolidBrush(lastColor)) g.FillRectangle(fill, priceBox);
                TextRenderer.DrawText(g, FormatPrice(last.Close, symbol), Font, priceBox, UiTheme.Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (hasVolume) TextRenderer.DrawText(g, "거래량", Font, new Point(7, (int)plot.Bottom + 2), UiTheme.Muted);
            }
        }
    }
}
