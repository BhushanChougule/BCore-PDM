using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // Audit Report (all users, read-only). A sortable, filterable table of the
    // whole audit trail (N:\PDM-SolidWorks\VAULT\audit.csv) — every Create, Save,
    // Lock, Unlock, Release, New Revision, Rollback, Remove, request, approval,
    // rejection and automatic orphan purge ever logged. Companion to the Vault
    // Dashboard: the dashboard shows the CURRENT state of every file; this shows
    // the HISTORY of events over time.
    //
    // Built on the same proven plumbing as VaultDashboardForm: a VirtualMode
    // DataGridView PAGINATED 20 rows per page with a bottom pager, EXCEL-STYLE
    // per-column filtering (the Timestamp column grouped to DAY granularity), a
    // global search box and a "whole-log counts" summary strip whose entries
    // double as quick filters. DPI-aware (S(v) = v * _scale, fonts = pt * _scale).
    //
    // The audit log is the source of truth; the report is read-only and never
    // touches it (it only reads the file and exports the filtered view to CSV).
    // Audit.csv is the deliberately Excel-friendly format used across the app, so
    // "export to Excel" stays a plain CSV (no NuGet / native dependency).
    public class AuditReportForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        // Palette (redefined per-form, like the other dialogs).
        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cBorder    = Color.FromArgb(220, 225, 232);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private readonly Color cMaroon    = Color.FromArgb(140, 60, 60);
        private readonly Color cRed       = Color.FromArgb(180, 75, 75);
        private readonly Color cPurple    = Color.FromArgb(105, 100, 165);
        private readonly Color cRowAlt    = Color.FromArgb(245, 247, 250);
        private readonly Color cFunnel    = Color.FromArgb(245, 205, 95); // active-filter glyph

        private const string AuditFile = @"N:\PDM-SolidWorks\VAULT\audit.csv";

        // Column indices (kept in one place so helpers stay readable).
        private const int ColTimestamp = 0;
        private const int ColUser      = 1;
        private const int ColAction    = 2;
        private const int ColFile      = 3;
        private const int ColPartNo    = 4;
        private const int ColRev       = 5;
        private const int ColNote      = 6;
        private const int ColCount     = 7;

        private DataGridView _grid;
        private TextBox _search;
        private Label _title;
        private Button _btnRefresh;
        private Button _btnExport;
        private Button _btnClear;
        private Button _btnDashboard; // switch back to the Vault Dashboard
        private Button _btnClose;
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _searchTimer;
        private Font _cellBold; // shared bold font: Action cell + header text

        // Summary strip: clickable count "links" (Total / Releases / Revisions /
        // Removals act as quick filters) + a plain showing/page label.
        private FlowLayoutPanel _summaryPanel;
        private Label _lblTotal, _lblRelease, _lblRevision, _lblRemoval, _lblShowing;
        private Font _summaryFont;       // base (bold)
        private Font _summaryFontActive; // active quick-filter (bold + underline)
        private ToolTip _summaryTip;     // shared tip for the clickable counts
        private Label _lblHint;          // faint discoverability footer
        private DateTime _loadedAt;      // snapshot time, shown as "as of HH:mm"
        // Whole-log counts — invariant under filtering, so cached once per load.
        private int _cntRelease, _cntRevision, _cntRemoval;
        // Cycle-time strip: average WIP→Released duration over a selectable
        // window, computed straight from the audit log (_all) — independent of
        // the grid's column filters/search (it's a time-window metric, not a
        // view metric). Reuses _summaryFont (no extra disposable).
        private FlowLayoutPanel _cyclePanel;
        private ComboBox _cycleWindow;   // Last 30 / 90 / 365 days / All time
        private Label _lblCyclePrefix;   // static "Cycle time (WIP→Released):"
        private Label _lblCycle;         // the computed result
        private Label _lblCycleDetails;  // "Details…" link → analytics popup

        private const int VisibleRows = 20;       // fixed grid height = 20 rows
        private const int PageSize = VisibleRows;  // 20 rows per page (the "20 row rule")
        private const double MaxScreenFraction = 0.80; // popup ≤ 80% of screen
        private int GlyphZone => S(20);            // right-edge hit area for the arrow

        // Current page (0-based) into _view, and the pager controls rebuilt for it.
        private int _page = 0;
        private readonly List<Control> _pagerControls = new List<Control>();
        private Font _pagerFont;
        private int PageCount => Math.Max(1, (_view.Count + PageSize - 1) / PageSize);
        private int PageStart => _page * PageSize;

        private List<AuditEntry> _all = new List<AuditEntry>();
        // The current filtered + sorted rows. In VirtualMode the grid renders
        // straight from this list (no DataGridViewRow objects), so it scales to
        // very large logs. Index in the grid == PageStart + grid row index.
        private List<AuditEntry> _view = new List<AuditEntry>();
        private const int WidthSampleRows = 400; // cap column-measure cost

        // Active per-column filters: column index → the SET of allowed filter keys.
        // A column NOT in the map = unfiltered.
        private readonly Dictionary<int, HashSet<string>> _colFilters =
            new Dictionary<int, HashSet<string>>();

        // Current sort. Default = Timestamp, newest first (freshest event on top).
        private const int DefaultSortColumn = ColTimestamp;
        private int _sortColumn = DefaultSortColumn;
        private ListSortDirection _sortDir = ListSortDirection.Descending;

        // One parsed audit row.
        private sealed class AuditEntry
        {
            public DateTime Timestamp;   // DateTime.MinValue if unparseable
            public string User;
            public string Action;
            public string FileName;
            public string PartNo;
            public string Revision;
            public string Note;
        }

        // Set when the user clicks the "Vault Dashboard" button: the caller closes
        // this form and reopens the dashboard instead (single-window switch).
        public bool SwitchToDashboard { get; private set; }

        public AuditReportForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadData();
        }

        // Keyboard navigation. ProcessCmdKey fires before any control handles the
        // key, so this works wherever focus is. PageUp/Down + Ctrl+Home/End drive
        // the pager; Ctrl+F jumps to the search box; Enter applies the search; Esc
        // closes. (No row-open: the report is a read-only log, not a file browser.)
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.F:
                    _search.Focus(); _search.SelectAll(); return true;
                case Keys.PageDown: GoToPage(_page + 1); return true;
                case Keys.PageUp:   GoToPage(_page - 1); return true;
                case Keys.Control | Keys.Home: GoToPage(0); return true;
                case Keys.Control | Keys.End:  GoToPage(PageCount - 1); return true;
                case Keys.Escape:
                    this.DialogResult = DialogResult.Cancel; Close(); return true;
                case Keys.Enter:
                    if (_search != null && _search.Focused)
                    { _searchTimer.Stop(); ApplyFilter(); return true; }
                    break; // let a focused button handle Enter
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ── Win32 cue banner (placeholder text — not available on .NET 4.8) ──
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        internal static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            else
                box.HandleCreated += (s, e) =>
                    SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        }

        private void BuildForm()
        {
            this.Text = "BCore PDM — Audit Report";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.ClientSize = new Size(S(1120), S(680));
            this.MinimumSize = new Size(S(720), S(460));
            this.Font = new Font("Segoe UI", 3.4f * _scale);

            Font fTitle  = new Font("Segoe UI", 7f * _scale, FontStyle.Bold);
            Font fCtrl   = new Font("Segoe UI", 4f * _scale);
            Font fGrid   = new Font("Segoe UI", 3.3f * _scale);
            Font fBtn    = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _cellBold    = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);
            _pagerFont   = new Font("Segoe UI", 4f * _scale);
            _summaryFont       = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _summaryFontActive = new Font("Segoe UI", 4f * _scale,
                FontStyle.Bold | FontStyle.Underline);

            // ── Top panel: title, search, Refresh, Export, Clear ──────────
            _topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(120),
                BackColor = cBg
            };

            _title = new Label
            {
                Text = "BCore AUDIT REPORT",
                Font = fTitle,
                ForeColor = cBrandDark,
                AutoSize = true,
                Location = new Point(S(14), S(10))
            };
            _topPanel.Controls.Add(_title);

            int rowY = S(54);

            _search = new TextBox
            {
                Font = fCtrl,
                AutoSize = false,
                Location = new Point(S(14), rowY),
                Width = S(330)
            };
            _search.TextChanged += (s, e) => DebouncedFilter();
            SetCueBanner(_search, "Search user, action, file, part no or note…");
            _topPanel.Controls.Add(_search);

            int ctrlH = _search.PreferredHeight;
            _search.Height = ctrlH;

            _btnRefresh = MakeButton("Refresh", cBrand, fBtn,
                new Point(S(354), rowY), S(110));
            _btnRefresh.Height = ctrlH;
            _btnRefresh.Click += (s, e) => LoadData();
            _topPanel.Controls.Add(_btnRefresh);

            _btnExport = MakeButton("Export CSV", cBrandDark, fBtn,
                new Point(S(474), rowY), S(130));
            _btnExport.Height = ctrlH;
            _btnExport.Click += (s, e) => ExportCsv();
            _topPanel.Controls.Add(_btnExport);

            _btnClear = MakeButton("Clear Filters", Color.FromArgb(120, 128, 140),
                fBtn, new Point(S(614), rowY), S(140));
            _btnClear.Height = ctrlH;
            _btnClear.Click += (s, e) => ClearAllFilters();
            _topPanel.Controls.Add(_btnClear);

            // Switch back to the Vault Dashboard (single-window nav): signal the
            // caller and close — the task pane reopens the dashboard in its place.
            _btnDashboard = MakeButton("« Vault Dashboard", cBrandDark, fBtn,
                new Point(S(764), rowY), S(170));
            _btnDashboard.Height = ctrlH;
            _btnDashboard.Click += (s, e) =>
            { SwitchToDashboard = true; this.DialogResult = DialogResult.Cancel; Close(); };
            _topPanel.Controls.Add(_btnDashboard);

            int summaryY = rowY + ctrlH + S(10);
            _summaryPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = cBg,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(S(14), summaryY)
            };
            _lblTotal    = MakeCountLabel("All events (clear filters)", () => ClearAllFilters());
            _lblRelease  = MakeCountLabel("Filter to Release events",      () => ToggleActionFilter("Release"));
            _lblRevision = MakeCountLabel("Filter to New Revision events", () => ToggleActionFilter("NewRevision"));
            _lblRemoval  = MakeCountLabel("Filter to Remove events",       () => ToggleActionFilter("RemoveFromVault"));
            _lblShowing  = new Label
            {
                Font = _summaryFont,
                ForeColor = cTextGray,
                AutoSize = true,
                Margin = new Padding(S(6), 0, 0, 0)
            };
            _summaryPanel.Controls.AddRange(new Control[]
            {
                _lblTotal, _lblRelease, _lblRevision, _lblRemoval, _lblShowing
            });
            _topPanel.Controls.Add(_summaryPanel);

            // Cycle-time strip — avg WIP→Released duration over a selectable
            // window, read straight from the audit log. A second strip below the
            // counts (like the dashboard's KPI tiles) so it doesn't crowd the
            // already-full control row.
            int cycleY = summaryY + (int)_summaryFont.GetHeight() + S(8);
            _cyclePanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = cBg,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(S(14), cycleY)
            };
            _lblCyclePrefix = new Label
            {
                Text = "Cycle time (WIP→Released):",
                Font = _summaryFont,
                ForeColor = cTextGray,
                AutoSize = true,
                Margin = new Padding(0, S(3), S(8), 0)
            };
            _cycleWindow = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = _summaryFont,
                Width = S(120),
                Margin = new Padding(0, 0, S(10), 0)
            };
            _cycleWindow.Items.AddRange(new object[]
            { "Last 30 days", "Last 90 days", "Last 365 days", "All time" });
            _cycleWindow.SelectedIndex = 1; // default 90 days
            _cycleWindow.SelectedIndexChanged += (s, e) =>
            { ComputeCycleTime(); LayoutTopControls(); }; // re-centre: width changed
            _lblCycle = new Label
            {
                Font = _summaryFont,
                ForeColor = cTextDark,
                AutoSize = true,
                Margin = new Padding(0, S(3), 0, 0)
            };
            // Clickable "Details…" link → the Cycle-Time Analytics popup
            // (drill-in breakdown, by-division/by-user roll-ups, bounce-backs).
            _lblCycleDetails = new Label
            {
                Text = "  Details…",
                Font = _summaryFont,
                ForeColor = cBrand,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Margin = new Padding(S(8), S(3), 0, 0)
            };
            _lblCycleDetails.Click += (s, e) => OpenCycleDetails();
            _cyclePanel.Controls.AddRange(new Control[]
            { _lblCyclePrefix, _cycleWindow, _lblCycle, _lblCycleDetails });
            _topPanel.Controls.Add(_cyclePanel);

            int hintY = cycleY + _cyclePanel.PreferredSize.Height + S(6);
            _lblHint = new Label
            {
                Text = "Click a column arrow to filter  ·  Click a header to sort  ·  "
                     + "Click a count to filter  ·  PgUp/PgDn to page",
                Font = new Font("Segoe UI", 3.1f * _scale),
                ForeColor = Color.FromArgb(150, 158, 170),
                AutoSize = true,
                Location = new Point(S(14), hintY)
            };
            _topPanel.Controls.Add(_lblHint);

            _topPanel.Height = hintY + (int)_lblHint.Font.GetHeight() + S(8);
            _topPanel.Resize += (s, e) => LayoutTopControls();

            // ── Bottom panel: pager (left) + Close (right) ────────────────
            _bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(46),
                BackColor = cBg
            };
            _btnClose = MakeButton("Close", cTextGray, fBtn,
                new Point(0, S(8)), S(110));
            _btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnClose.Location = new Point(this.ClientSize.Width - S(124), S(8));
            _btnClose.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; Close(); };
            _bottomPanel.Controls.Add(_btnClose);
            _bottomPanel.Resize += (s, e) => BuildPager();

            // ── Grid ──────────────────────────────────────────────────────
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = cBg,
                BorderStyle = BorderStyle.None,
                Font = fGrid,
                GridColor = cBorder,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false,
                AutoGenerateColumns = false,
                VirtualMode = true
            };
            _grid.ColumnHeadersHeight = S(26);
            _grid.RowTemplate.Height = S(22);
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = _cellBold;
            _grid.ColumnHeadersDefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleLeft;
            _grid.DefaultCellStyle.ForeColor = cTextDark;
            _grid.DefaultCellStyle.SelectionBackColor = cBrand;
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Padding = new Padding(S(4), 0, 0, 0);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = cRowAlt;
            _grid.CellPainting += Grid_CellPainting;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            _grid.CellValueNeeded += Grid_CellValueNeeded;
            _grid.CellFormatting += Grid_CellFormatting;

            // Typed DateTime + format so the Timestamp column sorts CHRONOLOGICALLY
            // (a string "MM/dd/yyyy HH:mm:ss" would sort alphabetically).
            AddColumn("Timestamp", typeof(DateTime), "MM/dd/yyyy HH:mm:ss");
            AddColumn("User");
            AddColumn("Action");
            AddColumn("File Name");
            AddColumn("Part No");
            AddColumn("Rev");
            AddColumn("Note");

            this.Controls.Add(_grid);
            this.Controls.Add(_bottomPanel);
            this.Controls.Add(_topPanel);

            _searchTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplyFilter(); };
            this.FormClosed += (s, e) =>
            {
                _searchTimer.Stop();
                _searchTimer.Dispose();
                _cellBold?.Dispose();
                // Null the pager font after disposing: _bottomPanel.Resize stays
                // wired during teardown and would call BuildPager → MeasureText on
                // a disposed font. BuildPager early-returns when _pagerFont == null.
                _pagerFont?.Dispose();
                _pagerFont = null;
                _summaryFont?.Dispose();
                _summaryFontActive?.Dispose();
                _summaryTip?.Dispose();
            };
        }

        private Button MakeButton(string text, Color back, Font font,
            Point loc, int width)
        {
            var b = new Button
            {
                Text = text,
                Font = font,
                Width = width,
                Height = S(26),
                Location = loc,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void AddColumn(string header, Type valueType = null, string format = null)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Programmatic,
                Resizable = DataGridViewTriState.True
            };
            col.HeaderCell.ToolTipText =
                "Click the arrow to filter · click the header to sort";
            if (valueType != null) col.ValueType = valueType;
            if (format != null) col.DefaultCellStyle.Format = format;
            _grid.Columns.Add(col);
        }

        // ── Data ──────────────────────────────────────────────────────────
        private void LoadData()
        {
            try
            {
                _all = ReadAuditLog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not load the audit log.\n\n" + ex.Message,
                    "BCore PDM — Audit Report",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _all = new List<AuditEntry>();
            }
            _loadedAt = DateTime.Now;
            ComputeCounts();
            ComputeCycleTime();
            ApplyFilter();
            AutoSizeColumns();
            FitFormSize();
            LayoutTopControls();
        }

        // Read + parse the whole audit.csv ONCE into memory. The log is text and
        // append-only; a report can afford to read it all (rendering stays cheap
        // because the grid is VirtualMode + paginated). Missing file = empty report.
        private List<AuditEntry> ReadAuditLog()
        {
            var list = new List<AuditEntry>();
            if (!File.Exists(AuditFile)) return list;

            string text;
            // FileShare.ReadWrite so an in-progress AuditLogger append (or the file
            // being open in Excel) never blocks the report from reading it.
            using (var fs = new FileStream(AuditFile, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            var records = ParseCsv(text);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                // Skip the header row (first record whose first field is "Timestamp").
                if (i == 0 && r.Length > 0 &&
                    string.Equals(r[0], "Timestamp", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (r.Length == 0) continue;
                // Skip a blank/empty line (parses to a single empty field): every
                // real audit event carries a Timestamp, so an all-empty record can
                // only be a stray blank line — never a phantom row in the report.
                if (r.All(string.IsNullOrEmpty)) continue;

                list.Add(new AuditEntry
                {
                    Timestamp = ParseTimestamp(Field(r, 0)),
                    User      = Field(r, 1),
                    Action    = Field(r, 2),
                    FileName  = Field(r, 3),
                    PartNo    = Field(r, 4),
                    Revision  = Field(r, 5),
                    Note      = Field(r, 6)
                });
            }
            return list;
        }

        private static string Field(string[] r, int idx) =>
            idx < r.Length ? (r[idx] ?? "") : "";

        // AuditLogger writes the timestamp as "yyyy-MM-dd HH:mm:ss"; parse that
        // exact form, fall back to a lenient parse, else MinValue (blank cell).
        private static DateTime ParseTimestamp(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d))
                return d;
            return DateTime.MinValue;
        }

        // RFC-4180 CSV parser: handles quoted fields, escaped quotes (""), commas
        // and newlines INSIDE quotes (the Note field can contain them). Mirrors
        // the escaping AuditLogger.Csv writes.
        //
        // UNTERMINATED-QUOTE RECOVERY (audit H10): a writer that crashed (or a
        // read racing a mid-append truncation — the file is opened
        // FileShare.ReadWrite) can leave a dangling opening quote mid-file.
        // A strict parser then swallows EVERYTHING after it into one giant
        // field — the rest of the log silently vanishes from the report.
        // Every real record starts "yyyy-MM-dd HH:mm:ss," — so inside quotes,
        // a newline followed by exactly that shape is treated as the record
        // boundary the writer intended. (A quoted multi-line Note whose inner
        // line begins with exactly a timestamp+comma would split — vanishingly
        // rare, and it misparses ONE note instead of losing the whole tail.)
        private static List<string[]> ParseCsv(string text)
        {
            var records = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return records;

            var field = new StringBuilder();
            var fields = new List<string>();
            bool inQuotes = false;
            int n = text.Length;

            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else if (c == '\n' && LooksLikeRecordStart(text, i + 1))
                    {
                        // Recover from a dangling quote: close the field and
                        // end the record here (see method summary). Drop the
                        // trailing \r the verbatim append already collected.
                        if (field.Length > 0 && field[field.Length - 1] == '\r')
                            field.Length--;
                        inQuotes = false;
                        fields.Add(field.ToString()); field.Clear();
                        records.Add(fields.ToArray()); fields.Clear();
                    }
                    else field.Append(c); // commas / newlines kept verbatim
                    continue;
                }

                if (c == '"') { inQuotes = true; }
                else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
                else if (c == '\r') { /* swallow; \n ends the record */ }
                else if (c == '\n')
                {
                    fields.Add(field.ToString()); field.Clear();
                    records.Add(fields.ToArray()); fields.Clear();
                }
                else field.Append(c);
            }
            // Trailing field/record with no closing newline.
            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                records.Add(fields.ToArray());
            }
            return records;
        }

        // Does text starting at i look like the beginning of a NEW audit
        // record, i.e. exactly "yyyy-MM-dd HH:mm:ss," (AuditLogger's stamp)?
        // Char-by-char check — runs once per newline inside a quoted field.
        private static bool LooksLikeRecordStart(string t, int i)
        {
            if (i + 20 > t.Length) return false;
            for (int k = 0; k < 20; k++)
            {
                char c = t[i + k];
                switch (k)
                {
                    case 4:
                    case 7: if (c != '-') return false; break;
                    case 10: if (c != ' ') return false; break;
                    case 13:
                    case 16: if (c != ':') return false; break;
                    case 19: if (c != ',') return false; break;
                    default: if (c < '0' || c > '9') return false; break;
                }
            }
            return true;
        }

        private void DebouncedFilter()
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        // The display text shown (and exported/measured) for a column.
        private string CellText(AuditEntry e, int col)
        {
            switch (col)
            {
                case ColTimestamp: return e.Timestamp == DateTime.MinValue
                            ? "" : e.Timestamp.ToString("MM/dd/yyyy HH:mm:ss");
                case ColUser:   return e.User ?? "";
                case ColAction: return e.Action ?? "";
                case ColFile:   return e.FileName ?? "";
                case ColPartNo: return e.PartNo ?? "";
                case ColRev:    return e.Revision ?? "";
                case ColNote:   return e.Note ?? "";
                default: return "";
            }
        }

        // Sort key: Timestamp sorts by the real DateTime (chronological); every
        // other column by lower-cased text.
        private Func<AuditEntry, IComparable> KeySelector(int col)
        {
            if (col == ColTimestamp) return e => e.Timestamp;
            return e => (CellText(e, col) ?? "").ToLowerInvariant();
        }

        // The value a column is FILTERED on. Same as the display text EXCEPT the
        // Timestamp column, grouped to DAY granularity so the filter list shows
        // distinct days (not every second) — checking a day keeps every time on it.
        private string FilterKey(AuditEntry e, int col)
        {
            if (col == ColTimestamp) return e.Timestamp == DateTime.MinValue
                ? "" : e.Timestamp.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            return CellText(e, col);
        }

        // True if `e` passes the global search box (matches user/action/file/
        // part no/note). Empty term = everything passes.
        private bool MatchesSearch(AuditEntry e, string term)
        {
            if (term.Length == 0) return true;
            return (e.User     ?? "").ToLowerInvariant().Contains(term)
                || (e.Action   ?? "").ToLowerInvariant().Contains(term)
                || (e.FileName ?? "").ToLowerInvariant().Contains(term)
                || (e.PartNo   ?? "").ToLowerInvariant().Contains(term)
                || (e.Note     ?? "").ToLowerInvariant().Contains(term);
        }

        // Filter keys of `col` over the rows that pass every OTHER active column
        // filter + the global search — so a column's dropdown NARROWS like Excel.
        // columnFiltersOnly: ignore the TRANSIENT global search and apply
        // other COLUMN filters only — see VaultDashboardForm's twin.
        private IEnumerable<string> KeysPassingOtherFilters(int col,
            bool columnFiltersOnly = false)
        {
            string term = columnFiltersOnly ? ""
                : (_search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var e in _all)
            {
                bool ok = true;
                foreach (var kv in _colFilters)
                {
                    if (kv.Key == col) continue;
                    if (!kv.Value.Contains(FilterKey(e, kv.Key))) { ok = false; break; }
                }
                if (!ok) continue;
                if (!MatchesSearch(e, term)) continue;
                yield return FilterKey(e, col);
            }
        }

        // Order a column's filter values: Timestamp CHRONOLOGICALLY (by the day
        // key; blanks first), everything else alphabetically.
        private List<string> OrderFilterValues(int col, IEnumerable<string> values)
        {
            if (col == ColTimestamp)
                return values.OrderBy(v =>
                {
                    DateTime d;
                    return DateTime.TryParseExact(v, "MM/dd/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        ? d : DateTime.MinValue; // blanks sort earliest
                }).ToList();
            return values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void ApplyFilter()
        {
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();

            var view = _all.Where(e =>
            {
                foreach (var kv in _colFilters)
                    if (!kv.Value.Contains(FilterKey(e, kv.Key)))
                        return false;
                return MatchesSearch(e, term);
            }).ToList();

            if (_sortColumn >= 0)
            {
                var key = KeySelector(_sortColumn);
                view = (_sortDir == ListSortDirection.Ascending
                    ? view.OrderBy(key)
                    : view.OrderByDescending(key)).ToList();
            }

            _view = view;
            _page = 0;
            ShowGridPage();          // also refreshes the summary (page indicator)
            LayoutTopControls();     // re-centre: the summary width changed
        }

        // Render the current page: clamp _page, set the grid's RowCount to just
        // this page's rows (≤ PageSize, so no vertical scrollbar), repaint, rebuild
        // the pager and refresh the summary. CurrentCell is cleared first so a
        // shrinking RowCount can't reference a now-invalid cell.
        private void ShowGridPage()
        {
            if (_page < 0) _page = 0;
            if (_page >= PageCount) _page = PageCount - 1;
            _grid.CurrentCell = null;
            _grid.RowCount = Math.Max(0, Math.Min(PageSize, _view.Count - PageStart));
            _grid.Invalidate();
            BuildPager();
            UpdateSummary(_view.Count);
        }

        private void GoToPage(int page)
        {
            int target = Math.Max(0, Math.Min(page, PageCount - 1));
            if (target == _page) return; // dead-end key press (PgUp at first / PgDn
                                         // at last): skip the pager teardown+repaint
            _page = target;
            ShowGridPage();
            LayoutTopControls();
        }

        // ── Pager: First · ‹ · numbered pages (with ellipsis) · › · Last ────
        private void BuildPager()
        {
            if (_bottomPanel == null || _pagerFont == null) return;
            // DEFER the old buttons' disposal — BuildPager is reached from a
            // pager button's OWN Click handler, and disposing a control while
            // its Click event is still on the stack is a latent intermittent
            // ObjectDisposedException (same fix as VaultDashboardForm).
            if (_pagerControls.Count > 0)
            {
                var old = new List<Control>(_pagerControls);
                _pagerControls.Clear();
                foreach (var c in old) _bottomPanel.Controls.Remove(c);
                Action disposeOld = () =>
                {
                    foreach (var c in old)
                        try { c.Dispose(); } catch { }
                };
                if (IsHandleCreated) BeginInvoke(disposeOld);
                else disposeOld();
            }

            int total = PageCount;
            int current = _page + 1; // 1-based for display

            var items = new List<Control>();
            items.Add(PagerLink("First", _page > 0, 0));
            items.Add(PagerLink("‹", _page > 0, _page - 1));
            foreach (int t in PageTokens(current, total))
                items.Add(t < 0 ? (Control)PagerEllipsis()
                                : PagerNumber(t, t - 1 == _page));
            items.Add(PagerLink("›", _page < total - 1, _page + 1));
            items.Add(PagerLink("Last", _page < total - 1, total - 1));

            int gap = S(3);
            int totalW = -gap;
            foreach (var c in items) totalW += c.Width + gap;

            int closeZone = S(140); // keep the pager clear of the Close button
            int avail = Math.Max(totalW, _bottomPanel.ClientSize.Width - closeZone);
            int x = Math.Max(S(8), (avail - totalW) / 2);
            int y = Math.Max(S(4), (_bottomPanel.ClientSize.Height - S(26)) / 2);
            foreach (var c in items)
            {
                c.Left = x;
                c.Top = y;
                x += c.Width + gap;
                _bottomPanel.Controls.Add(c);
                _pagerControls.Add(c);
            }
        }

        // The page numbers to show (1-based; -1 = ellipsis). ≤7 pages: all of
        // them. Otherwise: 1 … window-around-current … last (classic pattern).
        private static List<int> PageTokens(int current, int total)
        {
            var t = new List<int>();
            if (total <= 7)
            {
                for (int i = 1; i <= total; i++) t.Add(i);
                return t;
            }
            int start = Math.Max(2, current - 1);
            int end = Math.Min(total - 1, current + 1);
            if (current <= 3) { start = 2; end = 4; }
            if (current >= total - 2) { start = total - 3; end = total - 1; }
            t.Add(1);
            if (start > 2) t.Add(-1);
            for (int i = start; i <= end; i++) t.Add(i);
            if (end < total - 1) t.Add(-1);
            t.Add(total);
            return t;
        }

        private Button PagerLink(string text, bool enabled, int targetPage)
        {
            int w = TextRenderer.MeasureText(text, _pagerFont).Width + S(12);
            if (w < S(26)) w = S(26);
            var b = new Button
            {
                Text = text,
                Font = _pagerFont,
                Width = w,
                Height = S(26),
                FlatStyle = FlatStyle.Flat,
                BackColor = cBg,
                ForeColor = enabled ? cBrand : Color.FromArgb(180, 186, 196),
                Enabled = enabled,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = cRowAlt;
            if (enabled)
            {
                int tp = targetPage;
                b.Click += (s, e) => GoToPage(tp);
            }
            return b;
        }

        private Button PagerNumber(int pageNum, bool current)
        {
            string text = pageNum.ToString(CultureInfo.InvariantCulture);
            int w = TextRenderer.MeasureText(text, _pagerFont).Width + S(14);
            if (w < S(28)) w = S(28);
            var b = new Button
            {
                Text = text,
                Font = _pagerFont,
                Width = w,
                Height = S(26),
                FlatStyle = FlatStyle.Flat,
                TabStop = false
            };
            if (current)
            {
                b.BackColor = Color.White;
                b.ForeColor = cTextDark;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = cBorder;
                b.Cursor = Cursors.Default;
            }
            else
            {
                b.BackColor = cBg;
                b.ForeColor = cBrand;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = cRowAlt;
                b.Cursor = Cursors.Hand;
                int target = pageNum - 1;
                b.Click += (s, e) => GoToPage(target);
            }
            return b;
        }

        private Label PagerEllipsis()
        {
            return new Label
            {
                Text = "…",
                Font = _pagerFont,
                AutoSize = false,
                Width = S(18),
                Height = S(26),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(150, 156, 166),
                BackColor = cBg
            };
        }

        // ── VirtualMode providers (called for VISIBLE cells only) ───────────
        private void Grid_CellValueNeeded(object sender,
            DataGridViewCellValueEventArgs e)
        {
            int idx = PageStart + e.RowIndex;
            if (e.RowIndex < 0 || idx >= _view.Count) return;
            var en = _view[idx];
            if (e.ColumnIndex == ColTimestamp)
                e.Value = en.Timestamp == DateTime.MinValue
                    ? (object)DBNull.Value : en.Timestamp;
            else
                e.Value = CellText(en, e.ColumnIndex);
        }

        private void Grid_CellFormatting(object sender,
            DataGridViewCellFormattingEventArgs e)
        {
            int idx = PageStart + e.RowIndex;
            if (e.RowIndex < 0 || idx >= _view.Count) return;
            if (e.ColumnIndex == ColAction) // colour + bold by event category
            {
                e.CellStyle.ForeColor = ActionColor(_view[idx].Action);
                e.CellStyle.Font = _cellBold;
            }
        }

        // Colour-code the Action so the log is scannable: green = release/approve,
        // blue = new revision, orange = rollback, red = removal/reject, maroon =
        // lock/unlock, purple = engineer request, grey = create/save/auto.
        private Color ActionColor(string action)
        {
            if (Eq(action, "Release") || Eq(action, "ApproveRequest")) return cGreen;
            if (Eq(action, "NewRevision")) return cBrand;
            if (Eq(action, "Rollback")) return cOrange;
            if (Eq(action, "RemoveFromVault") || Eq(action, "RejectRequest")) return cRed;
            if (Eq(action, "MarkObsolete")) return cMaroon;
            if (Eq(action, "Reinstate")) return cBrand;
            if (Eq(action, "Lock") || Eq(action, "Unlock")) return cMaroon;
            if (action != null && action.StartsWith("Request",
                    StringComparison.OrdinalIgnoreCase)) return cPurple;
            return cTextGray; // Create, Save, AutoPurgeOrphan, anything else
        }

        private void ClearAllFilters()
        {
            _colFilters.Clear();
            _sortColumn = DefaultSortColumn;          // back to newest-first
            _sortDir = ListSortDirection.Descending;
            _search.Text = "";       // raises TextChanged → starts the debounce timer…
            _searchTimer.Stop();     // …cancel it; we apply once now instead of twice
            ApplyFilter();
            _grid.Invalidate();      // repaint headers (clear funnel/sort glyphs)
        }

        // ── Excel-style header: paint arrow + sort glyph ───────────────────
        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0) return;

            Rectangle cb = e.CellBounds;
            var g = e.Graphics;

            using (var bg = new SolidBrush(cBrandDark))
                g.FillRectangle(bg, cb);

            Rectangle textRect = new Rectangle(
                cb.X + S(5), cb.Y, cb.Width - S(34), cb.Height);
            TextRenderer.DrawText(g, _grid.Columns[e.ColumnIndex].HeaderText,
                _cellBold, textRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);

            bool filtered = _colFilters.ContainsKey(e.ColumnIndex);
            Color arrow = filtered ? cFunnel : Color.White;
            int ax = cb.Right - S(11);
            int ay = cb.Y + cb.Height / 2;
            Point[] tri =
            {
                new Point(ax - S(4), ay - S(2)),
                new Point(ax + S(4), ay - S(2)),
                new Point(ax,        ay + S(3))
            };
            using (var ab = new SolidBrush(arrow))
                g.FillPolygon(ab, tri);
            if (filtered)
                using (var fp = new Pen(cFunnel))
                    g.DrawLine(fp, ax - S(5), ay - S(5), ax + S(5), ay - S(5));

            if (_sortColumn == e.ColumnIndex)
            {
                int sx = cb.Right - S(24);
                int sy = cb.Y + cb.Height / 2;
                Point[] s = _sortDir == ListSortDirection.Ascending
                    ? new[] { new Point(sx - S(3), sy + S(2)),
                              new Point(sx + S(3), sy + S(2)),
                              new Point(sx,        sy - S(3)) }
                    : new[] { new Point(sx - S(3), sy - S(2)),
                              new Point(sx + S(3), sy - S(2)),
                              new Point(sx,        sy + S(3)) };
                using (var sb = new SolidBrush(Color.White))
                    g.FillPolygon(sb, s);
            }

            using (var sep = new Pen(Color.FromArgb(70, 110, 150)))
                g.DrawLine(sep, cb.Right - 1, cb.Top + S(4),
                    cb.Right - 1, cb.Bottom - S(4));

            e.Handled = true;
        }

        private void Grid_ColumnHeaderMouseClick(object sender,
            DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.Button != MouseButtons.Left) return;
            var rect = _grid.GetCellDisplayRectangle(e.ColumnIndex, -1, false);
            if (e.X >= rect.Width - GlyphZone)
                ShowColumnFilter(e.ColumnIndex);
            else
                ToggleSort(e.ColumnIndex);
        }

        private void ToggleSort(int col)
        {
            if (_sortColumn == col)
                _sortDir = _sortDir == ListSortDirection.Ascending
                    ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else { _sortColumn = col; _sortDir = ListSortDirection.Ascending; }
            ApplyFilter();
            _grid.Invalidate(); // repaint header sort glyph
        }

        private void ShowColumnFilter(int col)
        {
            var allKeys = new HashSet<string>(
                _all.Select(f => FilterKey(f, col)), StringComparer.OrdinalIgnoreCase);

            var shown = OrderFilterValues(col,
                KeysPassingOtherFilters(col).Distinct(StringComparer.OrdinalIgnoreCase));
            var shownSet = new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);

            HashSet<string> current;
            if (!_colFilters.TryGetValue(col, out current))
                current = null; // null = currently unfiltered (all allowed)

            // Values this column allows that are hidden by another COLUMN's
            // filter — preserved on commit (removing that filter later must
            // not silently drop them). Values hidden only by the TRANSIENT
            // global search are NOT preserved: folding them back meant
            // "search, Clear, tick one, OK" flooded the grid back the moment
            // the search box cleared (the C5 symptom one level up).
            var shownByColumns = new HashSet<string>(
                KeysPassingOtherFilters(col, columnFiltersOnly: true),
                StringComparer.OrdinalIgnoreCase);
            var hiddenByColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in allKeys)
                if (!shownByColumns.Contains(v) &&
                    (current == null || current.Contains(v)))
                    hiddenByColumns.Add(v);

            using (var dlg = new ColumnFilterDialog(_scale,
                _grid.Columns[col].HeaderText, shown, current,
                cBrand, cBrandDark, cBg, cTextDark))
            {
                var rect = _grid.GetCellDisplayRectangle(col, -1, true);
                Point scr = _grid.PointToScreen(new Point(rect.Left, rect.Bottom));
                var wa = Screen.FromControl(this).WorkingArea;
                int x = scr.X, y = scr.Y;
                if (x + dlg.Width > wa.Right) x = wa.Right - dlg.Width;
                if (x < wa.Left) x = wa.Left;
                if (y + dlg.Height > wa.Bottom)
                    y = Math.Max(wa.Top, scr.Y - rect.Height - dlg.Height);
                dlg.Location = new Point(x, y);

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // NO-CHANGE GUARD: OK without touching anything must stay
                    // a no-op even while the global search narrows the list —
                    // compare what the user ENDED UP ticking against what was
                    // allowed-and-shown when the dialog opened.
                    var beforeShown = new HashSet<string>(shownSet,
                        StringComparer.OrdinalIgnoreCase);
                    if (current != null) beforeShown.IntersectWith(current);
                    if (dlg.SelectedValues.SetEquals(beforeShown)) return;

                    // Ticked values + the column-hidden allowed ones.
                    var allowed = new HashSet<string>(dlg.SelectedValues,
                        StringComparer.OrdinalIgnoreCase);
                    allowed.UnionWith(hiddenByColumns);

                    if (allowed.Count >= allKeys.Count) _colFilters.Remove(col);
                    else _colFilters[col] = allowed;
                    ApplyFilter();
                    _grid.Invalidate(); // repaint funnel glyph
                }
            }
        }

        // Size each column to its widest value + ~20%, from a BOUNDED SAMPLE
        // (header + up to WidthSampleRows rows). O(sample), not O(rows).
        private void AutoSizeColumns()
        {
            int sample = Math.Min(_all.Count, WidthSampleRows);
            Font cellFont = _grid.Font;
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                int ci = col.Index;
                int max = TextRenderer.MeasureText(col.HeaderText, _cellBold).Width;
                for (int i = 0; i < sample; i++)
                {
                    int wv = TextRenderer.MeasureText(CellText(_all[i], ci), cellFont).Width;
                    if (wv > max) max = wv;
                }
                int w = (int)(max * 1.20) + GlyphZone + S(10); // arrow + cell padding
                if (w < S(56))  w = S(56);
                if (w > S(540)) w = S(540);
                col.Width = w;
            }
        }

        // Size the popup to fit the columns exactly, capped at 80% of the screen;
        // height is a CONSTANT 20 grid rows (fewer rows just leave blank space).
        private void FitFormSize()
        {
            int totalCols = 0;
            foreach (DataGridViewColumn c in _grid.Columns) totalCols += c.Width;

            var area = Screen.FromControl(this).WorkingArea;

            int borderW = this.Width - this.ClientSize.Width;   // 0 before shown
            int maxClientW = (int)(area.Width * MaxScreenFraction) - borderW;

            // A HORIZONTAL scrollbar appears when the columns are wider than the
            // (capped) client width — it sits along the grid's bottom and would
            // otherwise eat the 20th row. Reserve its height so all 20 rows stay
            // fully visible above it (the "20 row rule").
            bool needsHScroll = totalCols + S(4) > maxClientW;

            int gridH = _grid.ColumnHeadersHeight
                      + _grid.RowTemplate.Height * VisibleRows + S(2)
                      + (needsHScroll ? SystemInformation.HorizontalScrollBarHeight : 0);
            int desiredH = _topPanel.Height + gridH + _bottomPanel.Height;
            int borderH = this.Height - this.ClientSize.Height;
            int maxClientH = (int)(area.Height * MaxScreenFraction) - borderH;
            int clientH = Math.Min(desiredH, maxClientH);
            bool needsVScroll = desiredH > maxClientH; // height got clamped

            int chrome = S(4) + (needsVScroll ? SystemInformation.VerticalScrollBarWidth : 0);
            int clientW = totalCols + chrome;
            if (clientW > maxClientW) clientW = maxClientW;
            int minClientW = S(800);
            if (clientW < minClientW) clientW = minClientW;

            this.ClientSize = new Size(clientW, clientH);
        }

        // Centre the title, the control row and the summary horizontally.
        private void LayoutTopControls()
        {
            if (_topPanel == null || _search == null) return;
            int panelW = _topPanel.ClientSize.Width;
            int gap = S(10);

            if (_title != null)
                _title.Left = Math.Max(S(14), (panelW - _title.Width) / 2);

            int rowW = _search.Width + gap + _btnRefresh.Width + gap
                     + _btnExport.Width + gap + _btnClear.Width
                     + gap + _btnDashboard.Width;
            int startX = Math.Max(S(14), (panelW - rowW) / 2);
            _search.Left      = startX;
            _btnRefresh.Left  = _search.Right + gap;
            _btnExport.Left   = _btnRefresh.Right + gap;
            _btnClear.Left    = _btnExport.Right + gap;
            _btnDashboard.Left = _btnClear.Right + gap;

            if (_summaryPanel != null)
                _summaryPanel.Left = Math.Max(S(14),
                    (panelW - _summaryPanel.Width) / 2);

            if (_cyclePanel != null)
                _cyclePanel.Left = Math.Max(S(14),
                    (panelW - _cyclePanel.Width) / 2);

            if (_lblHint != null)
                _lblHint.Left = Math.Max(S(14), (panelW - _lblHint.Width) / 2);
        }

        // ── Cycle-time analytics (WIP → Released) ───────────────────────────
        // Average duration from a file entering WIP (Create / NewRevision /
        // Unlock / Reinstate) to its next Release, over the selected window
        // (filtered on the RELEASE timestamp). Computed straight from the audit
        // log (_all) — independent of the grid's column filters/search. A
        // release with no preceding WIP-entry event in the log (history began
        // mid-stream) is skipped: it cannot be measured. Hand-checkable — pick a
        // file, find its Release, subtract the most recent preceding WIP-entry.
        private void ComputeCycleTime()
        {
            if (_lblCycle == null) return;

            var recs = ComputeCycleRecords(CycleWindowDays());
            if (recs.Count == 0)
            {
                _lblCycle.Text = "no completed cycles in window";
                return;
            }

            var durations = recs.Select(r => r.Days).ToList();
            double avg = durations.Average();
            durations.Sort();
            int m = durations.Count;
            double median = (m % 2 == 1)
                ? durations[m / 2]
                : (durations[m / 2 - 1] + durations[m / 2]) / 2.0;
            double p90 = Percentile(durations, 0.90); // sorted ascending already

            _lblCycle.Text = string.Format(CultureInfo.InvariantCulture,
                "{0:0.0} d Avg · {1:0.0} d Median · {2:0.0} d p90 · {3} Releases",
                avg, median, p90, m);
        }

        // Build the per-cycle records for the window — the SINGLE source of
        // truth shared by the strip aggregate (above) and the Cycle-Time
        // Analytics detail popup. Groups events by file (DRAWINGS excluded — a
        // drawing follows its model, so counting it double-counts one cycle and
        // skews the average; cycle time is a part/assembly metric), pairs each
        // Release with the most recent preceding WIP-entry, and emits one record
        // per measured release. Division/Bounces are left blank here — the
        // caller enriches them (needs GetAllFiles + the RejectRequest scan).
        private List<CycleRecord> ComputeCycleRecords(int windowDays)
        {
            DateTime cutoff = windowDays > 0
                ? DateTime.Now.AddDays(-windowDays) : DateTime.MinValue;

            var byFile = new Dictionary<string, List<AuditEntry>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in _all)
            {
                if (e.Timestamp == DateTime.MinValue) continue;
                string key = e.FileName ?? "";
                if (key.Length == 0) continue;
                if (key.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    continue; // drawings follow the model — don't double-count
                List<AuditEntry> lst;
                if (!byFile.TryGetValue(key, out lst))
                { lst = new List<AuditEntry>(); byFile[key] = lst; }
                lst.Add(e);
            }

            var recs = new List<CycleRecord>();
            foreach (var kv in byFile)
            {
                var evs = kv.Value;
                evs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                DateTime? start = null;
                foreach (var e in evs)
                {
                    if (IsWipEntry(e.Action))
                        start = e.Timestamp;
                    else if (Eq(e.Action, "Release"))
                    {
                        if (start.HasValue && e.Timestamp >= cutoff)
                        {
                            double days = (e.Timestamp - start.Value).TotalDays;
                            if (days >= 0)
                                recs.Add(new CycleRecord
                                {
                                    FileName    = kv.Key,
                                    ReleasedBy  = e.User ?? "",
                                    StartTime   = start.Value,
                                    ReleaseTime = e.Timestamp,
                                    Days        = days
                                });
                        }
                        // A Release does not itself start a new WIP cycle — the
                        // next NewRevision/Unlock does. Clear so a second Release
                        // with no new WIP-entry between isn't measured twice.
                        start = null;
                    }
                }
            }
            return recs;
        }

        // Nearest-rank percentile (0..1) over an ASCENDING-sorted list.
        internal static double Percentile(List<double> sortedAsc, double p)
        {
            int n = sortedAsc.Count;
            if (n == 0) return 0;
            int rank = (int)Math.Ceiling(p * n);
            if (rank < 1) rank = 1;
            if (rank > n) rank = n;
            return sortedAsc[rank - 1];
        }

        // Open the Cycle-Time Analytics detail popup for the window currently
        // selected on the strip. Enriches the cycle records with Division
        // (filename → WIP path → division key via GetAllFiles) and per-file
        // bounce-backs (RejectRequest = a Master sending a request back), and
        // gathers the bounce-back rows (engineer parsed from the audit note).
        private void OpenCycleDetails()
        {
            try
            {
                int windowDays = CycleWindowDays();
                var recs = ComputeCycleRecords(windowDays);
                DateTime cutoff = windowDays > 0
                    ? DateTime.Now.AddDays(-windowDays) : DateTime.MinValue;

                // filename → division (one GetAllFiles read; non-fatal).
                var divByName = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in DatabaseManager.GetAllFiles())
                    {
                        if (string.IsNullOrEmpty(f.FileName) ||
                            divByName.ContainsKey(f.FileName)) continue;
                        divByName[f.FileName] = DivisionFromPath(f.FilePath);
                    }
                }
                catch { }

                // Bounce-backs (RejectRequest) within the window: per-file count
                // + the individual rows (engineer from "requested by X" note).
                var bounceByFile = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
                var bounces = new List<ReworkRow>();
                foreach (var e in _all)
                {
                    if (!Eq(e.Action, "RejectRequest")) continue;
                    if (e.Timestamp != DateTime.MinValue && e.Timestamp < cutoff)
                        continue;
                    string fn = e.FileName ?? "";
                    if (fn.Length > 0)
                    {
                        int c; bounceByFile.TryGetValue(fn, out c);
                        bounceByFile[fn] = c + 1;
                    }
                    bounces.Add(new ReworkRow
                    {
                        FileName = fn,
                        Engineer = ParseRequester(e.Note),
                        Time     = e.Timestamp
                    });
                }

                foreach (var r in recs)
                {
                    string d; r.Division =
                        divByName.TryGetValue(r.FileName, out d) ? d : "";
                    int b; r.Bounces =
                        bounceByFile.TryGetValue(r.FileName, out b) ? b : 0;
                }

                string label = _cycleWindow == null ? "All time"
                    : (_cycleWindow.SelectedItem?.ToString() ?? "All time");
                using (var f = new CycleTimeDetailForm(_scale, label, recs, bounces))
                    f.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open cycle-time details:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Division key from a file's WIP path ("…\WIP\A - Aurora Shelving\x.sldprt"
        // → "A"); "" when not under a recognizable WIP division. Self-contained
        // so this PR doesn't depend on another in-flight PR's helper.
        private static string DivisionFromPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "";
                const string marker = "\\WIP\\";
                int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                string rest = path.Substring(i + marker.Length);
                int slash = rest.IndexOf('\\');
                string sub = slash >= 0 ? rest.Substring(0, slash) : rest;
                int dash = sub.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0) sub = sub.Substring(0, dash);
                return sub.Trim().ToUpperInvariant();
            }
            catch { return ""; }
        }

        // Parse the requesting engineer from a RejectRequest note, written as
        // "requested by {engineer}; reason: {…}". "(unknown)" when absent.
        private static string ParseRequester(string note)
        {
            if (string.IsNullOrEmpty(note)) return "(unknown)";
            const string p = "requested by ";
            int i = note.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "(unknown)";
            string rest = note.Substring(i + p.Length);
            int semi = rest.IndexOf(';');
            string who = (semi >= 0 ? rest.Substring(0, semi) : rest).Trim();
            return who.Length > 0 ? who : "(unknown)";
        }

        // WIP-entry actions: events that (re)open a file for editing.
        private static bool IsWipEntry(string action) =>
            Eq(action, "Create") || Eq(action, "NewRevision") ||
            Eq(action, "Unlock") || Eq(action, "Reinstate");

        // Selected cycle-time window in days (0 = all time).
        private int CycleWindowDays()
        {
            switch (_cycleWindow == null ? 1 : _cycleWindow.SelectedIndex)
            {
                case 0:  return 30;
                case 1:  return 90;
                case 2:  return 365;
                default: return 0; // All time
            }
        }

        // Whole-log counts are invariant under filtering, so compute them ONCE per
        // load (Refresh) instead of re-scanning _all on every keystroke / page.
        private void ComputeCounts()
        {
            _cntRelease = _cntRevision = _cntRemoval = 0;
            foreach (var e in _all)
            {
                if (Eq(e.Action, "Release"))              _cntRelease++;
                else if (Eq(e.Action, "NewRevision"))     _cntRevision++;
                else if (Eq(e.Action, "RemoveFromVault")) _cntRemoval++;
            }
        }

        private void UpdateSummary(int showing)
        {
            _lblTotal.Text    = $"Total: {_all.Count}";
            _lblRelease.Text  = $"Releases: {_cntRelease}";
            _lblRevision.Text = $"Revisions: {_cntRevision}";
            _lblRemoval.Text  = $"Removals: {_cntRemoval}";

            int from = showing == 0 ? 0 : PageStart + 1;
            int to = Math.Min(showing, PageStart + PageSize);
            _lblShowing.Text =
                $"     (Showing {from}–{to} of {showing}" +
                $" · Page {_page + 1} of {PageCount}" +
                $" · as of {_loadedAt:HH:mm})";

            SetActive(_lblRelease,  IsActionFilter("Release"));
            SetActive(_lblRevision, IsActionFilter("NewRevision"));
            SetActive(_lblRemoval,  IsActionFilter("RemoveFromVault"));
        }

        private void SetActive(Label l, bool active) =>
            l.Font = active ? _summaryFontActive : _summaryFont;

        // A clickable count "link": blue, hand cursor. Text set in UpdateSummary.
        private Label MakeCountLabel(string tip, Action onClick)
        {
            var l = new Label
            {
                Font = _summaryFont,
                ForeColor = cBrand,
                AutoSize = true,
                Margin = new Padding(0, 0, S(14), 0),
                Cursor = Cursors.Hand
            };
            if (_summaryTip == null) _summaryTip = new ToolTip();
            _summaryTip.SetToolTip(l, tip);
            if (onClick != null) l.Click += (s, e) => onClick();
            return l;
        }

        // Is the Action column filtered to EXACTLY this one value?
        private bool IsActionFilter(string action)
        {
            HashSet<string> set;
            return _colFilters.TryGetValue(ColAction, out set)
                && set.Count == 1 && set.Contains(action);
        }

        // Click an action count → toggle the Action column filter to that value.
        private void ToggleActionFilter(string action)
        {
            if (IsActionFilter(action)) _colFilters.Remove(ColAction);
            else
            {
                // Zero-count quick filters are inert (see dashboard twin).
                int cnt = Eq(action, "Release") ? _cntRelease
                        : Eq(action, "NewRevision") ? _cntRevision
                        : Eq(action, "RemoveFromVault") ? _cntRemoval : -1;
                if (cnt == 0) return;
                _colFilters[ColAction] = new HashSet<string>(
                    new[] { action }, StringComparer.OrdinalIgnoreCase);
            }
            ApplyFilter();
            _grid.Invalidate(); // repaint the Action header funnel glyph
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ── Export the CURRENT (filtered) view to CSV ───────────────────────
        private void ExportCsv()
        {
            if (_view.Count == 0)
            {
                MessageBox.Show("Nothing to export — the list is empty.",
                    "BCore PDM — Audit Report",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "Export Audit Report",
                Filter = "CSV files (*.csv)|*.csv",
                FileName = "AuditReport_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    int cols = _grid.Columns.Count;
                    var sb = new StringBuilder();
                    var headers = _grid.Columns.Cast<DataGridViewColumn>()
                        .Select(c => Csv(c.HeaderText));
                    sb.AppendLine(string.Join(",", headers));

                    foreach (var en in _view)
                    {
                        var cells = new string[cols];
                        for (int ci = 0; ci < cols; ci++)
                            cells[ci] = Csv(CellText(en, ci));
                        sb.AppendLine(string.Join(",", cells));
                    }

                    File.WriteAllText(dlg.FileName, sb.ToString());
                    MessageBox.Show("Exported " + _view.Count +
                        " rows to:\n" + dlg.FileName,
                        "BCore PDM — Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Export failed.\n\n" + ex.Message,
                        "BCore PDM — Audit Report",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // Minimal RFC-4180 CSV escaping (mirrors AuditLogger.Csv), incl. its
        // formula-injection guard: the export is opened in Excel, which
        // EXECUTES fields starting with = + - @. Leading apostrophe makes
        // Excel render the value as text.
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            char c0 = field[0];
            if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' ||
                c0 == '\t' || c0 == '\r')
                field = "'" + field;
            if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Excel-style per-column filter popup: a searchable checkbox list of the
        //  (already narrowed) values it's given, with Select All / Clear and
        //  OK / Cancel. SelectedValues = the checked subset (the caller folds in
        //  hidden-allowed values and decides whether that means "no filter").
        //  Self-contained copy of the dashboard's dialog (house "one form, one
        //  file" convention — each form carries its own).
        // ────────────────────────────────────────────────────────────────────
        private sealed class ColumnFilterDialog : Form
        {
            private readonly float _scale;
            private int S(float v) => (int)(v * _scale);

            private const int DisplayCap = 2000;  // max items rendered in the list

            private readonly List<string> _allValues;            // raw distinct values
            private readonly Dictionary<string, bool> _state;    // raw → checked
            private readonly List<string> _visibleRaw = new List<string>();
            private CheckedListBox _list;
            private TextBox _search;
            private Label _count;
            private Button _ok;
            private Color _okBack;

            public HashSet<string> SelectedValues { get; private set; }

            public ColumnFilterDialog(float scale, string columnName,
                List<string> values, HashSet<string> current,
                Color cBrand, Color cBrandDark, Color cBg, Color cText)
            {
                _scale = scale;
                _allValues = values;
                _state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in values)
                    _state[v] = current == null || current.Contains(v);

                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                MinimizeBox = false;
                MaximizeBox = false;
                BackColor = cBg;
                Text = columnName;
                Font = new Font("Segoe UI", 3.6f * _scale);
                ClientSize = new Size(S(236), S(338));

                Font fCtrl = new Font("Segoe UI", 4f * _scale);
                Font fBtn  = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);

                _search = new TextBox
                {
                    Font = fCtrl,
                    Location = new Point(S(8), S(8)),
                    Width = S(220)
                };
                _search.TextChanged += (s, e) => RebuildList();
                AuditReportForm.SetCueBanner(_search, "Search…");
                Controls.Add(_search);

                int rowH = _search.PreferredHeight;
                _search.Height = rowH;

                int btnY = S(10) + rowH + S(6);
                var btnAll = MakeBtn("Select All", cBrand, fBtn,
                    new Point(S(8), btnY), S(108), rowH);
                btnAll.Click += (s, e) => SetMatched(true);
                Controls.Add(btnAll);
                var btnNone = MakeBtn("Clear", cBrandDark, fBtn,
                    new Point(S(120), btnY), S(108), rowH);
                btnNone.Click += (s, e) => SetMatched(false);
                Controls.Add(btnNone);

                int countY = btnY + rowH + S(6);
                _count = new Label
                {
                    Font = fCtrl,
                    AutoSize = false,
                    Location = new Point(S(8), countY),
                    Size = new Size(S(220), S(18)),
                    ForeColor = Color.FromArgb(120, 128, 140)
                };
                Controls.Add(_count);

                int listY = countY + S(20);
                int okY = ClientSize.Height - rowH - S(8);
                _list = new CheckedListBox
                {
                    Font = fCtrl,
                    Location = new Point(S(8), listY),
                    Size = new Size(S(220), okY - listY - S(6)),
                    CheckOnClick = true,
                    BorderStyle = BorderStyle.FixedSingle,
                    IntegralHeight = false,
                    BackColor = Color.White,
                    ForeColor = cText
                };
                _list.ItemCheck += List_ItemCheck;
                Controls.Add(_list);

                _okBack = cBrand;
                _ok = MakeBtn("OK", cBrand, fBtn,
                    new Point(S(8), okY), S(108), rowH);
                _ok.Click += (s, e) => { Commit(); DialogResult = DialogResult.OK; Close(); };
                Controls.Add(_ok);
                var cancel = MakeBtn("Cancel", Color.FromArgb(120, 128, 140), fBtn,
                    new Point(S(120), okY), S(108), rowH);
                cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(cancel);

                AcceptButton = _ok;
                CancelButton = cancel;

                RebuildList();
            }

            private Button MakeBtn(string text, Color back, Font font,
                Point loc, int width, int height)
            {
                var b = new Button
                {
                    Text = text,
                    Font = font,
                    Width = width,
                    Height = height,
                    Location = loc,
                    BackColor = back,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                return b;
            }

            private void List_ItemCheck(object sender, ItemCheckEventArgs e)
            {
                if (e.Index < 0 || e.Index >= _visibleRaw.Count) return;
                _state[_visibleRaw[e.Index]] = (e.NewValue == CheckState.Checked);
                UpdateOkEnabled();
            }

            // OK commits the checked matches; with NONE ticked it would commit
            // an empty filter and blank the whole grid (a typo'd search term +
            // Enter — OK is the AcceptButton — or Clear + OK). Greyed out until
            // at least one match is ticked, like Excel.
            private void UpdateOkEnabled()
            {
                if (_ok == null) return;
                string term = (_search.Text ?? "").Trim();
                bool any = false;
                foreach (var kv in _state)
                    if (kv.Value && MatchesTerm(kv.Key, term)) { any = true; break; }
                _ok.Enabled = any;
                _ok.BackColor = any ? _okBack : Color.FromArgb(170, 175, 182);
            }

            // True when the value (or its "(Blanks)" display form) matches the
            // search term. Shared by RebuildList and SetMatched so Select All /
            // Clear act on exactly the matched set — the rendered list caps at
            // DisplayCap, but matched values past the cap must flip too, or on
            // a high-cardinality column "Clear, then tick one" would leave the
            // rest checked and commit a filter that allows almost everything.
            private static bool MatchesTerm(string raw, string term)
            {
                if (term.Length == 0) return true;
                string disp = raw.Length == 0 ? "(Blanks)" : raw;
                return disp.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // Tick/untick every value matched by the search box (NOT just the
            // rendered subset — see MatchesTerm).
            private void SetMatched(bool check)
            {
                string term = (_search.Text ?? "").Trim();
                foreach (var raw in _allValues)
                    if (MatchesTerm(raw, term)) _state[raw] = check;
                RebuildList();
            }

            private void RebuildList()
            {
                string term = (_search.Text ?? "").Trim();
                _list.ItemCheck -= List_ItemCheck; // no feedback while repopulating
                _list.BeginUpdate();
                _list.Items.Clear();
                _visibleRaw.Clear();
                int matched = 0;
                foreach (var raw in _allValues)
                {
                    if (!MatchesTerm(raw, term)) continue;
                    matched++;
                    if (_visibleRaw.Count >= DisplayCap) continue;
                    _visibleRaw.Add(raw);
                    _list.Items.Add(raw.Length == 0 ? "(Blanks)" : raw, _state[raw]);
                }
                _list.EndUpdate();
                _list.ItemCheck += List_ItemCheck;

                if (matched > _visibleRaw.Count)
                    _count.Text = string.Format(
                        term.Length > 0
                            ? "{0:N0} of {1:N0} matches — OK filters to ticked"
                            : "{0:N0} of {1:N0} shown — type to narrow",
                        _visibleRaw.Count, matched);
                else if (term.Length > 0)
                    _count.Text = matched == 0
                        ? "No matches"
                        : string.Format("{0:N0} match{1} — OK filters to ticked",
                            matched, matched == 1 ? "" : "es");
                else
                    _count.Text = string.Format("{0:N0} value{1}",
                        matched, matched == 1 ? "" : "s");

                UpdateOkEnabled();
            }

            // OK while a search term is active commits the CHECKED MATCHES ONLY
            // (Excel's "filter to search results"). Values outside the search
            // keep whatever checked state they had from BEFORE the term was
            // typed — folding them in would quietly re-allow values the user
            // never saw, so "search, tick one, OK" showed almost everything
            // instead of the one ticked value.
            private void Commit()
            {
                string term = (_search.Text ?? "").Trim();
                SelectedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _state)
                    if (kv.Value && MatchesTerm(kv.Key, term))
                        SelectedValues.Add(kv.Key);
            }
        }
    }
}
