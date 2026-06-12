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
    // Full-screen Vault Dashboard (all users). A sortable, filterable table of
    // EVERY tracked file with its status, revision, owner and dates — gives
    // whole-vault visibility instead of searching file-by-file. Read-only for
    // everyone; engineers open it too (double-click only opens a file).
    //
    // Rows are PAGINATED 20 at a time (PageSize): the grid renders one page and
    // a pager at the bottom (First · ‹ · numbered pages with ellipsis · › · Last)
    // moves between them. The 20-row page size matches the fixed grid height, so
    // a page never needs a vertical scrollbar.
    //
    // DPI-aware (S(v) = v * _scale, fonts = pt * _scale), matching the other
    // BCore PDM forms. Uses a DataGridView (the one tabular surface in the app):
    // it scales to thousands of rows.
    //
    // Filtering is EXCEL-STYLE: every column header has a dropdown arrow that
    // opens a checkbox list of that column's distinct values (with its own
    // search + Select All / Clear). Clicking the header TEXT toggles the sort.
    // A funnel-tinted arrow marks columns with an active filter. A global search
    // box still does a quick cross-column text match (PartNo/Description/Name).
    //
    // Read-only view. Double-clicking a row defers the file open back to the
    // caller via FileToOpen (so VaultManager.OpenByPath runs after the modal
    // dialog closes, mirroring how OpenRequestsPopup works).
    public class VaultDashboardForm : Form
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
        private readonly Color cRowAlt    = Color.FromArgb(245, 247, 250);
        private readonly Color cFunnel    = Color.FromArgb(245, 205, 95); // active-filter glyph

        private DataGridView _grid;
        private TextBox _search;
        private Label _title;
        private Button _btnRefresh;
        private Button _btnExport;
        private Button _btnClear;
        private Button _btnAudit;   // switch to the Audit Report (single-window nav)
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _searchTimer;
        private Font _cellBold; // shared bold font: Status cell + header text

        // Summary strip: clickable count "links" (Total/WIP/Released/Locked/
        // Broken Refs act as quick filters) + a plain showing/page label.
        private FlowLayoutPanel _summaryPanel;
        private Label _lblTotal, _lblWip, _lblReleased, _lblLocked, _lblBroken, _lblShowing;
        private Font _summaryFont;       // base (bold)
        private Font _summaryFontActive; // active quick-filter (bold + underline)
        private ToolTip _summaryTip;     // shared tip for the clickable counts
        // Special non-column filter: show only files with broken references
        // (HasBrokenRefs is a flag, not a column, so it can't live in _colFilters).
        private bool _brokenRefsOnly = false;
        private Label _lblHint;       // faint discoverability footer in the top panel
        private DateTime _loadedAt;   // snapshot time, shown in the summary as "as of HH:mm"
        // Whole-vault counts — invariant under filtering, so cached once per load
        // (UpdateSummary used to re-scan _all 4× on every keystroke / page click).
        private int _cntWip, _cntRel, _cntLck, _cntBrk;
        private const int ColWipDays = 9; // appended "WIP Days" column index

        // Row right-click menu (Open / Open linked / Copy path / Open folder).
        private ContextMenuStrip _rowMenu;
        private ToolStripMenuItem _miOpen, _miOpenLinked, _miCopyPath, _miOpenFolder;
        private int _menuViewIndex = -1;  // ABSOLUTE _view index the menu was opened
                                          // on (not grid-relative): a page change
                                          // while the menu is up can't retarget it
        private string _menuLinkedPath;   // drawing for a model row, model for a drawing row
        private string _menuLinkedConfig; // config to land on when opening a drawing's model

        private const int VisibleRows = 20;     // fixed grid height = 20 rows
        private const int PageSize = VisibleRows; // 20 rows per page (the "20 row rule")
        private const double MaxScreenFraction = 0.80; // popup ≤ 80% of screen
        private int GlyphZone => S(20);         // right-edge hit area for the arrow

        // Current page (0-based) into _view, and the pager controls rebuilt for it.
        private int _page = 0;
        private readonly List<Control> _pagerControls = new List<Control>();
        private Font _pagerFont;
        private Button _btnClose;
        private int PageCount => Math.Max(1, (_view.Count + PageSize - 1) / PageSize);
        private int PageStart => _page * PageSize;

        private List<VaultFile> _all = new List<VaultFile>();
        // The current filtered + sorted rows. In VirtualMode the grid renders
        // straight from this list (no DataGridViewRow objects), so it scales to
        // 50–100k files. Index in the grid == index in _view.
        private List<VaultFile> _view = new List<VaultFile>();
        private const int WidthSampleRows = 400; // cap column-measure cost

        // Active per-column filters: column index → the SET of allowed display
        // values for that column. A column NOT in the map = unfiltered.
        private readonly Dictionary<int, HashSet<string>> _colFilters =
            new Dictionary<int, HashSet<string>>();

        // Current sort (manual, because header clicks are split sort/filter).
        // Default = Modified Date, newest first, so the freshest work is on top.
        private const int DefaultSortColumn = 6; // Modified Date
        private int _sortColumn = DefaultSortColumn;
        private ListSortDirection _sortDir = ListSortDirection.Descending;

        // Set when the user opens a row; the caller opens it after the dialog
        // closes. Null = nothing to open.
        public string FileToOpen { get; private set; }
        // Optional configuration to switch the opened file to (set when "Open
        // Model" is used on a config-specific drawing). Null/empty = active config.
        public string FileToOpenConfig { get; private set; }

        // Set when the user clicks the "Audit Report" button: the caller closes
        // this form and opens the Audit Report instead (single-window switch).
        public bool SwitchToAudit { get; private set; }

        public VaultDashboardForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadData();
        }

        // Keyboard navigation. ProcessCmdKey fires before any control handles the
        // key, so this works wherever focus is. PageUp/Down + Ctrl+Home/End drive
        // the pager; Enter opens the selected row (or applies the search); Ctrl+F
        // jumps to the search box; Esc closes.
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
                    if (_grid != null && _grid.Focused) { OpenSelectedRow(); return true; }
                    break; // let a focused button handle Enter
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OpenSelectedRow()
        {
            if (_grid.CurrentCell == null) return;
            int idx = PageStart + _grid.CurrentCell.RowIndex;
            if (idx < 0 || idx >= _view.Count) return;
            OpenDeferred(_view[idx].FilePath);
        }

        // ── Win32 cue banner (placeholder text — not available on .NET 4.8) ──
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
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
            this.Text = "BCore PDM — Vault Dashboard";
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
            // CENTERED horizontally by LayoutTopControls (on load + resize).
            _topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(120),     // tightened below once the summary is placed
                BackColor = cBg
            };

            _title = new Label
            {
                Text = "BCore VAULT DASHBOARD",
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
                AutoSize = false,            // lets Height match the buttons
                Location = new Point(S(14), rowY),
                Width = S(330)
            };
            _search.TextChanged += (s, e) => DebouncedFilter();
            SetCueBanner(_search, "Search part no, description or filename…");
            _topPanel.Controls.Add(_search);

            // Uniform control height: match search + buttons to the textbox's
            // natural (font-derived) height so the whole row lines up cleanly.
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

            // Switch to the Audit Report (single-window nav): signal the caller and
            // close — the task pane reopens the Audit Report in this window's place.
            _btnAudit = MakeButton("Audit Report »", cBrandDark, fBtn,
                new Point(S(764), rowY), S(150));
            _btnAudit.Height = ctrlH;
            _btnAudit.Click += (s, e) =>
            { SwitchToAudit = true; this.DialogResult = DialogResult.Cancel; Close(); };
            _topPanel.Controls.Add(_btnAudit);

            int summaryY = rowY + ctrlH + S(10);
            // Summary strip = a row of count "links". Total/WIP/Released/Locked/
            // Broken Refs are clickable quick filters; the trailing label shows the
            // page range. A FlowLayoutPanel lays them out left-to-right and is
            // centred as one unit by LayoutTopControls.
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
            _lblTotal    = MakeCountLabel("All files (clear filters)", () => ClearAllFilters());
            _lblWip      = MakeCountLabel("Filter to WIP",      () => ToggleStatusFilter("WIP"));
            _lblReleased = MakeCountLabel("Filter to Released", () => ToggleStatusFilter("Released"));
            _lblLocked   = MakeCountLabel("Filter to Locked",   () => ToggleStatusFilter("Locked"));
            _lblBroken   = MakeCountLabel("Show only broken references", () => ToggleBrokenFilter());
            _lblShowing  = new Label
            {
                Font = _summaryFont,
                ForeColor = cTextGray,
                AutoSize = true,
                Margin = new Padding(S(6), 0, 0, 0)
            };
            _summaryPanel.Controls.AddRange(new Control[]
            {
                _lblTotal, _lblWip, _lblReleased, _lblLocked, _lblBroken, _lblShowing
            });
            _topPanel.Controls.Add(_summaryPanel);

            // Faint discoverability footer below the counts — new users won't
            // guess the right-click menu / column-resize / clickable counts.
            int hintY = summaryY + (int)_summaryFont.GetHeight() + S(4);
            _lblHint = new Label
            {
                Text = "Double-click or right-click a row to open  ·  Drag a column edge to "
                     + "resize  ·  Click a count to filter  ·  PgUp/PgDn to page",
                Font = new Font("Segoe UI", 3.1f * _scale),
                ForeColor = Color.FromArgb(150, 158, 170),
                AutoSize = true,
                Location = new Point(S(14), hintY)
            };
            _topPanel.Controls.Add(_lblHint);

            // Tighten the panel to the hint so the grid sits right below it.
            _topPanel.Height = hintY + (int)_lblHint.Font.GetHeight() + S(8);
            _topPanel.Resize += (s, e) => LayoutTopControls();

            // ── Bottom panel: Close ───────────────────────────────────────
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
            // Re-centre the pager when the form (and so the bottom panel) resizes.
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
                // Widths are set per-column to fit content (+20%) in AutoSizeColumns.
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false,
                AutoGenerateColumns = false,
                // VirtualMode: scales to 50–100k rows — see CellValueNeeded.
                VirtualMode = true,
                Cursor = Cursors.Hand
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
            _grid.CellDoubleClick += Grid_CellDoubleClick;
            _grid.MouseDown += Grid_MouseDown; // right-click row menu
            // Excel-style header: paint the filter arrow + sort glyph; split the
            // header click between sort (text) and filter (arrow).
            _grid.CellPainting += Grid_CellPainting;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;

            // Row right-click menu: Open, Open linked drawing/model, Copy path,
            // Open containing folder. Item text/enabled is set per-row in
            // Grid_MouseDown (the linked item flips Drawing↔Model by row type).
            // Custom flat renderer (white background, brand-blue hover, no image
            // gutter) + house font so it matches the app, not the dull grey OS menu.
            _rowMenu = new ContextMenuStrip
            {
                Font = new Font("Segoe UI", 4f * _scale),
                ShowImageMargin = false,
                Renderer = new MenuRenderer(
                    new MenuColors(cBrand, cBorder, Color.White),
                    cTextDark, Color.FromArgb(160, 166, 176))
            };
            _miOpen       = new ToolStripMenuItem("Open", null, (s, e) => MenuOpen());
            _miOpenLinked = new ToolStripMenuItem("Open Drawing", null, (s, e) => MenuOpenLinked());
            _miCopyPath   = new ToolStripMenuItem("Copy File Path", null, (s, e) => MenuCopyPath());
            _miOpenFolder = new ToolStripMenuItem("Open Containing Folder", null, (s, e) => MenuOpenFolder());
            _rowMenu.Items.AddRange(new ToolStripItem[]
            {
                _miOpen, _miOpenLinked, new ToolStripSeparator(), _miCopyPath, _miOpenFolder
            });
            foreach (ToolStripItem it in _rowMenu.Items)
                it.Padding = new Padding(S(6), S(3), S(18), S(3));
            // VirtualMode providers — the grid pulls value / colour / tooltip for
            // ONLY the visible cells, so nothing is materialised for off-screen
            // rows (the key to handling tens of thousands of files).
            _grid.CellValueNeeded += Grid_CellValueNeeded;
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellToolTipTextNeeded += Grid_CellToolTipTextNeeded;

            AddColumn("File Name");
            AddColumn("Part No");
            AddColumn("Description");
            AddColumn("Status");
            AddColumn("Rev");
            AddColumn("Modified By");
            // Typed DateTime + format so the date columns sort CHRONOLOGICALLY
            // (a string "MM/dd/yyyy" would sort alphabetically — wrong order).
            AddColumn("Modified Date", typeof(DateTime), "MM/dd/yyyy HH:mm");
            AddColumn("Released By");
            AddColumn("Released Date", typeof(DateTime), "MM/dd/yyyy HH:mm");
            // Derived staleness metric: days since a WIP file was last modified
            // (blank for non-WIP). Appended at the end (index 9) so the existing
            // hard-coded column indices (Status=3, dates=6/8) are untouched.
            AddColumn("WIP Days");
            _grid.Columns[ColWipDays].DefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleRight;
            _grid.Columns[ColWipDays].HeaderCell.ToolTipText =
                "Days since last modified (WIP files only) · click to sort by staleness";

            // Add Fill control FIRST, then the docked edge panels, so the grid
            // occupies the leftover space between them.
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
                _rowMenu?.Dispose();
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
                // Programmatic: header clicks don't auto-sort (we split the click
                // between sort and the filter arrow, and sort the data ourselves).
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
                _all = DatabaseManager.GetAllFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not load the vault file list.\n\n" + ex.Message,
                    "BCore PDM — Vault Dashboard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _all = new List<VaultFile>();
            }
            _loadedAt = DateTime.Now;   // shown in the summary as "as of HH:mm"
            ComputeVaultCounts();       // cache whole-vault counts for the summary
            // Populate the grid, then size columns to the full data ONCE and fit
            // the form to them — so columns/width stay stable while the user
            // types in the search box (no per-keystroke resizing).
            ApplyFilter();
            AutoSizeColumns();
            FitFormSize();
            LayoutTopControls();
        }

        private void DebouncedFilter()
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        // The display text shown (and filtered/sorted on) for a column — kept in
        // lock-step with the values written into the grid in ApplyFilter, so the
        // filter checkbox list matches exactly what the user sees.
        private string CellText(VaultFile f, int col)
        {
            switch (col)
            {
                case 0: return f.FileName ?? "";
                case 1: return f.PartNumber ?? "";
                case 2: return f.Description ?? "";
                case 3: return f.Status ?? "";
                case 4: return f.Revision ?? "";
                case 5: return f.ModifiedBy ?? "";
                case 6: return f.ModifiedDate == DateTime.MinValue
                            ? "" : f.ModifiedDate.ToString("MM/dd/yyyy HH:mm");
                case 7: return f.ReleasedBy ?? "";
                case 8: return f.ReleasedDate == DateTime.MinValue
                            ? "" : f.ReleasedDate.ToString("MM/dd/yyyy HH:mm");
                case ColWipDays:
                    int d = WipDays(f);
                    return d < 0 ? "" : d.ToString(CultureInfo.InvariantCulture);
                default: return "";
            }
        }

        // Days a WIP file has sat since its last save (staleness). -1 = not a WIP
        // file (or no modified date): shown as a blank cell. As the smallest value
        // it sorts to the TOP ascending / the BOTTOM descending — so sorting the
        // column descending surfaces the stalest real WIP files first, blanks last.
        private int WipDays(VaultFile f)
        {
            if (!Eq(f.Status, "WIP") || f.ModifiedDate == DateTime.MinValue) return -1;
            int d = (int)(DateTime.Now.Date - f.ModifiedDate.Date).TotalDays;
            return d < 0 ? 0 : d;
        }

        // Sort key: date columns sort by the real DateTime (chronological); WIP
        // Days sorts by the numeric age; everything else by lower-cased text.
        private Func<VaultFile, IComparable> KeySelector(int col)
        {
            if (col == 6) return f => f.ModifiedDate;
            if (col == 8) return f => f.ReleasedDate;
            if (col == ColWipDays) return f => WipDays(f);
            return f => (CellText(f, col) ?? "").ToLowerInvariant();
        }

        // The value a column is FILTERED on. Same as the display text EXCEPT for
        // the date columns, which are grouped to DAY granularity (so the filter
        // list shows distinct days, not every minute) — checking a day keeps
        // every time on that day. Invariant culture so the key is locale-stable.
        private string FilterKey(VaultFile f, int col)
        {
            if (col == 6) return f.ModifiedDate == DateTime.MinValue
                ? "" : f.ModifiedDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            if (col == 8) return f.ReleasedDate == DateTime.MinValue
                ? "" : f.ReleasedDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            return CellText(f, col);
        }

        // Filter keys of `col` over the rows that pass every OTHER active column
        // filter + the global search — so a column's dropdown NARROWS to what's
        // relevant given the other filters, like Excel.
        private IEnumerable<string> KeysPassingOtherFilters(int col)
        {
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var f in _all)
            {
                if (_brokenRefsOnly && !f.HasBrokenRefs) continue;
                bool ok = true;
                foreach (var kv in _colFilters)
                {
                    if (kv.Key == col) continue;
                    if (!kv.Value.Contains(FilterKey(f, kv.Key))) { ok = false; break; }
                }
                if (!ok) continue;
                if (term.Length > 0 &&
                    !((f.PartNumber  ?? "").ToLowerInvariant().Contains(term)
                   || (f.Description ?? "").ToLowerInvariant().Contains(term)
                   || (f.FileName    ?? "").ToLowerInvariant().Contains(term)))
                    continue;
                yield return FilterKey(f, col);
            }
        }

        // Order a column's filter values for display: date columns CHRONOLOGICALLY
        // (parsing the day key; blanks first), everything else alphabetically.
        private List<string> OrderFilterValues(int col, IEnumerable<string> values)
        {
            if (col == 6 || col == 8)
                return values.OrderBy(v =>
                {
                    DateTime d;
                    return DateTime.TryParseExact(v, "MM/dd/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        ? d : DateTime.MinValue; // blanks sort earliest
                }).ToList();
            if (col == ColWipDays) // numeric: "2" before "10"; blank first
                return values.OrderBy(v =>
                {
                    int n;
                    return int.TryParse(v, out n) ? n : -1;
                }).ToList();
            return values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void ApplyFilter()
        {
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();

            var view = _all.Where(f =>
            {
                // Broken-refs-only quick filter (from the summary strip).
                if (_brokenRefsOnly && !f.HasBrokenRefs) return false;
                // Per-column (Excel-style) value filters — ALL must pass.
                // FilterKey (not CellText) so date columns match by DAY.
                foreach (var kv in _colFilters)
                    if (!kv.Value.Contains(FilterKey(f, kv.Key)))
                        return false;
                // Global quick text search across the three text columns.
                if (term.Length == 0) return true;
                return (f.PartNumber  ?? "").ToLowerInvariant().Contains(term)
                    || (f.Description ?? "").ToLowerInvariant().Contains(term)
                    || (f.FileName    ?? "").ToLowerInvariant().Contains(term);
            }).ToList();

            if (_sortColumn >= 0)
            {
                var key = KeySelector(_sortColumn);
                view = (_sortDir == ListSortDirection.Ascending
                    ? view.OrderBy(key)
                    : view.OrderByDescending(key)).ToList();
            }

            // VirtualMode + pagination: swap the backing list, jump back to page 1
            // (a new filter/sort/search starts at the top), and render that page.
            // No per-row objects are created, so this stays instant at 50–100k rows.
            _view = view;
            _page = 0;
            ShowGridPage();          // also refreshes the summary (page indicator)
            LayoutTopControls();     // re-centre: the summary width changed
        }

        // Render the current page: clamp _page, set the grid's RowCount to just
        // this page's rows (≤ PageSize, so no vertical scrollbar), repaint, and
        // rebuild the pager. CurrentCell is cleared first so a shrinking RowCount
        // can't reference a now-invalid cell.
        private void ShowGridPage()
        {
            if (_page < 0) _page = 0;
            if (_page >= PageCount) _page = PageCount - 1;
            _grid.CurrentCell = null;
            _grid.RowCount = Math.Max(0, Math.Min(PageSize, _view.Count - PageStart));
            _grid.Invalidate();
            BuildPager();
            // Refresh the summary HERE so the "Showing X–Y · Page N of M" indicator
            // can never desync from the rendered page — any future direct
            // ShowGridPage() caller stays correct without remembering to follow up.
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
        // Rebuilt on every page move / filter / resize. The current page is a
        // boxed button; First/Last/arrows grey out at the ends.
        private void BuildPager()
        {
            if (_bottomPanel == null || _pagerFont == null) return;
            foreach (var c in _pagerControls)
            {
                _bottomPanel.Controls.Remove(c);
                c.Dispose();
            }
            _pagerControls.Clear();

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

        // First / Last / ‹ / › link button (greyed + inert when not applicable).
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

        // One numbered page button. The current page is boxed (white + border);
        // the others are plain blue links.
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
            var f = _view[idx];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = f.FileName; break;
                case 1: e.Value = f.PartNumber; break;
                case 2: e.Value = f.Description; break;
                case 3: e.Value = f.Status; break;
                case 4: e.Value = f.Revision; break;
                case 5: e.Value = f.ModifiedBy; break;
                // Real DateTime so the column's format applies; DBNull for a
                // missing date → an empty (not 01/01/0001) cell.
                case 6: e.Value = f.ModifiedDate == DateTime.MinValue
                            ? (object)DBNull.Value : f.ModifiedDate; break;
                case 7: e.Value = f.ReleasedBy; break;
                case 8: e.Value = f.ReleasedDate == DateTime.MinValue
                            ? (object)DBNull.Value : f.ReleasedDate; break;
                case ColWipDays: e.Value = CellText(f, ColWipDays); break;
            }
        }

        private void Grid_CellFormatting(object sender,
            DataGridViewCellFormattingEventArgs e)
        {
            int idx = PageStart + e.RowIndex;
            if (e.RowIndex < 0 || idx >= _view.Count) return;
            var f = _view[idx];
            if (e.ColumnIndex == 3) // Status — coloured + bold
            {
                e.CellStyle.ForeColor = StatusColor(f.Status);
                e.CellStyle.Font = _cellBold;
            }
            else if (e.ColumnIndex == 0 && f.HasBrokenRefs) // File Name — red
            {
                e.CellStyle.ForeColor = cRed;
            }
        }

        private void Grid_CellToolTipTextNeeded(object sender,
            DataGridViewCellToolTipTextNeededEventArgs e)
        {
            int idx = PageStart + e.RowIndex;
            if (e.RowIndex < 0 || idx >= _view.Count) return; // keep header tooltips
            var f = _view[idx];
            if (e.ColumnIndex == 0 && f.HasBrokenRefs)
                e.ToolTipText = "Has broken references";
            else if (e.ColumnIndex == 3 && Eq(f.Status, "Locked") &&
                     !string.IsNullOrEmpty(f.LockedBy))
                e.ToolTipText = "Locked by " + f.LockedBy;
        }

        private void ClearAllFilters()
        {
            _colFilters.Clear();
            _brokenRefsOnly = false;                  // clear the broken-refs view
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

            // Header text (leaves room on the right for the glyphs).
            Rectangle textRect = new Rectangle(
                cb.X + S(5), cb.Y, cb.Width - S(34), cb.Height);
            TextRenderer.DrawText(g, _grid.Columns[e.ColumnIndex].HeaderText,
                _cellBold, textRect, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);

            // Filter arrow at the far right. Tinted (funnel) when this column has
            // an active filter, so a Master can see at a glance what's filtered.
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

            // Small sort triangle just left of the arrow when this is the sort col.
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

            // Faint divider between headers (the solid fill removed the default).
            using (var sep = new Pen(Color.FromArgb(70, 110, 150)))
                g.DrawLine(sep, cb.Right - 1, cb.Top + S(4),
                    cb.Right - 1, cb.Bottom - S(4));

            e.Handled = true;
        }

        private void Grid_ColumnHeaderMouseClick(object sender,
            DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.Button != MouseButtons.Left) return;
            // cutOverflow:false → the FULL column width, matching e.X (which is
            // relative to the cell's full left edge). Using the clipped width
            // would mis-split sort vs filter on a horizontally-scrolled column.
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

        // Open the Excel-style checkbox filter for one column, anchored under its
        // header arrow. The list NARROWS to the values present given every OTHER
        // active filter (+ the global search), like Excel.
        private void ShowColumnFilter(int col)
        {
            // Every distinct filter key for this column across the WHOLE dataset —
            // used to decide "all selected = no filter" and to know which allowed
            // values are merely hidden by another filter (must be preserved).
            var allKeys = new HashSet<string>(
                _all.Select(f => FilterKey(f, col)), StringComparer.OrdinalIgnoreCase);

            // The values actually shown: narrowed by the other filters + search.
            var shown = OrderFilterValues(col,
                KeysPassingOtherFilters(col).Distinct(StringComparer.OrdinalIgnoreCase));
            var shownSet = new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);

            HashSet<string> current;
            if (!_colFilters.TryGetValue(col, out current))
                current = null; // null = currently unfiltered (all allowed)

            // Values this column currently ALLOWS but that are hidden by another
            // filter (not in `shown`). Kept so removing the other filter later
            // doesn't silently drop them — they just aren't shown right now.
            var hiddenAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in allKeys)
                if (!shownSet.Contains(v) && (current == null || current.Contains(v)))
                    hiddenAllowed.Add(v);

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
                    // Ticked (visible) values + the allowed-but-hidden ones.
                    var allowed = new HashSet<string>(dlg.SelectedValues,
                        StringComparer.OrdinalIgnoreCase);
                    allowed.UnionWith(hiddenAllowed);

                    if (allowed.Count >= allKeys.Count) _colFilters.Remove(col);
                    else _colFilters[col] = allowed;
                    ApplyFilter();
                    _grid.Invalidate(); // repaint funnel glyph
                }
            }
        }

        // Size each column to its widest value + ~20%. Measured from a BOUNDED
        // SAMPLE (header + up to WidthSampleRows rows) with TextRenderer, NOT
        // GetPreferredWidth(AllCells) — the latter would measure every cell
        // (100k×9) and is unusable in VirtualMode anyway. O(sample), not O(rows).
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

        // Size the popup to fit the columns exactly (no blank space after the
        // last column), capped at 80% of the screen; height is a CONSTANT 20 grid
        // rows (fewer rows just leave blank space below).
        private void FitFormSize()
        {
            int totalCols = 0;
            foreach (DataGridViewColumn c in _grid.Columns) totalCols += c.Width;

            var area = Screen.FromControl(this).WorkingArea;

            // Height = a CONSTANT 20 grid rows + panels, capped at 80% of the
            // screen. On a small screen / high DPI the cap can bite, which forces
            // the grid to show a vertical scrollbar — detect that so the width can
            // reserve room for it (otherwise the last column clips under it).
            int gridH = _grid.ColumnHeadersHeight
                      + _grid.RowTemplate.Height * VisibleRows + S(2);
            int desiredH = _topPanel.Height + gridH + _bottomPanel.Height;
            int borderH = this.Height - this.ClientSize.Height;
            int maxClientH = (int)(area.Height * MaxScreenFraction) - borderH;
            int clientH = Math.Min(desiredH, maxClientH);
            bool needsVScroll = desiredH > maxClientH; // height got clamped

            // Pages cap at PageSize (== VisibleRows) rows, so normally no vertical
            // scrollbar — reserve its width ONLY when the height clamp forced one.
            int chrome = S(4) + (needsVScroll ? SystemInformation.VerticalScrollBarWidth : 0);
            int clientW = totalCols + chrome;

            int borderW = this.Width - this.ClientSize.Width;   // 0 before shown
            int maxClientW = (int)(area.Width * MaxScreenFraction) - borderW;
            if (clientW > maxClientW) clientW = maxClientW;
            int minClientW = S(800);                            // keep top row visible
            if (clientW < minClientW) clientW = minClientW;

            this.ClientSize = new Size(clientW, clientH);
        }

        // Centre the title, the control row (search / Refresh / Export / Clear)
        // and the summary horizontally in the top panel. Called on load and on
        // every top-panel resize so it stays centred.
        private void LayoutTopControls()
        {
            if (_topPanel == null || _search == null) return;
            int panelW = _topPanel.ClientSize.Width;
            int gap = S(10);

            if (_title != null)
                _title.Left = Math.Max(S(14), (panelW - _title.Width) / 2);

            int rowW = _search.Width + gap + _btnRefresh.Width + gap
                     + _btnExport.Width + gap + _btnClear.Width
                     + gap + _btnAudit.Width;
            int startX = Math.Max(S(14), (panelW - rowW) / 2);
            _search.Left     = startX;
            _btnRefresh.Left = _search.Right + gap;
            _btnExport.Left  = _btnRefresh.Right + gap;
            _btnClear.Left   = _btnExport.Right + gap;
            _btnAudit.Left   = _btnClear.Right + gap;

            if (_summaryPanel != null)
                _summaryPanel.Left = Math.Max(S(14),
                    (panelW - _summaryPanel.Width) / 2);

            if (_lblHint != null)
                _lblHint.Left = Math.Max(S(14), (panelW - _lblHint.Width) / 2);
        }

        // Whole-vault counts are invariant under filtering, so compute them ONCE
        // per load (Refresh) instead of re-scanning _all on every keystroke.
        private void ComputeVaultCounts()
        {
            _cntWip = _cntRel = _cntLck = _cntBrk = 0;
            foreach (var f in _all)
            {
                if (Eq(f.Status, "WIP"))           _cntWip++;
                else if (Eq(f.Status, "Released")) _cntRel++;
                else if (Eq(f.Status, "Locked"))   _cntLck++;
                if (f.HasBrokenRefs)               _cntBrk++;
            }
        }

        private void UpdateSummary(int showing)
        {
            _lblTotal.Text    = $"Total: {_all.Count}";
            _lblWip.Text      = $"WIP: {_cntWip}";
            _lblReleased.Text = $"Released: {_cntRel}";
            _lblLocked.Text   = $"Locked: {_cntLck}";
            _lblBroken.Text   = $"Broken Refs: {_cntBrk}";

            int from = showing == 0 ? 0 : PageStart + 1;
            int to = Math.Min(showing, PageStart + PageSize);
            _lblShowing.Text =
                $"     (Showing {from}–{to} of {showing}" +
                $" · Page {_page + 1} of {PageCount}" +
                $" · as of {_loadedAt:HH:mm})";

            // Underline the active quick-filter so it's obvious what's applied.
            SetActive(_lblWip,      IsStatusFilter("WIP"));
            SetActive(_lblReleased, IsStatusFilter("Released"));
            SetActive(_lblLocked,   IsStatusFilter("Locked"));
            SetActive(_lblBroken,   _brokenRefsOnly);
        }

        private void SetActive(Label l, bool active) =>
            l.Font = active ? _summaryFontActive : _summaryFont;

        // ── Summary quick-filter helpers ────────────────────────────────────
        // A clickable count "link": blue, hand cursor. Text is set in
        // UpdateSummary; the tip explains the click action.
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

        // Is the Status column filtered to EXACTLY this one value?
        private bool IsStatusFilter(string status)
        {
            HashSet<string> set;
            return _colFilters.TryGetValue(3, out set)
                && set.Count == 1 && set.Contains(status);
        }

        // Click a status count → toggle the Status column filter to that value.
        private void ToggleStatusFilter(string status)
        {
            if (IsStatusFilter(status)) _colFilters.Remove(3);
            else _colFilters[3] = new HashSet<string>(
                new[] { status }, StringComparer.OrdinalIgnoreCase);
            ApplyFilter();
            _grid.Invalidate(); // repaint the Status header funnel glyph
        }

        // Click "Broken Refs" → toggle the broken-references-only view.
        private void ToggleBrokenFilter()
        {
            _brokenRefsOnly = !_brokenRefsOnly;
            ApplyFilter();
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private Color StatusColor(string status)
        {
            if (Eq(status, "Released")) return cGreen;
            if (Eq(status, "Locked"))   return cMaroon;
            if (Eq(status, "WIP"))      return cOrange;
            return cTextGray;
        }

        // ── Row open (deferred to caller) ───────────────────────────────────
        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            int idx = PageStart + e.RowIndex;
            if (e.RowIndex < 0 || idx >= _view.Count) return;
            OpenDeferred(_view[idx].FilePath);
        }

        // ── Row right-click menu ────────────────────────────────────────────
        private void Grid_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return; // not on a data row (header / empty area)
            int idx = PageStart + hit.RowIndex;
            if (idx < 0 || idx >= _view.Count) return;

            _menuViewIndex = idx; // store the ABSOLUTE _view index, not grid-relative
            // Select the row under the cursor for visual feedback (FullRowSelect).
            _grid.CurrentCell = _grid[Math.Max(0, hit.ColumnIndex), hit.RowIndex];

            // The linked item flips Drawing↔Model by row type; disabled if none.
            // Resolved IN-MEMORY from _all (the dashboard never hits the DB per
            // row — see GetAllFiles), by the shared {basename}.ext convention.
            var f = _view[idx];
            bool isDrawing = (f.FileName ?? "")
                .EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);
            _menuLinkedPath = FindLinkedPath(f, wantDrawing: !isDrawing);
            // Opening a config-specific drawing's model → land on that config.
            _menuLinkedConfig = isDrawing
                ? DrawingConfigToOpen(f, _menuLinkedPath) : null;
            _miOpenLinked.Text = isDrawing ? "Open Model" : "Open Drawing";
            _miOpenLinked.Enabled = !string.IsNullOrEmpty(_menuLinkedPath);

            _rowMenu.Show(_grid, e.Location);
        }

        // The path of the file linked to f, resolved from the in-memory _all list
        // (no DB/disk hit). wantDrawing → f's drawing; else f's model.
        //
        // Drawing→model uses the drawing's ReferencedModel link FIRST (covers a
        // config-specific {configName}.slddrw, whose basename differs from the
        // model — e.g. DEMO.05.slddrw documents "FILE 1.sldprt" config DEMO.05),
        // then falls back to the shared {basename} convention. Model→drawing
        // prefers the shared {basename}.slddrw, else any drawing that references
        // this model. null if none.
        private string FindLinkedPath(VaultFile f, bool wantDrawing)
        {
            if (f == null || string.IsNullOrEmpty(f.FileName)) return null;
            string baseName = Path.GetFileNameWithoutExtension(f.FileName);

            if (!wantDrawing)
            {
                // f is a drawing → find its model.
                string refModel = f.ReferencedModel;
                string refName = string.IsNullOrEmpty(refModel)
                    ? null : Path.GetFileName(refModel);
                VaultFile byBase = null;
                foreach (var o in _all)
                {
                    if (ReferenceEquals(o, f)) continue;
                    if (!IsModel(o)) continue;
                    // Explicit reference match (by full path or filename) wins.
                    if (refName != null &&
                        (PathEq(o.FilePath, refModel) || NameEq(o.FileName, refName)))
                        return o.FilePath;
                    if (byBase == null && BaseEq(o.FileName, baseName))
                        byBase = o;
                }
                return byBase == null ? null : byBase.FilePath;
            }

            // f is a model → find its drawing.
            VaultFile byRef = null;
            foreach (var o in _all)
            {
                if (ReferenceEquals(o, f)) continue;
                if (!(o.FileName ?? "").EndsWith(".slddrw",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (BaseEq(o.FileName, baseName)) return o.FilePath; // shared drawing
                if (byRef == null && !string.IsNullOrEmpty(o.ReferencedModel) &&
                    PathEq(o.ReferencedModel, f.FilePath))
                    byRef = o; // a config-specific drawing referencing this model
            }
            return byRef == null ? null : byRef.FilePath;
        }

        private static bool IsModel(VaultFile o)
        {
            string ext = (Path.GetExtension(o.FileName ?? "") ?? "").ToLowerInvariant();
            return ext == ".sldprt" || ext == ".sldasm";
        }
        private static bool PathEq(string a, string b) =>
            !string.IsNullOrEmpty(a) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool NameEq(string a, string b) =>
            string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        private static bool BaseEq(string fileName, string baseName) =>
            string.Equals(Path.GetFileNameWithoutExtension(fileName ?? ""),
                baseName, StringComparison.OrdinalIgnoreCase);

        // The VaultFile the menu was opened on (null if the view changed under it).
        private VaultFile MenuRow()
        {
            int idx = _menuViewIndex; // absolute index captured at right-click time
            if (idx < 0 || idx >= _view.Count) return null;
            return _view[idx];
        }

        // Defer the open to the caller (OpenByPath runs after this modal closes).
        // config = optional configuration to switch the file to once open.
        private void OpenDeferred(string path, string config = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            FileToOpen = path;
            FileToOpenConfig = config;
            this.DialogResult = DialogResult.OK;
            Close();
        }

        private void MenuOpen()
        {
            var f = MenuRow();
            if (f != null) OpenDeferred(f.FilePath);
        }

        private void MenuOpenLinked()
        {
            if (!string.IsNullOrEmpty(_menuLinkedPath))
                OpenDeferred(_menuLinkedPath, _menuLinkedConfig);
        }

        // The configuration a drawing documents — used to land its model on the
        // right config. Prefers the drawing's ReferencedConfigs when it names a
        // SINGLE config; else falls back to a config-specific {configName}.slddrw
        // filename (basename ≠ the model's). Null = open at the active config
        // (shared / all-config drawing). The switch itself is best-effort.
        private string DrawingConfigToOpen(VaultFile drawing, string modelPath)
        {
            if (drawing == null) return null;
            var refs = (drawing.ReferencedConfigs ?? "")
                .Split(',').Select(s => s.Trim())
                .Where(s => s.Length > 0).ToList();
            if (refs.Count == 1) return refs[0];
            if (refs.Count > 1) return null; // documents several → don't force one

            string drwBase = Path.GetFileNameWithoutExtension(drawing.FileName ?? "");
            string modelBase = Path.GetFileNameWithoutExtension(
                Path.GetFileName(modelPath ?? ""));
            if (!string.IsNullOrEmpty(drwBase) &&
                !string.Equals(drwBase, modelBase, StringComparison.OrdinalIgnoreCase))
                return drwBase; // config-specific by filename convention
            return null;        // shared drawing → active config
        }

        private void MenuCopyPath()
        {
            var f = MenuRow();
            if (f == null || string.IsNullOrEmpty(f.FilePath)) return;
            try { Clipboard.SetText(f.FilePath); } catch { /* clipboard busy — ignore */ }
        }

        // Open Explorer with the file selected (or its folder if the file is gone).
        private void MenuOpenFolder()
        {
            var f = MenuRow();
            if (f == null || string.IsNullOrEmpty(f.FilePath)) return;
            try
            {
                if (File.Exists(f.FilePath))
                {
                    System.Diagnostics.Process.Start(
                        "explorer.exe", "/select,\"" + f.FilePath + "\"");
                    return;
                }
                string dir = Path.GetDirectoryName(f.FilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    MessageBox.Show("Folder not found:\n" + dir,
                        "BCore PDM — Vault Dashboard",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the folder.\n\n" + ex.Message,
                    "BCore PDM — Vault Dashboard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── Export the CURRENT (filtered) view to CSV ───────────────────────
        private void ExportCsv()
        {
            if (_view.Count == 0)
            {
                MessageBox.Show("Nothing to export — the list is empty.",
                    "BCore PDM — Vault Dashboard",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "Export Vault Dashboard",
                Filter = "CSV files (*.csv)|*.csv",
                FileName = "VaultDashboard_" +
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

                    // Export straight from the backing list (VirtualMode has no
                    // materialised rows). CellText gives each column's displayed
                    // string, including the formatted dates.
                    foreach (var f in _view)
                    {
                        var cells = new string[cols];
                        for (int ci = 0; ci < cols; ci++)
                            cells[ci] = Csv(CellText(f, ci));
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
                        "BCore PDM — Vault Dashboard",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // Minimal RFC-4180 CSV escaping (mirrors AuditLogger.Csv).
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Flat, on-brand renderer for the row right-click menu: white drop-down,
        //  brand-blue highlight on hover, subtle border. Replaces the dull grey
        //  gradient of the default OS menu so it matches the rest of the app.
        // ────────────────────────────────────────────────────────────────────
        private sealed class MenuColors : ProfessionalColorTable
        {
            private readonly Color _sel, _border, _bg;
            public MenuColors(Color sel, Color border, Color bg)
            { _sel = sel; _border = border; _bg = bg; }
            public override Color MenuItemSelected => _sel;
            public override Color MenuItemSelectedGradientBegin => _sel;
            public override Color MenuItemSelectedGradientEnd => _sel;
            public override Color MenuItemBorder => _sel;
            public override Color MenuBorder => _border;
            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color SeparatorDark => _border;
            public override Color SeparatorLight => _bg;
        }

        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _text, _textDisabled;
            public MenuRenderer(ProfessionalColorTable t, Color text, Color textDisabled)
                : base(t) { RoundedEdges = false; _text = text; _textDisabled = textDisabled; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = !e.Item.Enabled ? _textDisabled
                            : e.Item.Selected ? Color.White : _text;
                base.OnRenderItemText(e);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Excel-style per-column filter popup: a searchable checkbox list of the
        //  (already narrowed) values it's given, with Select All / Clear and
        //  OK / Cancel. SelectedValues = the checked subset (the caller folds in
        //  hidden-allowed values and decides whether that means "no filter").
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

            // The checked subset of the values — restricted to the in-popup
            // search matches when a term is active (never null). The caller
            // decides whether that equals "no filter" (it also folds in any
            // allowed values that were hidden by other filters).
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
                VaultDashboardForm.SetCueBanner(_search, "Search…");
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
            // a 100k-name column "Clear, then tick one" would leave 98k values
            // checked and commit a filter that allows almost everything.
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
                    // Cap the RENDERED items so a high-cardinality column (e.g.
                    // 100k file names) never builds a 100k-item list. _state still
                    // covers every value, so Commit and check state stay correct.
                    if (_visibleRaw.Count >= DisplayCap) continue;
                    _visibleRaw.Add(raw);
                    _list.Items.Add(raw.Length == 0 ? "(Blanks)" : raw, _state[raw]);
                }
                _list.EndUpdate();
                _list.ItemCheck += List_ItemCheck;

                if (matched > _visibleRaw.Count)
                    _count.Text = string.Format("{0:N0} of {1:N0} shown — type to narrow",
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
            // typed — folding them in would quietly re-allow files the user
            // never saw, so "search, tick one, OK" showed almost everything
            // instead of the one ticked file.
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
