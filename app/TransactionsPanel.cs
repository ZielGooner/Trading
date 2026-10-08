using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TradingLauncher
{
    public sealed class TransactionsPanel : UserControl
    {
        private readonly string root;
        private string account;
        private readonly DateField from = new DateField(), through = new DateField();
        private readonly SelectField symbol = new SelectField("전체", "BTCUSDT", "XRPUSDT", "SOLUSDT", "HYPEUSDT");
        private readonly SelectField view = new SelectField("완료 거래", "전체 체결");
        private readonly DataGridView grid = new DataGridView();
        private readonly Label count = new Label(), pageText = new Label(), status = new Label(), empty = new Label(), note = new Label();
        private readonly Button query = new ModernButton(), refresh = new ModernButton(), excel = new ModernButton(), previous = new ModernButton(), next = new ModernButton();
        private readonly FlowLayoutPanel filters = new FlowLayoutPanel();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly ReviewWorker worker = new ReviewWorker();
        private int page = 1, pages = 1, total;
        private bool loaded, suppressFilters, closing;
        public bool AutoLoad { get; set; }
        public bool IsBusy { get { return worker.IsBusy; } }
        public int RowCount { get { return grid.Rows.Count; } }
        public int CurrentPage { get { return page; } }
        public int TotalCount { get { return total; } }
        public DataGridView HistoryGrid { get { return grid; } }
        public event EventHandler OperationCompleted;

        public TransactionsPanel(string projectRoot)
        {
            root = Path.GetFullPath(projectRoot); AutoLoad = true;
            BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = UiTheme.Font(9F);
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8), ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new float[] { 52, 92, 36 }) rows.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (float height in new float[] { 40, 23, 25 }) rows.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(rows);
            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = UiTheme.Label("거래 내역", 19F, UiTheme.Text, FontStyle.Bold); title.Location = new Point(0, 0);
            var subtitle = UiTheme.Label("거래 기록을 한 페이지에 20개씩 확인하세요", 8.5F, UiTheme.Muted, FontStyle.Regular); subtitle.Location = new Point(1, 36);
            heading.Controls.Add(title); heading.Controls.Add(subtitle); rows.Controls.Add(heading, 0, 0);

            filters.Dock = DockStyle.Fill; filters.WrapContents = false; filters.Padding = new Padding(0, 6, 0, 0); filters.Margin = Padding.Empty;
            filters.BackColor = UiTheme.Surface;
            var filterCard = UiTheme.Card(); filterCard.Name = "HistoryFilterCard"; filterCard.Padding = new Padding(12, 12, 12, 10); filterCard.Margin = new Padding(0, 0, 0, 8);
            var filterRows = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            filterRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); filterRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filterRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var filterTitle = UiTheme.Label("세부 설정", 8.5F, UiTheme.Text, FontStyle.Bold); filterTitle.Name = "HistoryFilterTitle"; filterTitle.Margin = Padding.Empty;
            filterRows.Controls.Add(filterTitle, 0, 0);
            filterRows.Controls.Add(filters, 0, 1); filterCard.Controls.Add(filterRows); rows.Controls.Add(filterCard, 0, 1);
            filters.Controls.Add(FilterLabel("기간"));
            foreach (DateField picker in new DateField[] { from, through })
            {
                picker.Margin = new Padding(0, 2, 7, 0);
            }
            from.Name = "HistoryStartDate"; through.Name = "HistoryEndDate";
            symbol.Name = "HistorySymbol"; view.Name = "HistoryView";
            symbol.Margin = view.Margin = new Padding(0, 2, 8, 0);
            from.Value = KoreaToday().AddDays(-29); through.Value = KoreaToday();
            filters.Controls.Add(from); filters.Controls.Add(FilterLabel("~")); filters.Controls.Add(through);
            foreach (int days in new int[] { 7, 30, 90 })
            {
                int duration = days; var preset = new ModernButton(); UiTheme.StyleButton(preset, days + "일", 46, false); preset.Height = 32; preset.Margin = new Padding(0, 2, 8, 0);
                preset.Click += delegate { suppressFilters = true; through.Value = KoreaToday(); from.Value = through.Value.Date.AddDays(1 - duration); suppressFilters = false; InvalidateSelection(); };
                filters.Controls.Add(preset);
            }
            filters.Controls.Add(FilterLabel("심볼")); filters.Controls.Add(symbol);
            UiTheme.StyleButton(query, "조회", 66, true); query.Height = 32; query.Margin = new Padding(0, 2, 8, 0); query.Click += async delegate { page = 1; await RunOperation("list"); }; filters.Controls.Add(query);
            var reset = new ModernButton(); UiTheme.StyleButton(reset, "초기화", 72, false); reset.Height = 32; reset.Margin = new Padding(0, 2, 8, 0);
            reset.Click += delegate { SetFilters(KoreaToday().AddDays(-29), KoreaToday(), ""); }; filters.Controls.Add(reset);
            from.ValueChanged += delegate { if (!suppressFilters) InvalidateSelection(); };
            through.ValueChanged += delegate { if (!suppressFilters) InvalidateSelection(); };
            symbol.SelectedIndexChanged += delegate { if (!suppressFilters) InvalidateSelection(); };

            var toolbar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            count.Dock = DockStyle.Left; count.Width = 195; count.TextAlign = ContentAlignment.MiddleLeft; count.ForeColor = UiTheme.Muted;
            var options = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 456, WrapContents = false, Margin = Padding.Empty };
            view.Height = 30;
            view.SelectedIndexChanged += async delegate { InvalidateSelection(); if (AutoLoad) await RunOperation("list"); };
            UiTheme.StyleButton(refresh, "내역 새로고침", 150, false); refresh.Height = 30;
            UiTheme.StyleButton(excel, "엑셀 저장·열기", 150, false); excel.Height = 30;
            refresh.Click += async delegate { await RunOperation("sync"); };
            excel.Click += async delegate { await RunOperation("export"); };
            options.Controls.AddRange(new Control[] { view, refresh, excel }); toolbar.Controls.Add(count); toolbar.Controls.Add(options); rows.Controls.Add(toolbar, 0, 2);
            UiTheme.StyleGrid(grid); grid.RowTemplate.Height = 24; grid.ColumnHeadersHeight = 28; grid.Font = UiTheme.Font(8.5F);
            grid.Columns.Add("number", "번호"); grid.Columns.Add("time", "거래 시간 (KST)"); grid.Columns.Add("symbol", "심볼"); grid.Columns.Add("direction", "방향");
            grid.Columns.Add("entry", "진입가"); grid.Columns.Add("exit", "청산가"); grid.Columns.Add("quantity", "수량"); grid.Columns.Add("fees", "수수료"); grid.Columns.Add("gross", "실현 손익"); grid.Columns.Add("status", "상태");
            float[] weights = { 37, 145, 84, 52, 85, 85, 66, 83, 92, 145 };
            for (int i = 0; i < grid.Columns.Count; i++) { grid.Columns[i].FillWeight = weights[i]; grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; }
            grid.Columns[0].MinimumWidth = 42;
            grid.Resize += delegate { FitRows(); };
            for (int i = 4; i <= 8; i++) grid.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            var gridHost = UiTheme.Card(); gridHost.Name = "HistoryGridCard"; gridHost.Margin = Padding.Empty; gridHost.Padding = new Padding(1);
            empty.TextAlign = ContentAlignment.MiddleCenter; empty.BackColor = UiTheme.Surface; empty.ForeColor = UiTheme.Muted; empty.Font = UiTheme.Font(10F);
            gridHost.Controls.Add(grid); gridHost.Controls.Add(empty);
            gridHost.Resize += delegate { empty.SetBounds(1, grid.ColumnHeadersHeight + 2, Math.Max(0, gridHost.ClientSize.Width - 2), Math.Max(0, gridHost.ClientSize.Height - grid.ColumnHeadersHeight - 3)); };
            rows.Controls.Add(gridHost, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0), Margin = Padding.Empty };
            UiTheme.StyleButton(previous, "‹ 이전", 82, false); UiTheme.StyleButton(next, "다음 ›", 82, false); previous.Height = next.Height = 30;
            pageText.Size = new Size(270, 30); pageText.TextAlign = ContentAlignment.MiddleCenter;
            previous.Click += async delegate { if (page > 1) await RunOperation("list", page - 1); };
            next.Click += async delegate { if (page < pages) await RunOperation("list", page + 1); };
            footer.Controls.Add(previous); footer.Controls.Add(pageText); footer.Controls.Add(next);
            var sizeLabel = FilterLabel("페이지당 20개"); sizeLabel.Padding = new Padding(20, 7, 0, 0); footer.Controls.Add(sizeLabel); rows.Controls.Add(footer, 0, 4);
            note.Dock = status.Dock = DockStyle.Fill; note.ForeColor = status.ForeColor = UiTheme.Muted; note.Font = status.Font = UiTheme.Font(8F);
            rows.Controls.Add(note, 0, 5); rows.Controls.Add(status, 0, 6);
            SetEmpty("API 설정 후 저장된 거래 내역을 조회할 수 있습니다."); UpdateControls();
        }
        private static Label FilterLabel(string text) { return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 9, 6, 0), ForeColor = UiTheme.Muted }; }
        private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static DateTime KoreaToday() { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Korea).Date; }
        private static long KoreaMilliseconds(DateTime date) { return (long)(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), Korea) - Epoch).TotalMilliseconds; }
        public void SetFilters(DateTime start, DateTime end, string selectedSymbol)
        {
            suppressFilters = true; from.Value = start.Date; through.Value = end.Date;
            symbol.SelectedItem = string.IsNullOrEmpty(selectedSymbol) ? "전체" : selectedSymbol;
            suppressFilters = false; InvalidateSelection();
        }
        private void InvalidateSelection()
        {
            page = pages = 1; total = 0; loaded = false; grid.Rows.Clear();
            SetEmpty("조건이 변경되었습니다. 조회를 눌러 확인하세요."); status.Text = ""; UpdateControls();
        }
        private void SetEmpty(string text) { empty.Text = text; empty.Visible = grid.Rows.Count == 0; if (empty.Visible) empty.BringToFront(); }
        private void FitRows()
        {
            int height = Math.Max(20, Math.Min(26, (grid.ClientSize.Height - grid.ColumnHeadersHeight - 3) / 20));
            grid.RowTemplate.Height = height;
            foreach (DataGridViewRow row in grid.Rows) if (row.Height != height) row.Height = height;
        }
        private void UpdateControls()
        {
            filters.Enabled = view.Enabled = refresh.Enabled = !IsBusy;
            query.Enabled = excel.Enabled = !IsBusy && account != null;
            previous.Enabled = !IsBusy && page > 1; next.Enabled = !IsBusy && page < pages;
            count.Text = "전체 " + total.ToString("N0", CultureInfo.InvariantCulture) + "건 · 최신순";
            int begin = total == 0 ? 0 : (page - 1) * 20 + 1, end = Math.Min(total, page * 20);
            pageText.Text = begin + "–" + end + " / " + total + "건     " + page + " / " + pages + " 페이지";
            bool fills = view.SelectedIndex == 1;
            grid.Columns[4].HeaderText = fills ? "체결가" : "진입가"; grid.Columns[5].HeaderText = fills ? "매수/매도" : "청산가";
            note.Text = (fills ? "전체 계좌 체결 · 방향을 확인할 수 없는 체결은 — 표시" : "청산 시각 기준 · 프로그램 기록과 연결된 거래 · 미확인 값은 — 표시") + " · 실현 손익은 비용 반영 전";
        }
        private void IdentifyAccount()
        {
            string found = ReviewWorker.IdentifyAccount(root, account);
            if (account != found) { account = found; InvalidateSelection(); }
        }
        public async void ActivatePage(string currentAccount)
        {
            if (IsBusy || !AutoLoad) return;
            if (!string.IsNullOrEmpty(currentAccount) && currentAccount != account) { account = currentAccount; InvalidateSelection(); }
            try { IdentifyAccount(); } catch (Exception error) { status.Text = "계정 확인 실패: " + error.Message; return; }
            UpdateControls(); if (!loaded && account != null) await RunOperation("list");
        }
        public void MarkLedgerUpdated(string currentAccount) { loaded = false; if (Visible && AutoLoad && !IsBusy) ActivatePage(currentAccount); }
        public void RequestCancellation()
        {
            closing = true;
            worker.Cancel();
        }
        public void Receive(string line)
        {
            var value = json.Deserialize<Dictionary<string, object>>(line);
            string type = Convert.ToString(value["type"]);
            if (type != "result") { if (value.ContainsKey("message")) status.Text = Convert.ToString(value["message"]); return; }
            if (value.ContainsKey("account")) account = Convert.ToString(value["account"]);
            if (!value.ContainsKey("rows")) return;
            page = Convert.ToInt32(value["page"]); pages = Convert.ToInt32(value["pages"]); total = Convert.ToInt32(value["total"]);
            grid.Rows.Clear();
            bool fills = Convert.ToString(value["view"]) == "fills";
            foreach (object item in (IEnumerable)value["rows"])
            {
                var row = (Dictionary<string, object>)item;
                if (grid.Rows.Count >= 20) break;
                string direction = Convert.ToString(row["direction"]);
                string fee = Number(row["fees"], 4) + (row["fees"] == null ? "" : " " + row["fee_asset"]);
                int index = grid.Rows.Add((page - 1) * 20 + grid.Rows.Count + 1, TimeZoneInfo.ConvertTimeFromUtc(Epoch.AddMilliseconds(Convert.ToInt64(row["time"])), Korea).ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture), row["symbol"], string.IsNullOrEmpty(direction) ? "—" : direction,
                    Price(row["entry"], Convert.ToString(row["symbol"])), fills ? Convert.ToString(row["side"]) : Price(row["exit"], Convert.ToString(row["symbol"])), Number(row["quantity"], 8), fee, Profit(row["gross"]), row["status"]);
                grid.Rows[index].Cells[3].Style.ForeColor = direction == "long" ? UiTheme.Positive : direction == "short" ? UiTheme.Negative : UiTheme.Muted;
                if (row["gross"] != null) { decimal profit = Convert.ToDecimal(row["gross"], CultureInfo.InvariantCulture); grid.Rows[index].Cells[8].Style.ForeColor = profit > 0 ? UiTheme.Positive : profit < 0 ? UiTheme.Negative : UiTheme.Text; }
            }
            FitRows(); grid.ClearSelection(); loaded = true; SetEmpty("선택한 기간과 심볼에 해당하는 기록이 없습니다.");
            status.Text = value.ContainsKey("synced_at") && value["synced_at"] != null ? "마지막 동기화 " + TimeZoneInfo.ConvertTimeFromUtc(Epoch.AddMilliseconds(Convert.ToInt64(value["synced_at"])), Korea).ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture) + " KST · 저장 원장 기준" : "저장된 동기화 기록이 없습니다. 내역 새로고침으로 불러오세요.";
            if (value.ContainsKey("sync_error") && value["sync_error"] != null) status.Text += " · 최근 동기화 실패";
            UpdateControls();
        }
        private static string Number(object value, int decimals)
        {
            if (value == null) return "—";
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,##0." + new string('#', decimals), CultureInfo.InvariantCulture);
        }
        private static string Price(object value, string pair) { return value == null ? "—" : Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(pair == "XRPUSDT" ? "N4" : "N2", CultureInfo.InvariantCulture); }
        private static string Profit(object value) { return value == null ? "—" : Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("+#,##0.00;-#,##0.00;0.00", CultureInfo.InvariantCulture); }
        private async Task RunOperation(string action, int? requestedPage = null)
        {
            if (IsBusy || closing) return;
            Dictionary<string, object> request = null;
            bool succeeded = false;
            string excelPath = null;
            try
            {
                IdentifyAccount();
                if (account == null) throw new InvalidOperationException("메인 탭에서 API를 먼저 설정하세요.");
                long start = KoreaMilliseconds(from.Value.Date), end = KoreaMilliseconds(through.Value.Date.AddDays(1));
                if (end <= start || end - start > 365L * 86400000) throw new InvalidOperationException("조회 기간은 시작일 이후 1~365일로 설정하세요.");
                request = new Dictionary<string, object> { { "action", action }, { "account", account }, { "days", 30 }, { "start_ms", start }, { "end_ms", end }, { "symbol", symbol.SelectedIndex == 0 ? "" : symbol.SelectedItem }, { "page", requestedPage.HasValue ? requestedPage.Value : page }, { "view", view.SelectedIndex == 0 ? "trades" : "fills" } };
                if (action == "sync")
                {
                    var credentials = CredentialStore.Load(root);
                    try { request["api_key"] = credentials["key"]; request["api_secret"] = credentials["secret"]; } finally { credentials.Clear(); }
                }
                status.Text = action == "list" ? "저장 내역을 조회하는 중입니다…" : action == "sync" ? "거래소 체결·수수료·펀딩을 동기화하고 있습니다…" : "거래 내역 엑셀을 저장하고 있습니다…";
                int exitCode = await worker.RunAsync(root, request, delegate(string line)
                {
                    var value = json.Deserialize<Dictionary<string, object>>(line);
                    if (Convert.ToString(value["type"]) == "result") { succeeded = true; if (value.ContainsKey("excel")) excelPath = Convert.ToString(value["excel"]); }
                    if (!IsDisposed) Receive(line);
                }, UpdateControls);
                succeeded = succeeded && exitCode == 0;
                if (!succeeded && !IsDisposed && grid.Rows.Count == 0) SetEmpty("조회하지 못했습니다. 설정을 확인하고 다시 조회하세요.");
            }
            catch (Exception error) { if (!IsDisposed) { status.Text = "작업 실패: " + error.Message; if (grid.Rows.Count == 0) SetEmpty(status.Text); } }
            finally
            {
                if (request != null) request.Clear();
                if (!IsDisposed) { UpdateControls(); if (OperationCompleted != null) OperationCompleted(this, EventArgs.Empty); }
            }
            if (succeeded && !IsDisposed && !closing && action != "list")
            {
                if (action == "export" && !string.IsNullOrEmpty(excelPath) && File.Exists(excelPath))
                {
                    try { Process.Start(new ProcessStartInfo(excelPath) { UseShellExecute = true }); }
                    catch (Exception error) { status.Text = "엑셀은 저장했지만 열지 못했습니다: " + error.Message; }
                }
                await RunOperation("list");
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { closing = true; worker.Dispose(); } base.Dispose(disposing); }
    }
}
