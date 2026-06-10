using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // Full-screen Vault Dashboard (Masters only). A sortable, filterable table of
    // EVERY tracked file with its status, revision, owner and dates — gives a
    // Master whole-vault visibility instead of searching file-by-file.
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
        private Label _summary;
        private Label _title;
        private Button _btnRefresh;
        private Button _btnExport;
        private Button _btnClear;
        private Panel _topPanel;
        private Panel _bottomPanel;
        private System.Windows.Forms.Timer _searchTimer;
        private Font _cellBold; // shared bold font: Status cell + header text

        private const int VisibleRows = 20;     // fixed grid height = 20 rows
        private const double MaxScreenFraction = 0.80; // popup ≤ 80% of screen
        private int GlyphZone => S(20);         // right-edge hit area for the arrow

        private List<VaultFile> _all = new List<VaultFile>();

        // Active per-column filters: column index → the SET of allowed display
        // values for that column. A column NOT in the map = unfiltered.
        private readonly Dictionary<int, HashSet<string>> _colFilters =
            new Dictionary<int, HashSet<string>>();

        // Current sort (manual, because header clicks are split sort/filter).
        // Default = Modified Date, newest first, so the freshest work is on top.
        private const int DefaultSortColumn = 6; // Modified Date
        private int _sortColumn = DefaultSortColumn;
        private ListSortDirection _sortDir = ListSortDirection.Descending;

        // Set when the Master double-clicks a row; the caller opens it after the
        // dialog closes. Null = nothing to open.
        public string FileToOpen { get; private set; }

        public VaultDashboardForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadData();
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
            Font fSummary = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            Font fGrid   = new Font("Segoe UI", 3.3f * _scale);
            Font fBtn    = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _cellBold    = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);

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

            int summaryY = rowY + ctrlH + S(10);
            _summary = new Label
            {
                Font = fSummary,
                ForeColor = cTextGray,
                AutoSize = true,
                Location = new Point(S(14), summaryY)
            };
            _topPanel.Controls.Add(_summary);

            // Tighten the panel to the summary so the grid sits right below it
            // (removes the dead space under the counts).
            _topPanel.Height = summaryY + _summary.PreferredHeight + S(6);
            _topPanel.Resize += (s, e) => LayoutTopControls();

            // ── Bottom panel: Close ───────────────────────────────────────
            _bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(46),
                BackColor = cBg
            };
            Button btnClose = MakeButton("Close", cTextGray, fBtn,
                new Point(0, S(8)), S(110));
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Location = new Point(this.ClientSize.Width - S(124), S(8));
            btnClose.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; Close(); };
            _bottomPanel.Controls.Add(btnClose);

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
            // Excel-style header: paint the filter arrow + sort glyph; split the
            // header click between sort (text) and filter (arrow).
            _grid.CellPainting += Grid_CellPainting;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;

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
                default: return "";
            }
        }

        // Sort key: date columns sort by the real DateTime (chronological);
        // everything else sorts by its lower-cased display text.
        private Func<VaultFile, IComparable> KeySelector(int col)
        {
            if (col == 6) return f => f.ModifiedDate;
            if (col == 8) return f => f.ReleasedDate;
            return f => (CellText(f, col) ?? "").ToLowerInvariant();
        }

        private void ApplyFilter()
        {
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();

            var view = _all.Where(f =>
            {
                // Per-column (Excel-style) value filters — ALL must pass.
                foreach (var kv in _colFilters)
                    if (!kv.Value.Contains(CellText(f, kv.Key)))
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

            // Repopulate the grid. SuspendLayout keeps it snappy on big lists.
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var f in view)
            {
                // DBNull for a missing date → an empty (not 01/01/0001) cell that
                // still sorts as the earliest.
                object modVal = f.ModifiedDate == DateTime.MinValue
                    ? (object)DBNull.Value : f.ModifiedDate;
                object relVal = f.ReleasedDate == DateTime.MinValue
                    ? (object)DBNull.Value : f.ReleasedDate;

                int idx = _grid.Rows.Add(
                    f.FileName, f.PartNumber, f.Description, f.Status,
                    f.Revision, f.ModifiedBy, modVal, f.ReleasedBy, relVal);

                var row = _grid.Rows[idx];
                row.Tag = f.FilePath;

                var statusCell = row.Cells[3];
                statusCell.Style.ForeColor = StatusColor(f.Status);
                statusCell.Style.Font = _cellBold;

                if (f.HasBrokenRefs)
                {
                    row.Cells[0].Style.ForeColor = cRed;
                    row.Cells[0].ToolTipText = "Has broken references";
                }
            }
            _grid.ResumeLayout();

            UpdateSummary(view.Count);
            LayoutTopControls(); // re-centre: the summary width changed
        }

        private void ClearAllFilters()
        {
            _colFilters.Clear();
            _sortColumn = DefaultSortColumn;          // back to newest-first
            _sortDir = ListSortDirection.Descending;
            _search.Text = "";       // triggers a debounced re-filter…
            ApplyFilter();           // …and apply immediately too
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
            var rect = _grid.GetCellDisplayRectangle(e.ColumnIndex, -1, true);
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
        // header arrow. Distinct values come from the FULL data set for that
        // column (so the list is stable regardless of other active filters).
        private void ShowColumnFilter(int col)
        {
            var values = _all
                .Select(f => CellText(f, col))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            HashSet<string> current;
            if (!_colFilters.TryGetValue(col, out current))
                current = null; // null = currently unfiltered (all checked)

            using (var dlg = new ColumnFilterDialog(_scale,
                _grid.Columns[col].HeaderText, values, current,
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
                    if (dlg.SelectedValues == null) _colFilters.Remove(col);
                    else _colFilters[col] = dlg.SelectedValues;
                    ApplyFilter();
                    _grid.Invalidate(); // repaint funnel glyph
                }
            }
        }

        // Size each column to its widest value + ~20% so columns always look
        // uniform and tidy instead of evenly stretched. GetPreferredWidth(AllCells)
        // measures header + every cell; clamped to a sane min/max.
        private void AutoSizeColumns()
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                int pref = col.GetPreferredWidth(
                    DataGridViewAutoSizeColumnMode.AllCells, true);
                int w = (int)(pref * 1.20) + GlyphZone; // room for the filter arrow
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

            int chrome = S(4);
            if (_all.Count > VisibleRows)
                chrome += SystemInformation.VerticalScrollBarWidth;
            int clientW = totalCols + chrome;

            var area = Screen.FromControl(this).WorkingArea;
            int borderW = this.Width - this.ClientSize.Width;   // 0 before shown
            int maxClientW = (int)(area.Width * MaxScreenFraction) - borderW;
            if (clientW > maxClientW) clientW = maxClientW;
            int minClientW = S(800);                            // keep top row visible
            if (clientW < minClientW) clientW = minClientW;

            int gridH = _grid.ColumnHeadersHeight
                      + _grid.RowTemplate.Height * VisibleRows + S(2);
            int clientH = _topPanel.Height + gridH + _bottomPanel.Height;
            int borderH = this.Height - this.ClientSize.Height;
            int maxClientH = (int)(area.Height * MaxScreenFraction) - borderH;
            if (clientH > maxClientH) clientH = maxClientH;

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
                     + _btnExport.Width + gap + _btnClear.Width;
            int startX = Math.Max(S(14), (panelW - rowW) / 2);
            _search.Left     = startX;
            _btnRefresh.Left = _search.Right + gap;
            _btnExport.Left  = _btnRefresh.Right + gap;
            _btnClear.Left   = _btnExport.Right + gap;

            if (_summary != null)
                _summary.Left = Math.Max(S(14), (panelW - _summary.Width) / 2);
        }

        private void UpdateSummary(int showing)
        {
            int wip = _all.Count(f => Eq(f.Status, "WIP"));
            int rel = _all.Count(f => Eq(f.Status, "Released"));
            int lck = _all.Count(f => Eq(f.Status, "Locked"));
            int brk = _all.Count(f => f.HasBrokenRefs);

            _summary.Text =
                $"Total: {_all.Count}      WIP: {wip}      Released: {rel}" +
                $"      Locked: {lck}      Broken Refs: {brk}" +
                $"          (Showing {showing})";
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
            if (e.RowIndex < 0) return;
            string path = _grid.Rows[e.RowIndex].Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            FileToOpen = path;
            this.DialogResult = DialogResult.OK;
            Close();
        }

        // ── Export the CURRENT (filtered) view to CSV ───────────────────────
        private void ExportCsv()
        {
            if (_grid.Rows.Count == 0)
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
                    var sb = new StringBuilder();
                    var headers = _grid.Columns.Cast<DataGridViewColumn>()
                        .Select(c => Csv(c.HeaderText));
                    sb.AppendLine(string.Join(",", headers));

                    foreach (DataGridViewRow row in _grid.Rows)
                    {
                        // FormattedValue applies each column's display format, so
                        // the dates export as shown (not the raw DateTime).
                        var cells = row.Cells.Cast<DataGridViewCell>()
                            .Select(c => Csv(Convert.ToString(c.FormattedValue)));
                        sb.AppendLine(string.Join(",", cells));
                    }

                    File.WriteAllText(dlg.FileName, sb.ToString());
                    MessageBox.Show("Exported " + _grid.Rows.Count +
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
        //  Excel-style per-column filter popup: a searchable checkbox list of the
        //  column's distinct values, with Select All / Clear and OK / Cancel.
        //  SelectedValues == null  →  every value is ticked (i.e. NO filter).
        // ────────────────────────────────────────────────────────────────────
        private sealed class ColumnFilterDialog : Form
        {
            private readonly float _scale;
            private int S(float v) => (int)(v * _scale);

            private readonly List<string> _allValues;            // raw distinct values
            private readonly Dictionary<string, bool> _state;    // raw → checked
            private readonly List<string> _visibleRaw = new List<string>();
            private CheckedListBox _list;
            private TextBox _search;

            // null = all values selected (no filter); otherwise the allowed set.
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
                btnAll.Click += (s, e) => SetVisible(true);
                Controls.Add(btnAll);
                var btnNone = MakeBtn("Clear", cBrandDark, fBtn,
                    new Point(S(120), btnY), S(108), rowH);
                btnNone.Click += (s, e) => SetVisible(false);
                Controls.Add(btnNone);

                int listY = btnY + rowH + S(6);
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

                var ok = MakeBtn("OK", cBrand, fBtn,
                    new Point(S(8), okY), S(108), rowH);
                ok.Click += (s, e) => { Commit(); DialogResult = DialogResult.OK; Close(); };
                Controls.Add(ok);
                var cancel = MakeBtn("Cancel", Color.FromArgb(120, 128, 140), fBtn,
                    new Point(S(120), okY), S(108), rowH);
                cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(cancel);

                AcceptButton = ok;
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
            }

            // Tick/untick every CURRENTLY VISIBLE item (respects the search box).
            private void SetVisible(bool check)
            {
                foreach (var raw in _visibleRaw) _state[raw] = check;
                RebuildList();
            }

            private void RebuildList()
            {
                string term = (_search.Text ?? "").Trim();
                _list.ItemCheck -= List_ItemCheck; // no feedback while repopulating
                _list.BeginUpdate();
                _list.Items.Clear();
                _visibleRaw.Clear();
                foreach (var raw in _allValues)
                {
                    string disp = raw.Length == 0 ? "(Blanks)" : raw;
                    if (term.Length > 0 &&
                        disp.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _visibleRaw.Add(raw);
                    _list.Items.Add(disp, _state[raw]);
                }
                _list.EndUpdate();
                _list.ItemCheck += List_ItemCheck;
            }

            private void Commit()
            {
                var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _state) if (kv.Value) chosen.Add(kv.Key);
                // All values ticked → treat as no filter (null).
                SelectedValues = chosen.Count == _allValues.Count ? null : chosen;
            }
        }
    }
}
