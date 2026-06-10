using System;
using System.Collections.Generic;
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
    // free column sorting, full-row select, and it scales to thousands of rows.
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

        private DataGridView _grid;
        private TextBox _search;
        private ComboBox _statusFilter;
        private Label _summary;
        private System.Windows.Forms.Timer _searchTimer;
        private Font _cellBold; // one shared bold font for the Status cell

        private List<VaultFile> _all = new List<VaultFile>();

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
            Font fHeader = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);
            Font fBtn    = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _cellBold    = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);

            // ── Top panel: title, search, status filter, refresh, export ──
            Panel top = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(120),     // tightened below once the summary is placed
                BackColor = cBg
            };

            Label title = new Label
            {
                Text = "VAULT DASHBOARD",
                Font = fTitle,
                ForeColor = cBrandDark,
                AutoSize = true,
                Location = new Point(S(14), S(10))
            };
            top.Controls.Add(title);

            int rowY = S(54);

            _search = new TextBox
            {
                Font = fCtrl,
                AutoSize = false,            // lets Height match the other controls
                Location = new Point(S(14), rowY),
                Width = S(330)
            };
            _search.TextChanged += (s, e) => DebouncedFilter();
            SetCueBanner(_search, "Search part no, description or filename…");
            top.Controls.Add(_search);

            _statusFilter = new ComboBox
            {
                Font = fCtrl,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(S(354), rowY),
                Width = S(150)
            };
            _statusFilter.Items.AddRange(new object[]
                { "All statuses", "WIP", "Released", "Locked" });
            _statusFilter.SelectedIndex = 0;
            _statusFilter.SelectedIndexChanged += (s, e) => ApplyFilter();
            top.Controls.Add(_statusFilter);

            // Uniform control height: match search + buttons to the combo's
            // natural (font-derived) height so the whole row lines up cleanly.
            int ctrlH = _statusFilter.PreferredHeight;
            _search.Height = ctrlH;

            Button btnRefresh = MakeButton("Refresh", cBrand, fBtn,
                new Point(S(514), rowY), S(100));
            btnRefresh.Height = ctrlH;
            btnRefresh.Click += (s, e) => LoadData();
            top.Controls.Add(btnRefresh);

            Button btnExport = MakeButton("Export CSV", cBrandDark, fBtn,
                new Point(S(620), rowY), S(110));
            btnExport.Height = ctrlH;
            btnExport.Click += (s, e) => ExportCsv();
            top.Controls.Add(btnExport);

            int summaryY = rowY + ctrlH + S(10);
            _summary = new Label
            {
                Font = fSummary,
                ForeColor = cTextGray,
                AutoSize = true,
                Location = new Point(S(14), summaryY)
            };
            top.Controls.Add(_summary);

            // Tighten the panel to the summary so the grid sits right below it
            // (removes the dead space under the counts).
            top.Height = summaryY + _summary.PreferredHeight + S(6);

            // ── Bottom panel: Close ───────────────────────────────────────
            Panel bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(46),
                BackColor = cBg
            };
            Button btnClose = MakeButton("Close", cTextGray, fBtn,
                new Point(0, S(8)), S(110));
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // The bottom panel spans the full client width once docked, so use
            // the (already set) client width for the initial right-aligned X.
            btnClose.Location = new Point(this.ClientSize.Width - S(124), S(8));
            btnClose.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(btnClose);

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
                // Widths are set per-column to fit content (+10%) in AutoSizeColumns.
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
            _grid.ColumnHeadersDefaultCellStyle.Font = fHeader;
            _grid.ColumnHeadersDefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleLeft;
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4), 0, 0, 0);
            _grid.DefaultCellStyle.ForeColor = cTextDark;
            _grid.DefaultCellStyle.SelectionBackColor = cBrand;
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Padding = new Padding(S(4), 0, 0, 0);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = cRowAlt;
            _grid.CellDoubleClick += Grid_CellDoubleClick;

            AddColumn("File", 22);
            AddColumn("Part No", 14);
            AddColumn("Description", 24);
            AddColumn("Status", 9);
            AddColumn("Rev", 5);
            AddColumn("Modified By", 11);
            // Typed DateTime + format so the date columns sort CHRONOLOGICALLY
            // (a string "MM/dd/yyyy" would sort alphabetically — wrong order).
            AddColumn("Modified Date", 14, typeof(DateTime), "MM/dd/yyyy HH:mm");
            AddColumn("Released By", 11);
            AddColumn("Released Date", 14, typeof(DateTime), "MM/dd/yyyy HH:mm");

            // Add Fill control FIRST, then the docked edge panels, so the grid
            // occupies the leftover space between them.
            this.Controls.Add(_grid);
            this.Controls.Add(bottom);
            this.Controls.Add(top);

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

        private void AddColumn(string header, int fillWeight,
            Type valueType = null, string format = null)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                FillWeight = fillWeight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Automatic,
                Resizable = DataGridViewTriState.True
            };
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
            ApplyFilter();
        }

        private void DebouncedFilter()
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void ApplyFilter()
        {
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();
            string statusSel = _statusFilter.SelectedIndex <= 0
                ? null : (string)_statusFilter.SelectedItem;

            var view = _all.Where(f =>
            {
                if (statusSel != null &&
                    !string.Equals(f.Status, statusSel,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                if (term.Length == 0) return true;
                return (f.PartNumber  ?? "").ToLowerInvariant().Contains(term)
                    || (f.Description ?? "").ToLowerInvariant().Contains(term)
                    || (f.FileName    ?? "").ToLowerInvariant().Contains(term);
            }).ToList();

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

            AutoSizeColumns();
            UpdateSummary(view.Count);
        }

        // Size each column to its widest value + ~20% so columns always look
        // uniform and tidy (like a content-fit Status column) instead of evenly
        // stretched. GetPreferredWidth(AllCells) measures header + every cell;
        // clamped to a sane min/max so a long description can't blow out.
        private void AutoSizeColumns()
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                int pref = col.GetPreferredWidth(
                    DataGridViewAutoSizeColumnMode.AllCells, true);
                int w = (int)(pref * 1.20);
                if (w < S(48))  w = S(48);
                if (w > S(520)) w = S(520);
                col.Width = w;
            }
        }

        private void UpdateSummary(int showing)
        {
            int wip = _all.Count(f => Eq(f.Status, "WIP"));
            int rel = _all.Count(f => Eq(f.Status, "Released"));
            int lck = _all.Count(f => Eq(f.Status, "Locked"));
            int brk = _all.Count(f => f.HasBrokenRefs);

            _summary.Text =
                $"Total: {_all.Count}      WIP: {wip}      Released: {rel}" +
                $"      Locked: {lck}      Broken refs: {brk}" +
                $"          (showing {showing})";
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
                        // the Modified date exports as shown (not the raw DateTime).
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
    }
}
