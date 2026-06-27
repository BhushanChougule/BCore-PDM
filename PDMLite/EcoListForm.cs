using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // ── Engineering Change Orders — list view ─────────────────────────────
    //
    // DPI-aware (S(v)=v*_scale), house-styled (brand title bar, dark-header grid,
    // flat buttons; fonts disposed in Dispose(bool)). The Master entry point to
    // the ECO subsystem (opened from the task-pane "Change Orders" button). Lists
    // all ECOs in a read-only-ish DataGridView with a STATE filter dropdown + a
    // global search box (Number / Title / Reason / Created By). Double-click (or
    // "Edit…") opens the ECO in EcoForm; "New ECO…" creates one; "Export CSV"
    // dumps the visible rows (RFC-4180 + Excel formula-injection guard).
    //
    // The data set is small (one ECO per change, not per file), so a PLAIN
    // (non-virtual) grid is fine — unlike the whole-vault dashboard.
    internal class EcoListForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private const string AnyState = "— Any state —";

        private readonly ComboBox _stateFilter;
        private readonly TextBox  _search;
        private readonly DataGridView _grid;
        private readonly Label _count;
        private readonly Font _fTitle, _fLabel, _fInput, _fBtn, _fCell, _fHeader;

        private List<Eco> _all = new List<Eco>();
        private List<Eco> _view = new List<Eco>();

        public EcoListForm(float scale)
        {
            _scale = scale > 0 ? scale : 1f;

            _fTitle  = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fLabel  = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);
            _fInput  = new Font("Segoe UI", 3.6f * _scale);
            _fBtn    = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);
            _fCell   = new Font("Segoe UI", 3.4f * _scale);
            _fHeader = new Font("Segoe UI", 3.4f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Engineering Change Orders";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(820), S(560));
            MinimumSize     = new Size(S(620), S(420));

            int cW = ClientSize.Width;

            // ── Brand title bar ───────────────────────────────────────
            Panel titleBar = new Panel
            {
                BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Engineering Change Orders", Font = _fTitle,
                ForeColor = Color.White, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });

            // ── Bottom button bar ─────────────────────────────────────
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40) };

            Button btnClose = MakeBtn("Close", cDark);
            btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnClose.Location = new Point(cW - S(8) - btnClose.Width, S(7));
            btnClose.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(btnClose);

            Button btnExport = MakeBtn("Export CSV", cBrand);
            btnExport.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnExport.Location = new Point(
                btnClose.Left - S(6) - btnExport.Width, S(7));
            btnExport.Click += (s, e) => ExportCsv();
            bottom.Controls.Add(btnExport);

            Button btnEdit = MakeBtn("Edit…", cBrand);
            btnEdit.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            btnEdit.Location = new Point(S(8), S(7));
            btnEdit.Click += (s, e) => EditSelected();
            bottom.Controls.Add(btnEdit);

            Button btnNew = MakeBtn("New ECO…", cGreen);
            btnNew.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            btnNew.Location = new Point(btnEdit.Right + S(6), S(7));
            btnNew.Click += (s, e) => NewEco();
            bottom.Controls.Add(btnNew);

            // ── Top control row (state filter + search) ───────────────
            Panel top = new Panel { Dock = DockStyle.Top, Height = S(58) };

            top.Controls.Add(new Label
            {
                Text = "State:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(S(8), S(10)), AutoSize = false,
                Width = S(46), Height = S(20),
                TextAlign = ContentAlignment.MiddleLeft
            });
            _stateFilter = new ComboBox
            {
                Font = _fInput, Location = new Point(S(56), S(7)),
                Width = S(150), DropDownStyle = ComboBoxStyle.DropDownList
            };
            _stateFilter.Items.Add(AnyState);
            foreach (var st in EcoManager.States) _stateFilter.Items.Add(st);
            _stateFilter.SelectedIndex = 0;
            _stateFilter.SelectedIndexChanged += (s, e) => ApplyFilter();
            top.Controls.Add(_stateFilter);

            top.Controls.Add(new Label
            {
                Text = "Search:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(S(220), S(10)), AutoSize = false,
                Width = S(52), Height = S(20),
                TextAlign = ContentAlignment.MiddleLeft
            });
            _search = new TextBox
            {
                Font = _fInput, Location = new Point(S(274), S(7)),
                Width = S(260), Height = S(22)
            };
            _search.TextChanged += (s, e) => ApplyFilter();
            top.Controls.Add(_search);

            _count = new Label
            {
                Font = _fInput, ForeColor = cTextDark,
                Location = new Point(S(8), S(34)), AutoSize = false,
                Width = cW - S(16), Height = S(18),
                TextAlign = ContentAlignment.MiddleLeft
            };
            top.Controls.Add(_count);

            // ── Grid (Fill — added FIRST per the house z-order convention) ─
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, Font = _fCell,
                ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                BackgroundColor = Color.White,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToOrderColumns = false
            };
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = _fHeader;
            _grid.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.AlternatingRowsDefaultCellStyle.BackColor =
                Color.FromArgb(244, 247, 250);

            AddCol("ECO #", 14);
            AddCol("Title", 32);
            AddCol("State", 14);
            AddCol("Reason", 22);
            AddCol("Items", 8);
            AddCol("Created By", 14);
            AddCol("Created", 18);
            _grid.CellDoubleClick += (s, e) =>
            { if (e.RowIndex >= 0) EditSelected(); };

            // Z-order: Fill first, then docked edges (house convention).
            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(bottom);
            Controls.Add(titleBar);

            CancelButton = btnClose;
            LoadData();
        }

        private void AddCol(string header, int fillWeight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                FillWeight = fillWeight,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        }

        private Button MakeBtn(string text, Color back)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn, Width = S(100), Height = S(26),
                BackColor = back, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void LoadData()
        {
            try { _all = EcoManager.GetEcos() ?? new List<Eco>(); }
            catch { _all = new List<Eco>(); }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string state = _stateFilter.SelectedItem as string ?? AnyState;
            string term = (_search.Text ?? "").Trim().ToLowerInvariant();

            IEnumerable<Eco> q = _all;
            if (!string.Equals(state, AnyState, StringComparison.Ordinal))
                q = q.Where(e => string.Equals(e.State, state,
                    StringComparison.OrdinalIgnoreCase));
            if (term.Length > 0)
                q = q.Where(e =>
                    ((e.Number ?? "").ToLowerInvariant().Contains(term)) ||
                    ((e.Title ?? "").ToLowerInvariant().Contains(term)) ||
                    ((e.Reason ?? "").ToLowerInvariant().Contains(term)) ||
                    ((e.CreatedBy ?? "").ToLowerInvariant().Contains(term)));

            _view = q.ToList();
            RenderRows();
        }

        private void RenderRows()
        {
            _grid.Rows.Clear();
            foreach (var e in _view)
                _grid.Rows.Add(
                    e.Number ?? "",
                    e.Title ?? "",
                    e.State ?? "",
                    e.Reason ?? "",
                    (e.Items != null ? e.Items.Count : 0).ToString(
                        CultureInfo.InvariantCulture),
                    e.CreatedBy ?? "",
                    FmtDate(e.CreatedDate));
            _count.Text = _view.Count + " of " + _all.Count + " ECO(s)" +
                "   ·   double-click a row to open";
        }

        // ISO storage → MM/dd/yyyy HH:mm display (InvariantCulture, house rule).
        private static string FmtDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            DateTime dt;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt))
                return dt.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);
            return iso;
        }

        private Eco SelectedEco()
        {
            if (_grid.CurrentRow == null) return null;
            int i = _grid.CurrentRow.Index;
            if (i < 0 || i >= _view.Count) return null;
            return _view[i];
        }

        private void NewEco()
        {
            try
            {
                using (var f = new EcoForm(null))
                    if (f.ShowDialog(this) == DialogResult.OK) LoadData();
            }
            catch { }
        }

        private void EditSelected()
        {
            var eco = SelectedEco();
            if (eco == null) return;
            try
            {
                using (var f = new EcoForm(eco))
                    if (f.ShowDialog(this) == DialogResult.OK) LoadData();
            }
            catch { }
        }

        private void ExportCsv()
        {
            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "ECOs_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture) + ".csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(
                        "ECO #,Title,State,Reason,Description,Items," +
                        "Created By,Created,Closed By,Closed,Affected Items");
                    foreach (var e in _view)
                    {
                        string items = e.Items == null ? "" : string.Join("; ",
                            e.Items.Select(i =>
                                (i.PartNo ?? Path.GetFileName(i.FilePath ?? "")) +
                                " " + (i.FromRev ?? "") + "→" + (i.ToRev ?? "")));
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(e.Number), Csv(e.Title), Csv(e.State),
                            Csv(e.Reason), Csv(e.Description),
                            Csv((e.Items != null ? e.Items.Count : 0)
                                .ToString(CultureInfo.InvariantCulture)),
                            Csv(e.CreatedBy), Csv(FmtDate(e.CreatedDate)),
                            Csv(e.ClosedBy), Csv(FmtDate(e.ClosedDate)),
                            Csv(items)
                        }));
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show("Exported " + _view.Count + " ECO(s) to:\n" +
                        sfd.FileName, "BCore PDM — Exported",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not export:\n" + ex.Message,
                        "BCore PDM — Export Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // RFC-4180 + Excel formula-injection guard (mirrors AuditLogger.Csv).
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

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle?.Dispose(); _fLabel?.Dispose(); _fInput?.Dispose();
                _fBtn?.Dispose();   _fCell?.Dispose();  _fHeader?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
