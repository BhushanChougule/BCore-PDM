using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // Read-only "compare two as-released baselines" view: what changed in an
    // assembly's child set between any two of its releases — the core
    // engineering-change question ("what's different between ASM-100 REV B and
    // REV C?"). Pure data diff over the snapshots BaselineManager captured at
    // release (never live-read), so it is exact and always available. Opened
    // from BaselineViewerForm ("Compare…"); self-contained per the one-form-one-
    // file convention. Never writes the vault.
    public class BaselineCompareForm : Form
    {
        private readonly string _asmPath;
        private readonly string _asmName;
        private List<AssemblyBaseline> _baselines = new List<AssemblyBaseline>();

        private ComboBox _fromPicker, _toPicker;
        private CheckBox _showUnchanged;
        private DataGridView _grid;
        private Label _summary;
        private Button _btnExport;

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        private static readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private static readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private static readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private static readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private static readonly Color cRed       = Color.FromArgb(180, 75, 75);
        private static readonly Color cTextDark  = Color.FromArgb(60, 64, 72);
        private static readonly Color cGray       = Color.FromArgb(140, 140, 140);
        private static readonly Color cBg         = Color.FromArgb(245, 247, 250);

        private Font _fHeader, _fSub, _fMeta, _fBtn, _fGrid, _fGridHead;

        // One diff row.
        private sealed class DiffRow
        {
            public string Change;   // Added / Removed / Changed / Unchanged
            public string Name;
            public string PartNo;
            public string FromRev;
            public string ToRev;
            public long   FromQty;
            public long   ToQty;
            public Color  Colour;
        }

        public BaselineCompareForm(string assemblyPath, string assemblyName)
        {
            _asmPath = assemblyPath;
            _asmName = assemblyName;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fSub      = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _fMeta     = new Font("Segoe UI", 3.5f * _scale);
            _fBtn      = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridHead = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            try { _baselines = DatabaseManager.GetBaselines(_asmPath); }
            catch { _baselines = new List<AssemblyBaseline>(); }

            BuildUI();
            Recompute();
        }

        private void BuildUI()
        {
            this.Text = "BCore PDM — Compare Baselines";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.KeyPreview = true;
            this.ClientSize = new Size(S(640), S(560));
            this.MinimumSize = new Size(S(480), S(360));

            var headerBar = new Panel { BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(30) };
            headerBar.Controls.Add(new Label
            {
                Text = "Compare As-Released Baselines",
                Font = _fHeader, ForeColor = Color.White,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });

            var top = new Panel { Dock = DockStyle.Top, Height = S(92), BackColor = cBg };
            top.Controls.Add(new Label
            {
                Text = string.IsNullOrEmpty(_asmName)
                    ? Path.GetFileName(_asmPath ?? "") : _asmName,
                Font = _fSub, ForeColor = cTextDark,
                Location = new Point(S(14), S(8)),
                AutoSize = false, Width = S(600), Height = S(20), AutoEllipsis = true
            });

            top.Controls.Add(new Label
            {
                Text = "From:", Font = _fMeta, ForeColor = cTextDark,
                Location = new Point(S(14), S(36)), AutoSize = true
            });
            _fromPicker = new ComboBox
            {
                Font = _fMeta, DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(S(54), S(33)), Width = S(230)
            };
            top.Controls.Add(_fromPicker);

            top.Controls.Add(new Label
            {
                Text = "To:", Font = _fMeta, ForeColor = cTextDark,
                Location = new Point(S(300), S(36)), AutoSize = true
            });
            _toPicker = new ComboBox
            {
                Font = _fMeta, DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(S(330), S(33)), Width = S(230)
            };
            top.Controls.Add(_toPicker);

            // Populate both pickers with every release (most recent first).
            foreach (var b in _baselines)
            {
                string label = "REV " + b.Revision +
                    (string.IsNullOrEmpty(b.Config) ? "" : " (" + b.Config + ")") +
                    "  —  " + b.ReleasedDate;
                _fromPicker.Items.Add(label);
                _toPicker.Items.Add(label);
            }
            // Default: From = older of the two latest (index 1), To = newest (0).
            if (_baselines.Count >= 2) { _fromPicker.SelectedIndex = 1; _toPicker.SelectedIndex = 0; }
            else if (_baselines.Count == 1) { _fromPicker.SelectedIndex = 0; _toPicker.SelectedIndex = 0; }
            _fromPicker.SelectedIndexChanged += (s, e) => Recompute();
            _toPicker.SelectedIndexChanged += (s, e) => Recompute();

            _showUnchanged = new CheckBox
            {
                Text = "Show unchanged", Font = _fMeta, ForeColor = cTextDark,
                Location = new Point(S(14), S(62)), AutoSize = true, Checked = false
            };
            _showUnchanged.CheckedChanged += (s, e) => Recompute();
            top.Controls.Add(_showUnchanged);

            _summary = new Label
            {
                Font = _fMeta, ForeColor = Color.FromArgb(110, 116, 126),
                Location = new Point(S(150), S(63)),
                AutoSize = false, Width = S(470), Height = S(18), AutoEllipsis = true
            };
            top.Controls.Add(_summary);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = cBg };
            var btnClose = MakeButton("Close", Color.FromArgb(220, 220, 220), cTextDark);
            btnClose.Click += (s, e) => this.Close();
            _btnExport = MakeButton("Export CSV", cBrand, Color.White);
            _btnExport.Click += (s, e) => ExportCsv();
            bottom.Controls.Add(_btnExport);
            bottom.Controls.Add(btnClose);
            bottom.Resize += (s, e) =>
            {
                btnClose.Location = new Point(bottom.Width - btnClose.Width - S(14), S(9));
                _btnExport.Location = new Point(btnClose.Left - _btnExport.Width - S(8), S(9));
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                Font = _fGrid, AllowUserToResizeColumns = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = _fGridHead;
            _grid.ColumnHeadersHeight = S(26);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 251);
            _grid.RowTemplate.Height = S(22);
            AddCol("Change", 0.16f);
            AddCol("Component", 0.30f);
            AddCol("Part No", 0.20f);
            AddCol("Rev (from)", 0.11f);
            AddCol("Rev (to)", 0.11f);
            AddCol("Qty", 0.12f); // "from → to"
            _grid.CellFormatting += Grid_CellFormatting;

            // Fill control first (resolved last → middle), then edges; inner top
            // before header so the header docks outermost (house z-order).
            this.Controls.Add(_grid);
            this.Controls.Add(bottom);
            this.Controls.Add(top);
            this.Controls.Add(headerBar);

            btnClose.Location = new Point(bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(btnClose.Left - _btnExport.Width - S(8), S(9));

            this.FormClosed += (s, e) =>
            {
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fBtn?.Dispose(); _fGrid?.Dispose(); _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
        }

        private void AddCol(string header, float weight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = weight * 100f,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        }

        private AssemblyBaseline At(ComboBox cb)
        {
            int i = cb?.SelectedIndex ?? -1;
            return (i < 0 || i >= _baselines.Count) ? null : _baselines[i];
        }

        // One rolled-up parts-list line for a baseline: a unique part
        // (path + config) with its EXTENDED, whole-tree quantity.
        private sealed class AggRow
        {
            public string Name;
            public string PartNo;
            public string Revision;
            public long   Qty; // extended (rolled-up) quantity
        }

        // Roll a baseline's stored depth-first tree up into a flat parts map
        // keyed by PATH + CONFIG, with EXTENDED quantities. Keying by path
        // alone collapsed a multi-config part (e.g. MAX used as MAX.01 / MAX.02
        // / MAX.03 — three distinct part numbers) into a single row, losing the
        // other configs; keying by path+config gives each config its own diff
        // line. Quantities roll up the tree (a part used in two sub-assemblies,
        // or inside a sub-assembly used N times, is counted across the whole
        // tree) — mirrors the viewer's Flatten so a "1 → 2" qty change is real.
        private static Dictionary<string, AggRow> Flatten(AssemblyBaseline b)
        {
            var map = new Dictionary<string, AggRow>(StringComparer.OrdinalIgnoreCase);
            if (b == null || b.Components == null) return map;

            // ext[level] = extended qty of the current open ancestor at that
            // level; a component's extended qty = its per-parent Qty × its
            // parent's extended qty (top-level parent = 1).
            var ext = new List<long>();
            foreach (var c in b.Components)
            {
                int lvl = c.Level < 0 ? 0 : c.Level;
                long qty = c.Qty <= 0 ? 1 : c.Qty;
                long parent = lvl == 0 ? 1
                    : (lvl - 1 < ext.Count ? ext[lvl - 1] : 1);
                long e = parent * qty;
                while (ext.Count <= lvl) ext.Add(1);
                ext[lvl] = e;

                string key = (c.Path ?? "").ToLowerInvariant() + "|" +
                             (c.Config ?? "").ToLowerInvariant();
                AggRow r;
                if (!map.TryGetValue(key, out r))
                {
                    r = new AggRow
                    {
                        Name = c.Name, PartNo = c.PartNo, Revision = c.Revision
                    };
                    map[key] = r;
                }
                r.Qty += e;
                // PartNo/Revision/Name are consistent per path+config; keep the
                // first non-empty in case an occurrence has a blank.
                if (string.IsNullOrEmpty(r.PartNo))   r.PartNo = c.PartNo;
                if (string.IsNullOrEmpty(r.Revision)) r.Revision = c.Revision;
                if (string.IsNullOrEmpty(r.Name))     r.Name = c.Name;
            }
            return map;
        }

        private List<DiffRow> _rows = new List<DiffRow>();

        private void Recompute()
        {
            if (_grid == null) return;
            _rows = BuildDiff();
            _grid.Rows.Clear();

            bool showUnch = _showUnchanged != null && _showUnchanged.Checked;
            int added = 0, removed = 0, changed = 0, unchanged = 0;
            foreach (var r in _rows)
            {
                if (r.Change == "Added") added++;
                else if (r.Change == "Removed") removed++;
                else if (r.Change == "Changed") changed++;
                else unchanged++;

                if (r.Change == "Unchanged" && !showUnch) continue;
                _grid.Rows.Add(r.Change, Path.GetFileNameWithoutExtension(r.Name ?? ""),
                    r.PartNo, r.FromRev, r.ToRev, QtyCell(r));
            }

            var from = At(_fromPicker); var to = At(_toPicker);
            if (from == null || to == null)
                _summary.Text = "Select two releases to compare.";
            else if (ReferenceEquals(from, to) ||
                     (from.Revision == to.Revision && from.ReleasedDate == to.ReleasedDate))
                _summary.Text = "Same release selected — choose two different releases.";
            else
                _summary.Text = added + " added · " + removed + " removed · " +
                    changed + " changed · " + unchanged + " unchanged";
            if (_btnExport != null) _btnExport.Enabled = _rows.Count > 0;
        }

        private static string QtyCell(DiffRow r)
        {
            if (r.Change == "Added") return r.ToQty.ToString();
            if (r.Change == "Removed") return r.FromQty.ToString();
            return r.FromQty == r.ToQty
                ? r.ToQty.ToString()
                : r.FromQty + " → " + r.ToQty;
        }

        private List<DiffRow> BuildDiff()
        {
            var rows = new List<DiffRow>();
            var from = At(_fromPicker);
            var to = At(_toPicker);
            if (from == null || to == null) return rows;

            // Roll each baseline up to a flat parts list keyed by path+config
            // (extended quantities), then diff those — so multi-config parts and
            // parts used in several places compare correctly.
            var fromMap = Flatten(from);
            var toMap = Flatten(to);

            var keys = new HashSet<string>(fromMap.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in toMap.Keys) keys.Add(k);

            foreach (var k in keys)
            {
                AggRow fc, tc;
                fromMap.TryGetValue(k, out fc);
                toMap.TryGetValue(k, out tc);

                if (fc == null && tc != null)
                    rows.Add(new DiffRow
                    {
                        Change = "Added", Name = tc.Name, PartNo = tc.PartNo,
                        FromRev = "", ToRev = tc.Revision, FromQty = 0, ToQty = tc.Qty,
                        Colour = cGreen
                    });
                else if (fc != null && tc == null)
                    rows.Add(new DiffRow
                    {
                        Change = "Removed", Name = fc.Name, PartNo = fc.PartNo,
                        FromRev = fc.Revision, ToRev = "", FromQty = fc.Qty, ToQty = 0,
                        Colour = cRed
                    });
                else
                {
                    bool revChanged = !string.Equals(fc.Revision ?? "", tc.Revision ?? "",
                        StringComparison.OrdinalIgnoreCase);
                    bool qtyChanged = fc.Qty != tc.Qty;
                    rows.Add(new DiffRow
                    {
                        Change = (revChanged || qtyChanged) ? "Changed" : "Unchanged",
                        Name = tc.Name ?? fc.Name,
                        PartNo = string.IsNullOrEmpty(tc.PartNo) ? fc.PartNo : tc.PartNo,
                        FromRev = fc.Revision, ToRev = tc.Revision,
                        FromQty = fc.Qty, ToQty = tc.Qty,
                        Colour = (revChanged || qtyChanged) ? cOrange : cGray
                    });
                }
            }

            // Added → Removed → Changed → Unchanged, then by part number (so a
            // multi-config part's rows — MAX.01/.02/.03 — order predictably),
            // then by name.
            int Rank(string c) => c == "Added" ? 0 : c == "Removed" ? 1
                : c == "Changed" ? 2 : 3;
            rows.Sort((a, b) =>
            {
                int r = Rank(a.Change).CompareTo(Rank(b.Change));
                if (r != 0) return r;
                int p = string.Compare(a.PartNo ?? "", b.PartNo ?? "",
                    StringComparison.OrdinalIgnoreCase);
                return p != 0 ? p
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return rows;
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].HeaderText != "Change")
                return;
            string s = (e.Value as string) ?? "";
            if (s == "Added") e.CellStyle.ForeColor = cGreen;
            else if (s == "Removed") e.CellStyle.ForeColor = cRed;
            else if (s == "Changed") e.CellStyle.ForeColor = cOrange;
            else e.CellStyle.ForeColor = cGray;
            e.CellStyle.Font = _fGridHead; // bold the change column
        }

        private void ExportCsv()
        {
            var from = At(_fromPicker); var to = At(_toPicker);
            if (from == null || to == null || _rows.Count == 0) return;
            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(_asmName ?? "assembly"))
                    + "_R" + from.Revision + "_vs_R" + to.Revision + "_COMPARE.csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool showUnch = _showUnchanged.Checked;
                    var sb = new StringBuilder();
                    sb.AppendLine("Assembly,From REV,To REV");
                    sb.AppendLine(Csv(_asmName) + "," + Csv(from.Revision) + "," + Csv(to.Revision));
                    sb.AppendLine();
                    sb.AppendLine("Change,Component,PartNo,Rev (from),Rev (to),Qty (from),Qty (to)");
                    foreach (var r in _rows)
                    {
                        if (r.Change == "Unchanged" && !showUnch) continue;
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(r.Change),
                            Csv(Path.GetFileNameWithoutExtension(r.Name ?? "")),
                            Csv(r.PartNo), Csv(r.FromRev), Csv(r.ToRev),
                            Csv(r.FromQty.ToString()), Csv(r.ToQty.ToString())
                        }));
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show("Comparison exported to:\n" + sfd.FileName,
                        "BCore PDM — Exported",
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
            if (string.IsNullOrEmpty(name)) return "assembly";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(
                c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        private Button MakeButton(string text, Color back, Color fore)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn, Width = S(96), Height = S(26),
                BackColor = back, ForeColor = fore,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
