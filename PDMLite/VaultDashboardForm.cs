using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

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
        private Button _btnPrint;   // batch-print released drawing PDFs
        private Button _btnAudit;   // switch to the Audit Report (single-window nav)
        // Current released drawing PDFs live here ({DrawingNo} REV {Rev}.pdf).
        private const string PdfExportFolder = @"N:\PDM-SolidWorks\EXPORTS\PDF";
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _searchTimer;
        private Font _cellBold; // shared bold font: Status cell + header text

        // Summary strip: clickable count "links" (Total/Mine/WIP/Released/Locked/
        // Obsolete/Broken Refs/Stale WIP act as quick filters), justified across the
        // control-row width. _lblShowing (the "Showing… · Page…" status) lives in the
        // BOTTOM panel on the pager line, not here.
        private Label[] _countLabels;   // the 8 clickable count quick-filters, justified across the control-row width
        private Label _lblTotal, _lblMine, _lblWip, _lblReleased, _lblLocked, _lblObsolete, _lblBroken, _lblStale, _lblShowing;
        private Font _summaryFont;       // base (bold)
        private Font _summaryFontActive; // active quick-filter (bold + underline)
        private ToolTip _summaryTip;     // shared tip for the clickable counts
        // Special non-column filter: show only files with broken references
        // (HasBrokenRefs is a flag, not a column, so it can't live in _colFilters).
        private bool _brokenRefsOnly = false;
        private bool _staleWipOnly = false;  // "Stale WIP" quick-filter (WipDays > StaleWipDays)
        // "My Work" lens: files this user last saved (ModifiedBy == me). The #1 PDM
        // dashboard lens. A shortcut over the Modified By column filter, so it reuses
        // _colFilters[ColModifiedBy] rather than a separate flag.
        private readonly string _me = (Environment.UserName ?? "").Trim();
        private Label _lblHint;       // faint discoverability footer in the top panel
        private DateTime _loadedAt;   // snapshot time, shown in the summary as "as of HH:mm"
        // KPI tiles (read-only, boxed) below the clickable quick-filter strip.
        // Two kinds: STATE-OF-VIEW tiles (avg WIP age, broken refs) recompute over
        // the FILTERED view on every filter/search change (ComputeKpis); ACTIVITY
        // tiles (Released-7d, Open Requests) are WHOLE-VAULT throughput/queue
        // metrics computed once per load and stable under filtering — Released-7d
        // in ComputeVaultCounts' _all scan, Open Requests via GetPendingRequests.
        // (A file released in the 7d window counts even if a New Revision has since
        // returned it to WIP — it WAS released this week; making it filter-based
        // wrongly dropped those when you clicked the Released quick-filter.)
        // Distinct from the quick-filter counts: not clickable, boxed, dark text.
        private FlowLayoutPanel _kpiPanel;
        private Label _kpiAvgAge, _kpiReleased7d, _kpiOpenReq, _kpiBroken;
        private Font _kpiFont;
        private double _kpiAvgWipAge;     // avg WipDays over WIP files in _view
        private int _kpiReleased7dCount;  // whole-vault files released within 7 days (throughput)
        private int _kpiReleased7dPrev;   // whole-vault files released 7–14 days ago (trend baseline)
        private int _kpiBrokenInView;     // broken-ref files in _view
        private int _kpiOpenReqCount;     // pending requests (whole vault, per load)
        private int _oldestReqDays = -1;  // age (days) of the oldest pending request, -1 = none
        // Status-distribution doughnut (MS Chart, the framework's built-in control —
        // ships with .NET 4.8, NO NuGet). WHOLE-VAULT mix (matches the count strip),
        // refreshed once per load. Sits in the top panel's right gutter; responsive
        // (hidden when the form is too narrow to avoid overlapping the centred block).
        private Chart _statusChart;
        private Font _chartFont;
        // Whole-vault counts — invariant under filtering, so cached once per load
        // (UpdateSummary used to re-scan _all 4× on every keystroke / page click).
        private int _cntWip, _cntRel, _cntLck, _cntObs, _cntBrk, _cntStale, _cntMine;
        private const int ColRev = 4;        // "Rev" column index
        private const int ColModifiedBy = 5; // first of the metadata columns (5..9)
        private const int ColWipDays = 9; // appended "WIP Days" column index

        // Row right-click menu (Open / Open linked / Copy path / Open folder).
        private ContextMenuStrip _rowMenu;
        private ToolStripMenuItem _miOpen, _miOpenLinked, _miCopyPath, _miOpenFolder;
        private ToolStripMenuItem _miBaseline; // "View As-Released Baseline" (assembly rows)
        // "Where Used" — shown for models only (parts/sub-assemblies); read-only,
        // all users. Toggled per-row in Grid_MouseDown.
        private ToolStripMenuItem _miWhereUsed;
        // Master-only lifecycle items — constructed ONLY for a Master (see
        // _isMaster) so engineers never see them or a dangling separator.
        private ToolStripMenuItem _miObsolete, _miReinstate;
        // Remove from Vault — Masters only (moved here from the task pane). Retires
        // the file BY PATH (need not be open). Built only for Masters.
        private ToolStripMenuItem _miRemove;
        private bool _isMaster;
        private VaultFile _menuRow;       // the ROW OBJECT the menu was opened on —
                                          // not an index: the search debounce can
                                          // rebuild _view while the menu is up, and
                                          // a stale index then opened a DIFFERENT
                                          // file than the one right-clicked
        private string _menuLinkedPath;   // drawing for a model row, model for a drawing row
        private string _menuLinkedConfig; // config to land on when opening a drawing's model

        private const int VisibleRows = 20;     // fixed grid height = 20 rows
        private const int PageSize = VisibleRows; // 20 rows per page (the "20 row rule")
        private const double MaxScreenFraction = 0.80; // popup ≤ 80% of screen
        private const int StaleWipDays = 30; // WIP older than this = "stale" (aging surfacing)
        private const int RequestSlaDays = 3; // pending request older than this = SLA breach
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
            // Role decides whether the Master-only lifecycle menu items exist.
            try
            {
                _isMaster = string.Equals(
                    DatabaseManager.GetUserRole(System.Environment.UserName),
                    "Master", StringComparison.OrdinalIgnoreCase);
            }
            catch { _isMaster = false; }
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

        // ── Batch print released drawings ───────────────────────────────────
        // Print the current released drawing PDF (EXPORTS\PDF\{DrawingNo} REV
        // {Rev}.pdf) for the files in scope: the SELECTED rows (current page) if
        // any are selected, else the whole FILTERED view. Only RELEASED files
        // have a current released PDF. Missing PDFs are reported, never fatal.
        private void PrintDrawings()
        {
            // Scope: explicit selection (current page) wins; else the filtered view.
            var scope = new List<VaultFile>();
            if (_grid.SelectedRows.Count > 0)
            {
                foreach (DataGridViewRow row in _grid.SelectedRows)
                {
                    int idx = PageStart + row.Index;
                    if (idx >= 0 && idx < _view.Count) scope.Add(_view[idx]);
                }
            }
            else scope.AddRange(_view);

            var releasedPaths = scope
                .Where(f => Eq(f.Status, "Released") &&
                            !string.IsNullOrEmpty(f.FilePath))
                .Select(f => f.FilePath)
                .ToList();

            if (releasedPaths.Count == 0)
            {
                MessageBox.Show(
                    "No RELEASED files in the current selection/view.\n\n" +
                    "Select rows (Ctrl/Shift-click) or filter to a set that " +
                    "includes released files, then try again.",
                    "BCore PDM — Print Drawings",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Resolve each released file's drawing identity (DrawingNo + Rev) in
            // ONE vault.xml load, then locate the current PDF in EXPORTS\PDF.
            List<DrawingExportRef> refs;
            try { refs = DatabaseManager.GetDrawingExportRefs(releasedPaths); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read drawing numbers.\n\n" + ex.Message,
                    "BCore PDM — Print Drawings",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Index the PDFs currently in EXPORTS\PDF (filename → full path).
            var pdfByName = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(PdfExportFolder))
                    foreach (string p in Directory.GetFiles(PdfExportFolder, "*.pdf"))
                        pdfByName[Path.GetFileName(p)] = p;
            }
            catch { /* share unreachable → everything reports as missing */ }

            var toPrint = new List<string>();                 // full paths, deduped
            var seenPdf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();                 // no current PDF
            foreach (var r in refs)
            {
                string found = ResolvePdf(r, pdfByName);
                if (found == null)
                {
                    missing.Add(r.DrawingNo + (string.IsNullOrEmpty(r.Revision)
                        ? "" : " REV " + r.Revision));
                    continue;
                }
                if (seenPdf.Add(found)) toPrint.Add(found);
            }

            if (toPrint.Count == 0)
            {
                MessageBox.Show(
                    "No released drawing PDFs were found for the selected files." +
                    (missing.Count > 0
                        ? "\n\nNo current PDF for:\n  • " +
                          string.Join("\n  • ", missing.Take(20))
                        : ""),
                    "BCore PDM — Print Drawings",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    "Print " + toPrint.Count +
                    " released drawing PDF(s) to the default printer?" +
                    (missing.Count > 0
                        ? "\n\n(" + missing.Count +
                          " drawing(s) had no current PDF and will be skipped.)"
                        : ""),
                    "BCore PDM — Print Drawings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
                return;

            int ok = 0;
            var failed = new List<string>();
            foreach (string pdf in toPrint)
            {
                if (ExportManager.PrintPdf(pdf)) ok++;
                else failed.Add(Path.GetFileName(pdf));
            }

            var sb = new StringBuilder();
            sb.Append("Sent ").Append(ok).Append(" of ").Append(toPrint.Count)
              .Append(" drawing PDF(s) to the default printer.");
            if (failed.Count > 0)
                sb.Append("\n\nFailed to print:\n  • ")
                  .Append(string.Join("\n  • ", failed.Take(20)));
            if (missing.Count > 0)
                sb.Append("\n\nNo current PDF (skipped):\n  • ")
                  .Append(string.Join("\n  • ", missing.Take(20)));
            MessageBox.Show(sb.ToString(), "BCore PDM — Print Drawings",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Find the current released PDF for a drawing ref. Exact name first
        // ("{DrawingNo} REV {Rev}.pdf"), then any current PDF for that drawing
        // number (the rev may differ from the stored config rev). null = none.
        private static string ResolvePdf(DrawingExportRef r,
            Dictionary<string, string> pdfByName)
        {
            if (r == null || string.IsNullOrEmpty(r.DrawingNo)) return null;
            string exact = r.DrawingNo + " REV " + (r.Revision ?? "") + ".pdf";
            string full;
            if (pdfByName.TryGetValue(exact, out full)) return full;
            string prefix = r.DrawingNo + " REV ";
            foreach (var kv in pdfByName)
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
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

            // Batch-print released drawing PDFs for the selected rows (or the
            // whole filtered view when nothing is selected).
            // Same size + colour as Audit Report (it stacks flush beneath it) so
            // the two read as a uniform pair.
            _btnPrint = MakeButton("Print Drawings", cBrandDark, fBtn,
                new Point(S(758), rowY), S(150));
            _btnPrint.Height = ctrlH;
            _btnPrint.Click += (s, e) => PrintDrawings();
            _topPanel.Controls.Add(_btnPrint);

            // Switch to the Audit Report (single-window nav): signal the caller and
            // close — the task pane reopens the Audit Report in this window's place.
            _btnAudit = MakeButton("Audit Report »", cBrandDark, fBtn,
                new Point(S(764), rowY), S(150));
            _btnAudit.Height = ctrlH;
            _btnAudit.Click += (s, e) =>
            { SwitchToAudit = true; this.DialogResult = DialogResult.Cancel; Close(); };
            _topPanel.Controls.Add(_btnAudit);

            int summaryY = rowY + ctrlH + S(10);
            // Summary strip = a row of clickable count "links" (Total/Mine/WIP/
            // Released/Locked/Obsolete/Broken Refs/Stale WIP — quick filters). They
            // are direct _topPanel children JUSTIFIED across the control-row width
            // (search → Audit Report) by LayoutTopControls, so the strip lines up
            // with the row above it (a FlowLayoutPanel can't space-between).
            _lblTotal    = MakeCountLabel("All files (clear filters)", () => ClearAllFilters());
            _lblMine     = MakeCountLabel("My Work — files I last saved (" + _me + ")", () => ToggleMineFilter());
            _lblWip      = MakeCountLabel("Filter to WIP",      () => ToggleStatusFilter("WIP"));
            _lblReleased = MakeCountLabel("Filter to Released", () => ToggleStatusFilter("Released"));
            _lblLocked   = MakeCountLabel("Filter to Locked",   () => ToggleStatusFilter("Locked"));
            _lblObsolete = MakeCountLabel("Filter to Obsolete", () => ToggleStatusFilter("Obsolete"));
            _lblBroken   = MakeCountLabel("Show only broken references", () => ToggleBrokenFilter());
            _lblStale    = MakeCountLabel("Show only WIP files not saved in over " + StaleWipDays + " days (stuck/neglected work)", () => ToggleStaleFilter());
            _countLabels = new[]
            {
                _lblTotal, _lblMine, _lblWip, _lblReleased, _lblLocked, _lblObsolete,
                _lblBroken, _lblStale
            };
            foreach (var l in _countLabels)
            {
                l.Top = summaryY;                 // Left is justified in LayoutTopControls
                _topPanel.Controls.Add(l);
            }

            // KPI tiles — at-a-glance metrics, read-only and boxed so they read
            // as a distinct strip from the clickable count quick-filters above.
            int kpiY = summaryY + (int)_summaryFont.GetHeight() + S(8);
            _kpiFont = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _kpiPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = cBg,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Location = new Point(S(14), kpiY)
            };
            _kpiAvgAge     = MakeKpiTile("Average age (days since last save) of WIP files in the current view");
            _kpiReleased7d = MakeKpiTile("Files released within the last 7 days across the whole vault (release throughput; not affected by filters). ▲/▼ shows the change vs the prior 7 days.");
            _kpiOpenReq    = MakeKpiTile("Pending engineer requests across the whole vault (Unlock / Revision / Release). Shows the oldest request's age; turns maroon when the oldest is over " + RequestSlaDays + " days (responsiveness SLA — open Pending Requests to act).");
            _kpiBroken     = MakeKpiTile("Files with broken references in the current view");
            _kpiPanel.Controls.AddRange(new Control[]
            { _kpiAvgAge, _kpiReleased7d, _kpiOpenReq, _kpiBroken });
            _topPanel.Controls.Add(_kpiPanel);

            // Faint discoverability footer below the KPI tiles — new users won't
            // guess the right-click menu / column-resize / clickable counts. The
            // "Showing… · Page… · as of…" status lives in the BOTTOM panel now,
            // right-aligned on the pager line (built below), so the hint owns this
            // line alone (centred in LayoutTopControls).
            int hintY = kpiY + (int)_kpiFont.GetHeight() + S(10);
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

            // Status-distribution doughnut, parked in the top panel's RIGHT COLUMN.
            // LayoutTopControls reserves a right-hand column for it and centres the
            // content in the remaining width. It spans from the TITLE row (S(10),
            // the empty right gutter clears the centred title) down to the hint, so
            // it has the full panel height for the doughnut + 3-entry legend (the
            // shorter control-row→hint band clipped the last legend entry); still
            // adds NO panel height.
            // NON-FATAL: BuildStatusChart references System.Windows.Forms.Data-
            // Visualization (in-box on .NET 4.8, so always present in practice). If
            // that assembly ever fails to load, degrade to NO chart rather than
            // letting the exception propagate onto SOLIDWORKS' message loop and
            // break the whole dashboard (_statusChart stays null; UpdateStatusChart
            // and LayoutTopControls both null-guard it).
            try
            {
                _chartFont = new Font("Segoe UI", 3.0f * _scale);
                _statusChart = BuildStatusChart(S(10),
                    hintY + (int)_lblHint.Font.GetHeight());
                _topPanel.Controls.Add(_statusChart);
            }
            catch { _statusChart = null; }

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
            // "Showing N–M of … · Page … · as of …" sits in the BOTTOM-RIGHT, on the
            // pager line, just left of the Close button. BuildPager owns its position
            // on every resize (no Anchor — AutoSize + Anchor=Right is a WinForms
            // quirk). Text set in UpdateSummary, which runs before BuildPager.
            _lblShowing = new Label
            {
                Font = _summaryFont,
                ForeColor = cTextGray,
                AutoSize = true
            };
            _bottomPanel.Controls.Add(_lblShowing);
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
                MultiSelect = true,   // batch-print: select several rows at once
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
            // Assembly rows only (toggled in Grid_MouseDown): the as-released
            // baseline = the exact child file/rev set the assembly was last
            // released against. Opens read-only on top of the dashboard.
            _miBaseline   = new ToolStripMenuItem("View As-Released Baseline", null, (s, e) => MenuViewBaseline());
            // Where Used: models only (toggled in Grid_MouseDown). Read-only,
            // all users; opens nested-modal on top of the dashboard.
            _miWhereUsed  = new ToolStripMenuItem("Where Used…", null, (s, e) => MenuWhereUsed());
            _miCopyPath   = new ToolStripMenuItem("Copy File Path", null, (s, e) => MenuCopyPath());
            _miOpenFolder = new ToolStripMenuItem("Open Containing Folder", null, (s, e) => MenuOpenFolder());
            var menuItems = new List<ToolStripItem>
            {
                _miOpen, _miOpenLinked, _miBaseline, _miWhereUsed, new ToolStripSeparator(), _miCopyPath, _miOpenFolder
            };
            // Master-only lifecycle actions (Grid_MouseDown shows exactly one per
            // row by status). Built only for Masters → engineers never see them
            // or a dangling separator.
            if (_isMaster)
            {
                _miObsolete  = new ToolStripMenuItem("Mark Obsolete…", null, (s, e) => MenuMarkObsolete());
                _miReinstate = new ToolStripMenuItem("Reinstate from Obsolete", null, (s, e) => MenuReinstate());
                // Retirement (moves the file + snapshot + exports to SCRAP and
                // deletes the record). Sits with the other lifecycle actions.
                _miRemove    = new ToolStripMenuItem("Remove from Vault…", null, (s, e) => MenuRemove());
                menuItems.Add(new ToolStripSeparator());
                menuItems.Add(_miObsolete);
                menuItems.Add(_miReinstate);
                menuItems.Add(_miRemove);
            }
            _rowMenu.Items.AddRange(menuItems.ToArray());
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
                DataGridViewContentAlignment.MiddleCenter;
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
                _kpiFont?.Dispose();
                _chartFont?.Dispose();   // Chart doesn't own the Font we assign it
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
            UpdateStatusChart();        // refill the doughnut from the new counts
            // Open-requests KPI is a WHOLE-VAULT metric (no per-file dimension to
            // filter on) — read it once per load. Also derive the OLDEST pending
            // request's age (the responsiveness SLA: there are no due dates in the
            // model, so "time in queue" is the honest SLA signal). Network blip →
            // 0 / no age, never throws.
            try
            {
                var pending = DatabaseManager.GetPendingRequests();
                _kpiOpenReqCount = pending.Count;
                _oldestReqDays = -1;
                foreach (var r in pending)
                {
                    DateTime d;
                    if (DateTime.TryParse(r.RequestDate, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out d))
                    {
                        int age = (int)(DateTime.Now.Date - d.Date).TotalDays;
                        if (age > _oldestReqDays) _oldestReqDays = age;
                    }
                }
            }
            catch { _kpiOpenReqCount = 0; _oldestReqDays = -1; }
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
        // columnFiltersOnly: ignore the TRANSIENT view narrowing (global
        // search box + Broken Refs toggle) and apply other COLUMN filters
        // only — used by ShowColumnFilter to compute which allowed values are
        // hidden by another column's filter (those are preserved on commit;
        // search-hidden ones are not).
        private IEnumerable<string> KeysPassingOtherFilters(int col,
            bool columnFiltersOnly = false)
        {
            string term = columnFiltersOnly ? ""
                : (_search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var f in _all)
            {
                if (!columnFiltersOnly && _brokenRefsOnly && !f.HasBrokenRefs)
                    continue;
                if (!columnFiltersOnly && _staleWipOnly && WipDays(f) <= StaleWipDays)
                    continue;
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
                // Stale-WIP-only quick filter (WipDays is -1 for non-WIP → excluded).
                if (_staleWipOnly && WipDays(f) <= StaleWipDays) return false;
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
            ComputeKpis();           // recompute the view-based KPI tiles ONCE per
                                     // filter change (UpdateSummary just reads them,
                                     // so paging doesn't rescan — matches the cached
                                     // whole-vault counts' performance discipline)
            ShowGridPage();          // also refreshes the summary (page indicator)
            LayoutTopControls();     // re-centre: the summary/KPI width changed
        }

        // Recompute the VIEW-based KPI tiles over the current filtered _view:
        //   avg WIP age (days) · releases in the last 7 days · broken refs.
        // (Open Requests is whole-vault, fetched once per load in LoadData.)
        private void ComputeKpis()
        {
            long ageSum = 0; int wipCount = 0, brk = 0;
            foreach (var f in _view)
            {
                int age = WipDays(f);          // -1 for non-WIP / no modified date
                if (age >= 0) { ageSum += age; wipCount++; }
                if (f.HasBrokenRefs) brk++;
            }
            _kpiAvgWipAge = wipCount > 0 ? (double)ageSum / wipCount : 0;
            _kpiBrokenInView = brk;
            // _kpiReleased7dCount is whole-vault (release throughput), computed
            // once per load in ComputeVaultCounts — NOT recomputed per filter.
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
            // UpdateSummary FIRST so the "Showing X–Y · Page N of M" label's text
            // (and width) is current — BuildPager positions that label at the bottom
            // right and reserves the pager's space to its LEFT, so it must run after.
            // (Also keeps the indicator from ever desyncing from the rendered page.)
            UpdateSummary(_view.Count);
            BuildPager();
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
            // DEFER the old buttons' disposal: BuildPager is reached from a
            // pager button's OWN Click handler (GoToPage → ShowGridPage →
            // BuildPager), and disposing a control while its Click event is
            // still on the stack is a latent intermittent
            // ObjectDisposedException inside WinForms' button-up handling.
            // Remove them from the panel now; dispose them when the message
            // loop comes back around. (Before the handle exists — the ctor's
            // initial build — no click can be on the stack: dispose inline.)
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

            // Position the "Showing… · Page… · as of…" label at the BOTTOM-RIGHT,
            // vertically centred on the pager line, just left of the Close button
            // (its Text/Width are set by UpdateSummary, which runs before BuildPager
            // in ShowGridPage). The pager then centres in the area to its LEFT.
            int rightLimit = _bottomPanel.ClientSize.Width - S(140); // Close-button zone
            if (_lblShowing != null)
            {
                // Close button is anchored Right at panelW - S(124); derive its left
                // from the panel width (deterministic — doesn't depend on the anchor
                // having repositioned _btnClose yet during this resize pass).
                int closeLeft = _bottomPanel.ClientSize.Width - S(124);
                int sLeft = Math.Max(S(8), closeLeft - S(16) - _lblShowing.Width);
                _lblShowing.Left = sLeft;
                _lblShowing.Top = Math.Max(S(2),
                    (_bottomPanel.ClientSize.Height - _lblShowing.Height) / 2);
                rightLimit = sLeft - S(12);   // keep the pager clear of Showing
            }

            int avail = Math.Max(totalW, rightLimit);
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
            // Reason-for-change of the latest release — surfaced on the Status
            // (when Released), Released By and Released Date cells, so the "why"
            // is one hover away without adding a column. Cols: Status=3,
            // Released By=7, Released Date=8.
            else if (!string.IsNullOrWhiteSpace(f.ReleaseReason) &&
                     (e.ColumnIndex == 7 || e.ColumnIndex == 8 ||
                      (e.ColumnIndex == 3 && Eq(f.Status, "Released"))))
                e.ToolTipText = "Reason: " + f.ReleaseReason;
            // Obsolete row Status cell: the obsolete reason + the replacement
            // part (if recorded), so the "why" and "use-instead" are one hover away.
            else if (e.ColumnIndex == 3 && Eq(f.Status, "Obsolete"))
            {
                string t = string.IsNullOrWhiteSpace(f.ObsoleteReason)
                    ? "Obsolete" : "Obsolete: " + f.ObsoleteReason;
                if (!string.IsNullOrWhiteSpace(f.SupersededBy))
                    t += "\nSuperseded by: " + f.SupersededBy;
                e.ToolTipText = t;
            }
        }

        private void ClearAllFilters()
        {
            _colFilters.Clear();
            _brokenRefsOnly = false;                  // clear the broken-refs view
            _staleWipOnly = false;                    // clear the stale-WIP view
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
                // Per-column slack on top of the measured text: the short "Rev"
                // header needs more (1.20) so it doesn't ellipsis to "R..."; the
                // metadata columns (Modified By/Date, Released By/Date, WIP Days)
                // are tightened to 1.10; everything else gets 1.15.
                double mult;
                if (ci == ColRev) mult = 1.20;
                else if (ci >= ColModifiedBy && ci <= ColWipDays) mult = 1.10;
                else mult = 1.15;
                int w = (int)(max * mult) + GlyphZone + S(10); // arrow + cell padding
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

            int borderW = this.Width - this.ClientSize.Width;   // 0 before shown
            int maxClientW = (int)(area.Width * MaxScreenFraction) - borderW;

            // A HORIZONTAL scrollbar appears when the columns are wider than the
            // (capped) client width — it sits along the grid's bottom and would
            // otherwise eat the 20th row. Reserve its height so all 20 rows stay
            // fully visible above it (the "20 row rule").
            bool needsHScroll = totalCols + S(4) > maxClientW;

            // Height = a CONSTANT 20 grid rows + panels, capped at 80% of the
            // screen. On a small screen / high DPI the cap can bite, which forces
            // the grid to show a vertical scrollbar — detect that so the width can
            // reserve room for it (otherwise the last column clips under it).
            int gridH = _grid.ColumnHeadersHeight
                      + _grid.RowTemplate.Height * VisibleRows + S(2)
                      + (needsHScroll ? SystemInformation.HorizontalScrollBarHeight : 0);
            int desiredH = _topPanel.Height + gridH + _bottomPanel.Height;
            int borderH = this.Height - this.ClientSize.Height;
            int maxClientH = (int)(area.Height * MaxScreenFraction) - borderH;
            int clientH = Math.Min(desiredH, maxClientH);
            bool needsVScroll = desiredH > maxClientH; // height got clamped

            // Pages cap at PageSize (== VisibleRows) rows, so normally no vertical
            // scrollbar — reserve its width ONLY when the height clamp forced one.
            int chrome = S(4) + (needsVScroll ? SystemInformation.VerticalScrollBarWidth : 0);
            int clientW = totalCols + chrome;
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

            // Print Drawings is NOT in this row (it stacks under Audit Report
            // below) so the control row stays narrow enough to keep the status
            // doughnut visible — adding a 6th button widened the row past the
            // chart-reserve threshold and hid the chart.
            int rowW = _search.Width + gap + _btnRefresh.Width + gap
                     + _btnExport.Width + gap + _btnClear.Width
                     + gap + _btnAudit.Width;

            // Natural minimum width of the count strip (sum of the 8 labels + the
            // tightest S(6) gaps). Folded into 'widest' below so the chart hides /
            // centerW shrinks if the counts would otherwise overflow the control row
            // (realistic at large-vault scale: "Total: 99999" … "Stale WIP: 999").
            int nCounts = _countLabels != null ? _countLabels.Length : 0;
            int countsSumW = 0;
            if (_countLabels != null)
                foreach (var l in _countLabels) countsSumW += l.Width;
            int countsMinW = countsSumW + Math.Max(0, nCounts - 1) * S(6);

            // The status doughnut takes a RESERVED right-hand COLUMN; the control
            // row / counts / KPI / hint are centred in the REMAINING width (centerW).
            // The counts JUSTIFY across the control-row width when they fit, so the
            // chart guard bounds them by max(rowW, their natural width).
            int widest = rowW;
            if (_kpiPanel != null) widest = Math.Max(widest, _kpiPanel.Width);
            widest = Math.Max(widest, countsMinW);

            int centerW = panelW;
            if (_statusChart != null)
            {
                int reserve = _statusChart.Width + S(16);
                bool room = (panelW - reserve) >= widest;
                _statusChart.Visible = room;
                if (room)
                {
                    centerW = panelW - reserve;
                    _statusChart.Left = panelW - _statusChart.Width - S(14);
                }
            }

            // The TITLE is centred in the FULL width (it's a header above the chart
            // band, so it can't overlap), per the "centre the title" request.
            if (_title != null)
                _title.Left = Math.Max(S(14), (panelW - _title.Width) / 2);

            int startX = Math.Max(S(14), (centerW - rowW) / 2);
            _search.Left     = startX;
            _btnRefresh.Left = _search.Right + gap;
            _btnExport.Left  = _btnRefresh.Right + gap;
            _btnClear.Left   = _btnExport.Right + gap;
            _btnAudit.Left   = _btnClear.Right + gap;

            // Print Drawings is STACKED directly beneath Audit Report (right edges
            // flush), in the band between the control row and the KPI strip — kept
            // OUT of the control row so the row stays narrow and the status chart
            // stays visible. The count strip below shortens to clear its column.
            _btnPrint.Left = _btnAudit.Right - _btnPrint.Width;
            _btnPrint.Top  = _btnAudit.Bottom + S(6);

            // JUSTIFY the count quick-filters across the span from the control
            // row's left (search.Left) to JUST LEFT of the stacked Print Drawings
            // button (space-between: first flush left, last flush right, equal
            // gaps). If the labels can't fit that (now narrower) span, pack them
            // from the left at the tight S(6) gap — LEFT-ANCHORED so they never run
            // under the Print button or the reserved chart column (centring in
            // centerW would overlap the stacked button).
            if (nCounts > 0)
            {
                int left = _search.Left;
                int right = _btnPrint.Left - gap;   // stop before the stacked Print button
                bool fits = countsMinW <= right - left;
                int gapC = fits
                    ? (nCounts > 1 ? (right - left - countsSumW) / (nCounts - 1) : 0)
                    : S(6);
                int cx = left;   // left-anchored for both fit + overflow
                for (int i = 0; i < nCounts; i++)
                {
                    _countLabels[i].Left = cx;
                    cx += _countLabels[i].Width + gapC;
                }
                // Land the LAST count's right edge EXACTLY on the strip's right
                // bound (just left of the stacked Print Drawings button) — absorbs
                // the gap integer-division remainder; only in the fits case.
                if (fits)
                    _countLabels[nCounts - 1].Left =
                        right - _countLabels[nCounts - 1].Width;
            }

            if (_kpiPanel != null)
                _kpiPanel.Left = Math.Max(S(14),
                    (centerW - _kpiPanel.Width) / 2);

            // The hint owns its line alone now (Showing moved to the bottom pager).
            if (_lblHint != null)
                _lblHint.Left = Math.Max(S(14), (centerW - _lblHint.Width) / 2);
        }

        // Build the status-distribution doughnut (MS Chart, built into .NET 4.8 —
        // no NuGet). Configured once; data filled by UpdateStatusChart once per load.
        private Chart BuildStatusChart(int topY, int bottomY)
        {
            var chart = new Chart
            {
                Width = S(180),
                // Height = exactly the title→hint band. A fixed floor here (e.g.
                // Math.Max(S(120), …)) is DECOUPLED from _topPanel.Height (which is
                // derived from the hint font), so at DPIs where the band < the floor
                // the chart grew taller than the panel and the bottom-docked legend
                // clipped under the grid. The band is always large (title row →
                // hint) and the panel contains it with an S(8) margin, so honour it.
                Height = Math.Max(S(40), bottomY - topY),
                Top = topY,
                BackColor = cBg,
                AntiAliasing = AntiAliasingStyles.All,
                Visible = false                 // LayoutTopControls decides
            };
            chart.ChartAreas.Add(new ChartArea("main") { BackColor = cBg });
            var s = new Series("status")
            {
                ChartArea = "main",
                ChartType = SeriesChartType.Doughnut,
                Font = _chartFont
            };
            s["DoughnutRadius"] = "55";          // ring thickness
            s["PieLabelStyle"] = "Disabled";     // names+counts live in the legend
            chart.Series.Add(s);
            chart.Legends.Add(new Legend("leg")
            {
                Docking = Docking.Bottom,
                Alignment = StringAlignment.Center,
                Font = _chartFont,
                BackColor = cBg
            });
            chart.Titles.Add(new Title("Vault status", Docking.Top, _chartFont, cTextDark));
            return chart;
        }

        // Fill the doughnut from the WHOLE-VAULT status counts (matches the count
        // strip — invariant under filtering, so refreshed once per load, not per
        // filter). A zero-count status is omitted so the ring stays clean.
        private void UpdateStatusChart()
        {
            if (_statusChart == null) return;
            try
            {
                var s = _statusChart.Series["status"];
                s.Points.Clear();
                AddStatusSlice(s, "WIP",      _cntWip, cOrange);
                AddStatusSlice(s, "Released", _cntRel, cGreen);
                AddStatusSlice(s, "Locked",   _cntLck, cMaroon);
                AddStatusSlice(s, "Obsolete", _cntObs, StatusColor("Obsolete"));
            }
            catch { /* charting is non-essential — never break a load over it */ }
        }

        private void AddStatusSlice(Series s, string name, int val, Color c)
        {
            if (val <= 0) return;               // omit empty statuses
            int i = s.Points.AddXY(name, val);
            s.Points[i].Color = c;
            s.Points[i].LegendText = name + " (" + val + ")";
            s.Points[i].ToolTip = name + ": " + val;
        }

        // Whole-vault counts are invariant under filtering, so compute them ONCE
        // per load (Refresh) instead of re-scanning _all on every keystroke.
        private void ComputeVaultCounts()
        {
            _cntWip = _cntRel = _cntLck = _cntObs = _cntBrk = _cntStale = _cntMine = 0;
            // Released (7d) is a WHOLE-VAULT throughput metric (release velocity),
            // so it is counted here in the once-per-load _all scan — NOT over the
            // filtered _view. A file released in the window still counts even if a
            // New Revision / Unlock has since put it back to WIP (it WAS released
            // this week), and the number stays stable as the user filters/searches.
            // rel7Prev is the SAME measure one week earlier (latest release 7–14
            // days ago) — the baseline for the tile's ▲/▼ trend arrow.
            int rel7 = 0, rel7Prev = 0;
            DateTime cutoff7  = DateTime.Now.AddDays(-7);
            DateTime cutoff14 = DateTime.Now.AddDays(-14);
            foreach (var f in _all)
            {
                if (Eq(f.Status, "WIP"))            _cntWip++;
                else if (Eq(f.Status, "Released"))  _cntRel++;
                else if (Eq(f.Status, "Locked"))    _cntLck++;
                else if (Eq(f.Status, "Obsolete"))  _cntObs++;
                if (f.HasBrokenRefs)                _cntBrk++;
                if (WipDays(f) > StaleWipDays)       _cntStale++; // WipDays is -1 for non-WIP
                if (_me.Length > 0 && Eq(f.ModifiedBy ?? "", _me)) _cntMine++;
                if (f.ReleasedDate != DateTime.MinValue)
                {
                    if (f.ReleasedDate >= cutoff7)        rel7++;
                    else if (f.ReleasedDate >= cutoff14)  rel7Prev++;
                }
            }
            _kpiReleased7dCount = rel7;
            _kpiReleased7dPrev = rel7Prev;
        }

        private void UpdateSummary(int showing)
        {
            _lblTotal.Text    = $"Total: {_all.Count}";
            _lblMine.Text     = $"Mine: {_cntMine}";
            _lblWip.Text      = $"WIP: {_cntWip}";
            _lblReleased.Text = $"Released: {_cntRel}";
            _lblLocked.Text   = $"Locked: {_cntLck}";
            _lblObsolete.Text = $"Obsolete: {_cntObs}";
            _lblBroken.Text   = $"Broken Refs: {_cntBrk}";
            _lblStale.Text    = $"Stale WIP (>{StaleWipDays}d): {_cntStale}";

            int from = showing == 0 ? 0 : PageStart + 1;
            int to = Math.Min(showing, PageStart + PageSize);
            _lblShowing.Text =
                $"Showing {from}–{to} of {showing}" +
                $"  ·  Page {_page + 1} of {PageCount}" +
                $"  ·  as of {_loadedAt:HH:mm}";

            // Underline the active quick-filter so it's obvious what's applied.
            SetActive(_lblMine,     IsMineFilter());
            SetActive(_lblWip,      IsStatusFilter("WIP"));
            SetActive(_lblReleased, IsStatusFilter("Released"));
            SetActive(_lblLocked,   IsStatusFilter("Locked"));
            SetActive(_lblObsolete, IsStatusFilter("Obsolete"));
            SetActive(_lblBroken,   _brokenRefsOnly);
            SetActive(_lblStale,    _staleWipOnly);

            // KPI tiles (values cached by ComputeKpis / LoadData).
            if (_kpiAvgAge != null)
            {
                _kpiAvgAge.Text = "Avg WIP age: " +
                    _kpiAvgWipAge.ToString("0.0", CultureInfo.InvariantCulture) + " d";
                // Release-velocity trend vs the prior 7 days: ▲ up / ▼ down / ▬ flat,
                // with the absolute change. Whole-vault, so it doesn't move on filter.
                int d = _kpiReleased7dCount - _kpiReleased7dPrev;
                string trend = d > 0 ? "  ▲" + d : d < 0 ? "  ▼" + (-d) : "  ▬";
                _kpiReleased7d.Text = "Released (7d): " + _kpiReleased7dCount + trend;
                // Open requests + the responsiveness SLA: append the oldest pending
                // request's age, and turn the tile maroon when it breaches the SLA
                // (older than RequestSlaDays) — a queue going stale is a Master's
                // cue to act. No due dates exist, so "oldest in queue" IS the SLA.
                string reqAge = (_kpiOpenReqCount > 0 && _oldestReqDays >= 0)
                    ? "  ·  oldest " + _oldestReqDays + "d" : "";
                _kpiOpenReq.Text = "Open requests: " + _kpiOpenReqCount + reqAge;
                _kpiOpenReq.ForeColor =
                    (_kpiOpenReqCount > 0 && _oldestReqDays > RequestSlaDays)
                        ? cMaroon : cTextDark;
                _kpiBroken.Text     = "Broken refs: " + _kpiBrokenInView;
            }
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

        // A read-only KPI "tile": boxed, dark bold text, NOT clickable. Text is
        // set in UpdateSummary; the tip explains the metric. Reuses the shared
        // _summaryTip (already created by the count labels above, disposed on
        // FormClosed) so no extra disposable is introduced.
        private Label MakeKpiTile(string tip)
        {
            var l = new Label
            {
                Font = _kpiFont,
                ForeColor = cTextDark,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSize = true,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(0, 0, S(10), 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            if (_summaryTip == null) _summaryTip = new ToolTip();
            _summaryTip.SetToolTip(l, tip);
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
            else
            {
                // A zero-count quick filter would blank the whole grid and
                // dead-end every other column's popup (empty list, OK
                // disabled) — make a "0" link inert instead.
                int cnt = Eq(status, "WIP") ? _cntWip
                        : Eq(status, "Released") ? _cntRel
                        : Eq(status, "Locked") ? _cntLck
                        : Eq(status, "Obsolete") ? _cntObs : -1;
                if (cnt == 0) return;
                _colFilters[3] = new HashSet<string>(
                    new[] { status }, StringComparer.OrdinalIgnoreCase);
            }
            ApplyFilter();
            _grid.Invalidate(); // repaint the Status header funnel glyph
        }

        // Is the Modified By column filtered to EXACTLY this user (the "Mine" lens)?
        private bool IsMineFilter()
        {
            HashSet<string> set;
            return _me.Length > 0 && _colFilters.TryGetValue(ColModifiedBy, out set)
                && set.Count == 1 && set.Contains(_me);
        }

        // Click "Mine" → toggle the Modified By column filter to the current user.
        // Reuses _colFilters[ColModifiedBy] (so the Modified By header funnel lights
        // up too), exactly like the Status quick-filters share _colFilters[3].
        private void ToggleMineFilter()
        {
            if (IsMineFilter()) _colFilters.Remove(ColModifiedBy);
            else
            {
                if (_cntMine == 0) return;   // zero-count link is inert (blanks grid)
                _colFilters[ColModifiedBy] = new HashSet<string>(
                    new[] { _me }, StringComparer.OrdinalIgnoreCase);
            }
            ApplyFilter();
            _grid.Invalidate(); // repaint the Modified By header funnel glyph
        }

        // Click "Broken Refs" → toggle the broken-references-only view.
        private void ToggleBrokenFilter()
        {
            // Turning ON a zero-count filter would blank the grid (no broken-ref
            // rows) and dead-end — make the "0" link inert, exactly like the
            // Status quick filters above (the guard was there but not here —
            // found in PR-52 testing). Turning it OFF is always allowed.
            if (!_brokenRefsOnly && _cntBrk == 0) return;
            _brokenRefsOnly = !_brokenRefsOnly;
            ApplyFilter();
        }

        // Click "Stale WIP" → toggle the stale-WIP-only view (WipDays > StaleWipDays).
        // Same zero-count guard as Broken Refs: turning ON a 0 would blank the grid.
        private void ToggleStaleFilter()
        {
            if (!_staleWipOnly && _cntStale == 0) return;
            _staleWipOnly = !_staleWipOnly;
            ApplyFilter();
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private Color StatusColor(string status)
        {
            if (Eq(status, "Released")) return cGreen;
            if (Eq(status, "Locked"))   return cMaroon;
            if (Eq(status, "WIP"))      return cOrange;
            if (Eq(status, "Obsolete")) return Color.FromArgb(120, 120, 120);
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

            _menuRow = _view[idx]; // capture the OBJECT — see field comment
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

            // Baselines are an assembly-only concept (the resolved child set).
            _miBaseline.Visible = (f.FileName ?? "")
                .EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);

            // Where Used applies to models only (a drawing isn't a component).
            string ext = (Path.GetExtension(f.FileName ?? "") ?? "").ToLowerInvariant();
            _miWhereUsed.Visible = ext == ".sldprt" || ext == ".sldasm";

            // Master lifecycle items. Mark Obsolete shows on every row — on an
            // already-Obsolete row it relabels to "Update Obsolete Details…" so a
            // Master can set/change the reason or replacement after the fact
            // (VaultManager.MarkObsolete handles both). Reinstate shows only on
            // an Obsolete row.
            if (_miObsolete != null)
            {
                bool isObsolete = (f.Status ?? "")
                    .Equals("Obsolete", StringComparison.OrdinalIgnoreCase);
                _miObsolete.Visible = true;
                _miObsolete.Text = isObsolete
                    ? "Update Obsolete Details…" : "Mark Obsolete…";
                _miReinstate.Visible = isObsolete;
            }
            // Remove from Vault: Masters only; disabled (not hidden) on a Released
            // row so the menu stays predictable but the action is clearly blocked
            // (a Released file must be Unlocked / New-Revisioned first).
            if (_miRemove != null)
            {
                _miRemove.Visible = true;
                _miRemove.Enabled = !(f.Status ?? "")
                    .Equals("Released", StringComparison.OrdinalIgnoreCase);
            }

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

        // The VaultFile the menu was opened on. The object itself is captured at
        // right-click time, so a filter/search/page change while the menu is up
        // can never retarget the action onto a different row.
        private VaultFile MenuRow()
        {
            return _menuRow;
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

        // ── Master lifecycle actions (Obsolete) ─────────────────────────────
        // Act on the right-clicked file BY PATH (no open needed). VaultManager
        // confirms, prompts for a reason and re-checks the Master role itself;
        // its dialogs are modal ON TOP of the dashboard (it stays open, like
        // MenuViewBaseline). Refresh the snapshot afterwards so the row's new
        // status shows.
        private void MenuMarkObsolete()
        {
            var f = MenuRow();
            if (f == null) return;
            VaultManager.MarkObsolete(f.FilePath);
            LoadData();
        }

        private void MenuReinstate()
        {
            var f = MenuRow();
            if (f == null) return;
            VaultManager.ReinstateFromObsolete(f.FilePath);
            LoadData();
        }

        // Retire the right-clicked file (BY PATH — VaultManager opens it to read
        // the per-config export identity, then scraps + deletes the record + closes
        // it; its confirm / reason / role dialogs are modal on top of the dashboard,
        // which stays open like the Obsolete actions). Refresh so the row drops out.
        private void MenuRemove()
        {
            var f = MenuRow();
            if (f == null) return;
            VaultManager.RemoveFromVault(f.FilePath);
            LoadData();
        }

        // Show which assemblies reference this file (read-only, on demand).
        // Opens ON TOP of the dashboard (nested modal) so the dashboard stays
        // put. If the user double-clicks a parent row there, the viewer closes
        // with a FileToOpen — bubble it up through the dashboard's deferred-open
        // so the file opens AFTER both modals close (like View Baseline).
        private void MenuWhereUsed()
        {
            var f = MenuRow();
            if (f == null) return;
            try
            {
                string toOpen = null;
                using (var v = new WhereUsedForm(f.FilePath, f.FileName, f.PartNumber))
                {
                    v.ShowDialog(this);
                    toOpen = v.FileToOpen;
                }
                if (!string.IsNullOrEmpty(toOpen)) OpenDeferred(toOpen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Where Used:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Show the assembly's captured as-released baselines (read-only). Opens
        // ON TOP of the dashboard (nested modal) so the dashboard stays put. If
        // the user double-clicks a component there, the viewer closes with a
        // FileToOpen — bubble it up through the dashboard's deferred-open so the
        // file opens AFTER both modals close (like double-clicking a dashboard row).
        private void MenuViewBaseline()
        {
            var f = MenuRow();
            if (f == null) return;
            try
            {
                string toOpen = null;
                using (var v = new BaselineViewerForm(f.FilePath, f.FileName))
                {
                    v.ShowDialog(this);
                    toOpen = v.FileToOpen;
                }
                if (!string.IsNullOrEmpty(toOpen)) OpenDeferred(toOpen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the baseline viewer:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

        // Minimal RFC-4180 CSV escaping (mirrors AuditLogger.Csv), incl. its
        // formula-injection guard: the export is opened in Excel, which
        // EXECUTES fields starting with = + - @ — a Description crafted that
        // way would run on whoever opens the export. Leading apostrophe makes
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
