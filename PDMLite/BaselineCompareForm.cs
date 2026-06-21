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

        private ComboBox _fromPicker, _toPicker, _typeFilter;
        private CheckBox _showUnchanged;
        private DataGridView _grid;
        private Panel _topPanel;
        private Label _nameLbl, _lblFrom, _lblTo, _lblShow;
        private Label _summary, _reasonFrom, _reasonTo, _impactLabel;
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
            public string FromDesc;
            public string ToDesc;
            public string FromRev;
            public string ToRev;
            public long   FromQty;
            public long   ToQty;
            public double FromWeight; // extended LEAF weight (lb), From side
            public double ToWeight;   // extended LEAF weight (lb), To side
            public bool   NoRevBump;  // Changed, but the revision letter did NOT change
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
            this.ClientSize = new Size(S(880), S(580));
            this.MinimumSize = new Size(S(620), S(380));

            var headerBar = new Panel { BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(30) };
            headerBar.Controls.Add(new Label
            {
                Text = "Compare As-Released Baselines",
                Font = _fHeader, ForeColor = Color.White,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });

            // Top block — all positions are set (and re-centered on resize) by
            // LayoutTop, so the header content centres like the other BCore popups.
            _topPanel = new Panel { Dock = DockStyle.Top, Height = S(132), BackColor = cBg };
            var top = _topPanel;
            var cReason = Color.FromArgb(110, 116, 126);

            _nameLbl = new Label
            {
                Text = string.IsNullOrEmpty(_asmName)
                    ? Path.GetFileName(_asmPath ?? "") : _asmName,
                Font = _fSub, ForeColor = cTextDark,
                AutoSize = false, Height = S(20),
                TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true
            };
            top.Controls.Add(_nameLbl);

            _lblFrom = new Label { Text = "From:", Font = _fMeta, ForeColor = cTextDark, AutoSize = true };
            top.Controls.Add(_lblFrom);
            _fromPicker = new ComboBox
            {
                Font = _fMeta, DropDownStyle = ComboBoxStyle.DropDownList, Width = S(260)
            };
            top.Controls.Add(_fromPicker);
            _lblTo = new Label { Text = "To:", Font = _fMeta, ForeColor = cTextDark, AutoSize = true };
            top.Controls.Add(_lblTo);
            _toPicker = new ComboBox
            {
                Font = _fMeta, DropDownStyle = ComboBoxStyle.DropDownList, Width = S(260)
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

            // Reason-for-change of each release — From aligns under the From picker,
            // To aligns under the To picker (set in LayoutTop).
            _reasonFrom = new Label
            {
                Font = _fMeta, ForeColor = cReason,
                AutoSize = false, Height = S(16), AutoEllipsis = true
            };
            _reasonTo = new Label
            {
                Font = _fMeta, ForeColor = cReason,
                AutoSize = false, Height = S(16), AutoEllipsis = true
            };
            top.Controls.Add(_reasonFrom);
            top.Controls.Add(_reasonTo);

            // Left column: "Show:" filter (top) over "Show unchanged" (below).
            _lblShow = new Label { Text = "Show:", Font = _fMeta, ForeColor = cTextDark, AutoSize = true };
            top.Controls.Add(_lblShow);
            _typeFilter = new ComboBox
            {
                Font = _fMeta, DropDownStyle = ComboBoxStyle.DropDownList, Width = S(130)
            };
            _typeFilter.Items.AddRange(new object[] { "All changes", "Added", "Removed", "Changed" });
            _typeFilter.SelectedIndex = 0;
            _typeFilter.SelectedIndexChanged += (s, e) => Recompute();
            top.Controls.Add(_typeFilter);

            _showUnchanged = new CheckBox
            {
                // Leading spaces add a gap between the tick box and the label.
                Text = "  Show unchanged", Font = _fMeta, ForeColor = cTextDark,
                AutoSize = true, Checked = false
            };
            _showUnchanged.CheckedChanged += (s, e) => Recompute();
            top.Controls.Add(_showUnchanged);

            // Right column: counts summary (top) over the change-impact line (below).
            _summary = new Label { Font = _fMeta, ForeColor = cReason, AutoSize = true };
            top.Controls.Add(_summary);
            _impactLabel = new Label { Font = _fMeta, ForeColor = cTextDark, AutoSize = true };
            top.Controls.Add(_impactLabel);

            top.Resize += (s, e) => LayoutTop(top);

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
            _grid.ColumnHeadersDefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            // Tall enough that a two-line header ("Rev" / "(from)") shows fully.
            _grid.ColumnHeadersHeight = S(40);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 251);
            _grid.RowTemplate.Height = S(22);
            AddCol("Change", 0.12f);
            AddCol("Component", 0.16f);
            AddCol("Part No", 0.13f);
            AddCol("Description", 0.19f);
            AddCol("Rev (from)", 0.09f, true);
            AddCol("Rev (to)", 0.09f, true);
            AddCol("Qty", 0.09f, true);       // "from → to"
            AddCol("Δ", 0.05f, true);          // net qty (+/-)
            AddCol("Wt Δ (lb)", 0.09f, true); // extended weight delta
            _grid.CellFormatting += Grid_CellFormatting;

            // Fill control first (resolved last → middle), then edges; inner top
            // before header so the header docks outermost (house z-order).
            this.Controls.Add(_grid);
            this.Controls.Add(bottom);
            this.Controls.Add(top);
            this.Controls.Add(headerBar);

            btnClose.Location = new Point(bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(btnClose.Left - _btnExport.Width - S(8), S(9));
            LayoutTop(top); // initial centred layout (re-runs on resize + Recompute)

            this.FormClosed += (s, e) =>
            {
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fBtn?.Dispose(); _fGrid?.Dispose(); _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
        }

        // Centre the header block (name · pickers · reasons · filter+summary),
        // re-run on resize and after every Recompute. Mirrors the As-Released
        // Baseline viewer's centred top block.
        private void LayoutTop(Panel top)
        {
            if (top == null || _fromPicker == null) return;
            int W = top.ClientSize.Width;
            int cx = W / 2;
            int pad = S(10);

            // Row A: name spans the width, centred via TextAlign.
            _nameLbl.SetBounds(pad, S(8), Math.Max(W - 2 * pad, S(120)), S(20));

            // Row B: From [picker]   To [picker], centred as a group.
            int pw = S(260), g = S(28);
            int fW = _lblFrom.Width, tW = _lblTo.Width;
            int groupW = fW + S(6) + pw + g + tW + S(6) + pw;
            int bx = Math.Max(pad, cx - groupW / 2);
            int by = S(33);
            _lblFrom.Location = new Point(bx, by + S(4));
            _fromPicker.Location = new Point(bx + fW + S(6), by);
            int tx = _fromPicker.Right + g;
            _lblTo.Location = new Point(tx, by + S(4));
            _toPicker.Location = new Point(tx + tW + S(6), by);

            // Row C: reasons — From under the From picker; the To reason VALUE
            // aligns to the To picker box's left edge so it sits below the rev box.
            int ry = S(60);
            _reasonFrom.SetBounds(_lblFrom.Left, ry,
                Math.Max(S(40), _lblTo.Left - _lblFrom.Left - g), S(16));
            _reasonTo.SetBounds(_toPicker.Left, ry,
                Math.Max(S(40), W - pad - _toPicker.Left), S(16));

            // Rows D/E: a 2×2 block — left column [Show: filter / Show unchanged]
            // over right column [counts / impact] — centred as one unit, columns
            // aligned (Show unchanged under Show:, impact under counts).
            int leftColW = Math.Max(_lblShow.Width + S(6) + _typeFilter.Width,
                _showUnchanged.Width);
            int rightColW = Math.Max(_summary.Width, _impactLabel.Width);
            int cg = S(40);
            int total = leftColW + cg + rightColW;
            int sx = Math.Max(pad, cx - total / 2);
            int rx = sx + leftColW + cg;
            int dY = S(86), eY = S(108);
            _lblShow.Location = new Point(sx, dY + S(3));
            _typeFilter.Location = new Point(sx + _lblShow.Width + S(6), dY);
            _summary.Location = new Point(rx, dY + S(3));
            _showUnchanged.Location = new Point(sx, eY);
            _impactLabel.Location = new Point(rx, eY);
        }

        private void AddCol(string header, float weight, bool center = false)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = weight * 100f,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            if (center)
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(col);
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
            public string Description;
            public long   Qty;    // extended (rolled-up) quantity
            public double Weight; // extended LEAF weight (lb); 0 for sub-assemblies
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
            var comps = b.Components;
            var ext = new List<long>();
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
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
                        Name = c.Name, PartNo = c.PartNo,
                        Revision = c.Revision, Description = c.Description
                    };
                    map[key] = r;
                }
                r.Qty += e;
                // Weight rolls up only on LEAF components (no deeper child): a
                // sub-assembly's own stored mass already covers its children, so
                // counting leaves matches the viewer's footer mass AND lets the
                // per-row weight deltas sum to the assembly total.
                bool isLeaf = !(i + 1 < comps.Count && comps[i + 1].Level > lvl);
                if (isLeaf) r.Weight += c.Weight * e;
                // PartNo/Revision/Name/Description are consistent per path+config;
                // keep the first non-empty in case an occurrence has a blank.
                if (string.IsNullOrEmpty(r.PartNo))      r.PartNo = c.PartNo;
                if (string.IsNullOrEmpty(r.Revision))    r.Revision = c.Revision;
                if (string.IsNullOrEmpty(r.Name))        r.Name = c.Name;
                if (string.IsNullOrEmpty(r.Description)) r.Description = c.Description;
            }
            return map;
        }

        private List<DiffRow> _rows = new List<DiffRow>();

        private void Recompute()
        {
            if (_grid == null) return;
            _rows = BuildDiff();
            _grid.Rows.Clear();

            int added = 0, removed = 0, changed = 0, unchanged = 0, noRevBump = 0;
            foreach (var r in _rows)
            {
                if (r.Change == "Added") added++;
                else if (r.Change == "Removed") removed++;
                else if (r.Change == "Changed") { changed++; if (r.NoRevBump) noRevBump++; }
                else unchanged++;

                if (!IsVisible(r)) continue;
                int row = _grid.Rows.Add(
                    r.Change + (r.NoRevBump ? " *" : ""),
                    Path.GetFileNameWithoutExtension(r.Name ?? ""),
                    r.PartNo, DescCell(r), r.FromRev, r.ToRev,
                    QtyCell(r), DeltaCell(r), WeightDeltaCell(r));
                if (r.NoRevBump)
                    _grid.Rows[row].Cells[0].ToolTipText =
                        "Changed without a revision bump (quantity or description edited)";
            }

            var from = At(_fromPicker); var to = At(_toPicker);
            bool valid = from != null && to != null && !ReferenceEquals(from, to) &&
                !(from.Revision == to.Revision && from.ReleasedDate == to.ReleasedDate);

            if (_reasonFrom != null && _reasonTo != null)
            {
                _reasonFrom.Text = from == null ? "" : "Reason — From: " + ReasonOf(from);
                _reasonTo.Text = to == null ? "" : ReasonOf(to);
            }

            if (from == null || to == null)
                _summary.Text = "Select two releases to compare.";
            else if (!valid)
                _summary.Text = "Same release selected — choose two different releases.";
            else
                _summary.Text = added + " added · " + removed + " removed · " +
                    changed + " changed · " + unchanged + " unchanged" +
                    (noRevBump > 0 ? "    ( * " + noRevBump + " changed w/o rev bump)" : "");

            if (_impactLabel != null)
            {
                if (!valid) _impactLabel.Text = "";
                else
                {
                    long qF = TotalQty(from), qT = TotalQty(to);
                    double wF = TotalWeight(from), wT = TotalWeight(to);
                    _impactLabel.Text =
                        "Qty " + qF + " → " + qT + " (" + Signed(qT - qF) + ")" +
                        "        Weight " + Fmt(wF) + " → " + Fmt(wT) + " lb (" +
                        SignedF(wT - wF) + ")";
                }
            }

            if (_btnExport != null) _btnExport.Enabled = valid && _rows.Count > 0;

            // The summary/reason text widths just changed — re-centre the block.
            LayoutTop(_topPanel);
        }

        // A row is visible when it passes the change-type filter. "All changes"
        // shows Added/Removed/Changed, plus Unchanged when the checkbox is ticked.
        private bool IsVisible(DiffRow r)
        {
            string sel = _typeFilter?.SelectedItem as string ?? "All changes";
            if (sel == "Added")   return r.Change == "Added";
            if (sel == "Removed") return r.Change == "Removed";
            if (sel == "Changed") return r.Change == "Changed";
            bool showUnch = _showUnchanged != null && _showUnchanged.Checked;
            return r.Change != "Unchanged" || showUnch;
        }

        private static string ReasonOf(AssemblyBaseline b) =>
            string.IsNullOrWhiteSpace(b?.Reason) ? "—" : b.Reason.Trim();

        // Description cell: "old → new" when it changed (so an un-bumped edit is
        // visible), else the single value.
        private static string DescCell(DiffRow r)
        {
            string f = (r.FromDesc ?? "").Trim(), t = (r.ToDesc ?? "").Trim();
            if (r.Change == "Added") return t;
            if (r.Change == "Removed") return f;
            return string.Equals(f, t, StringComparison.OrdinalIgnoreCase)
                ? t : (f + " → " + t);
        }

        private static string DeltaCell(DiffRow r)
        {
            long d = r.ToQty - r.FromQty;
            return d == 0 ? "" : Signed(d);
        }

        private static string WeightDeltaCell(DiffRow r)
        {
            double d = r.ToWeight - r.FromWeight;
            return Math.Abs(d) < 0.0005 ? "" : SignedF(d);
        }

        // Sum of per-line quantities — matches the viewer's "Total Qty" footer.
        private static long TotalQty(AssemblyBaseline b)
        {
            long t = 0;
            if (b?.Components != null)
                foreach (var c in b.Components) t += c.Qty <= 0 ? 1 : c.Qty;
            return t;
        }

        // Total assembly mass (lb) = sum of the flattened LEAF weights (matches
        // the viewer footer; sub-assemblies contribute 0 to avoid double-count).
        private static double TotalWeight(AssemblyBaseline b)
        {
            double t = 0;
            foreach (var r in Flatten(b).Values) t += r.Weight;
            return t;
        }

        private static string Signed(long v) =>
            v == 0 ? "0" : (v > 0 ? "+" + v : v.ToString());

        private static string SignedF(double v)
        {
            if (Math.Abs(v) < 0.0005) return "0";
            return (v > 0 ? "+" : "-") + Math.Abs(v).ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Fmt(double v) =>
            v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
                        FromDesc = "", ToDesc = tc.Description,
                        FromRev = "", ToRev = tc.Revision, FromQty = 0, ToQty = tc.Qty,
                        FromWeight = 0, ToWeight = tc.Weight, Colour = cGreen
                    });
                else if (fc != null && tc == null)
                    rows.Add(new DiffRow
                    {
                        Change = "Removed", Name = fc.Name, PartNo = fc.PartNo,
                        FromDesc = fc.Description, ToDesc = "",
                        FromRev = fc.Revision, ToRev = "", FromQty = fc.Qty, ToQty = 0,
                        FromWeight = fc.Weight, ToWeight = 0, Colour = cRed
                    });
                else
                {
                    bool revChanged = !string.Equals(fc.Revision ?? "", tc.Revision ?? "",
                        StringComparison.OrdinalIgnoreCase);
                    bool qtyChanged = fc.Qty != tc.Qty;
                    bool descChanged = !string.Equals((fc.Description ?? "").Trim(),
                        (tc.Description ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                    bool anyChange = revChanged || qtyChanged || descChanged;
                    rows.Add(new DiffRow
                    {
                        Change = anyChange ? "Changed" : "Unchanged",
                        Name = tc.Name ?? fc.Name,
                        PartNo = string.IsNullOrEmpty(tc.PartNo) ? fc.PartNo : tc.PartNo,
                        FromDesc = fc.Description, ToDesc = tc.Description,
                        FromRev = fc.Revision, ToRev = tc.Revision,
                        FromQty = fc.Qty, ToQty = tc.Qty,
                        FromWeight = fc.Weight, ToWeight = tc.Weight,
                        // A change with NO rev bump — the shop's rule is that any
                        // change bumps the rev, so flag a qty/description edit that
                        // left the rev letter untouched.
                        NoRevBump = anyChange && !revChanged,
                        Colour = anyChange ? cOrange : cGray
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
            if (s.StartsWith("Added")) e.CellStyle.ForeColor = cGreen;
            else if (s.StartsWith("Removed")) e.CellStyle.ForeColor = cRed;
            else if (s.StartsWith("Changed")) e.CellStyle.ForeColor = cOrange;
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
                    var sb = new StringBuilder();
                    sb.AppendLine("Assembly,From REV,To REV");
                    sb.AppendLine(Csv(_asmName) + "," + Csv("REV " + from.Revision) +
                        "," + Csv("REV " + to.Revision));
                    sb.AppendLine("Reason (from),Reason (to)");
                    sb.AppendLine(Csv(ReasonOf(from)) + "," + Csv(ReasonOf(to)));
                    sb.AppendLine();
                    sb.AppendLine("Change,Component,PartNo,Description (from),Description (to)," +
                        "Rev (from),Rev (to),Qty (from),Qty (to),Qty Delta,Weight Delta (lb)");
                    foreach (var r in _rows)
                    {
                        if (!IsVisible(r)) continue;
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(r.Change + (r.NoRevBump ? " (no rev bump)" : "")),
                            Csv(Path.GetFileNameWithoutExtension(r.Name ?? "")),
                            Csv(r.PartNo), Csv(r.FromDesc), Csv(r.ToDesc),
                            Csv(r.FromRev), Csv(r.ToRev),
                            Csv(r.FromQty.ToString()), Csv(r.ToQty.ToString()),
                            Csv(DeltaCell(r)), Csv(WeightDeltaCell(r))
                        }));
                    }
                    sb.AppendLine();
                    sb.AppendLine("Totals,Qty (from),Qty (to),Qty Delta," +
                        "Weight from (lb),Weight to (lb),Weight Delta (lb)");
                    sb.AppendLine(string.Join(",", new[]
                    {
                        "",
                        Csv(TotalQty(from).ToString()), Csv(TotalQty(to).ToString()),
                        Csv(Signed(TotalQty(to) - TotalQty(from))),
                        Csv(Fmt(TotalWeight(from))), Csv(Fmt(TotalWeight(to))),
                        Csv(SignedF(TotalWeight(to) - TotalWeight(from)))
                    }));
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
            if (field.Length > 0 && "=+-@\t\r".IndexOf(field[0]) >= 0)
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
