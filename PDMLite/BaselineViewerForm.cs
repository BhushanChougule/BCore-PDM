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
        private Button _btnExportAll;
        private Button _btnExpandAll, _btnCollapseAll, _btnFlatten;

        // Double-click a row to open that component (deferred — see the dashboard
        // caller); null when the viewer is just closed.
        public string FileToOpen { get; private set; }

        // Tree state for the SELECTED baseline. _full = its components in stored
        // depth-first order; _hasKids[i] = component i is a sub-assembly with
        // children; _extQty[i] = extended (rolled-up) quantity; _collapsed =
        // component indices whose subtree is hidden; _rowToComp maps a grid row
        // back to its _full index (click toggling, double-click open).
        private List<BaselineComponent> _full = new List<BaselineComponent>();
        private bool[] _hasKids = new bool[0];
        private long[] _extQty = new long[0];
        private string[] _outline = new string[0]; // outline number per _full row (1, 1.1, 1.3.1)
        private readonly HashSet<int> _collapsed = new HashSet<int>();
        private readonly List<int> _rowToComp = new List<int>();
        private bool _flat; // false = indented tree, true = flattened parts list

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
            this.ClientSize = new Size(S(780), S(560));
            this.MinimumSize = new Size(S(600), S(360));

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

            // ── Top panel (centered): name · release picker · meta · buttons · tip ──
            var top = new Panel { Dock = DockStyle.Top, Height = S(134), BackColor = cBg };

            var nameLbl = new Label
            {
                Text = string.IsNullOrEmpty(_asmName)
                    ? Path.GetFileName(_asmPath ?? "") : _asmName,
                Font = _fSub,
                ForeColor = cTextDark,
                AutoSize = false,
                Height = S(20),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };
            top.Controls.Add(nameLbl);

            var lblRelease = new Label
            {
                Text = "Release:",
                Font = _fLabel,
                ForeColor = cTextDark,
                AutoSize = true
            };
            top.Controls.Add(lblRelease);

            _revPicker = new ComboBox
            {
                Font = _fLabel,
                DropDownStyle = ComboBoxStyle.DropDownList,
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
                AutoSize = false,
                Height = S(18),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };
            top.Controls.Add(_metaLabel);

            // Tree controls: expand everything, collapse to top-level-only, or
            // flatten to a rolled-up parts list. (Per sub-assembly: click its
            // ▸/▾ row to toggle.)
            _btnExpandAll = MakeButton("Expand All", cBrand, Color.White);
            _btnExpandAll.Click += (s, e) => { _collapsed.Clear(); RenderRows(); };
            top.Controls.Add(_btnExpandAll);

            _btnCollapseAll = MakeButton("Collapse All", Color.FromArgb(220, 220, 220), cTextDark);
            _btnCollapseAll.Click += (s, e) =>
            {
                _collapsed.Clear();
                for (int i = 0; i < _hasKids.Length; i++)
                    if (_hasKids[i]) _collapsed.Add(i);
                RenderRows();
            };
            top.Controls.Add(_btnCollapseAll);

            // Flatten = rolled-up parts list (each leaf part once, EXTENDED total
            // qty across all sub-assembly levels). Toggle back to the tree.
            _btnFlatten = MakeButton("Flatten", cBrandDark, Color.White);
            _btnFlatten.Click += (s, e) => SetFlat(!_flat);
            top.Controls.Add(_btnFlatten);

            var tipLbl = new Label
            {
                Text = "Tip: Click a ▸/▾ row to expand/collapse; double-click a row to open it.",
                Font = _fMeta, ForeColor = Color.FromArgb(140, 146, 156),
                AutoSize = false, Height = S(16),
                TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true
            };
            top.Controls.Add(tipLbl);

            // Centre every row horizontally; re-centre when the form is resized.
            Action layoutTop = () =>
            {
                int W = top.ClientSize.Width, m = S(8);
                int full = Math.Max(0, W - 2 * m);
                nameLbl.Bounds   = new Rectangle(m, S(8),  full, S(20));
                _metaLabel.Bounds = new Rectangle(m, S(58), full, S(18));
                tipLbl.Bounds    = new Rectangle(m, S(112), full, S(16));

                int relGroup = lblRelease.Width + S(6) + _revPicker.Width;
                int relX = Math.Max(m, (W - relGroup) / 2);
                lblRelease.Location = new Point(relX, S(34));
                _revPicker.Location = new Point(relX + lblRelease.Width + S(6), S(31));

                int bGroup = _btnExpandAll.Width + S(8) + _btnCollapseAll.Width +
                    S(8) + _btnFlatten.Width;
                int bX = Math.Max(m, (W - bGroup) / 2);
                _btnExpandAll.Location  = new Point(bX, S(84));
                _btnCollapseAll.Location = new Point(_btnExpandAll.Right + S(8), S(84));
                _btnFlatten.Location    = new Point(_btnCollapseAll.Right + S(8), S(84));
            };
            top.Resize += (s, e) => layoutTop();
            layoutTop();

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

            // All revisions → one Excel file, a worksheet per release.
            _btnExportAll = MakeButton("Export All Revs", cBrandDark, Color.White);
            _btnExportAll.Width = S(124);
            _btnExportAll.Click += (s, e) => ExportAllXlsx();

            // Anchor to the bottom-right; lay out on resize (Close | CSV | All).
            bottom.Controls.Add(_btnExport);
            bottom.Controls.Add(_btnExportAll);
            bottom.Controls.Add(btnClose);
            bottom.Resize += (s, e) => LayoutBottomButtons(bottom, btnClose);

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
            _grid.ColumnHeadersDefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter; // all titles centered
            _grid.ColumnHeadersHeight = S(26);
            _grid.AlternatingRowsDefaultCellStyle.BackColor =
                Color.FromArgb(245, 248, 251);
            _grid.RowTemplate.Height = S(22);

            // No. column FIRST = the tree column (indent + arrow + outline number
            // like 1 / 1.1 / 1.3.1). Rev + Qty + Weight VALUES centered; rest left.
            AddCol("No.", 0.09f);
            AddCol("Component", 0.16f);
            AddCol("Part No", 0.14f);
            AddCol("Description", 0.21f);
            AddCol("Rev", 0.07f, DataGridViewContentAlignment.MiddleCenter);
            AddCol("Status", 0.12f);
            AddCol("Qty", 0.08f, DataGridViewContentAlignment.MiddleCenter);
            AddCol("Weight (lb)", 0.13f, DataGridViewContentAlignment.MiddleCenter);
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellClick += Grid_CellClick;            // toggle a subtree
            _grid.CellDoubleClick += Grid_CellDoubleClick; // open the component

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
            LayoutBottomButtons(bottom, btnClose);

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
                // Don't close the form when Escape is just dismissing the open
                // Release dropdown.
                if (e.KeyCode == Keys.Escape && !_revPicker.DroppedDown) this.Close();
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
            _flat = false;      // each release opens as a tree

            // A component is an expandable sub-assembly if the NEXT row is deeper
            // (depth-first order guarantees a node's descendants follow it).
            _hasKids = new bool[_full.Count];
            for (int i = 0; i < _full.Count; i++)
                _hasKids[i] = i + 1 < _full.Count &&
                              _full[i + 1].Level > _full[i].Level;

            // Outline (BOM item) numbers: 1, 2 … at the top; N.1, N.2 … under N.
            // Intrinsic to tree position, so unchanged by collapse.
            _outline = ComputeOutline(_full);
            // Extended (rolled-up) quantity = product of Qty along the path to root.
            _extQty = ComputeExtQty(_full);

            bool enable = b != null;
            if (_btnFlatten != null) { _btnFlatten.Enabled = enable; _btnFlatten.Text = "Flatten"; }
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
            if (_btnExport != null) _btnExport.Enabled = _full.Count > 0;
        }

        // Switch between the indented tree and the flattened parts list.
        private void SetFlat(bool flat)
        {
            _flat = flat;
            if (_btnFlatten != null) _btnFlatten.Text = flat ? "Show Tree" : "Flatten";
            // Tree-only controls don't apply to the flattened list.
            if (_btnExpandAll != null) _btnExpandAll.Enabled = !flat && _full.Count > 0;
            if (_btnCollapseAll != null) _btnCollapseAll.Enabled = !flat && _full.Count > 0;
            RenderRows();
        }

        private void RenderRows() { if (_flat) RenderFlat(); else RenderTree(); }

        // Indented tree. Honours _collapsed: a collapsed sub-assembly hides every
        // deeper row until the tree returns to its level (works for nested
        // collapses). Arrow ▾ (expanded) / ▸ (collapsed) on sub-assemblies; Qty
        // is per-immediate-parent; Weight is the unit weight. _rowToComp maps
        // each grid row back to its _full index (click toggle, double-click open).
        private void RenderTree()
        {
            if (_grid == null) return;
            _grid.Rows.Clear();
            _rowToComp.Clear();
            _grid.Columns[6].HeaderText = "Qty"; // per-parent in tree mode

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
                string no = new string(' ', level * 3) + arrow +
                    (i < _outline.Length ? _outline[i] : "");
                string name = Path.GetFileNameWithoutExtension(c.Name ?? "");

                int row = _grid.Rows.Add(no, name, c.PartNo, c.Description,
                    c.Revision, c.Status, c.Qty, FmtWeight(c.Weight));
                if (kids) _grid.Rows[row].Tag = "sub"; // bolded in CellFormatting
                _rowToComp.Add(i);

                if (collapsed) hideDeeperThan = level; // hide its subtree
            }
            UpdateCount(_full.Count, "Lines", _full.Sum(c => Math.Max(c.Qty, 0)));
        }

        // Flattened parts list: every LEAF part once (grouped by Part No / file),
        // with the EXTENDED total quantity rolled up across all sub-assembly
        // levels — the purchasing view. Sub-assembly lines are omitted (their
        // children are promoted), so quantities never double-count.
        private void RenderFlat()
        {
            if (_grid == null) return;
            _grid.Rows.Clear();
            _rowToComp.Clear();
            _grid.Columns[6].HeaderText = "Total Qty"; // extended in flat mode

            var order = new List<string>();
            var firstIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totalQty = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _full.Count; i++)
            {
                if (!IsLeaf(_full, i)) continue; // parts only
                var c = _full[i];
                string key = !string.IsNullOrEmpty(c.PartNo)
                    ? c.PartNo.ToLowerInvariant()
                    : (c.Path ?? "").ToLowerInvariant() + "|" +
                      (c.Config ?? "").ToLowerInvariant();
                if (!totalQty.ContainsKey(key))
                {
                    totalQty[key] = 0; firstIdx[key] = i; order.Add(key);
                }
                totalQty[key] += i < _extQty.Length ? _extQty[i] : Math.Max(c.Qty, 1);
            }
            order.Sort((x, y) => string.Compare(
                _full[firstIdx[x]].PartNo ?? "", _full[firstIdx[y]].PartNo ?? "",
                StringComparison.OrdinalIgnoreCase));

            int item = 1;
            long sumQty = 0;
            foreach (string key in order)
            {
                int i = firstIdx[key];
                var c = _full[i];
                long qty = totalQty[key];
                sumQty += qty;
                _grid.Rows.Add((item++).ToString(),
                    Path.GetFileNameWithoutExtension(c.Name ?? ""),
                    c.PartNo, c.Description, c.Revision, c.Status,
                    qty.ToString(), FmtWeight(c.Weight));
                _rowToComp.Add(i);
            }
            UpdateCount(order.Count, "Parts", sumQty);
        }

        // Footer: "Lines: 10  ·  Total Qty: 12  ·  Weight: 8.477 lbs"
        // (the label noun is "Lines" in the tree, "Parts" when flattened;
        //  weight shown only when known).
        private void UpdateCount(int n, string noun, long qty)
        {
            double mass = TotalMassLb(_full, _extQty);
            _countLabel.Text = noun + ": " + n +
                "   ·   Total Qty: " + qty +
                (mass > 0 ? "   ·   Weight: " + mass.ToString("0.###",
                    CultureInfo.InvariantCulture) + " lbs" : "");
        }

        // Click a sub-assembly's No./name to expand/collapse it (tree mode only).
        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_flat) return;
            if (e.RowIndex < 0 || e.ColumnIndex > 1) return; // No. / Component only
            if (e.RowIndex >= _rowToComp.Count) return;
            int comp = _rowToComp[e.RowIndex];
            if (comp < 0 || comp >= _hasKids.Length || !_hasKids[comp]) return;

            if (!_collapsed.Remove(comp)) _collapsed.Add(comp); // toggle
            // Defer the rebuild so we don't mutate Rows inside the grid's own
            // click processing (avoids re-entrancy on the cell that was clicked).
            // Guard the handle: a double-click toggles here then Close()s the
            // form, so a queued rebuild could otherwise hit a destroyed grid
            // (same IsHandleCreated guard the dashboard/audit pagers use).
            if (IsHandleCreated) BeginInvoke((Action)RenderRows);
        }

        // Double-click any row → open that component (current copy). Deferred:
        // the dashboard caller opens it AFTER this modal closes (see MenuViewBaseline).
        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rowToComp.Count) return;
            int comp = _rowToComp[e.RowIndex];
            if (comp < 0 || comp >= _full.Count) return;
            string path = _full[comp].Path;
            if (string.IsNullOrEmpty(path)) return;
            FileToOpen = path;
            this.Close();
        }

        // Colour the Status cell; bold the Component cell on sub-assembly rows so
        // the structure reads as a tree.
        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string header = _grid.Columns[e.ColumnIndex].HeaderText;

            if ((header == "No." || header == "Component") &&
                (_grid.Rows[e.RowIndex].Tag as string) == "sub")
                e.CellStyle.Font = _fGridHead; // bold sub-assembly rows

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

        // BOM outline numbers for a depth-first component list (1, 1.1, 1.3.1 …).
        // Shared by the on-screen tree and the Excel export.
        private static string[] ComputeOutline(List<BaselineComponent> comps)
        {
            var outline = new string[comps.Count];
            var counters = new List<int>();
            for (int i = 0; i < comps.Count; i++)
            {
                int level = Math.Max(comps[i].Level, 0);
                while (counters.Count < level + 1) counters.Add(0);
                if (counters.Count > level + 1)
                    counters.RemoveRange(level + 1, counters.Count - (level + 1));
                counters[level]++;
                outline[i] = string.Join(".", counters);
            }
            return outline;
        }

        // Extended (rolled-up) quantity per node = product of Qty along the path
        // from the root. mult[L] tracks the current node's extended qty at level L;
        // a node at level L multiplies its parent's (mult[L-1]).
        private static long[] ComputeExtQty(List<BaselineComponent> comps)
        {
            var ext = new long[comps.Count];
            var mult = new List<long>();
            for (int i = 0; i < comps.Count; i++)
            {
                int level = Math.Max(comps[i].Level, 0);
                long parent = level == 0 ? 1
                    : (level - 1 < mult.Count ? mult[level - 1] : 1);
                long e = parent * Math.Max(comps[i].Qty, 1);
                while (mult.Count <= level) mult.Add(1);
                mult[level] = e;
                ext[i] = e;
            }
            return ext;
        }

        // A leaf has no deeper row immediately after it (depth-first order).
        private static bool IsLeaf(List<BaselineComponent> comps, int i)
            => i + 1 >= comps.Count || comps[i + 1].Level <= comps[i].Level;

        // Total assembly mass = Σ over LEAF parts (unit weight × extended qty),
        // so sub-assembly weights never double-count their children.
        private static double TotalMassLb(List<BaselineComponent> comps, long[] ext)
        {
            double total = 0;
            for (int i = 0; i < comps.Count; i++)
                if (IsLeaf(comps, i) && i < ext.Length)
                    total += comps[i].Weight * ext[i];
            return total;
        }

        private static string FmtWeight(double lb)
            => lb > 0 ? lb.ToString("0.###", CultureInfo.InvariantCulture) : "";

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
                    // ALWAYS the FULL tree (ignores on-screen collapse).
                    var outline = ComputeOutline(b.Components);
                    var ext = ComputeExtQty(b.Components);
                    double mass = TotalMassLb(b.Components, ext);

                    var sb = new StringBuilder();
                    sb.AppendLine("Assembly,PartNo,Revision,Config,ReleasedBy,ReleasedDate,Total Mass (lb)");
                    sb.AppendLine(string.Join(",", new[]
                    {
                        Csv(_asmName), Csv(b.PartNo), Csv(b.Revision), Csv(b.Config),
                        Csv(b.ReleasedBy), Csv(FmtDate(b.ReleasedDate)),
                        Csv(mass > 0 ? mass.ToString("0.###", CultureInfo.InvariantCulture) : "")
                    }));
                    sb.AppendLine();
                    sb.AppendLine("No.,Level,Component,Part No,Config,Description,Revision,Status,Qty,Ext Qty,Weight (lb)");
                    for (int i = 0; i < b.Components.Count; i++)
                    {
                        var c = b.Components[i];
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(i < outline.Length ? outline[i] : ""),
                            Csv(c.Level.ToString()),
                            Csv(new string(' ', Math.Max(c.Level, 0) * 2) +
                                Path.GetFileNameWithoutExtension(c.Name ?? "")),
                            Csv(c.PartNo), Csv(c.Config), Csv(c.Description),
                            Csv(c.Revision), Csv(c.Status),
                            Csv(c.Qty.ToString()),
                            Csv((i < ext.Length ? ext[i] : c.Qty).ToString()),
                            Csv(FmtWeight(c.Weight))
                        }));
                    }
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

        // Right-align Close, then Export CSV, then Export All Revs (right→left).
        private void LayoutBottomButtons(Panel bottom, Button btnClose)
        {
            btnClose.Location = new Point(
                bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(
                btnClose.Left - _btnExport.Width - S(8), S(9));
            _btnExportAll.Location = new Point(
                _btnExport.Left - _btnExportAll.Width - S(8), S(9));
        }

        // Export EVERY captured baseline to one .xlsx — a worksheet per release
        // (CSV can't hold multiple sheets). Each sheet has a small meta block +
        // the full indented BOM (all levels, regardless of on-screen collapse).
        private void ExportAllXlsx()
        {
            if (_baselines == null || _baselines.Count == 0) return;
            using (var sfd = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(_asmName ?? "baseline"))
                    + "_ALL-BASELINES.xlsx"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sheets = new List<XlsxWriter.Sheet>();
                    foreach (var b in _baselines)
                    {
                        var outline = ComputeOutline(b.Components);
                        var ext = ComputeExtQty(b.Components);
                        double mass = TotalMassLb(b.Components, ext);

                        var sh = new XlsxWriter.Sheet("REV " + b.Revision +
                            (string.IsNullOrEmpty(b.Config) ? "" : " " + b.Config));
                        sh.Add("Assembly", _asmName ?? "");
                        sh.Add("Part No", b.PartNo);
                        sh.Add("Revision", b.Revision);
                        sh.Add("Config", b.Config);
                        sh.Add("Released By", b.ReleasedBy);
                        sh.Add("Released", FmtDate(b.ReleasedDate));
                        sh.Add("Total Mass (lb)",
                            mass > 0 ? mass.ToString("0.###", CultureInfo.InvariantCulture) : "");
                        sh.AddBlank();
                        sh.Add("No.", "Level", "Component", "Part No", "Config",
                            "Description", "Revision", "Status", "Qty", "Ext Qty",
                            "Weight (lb)");

                        for (int i = 0; i < b.Components.Count; i++)
                        {
                            var c = b.Components[i];
                            sh.Add(
                                i < outline.Length ? outline[i] : "",
                                c.Level.ToString(),
                                new string(' ', Math.Max(c.Level, 0) * 2) +
                                    Path.GetFileNameWithoutExtension(c.Name ?? ""),
                                c.PartNo ?? "", c.Config ?? "", c.Description ?? "",
                                c.Revision ?? "", c.Status ?? "",
                                c.Qty.ToString(),
                                (i < ext.Length ? ext[i] : c.Qty).ToString(),
                                FmtWeight(c.Weight));
                        }
                        sheets.Add(sh);
                    }

                    XlsxWriter.Write(sfd.FileName, sheets);
                    MessageBox.Show(
                        sheets.Count + " release" + (sheets.Count == 1 ? "" : "s") +
                        " exported to:\n" + sfd.FileName,
                        "BCore PDM — Baselines Exported",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not export the workbook:\n" + ex.Message,
                        "BCore PDM — Export Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
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
