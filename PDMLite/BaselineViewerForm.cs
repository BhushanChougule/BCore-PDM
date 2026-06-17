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
    // Read-only viewer for an assembly's captured "as-released" baselines.
    // Shows, for any released revision, the EXACT child file set (name, part
    // number, revision, status, qty) the assembly was released against — the
    // snapshot persisted by BaselineManager at release time. A release selector
    // switches between revisions when the assembly has been released more than
    // once. Self-contained (own palette / S() / CSV escaping) per the house
    // one-form-one-file convention. Never writes the vault — pure read + export.
    public class BaselineViewerForm : Form
    {
        private readonly string _asmPath;
        private readonly string _asmName;
        private List<AssemblyBaseline> _baselines = new List<AssemblyBaseline>();

        private ComboBox _revPicker;
        private Label _metaLabel;
        private DataGridView _grid;
        private Label _countLabel;
        private Button _btnExport;
        private Button _btnExpandAll, _btnCollapseAll;

        // Tree state for the SELECTED baseline. _full = its components in stored
        // depth-first order; _hasKids[i] = component i is a sub-assembly with
        // children; _collapsed = component indices whose subtree is hidden;
        // _rowToComp maps a grid row back to its _full index (for click toggling).
        private List<BaselineComponent> _full = new List<BaselineComponent>();
        private bool[] _hasKids = new bool[0];
        private readonly HashSet<int> _collapsed = new HashSet<int>();
        private readonly List<int> _rowToComp = new List<int>();

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        // House palette (matches the dashboard / task pane).
        private static readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private static readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private static readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private static readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private static readonly Color cMaroon    = Color.FromArgb(140, 60, 60);
        private static readonly Color cTextDark   = Color.FromArgb(60, 64, 72);
        private static readonly Color cBg         = Color.FromArgb(245, 247, 250);

        private Font _fHeader, _fSub, _fMeta, _fLabel, _fBtn, _fGrid, _fGridHead;

        public BaselineViewerForm(string assemblyPath, string assemblyName)
        {
            _asmPath = assemblyPath;
            _asmName = assemblyName;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fSub      = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _fMeta     = new Font("Segoe UI", 3.5f * _scale);
            _fLabel    = new Font("Segoe UI", 3.7f * _scale);
            _fBtn      = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridHead = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            try { _baselines = DatabaseManager.GetBaselines(_asmPath); }
            catch { _baselines = new List<AssemblyBaseline>(); }

            BuildUI();
            LoadSelected();
        }

        private void BuildUI()
        {
            this.Text = "BCore PDM — As-Released Baseline";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.KeyPreview = true;
            this.ClientSize = new Size(S(560), S(560));
            this.MinimumSize = new Size(S(420), S(360));

            // ── Header bar ────────────────────────────────────────────
            var headerBar = new Panel
            {
                BackColor = cBrandDark,
                Dock = DockStyle.Top,
                Height = S(30)
            };
            headerBar.Controls.Add(new Label
            {
                Text = "As-Released Baseline",
                Font = _fHeader,
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });

            // ── Top panel: assembly name + release picker + meta + tree buttons ──
            var top = new Panel { Dock = DockStyle.Top, Height = S(116), BackColor = cBg };

            var nameLbl = new Label
            {
                Text = string.IsNullOrEmpty(_asmName)
                    ? Path.GetFileName(_asmPath ?? "") : _asmName,
                Font = _fSub,
                ForeColor = cTextDark,
                Location = new Point(S(14), S(8)),
                AutoSize = false,
                Width = S(520),
                Height = S(20),
                AutoEllipsis = true
            };
            top.Controls.Add(nameLbl);

            top.Controls.Add(new Label
            {
                Text = "Release:",
                Font = _fLabel,
                ForeColor = cTextDark,
                Location = new Point(S(14), S(34)),
                AutoSize = true
            });

            _revPicker = new ComboBox
            {
                Font = _fLabel,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(S(70), S(31)),
                Width = S(380),                 // wide enough for "REV x (cfg) — date"
                DropDownWidth = S(420)          // so the open list never clips
            };
            foreach (var b in _baselines)
                _revPicker.Items.Add("REV " + b.Revision +
                    (string.IsNullOrEmpty(b.Config) ? "" : "   (" + b.Config + ")") +
                    "   —   " + FmtDate(b.ReleasedDate));
            if (_revPicker.Items.Count > 0) _revPicker.SelectedIndex = 0;
            _revPicker.SelectedIndexChanged += (s, e) => LoadSelected();
            top.Controls.Add(_revPicker);

            _metaLabel = new Label
            {
                Font = _fMeta,
                ForeColor = Color.FromArgb(110, 116, 126),
                Location = new Point(S(14), S(58)),
                AutoSize = false,
                Width = S(520),
                Height = S(18),
                AutoEllipsis = true
            };
            top.Controls.Add(_metaLabel);

            // Tree controls: collapse to top-level-only, or expand everything.
            // (Per sub-assembly: click its ▸/▾ row to toggle.)
            _btnExpandAll = MakeButton("Expand All", cBrand, Color.White);
            _btnExpandAll.Location = new Point(S(14), S(84));
            _btnExpandAll.Click += (s, e) => { _collapsed.Clear(); RenderRows(); };
            top.Controls.Add(_btnExpandAll);

            _btnCollapseAll = MakeButton("Collapse All", Color.FromArgb(220, 220, 220), cTextDark);
            _btnCollapseAll.Location = new Point(_btnExpandAll.Right + S(8), S(84));
            _btnCollapseAll.Click += (s, e) =>
            {
                _collapsed.Clear();
                for (int i = 0; i < _hasKids.Length; i++)
                    if (_hasKids[i]) _collapsed.Add(i);
                RenderRows();
            };
            top.Controls.Add(_btnCollapseAll);

            top.Controls.Add(new Label
            {
                Text = "Tip: click a ▸/▾ sub-assembly row to expand or collapse it.",
                Font = _fMeta, ForeColor = Color.FromArgb(140, 146, 156),
                Location = new Point(_btnCollapseAll.Right + S(12), S(89)),
                AutoSize = false, Width = S(300), Height = S(16), AutoEllipsis = true
            });

            // ── Bottom button row ─────────────────────────────────────
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = cBg };

            _countLabel = new Label
            {
                Font = _fMeta,
                ForeColor = cTextDark,
                Location = new Point(S(14), S(14)),
                AutoSize = true
            };
            bottom.Controls.Add(_countLabel);

            var btnClose = MakeButton("Close", Color.FromArgb(220, 220, 220),
                cTextDark);
            btnClose.Click += (s, e) => this.Close();

            _btnExport = MakeButton("Export CSV", cBrand, Color.White);
            _btnExport.Click += (s, e) => ExportCsv();

            // Anchor both to the bottom-right; lay out on resize.
            bottom.Controls.Add(_btnExport);
            bottom.Controls.Add(btnClose);
            bottom.Resize += (s, e) =>
            {
                btnClose.Location = new Point(
                    bottom.Width - btnClose.Width - S(14), S(9));
                _btnExport.Location = new Point(
                    btnClose.Left - _btnExport.Width - S(8), S(9));
            };

            // ── Component grid (fills the middle) ─────────────────────
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = _fGrid,
                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                AllowUserToResizeColumns = true
            };
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = _fGridHead;
            _grid.ColumnHeadersHeight = S(26);
            _grid.AlternatingRowsDefaultCellStyle.BackColor =
                Color.FromArgb(245, 248, 251);
            _grid.RowTemplate.Height = S(22);

            AddCol("Component", 0.34f);
            AddCol("Part No", 0.22f);
            AddCol("Rev", 0.10f);
            AddCol("Status", 0.18f);
            AddCol("Qty", 0.10f, DataGridViewContentAlignment.MiddleRight);
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellClick += Grid_CellClick; // toggle a sub-assembly's subtree

            // Docking resolves by z-order: the Fill control must be added FIRST
            // (it is resolved LAST, so it takes the leftover middle), then the
            // edge panels. Among the two Top panels the one added LAST docks
            // outermost, so add the inner "top" panel before the header bar.
            this.Controls.Add(_grid);
            this.Controls.Add(bottom);
            this.Controls.Add(top);
            this.Controls.Add(headerBar);

            // Initial button layout (the Resize handler covers later resizes;
            // 'bottom' now has its docked width).
            btnClose.Location = new Point(
                bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(
                btnClose.Left - _btnExport.Width - S(8), S(9));

            this.FormClosed += (s, e) =>
            {
                _fHeader?.Dispose();
                _fSub?.Dispose();
                _fMeta?.Dispose();
                _fLabel?.Dispose();
                _fBtn?.Dispose();
                _fGrid?.Dispose();
                _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) this.Close();
            };
        }

        private void AddCol(string header, float weight,
            DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = weight * 100f,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            col.DefaultCellStyle.Alignment = align;
            _grid.Columns.Add(col);
        }

        private AssemblyBaseline Selected()
        {
            int i = _revPicker?.SelectedIndex ?? -1;
            if (i < 0 || i >= _baselines.Count) return null;
            return _baselines[i];
        }

        private void LoadSelected()
        {
            if (_grid == null) return;

            var b = Selected();
            _full = b?.Components ?? new List<BaselineComponent>();
            _collapsed.Clear(); // start fully expanded on every release switch

            // A component is an expandable sub-assembly if the NEXT row is deeper
            // (depth-first order guarantees a node's descendants follow it).
            _hasKids = new bool[_full.Count];
            for (int i = 0; i < _full.Count; i++)
                _hasKids[i] = i + 1 < _full.Count &&
                              _full[i + 1].Level > _full[i].Level;

            bool enable = b != null;
            if (_btnExpandAll != null) _btnExpandAll.Enabled = enable;
            if (_btnCollapseAll != null) _btnCollapseAll.Enabled = enable;

            if (b == null)
            {
                _grid.Rows.Clear();
                _rowToComp.Clear();
                _metaLabel.Text = _baselines.Count == 0
                    ? "No baselines captured yet — this assembly has not been " +
                      "released since the baseline feature shipped."
                    : "";
                _countLabel.Text = "";
                if (_btnExport != null) _btnExport.Enabled = false;
                return;
            }

            _metaLabel.Text =
                "Part No " + (string.IsNullOrEmpty(b.PartNo) ? "(none)" : b.PartNo) +
                "   ·   REV " + b.Revision +
                "   ·   Released by " + b.ReleasedBy + " on " + FmtDate(b.ReleasedDate);

            RenderRows();

            int total = _full.Sum(c => Math.Max(c.Qty, 0));
            _countLabel.Text = _full.Count + " line" +
                (_full.Count == 1 ? "" : "s") + "  ·  " + total + " total qty";
            if (_btnExport != null) _btnExport.Enabled = _full.Count > 0;
        }

        // Rebuild the visible grid rows from _full, honouring _collapsed: a
        // collapsed sub-assembly hides every deeper row until the tree returns
        // to its level or shallower (works for nested collapses). The arrow
        // shows ▾ (expanded) / ▸ (collapsed) on sub-assemblies; leaves align
        // under the name. _rowToComp maps each grid row back to its _full index.
        private void RenderRows()
        {
            if (_grid == null) return;
            _grid.Rows.Clear();
            _rowToComp.Clear();

            int hideDeeperThan = -1; // -1 = not hiding; else hide rows with Level >
            for (int i = 0; i < _full.Count; i++)
            {
                var c = _full[i];
                int level = Math.Max(c.Level, 0);

                if (hideDeeperThan >= 0 && level > hideDeeperThan)
                    continue;                 // descendant of a collapsed node
                hideDeeperThan = -1;          // back to a visible level

                bool kids = i < _hasKids.Length && _hasKids[i];
                bool collapsed = kids && _collapsed.Contains(i);
                string arrow = kids ? (collapsed ? "▸ " : "▾ ") : "   ";
                string label = new string(' ', level * 4) + arrow +
                    Path.GetFileNameWithoutExtension(c.Name ?? "");

                int row = _grid.Rows.Add(label, c.PartNo, c.Revision, c.Status, c.Qty);
                if (kids) _grid.Rows[row].Tag = "sub"; // bolded in CellFormatting
                _rowToComp.Add(i);

                if (collapsed) hideDeeperThan = level; // hide its subtree
            }
        }

        // Click a sub-assembly's Component cell (the ▸/▾ row) to expand/collapse it.
        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return; // Component column only
            if (e.RowIndex >= _rowToComp.Count) return;
            int comp = _rowToComp[e.RowIndex];
            if (comp < 0 || comp >= _hasKids.Length || !_hasKids[comp]) return;

            if (!_collapsed.Remove(comp)) _collapsed.Add(comp); // toggle
            // Defer the rebuild so we don't mutate Rows inside the grid's own
            // click processing (avoids re-entrancy on the cell that was clicked).
            BeginInvoke((Action)RenderRows);
        }

        // Colour the Status cell; bold the Component cell on sub-assembly rows so
        // the structure reads as a tree.
        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string header = _grid.Columns[e.ColumnIndex].HeaderText;

            if (header == "Component" &&
                (_grid.Rows[e.RowIndex].Tag as string) == "sub")
                e.CellStyle.Font = _fGridHead; // bold sub-assembly headings

            if (header != "Status") return;
            string s = (e.Value as string) ?? "";
            if (s.Equals("Released", StringComparison.OrdinalIgnoreCase))
                e.CellStyle.ForeColor = cGreen;
            else if (s.Equals("Locked", StringComparison.OrdinalIgnoreCase))
                e.CellStyle.ForeColor = cMaroon;
            else if (s.Equals("WIP", StringComparison.OrdinalIgnoreCase))
                e.CellStyle.ForeColor = cOrange;
            else
                e.CellStyle.ForeColor = Color.FromArgb(140, 140, 140);
        }

        // Display a stored "yyyy-MM-dd HH:mm:ss" timestamp as MM/dd/yyyy HH:mm:ss
        // (house convention). Storage stays ISO so it sorts chronologically.
        private static string FmtDate(string stored)
        {
            DateTime dt;
            if (DateTime.TryParseExact(stored, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            return stored ?? "";
        }

        private void ExportCsv()
        {
            var b = Selected();
            if (b == null) return;
            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(_asmName ?? "baseline"))
                    + "-R" + b.Revision + "_BASELINE.csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Assembly,PartNo,Revision,Config,ReleasedBy,ReleasedDate");
                    sb.AppendLine(string.Join(",", new[]
                    {
                        Csv(_asmName), Csv(b.PartNo), Csv(b.Revision), Csv(b.Config),
                        Csv(b.ReleasedBy), Csv(FmtDate(b.ReleasedDate))
                    }));
                    sb.AppendLine();
                    // Level column + indented name preserve the tree in Excel.
                    sb.AppendLine("Level,Component,PartNo,Revision,Status,Qty");
                    foreach (var c in b.Components)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(c.Level.ToString()),
                            Csv(new string(' ', Math.Max(c.Level, 0) * 2) +
                                Path.GetFileNameWithoutExtension(c.Name ?? "")),
                            Csv(c.PartNo), Csv(c.Revision), Csv(c.Status),
                            Csv(c.Qty.ToString())
                        }));
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show("Baseline exported to:\n" + sfd.FileName,
                        "BCore PDM — Baseline Exported",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not export the baseline:\n" + ex.Message,
                        "BCore PDM — Export Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // RFC-4180 escaping + Excel formula-injection guard (a field beginning
        // with = + - @ is prefixed with an apostrophe so Excel treats it as text).
        private static string Csv(string field)
        {
            field = field ?? "";
            if (field.Length > 0 && "=+-@".IndexOf(field[0]) >= 0)
                field = "'" + field;
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                field = "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "baseline";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(
                c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        private Button MakeButton(string text, Color back, Color fore)
        {
            var b = new Button
            {
                Text = text,
                Font = _fBtn,
                Width = S(96),
                Height = S(26),
                BackColor = back,
                ForeColor = fore,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
