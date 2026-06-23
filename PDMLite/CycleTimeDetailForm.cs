using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // One measured WIP→Released cycle (a row in the analytics drill-in). Built by
    // AuditReportForm.ComputeCycleRecords; Division + Bounces are enriched by the
    // caller (filename → WIP path → division via GetAllFiles, and the per-file
    // RejectRequest count).
    internal sealed class CycleRecord
    {
        public string   FileName;
        public string   Division;
        public string   ReleasedBy;
        public DateTime StartTime;
        public DateTime ReleaseTime;
        public double   Days;
        public int      Bounces;
    }

    // One bounce-back: a Master rejecting an engineer's request (audit
    // RejectRequest). Engineer is parsed from the "requested by X" note.
    internal sealed class ReworkRow
    {
        public string   FileName;
        public string   Engineer;
        public DateTime Time;
    }

    // The Cycle-Time Analytics popup (opened from the Audit Report's cycle strip
    // "Details…" link). A pure VIEW — it receives the already-computed records
    // for the window selected on the strip and renders:
    //   • a summary (avg / median / p90 / count + bounce-backs),
    //   • a per-release drill-in grid (File · Division · Released By · WIP Start ·
    //     Released · Days · Bounces), sortable,
    //   • a roll-up grid grouped By Division or By Released-By (count · avg · p90),
    //   • CSV export of the drill-in.
    // DPI-aware (S(v)=v*_scale), house styling; fonts are fields disposed in
    // Dispose(bool). Plain (non-virtual) grids — the data is bounded by the
    // number of measured releases in the window.
    internal class CycleTimeDetailForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cBorder    = Color.FromArgb(220, 225, 232);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cRowAlt    = Color.FromArgb(245, 247, 250);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private Font _fTitle, _fLabel, _fGrid, _fGridBold, _fBtn;

        private readonly string _windowLabel;
        private readonly List<CycleRecord> _records;
        private readonly List<ReworkRow> _bounces;

        private Label _summary;
        private ComboBox _groupBy;
        private DataGridView _gridMain, _gridRollup;

        public CycleTimeDetailForm(float scale, string windowLabel,
            List<CycleRecord> records, List<ReworkRow> bounces)
        {
            _scale = scale <= 0 ? 1f : scale;
            _windowLabel = windowLabel ?? "All time";
            _records = records ?? new List<CycleRecord>();
            _bounces = bounces ?? new List<ReworkRow>();

            _fTitle    = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fLabel    = new Font("Segoe UI", 3.7f * _scale);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridBold = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);
            _fBtn      = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Cycle-Time Analytics";
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox     = false;
            BackColor       = cBg;

            var area = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(S(960), (int)(area.Width * 0.9));
            int h = Math.Min(S(660), (int)(area.Height * 0.9));
            ClientSize  = new Size(w, h);
            MinimumSize = new Size(S(680), S(460));

            Build();
            FillMain();
            RebuildRollup();
            UpdateSummary();
        }

        private void Build()
        {
            // Docking, house convention: add the Fill control FIRST, then the
            // edge-docked siblings (WinForms lays docked children out in reverse
            // z-order, so the Fill control — added first — claims the remainder).
            // Each container has at most ONE edge per side to avoid ambiguity.

            // ── contentRoot (Fill) holds the centre area + the header strip ──
            var contentRoot = new Panel { Dock = DockStyle.Fill, BackColor = cBg };

            var centre = new Panel { Dock = DockStyle.Fill, BackColor = cBg,
                Padding = new Padding(S(12), S(4), S(12), S(4)) };

            _gridMain = MakeGrid();
            _gridMain.Dock = DockStyle.Fill;
            centre.Controls.Add(_gridMain);            // Fill first

            var rollupPanel = new Panel { Dock = DockStyle.Bottom,
                Height = S(168), BackColor = cBg };
            _gridRollup = MakeGrid();
            _gridRollup.Dock = DockStyle.Fill;
            rollupPanel.Controls.Add(_gridRollup);     // Fill first
            rollupPanel.Controls.Add(new Label
            {
                Text = "Summary by group", Font = _fGridBold, ForeColor = cBrandDark,
                Dock = DockStyle.Top, Height = S(18)
            });                                        // edge after
            centre.Controls.Add(rollupPanel);          // edge after the Fill grid

            contentRoot.Controls.Add(centre);          // Fill first

            // Header strip (summary + roll-up group selector).
            var header = new Panel { BackColor = cBg, Dock = DockStyle.Top,
                Height = S(80) };
            _summary = new Label
            {
                Font = _fLabel, ForeColor = cTextDark, AutoSize = false,
                Location = new Point(S(12), S(6)),
                Size = new Size(ClientSize.Width - S(24), S(42)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            header.Controls.Add(_summary);
            header.Controls.Add(new Label
            {
                Text = "Roll-up group:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(S(12), S(54)), AutoSize = true
            });
            _groupBy = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Font = _fLabel,
                Location = new Point(S(116), S(51)), Width = S(160)
            };
            _groupBy.Items.AddRange(new object[] { "By Division", "By Released By" });
            _groupBy.SelectedIndex = 0;
            _groupBy.SelectedIndexChanged += (s, e) => RebuildRollup();
            header.Controls.Add(_groupBy);
            contentRoot.Controls.Add(header);          // edge after the Fill centre

            Controls.Add(contentRoot);                 // Fill first (form level)

            // ── Title bar (Top) ───────────────────────────────────────
            var title = new Panel
            {
                BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(32)
            };
            title.Controls.Add(new Label
            {
                Text = "Cycle-Time Analytics", Font = _fTitle,
                ForeColor = Color.White, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(title);                       // edge after

            // ── Bottom bar (Export / Close) ───────────────────────────
            var bottom = new Panel
            {
                BackColor = cBg, Dock = DockStyle.Bottom, Height = S(44)
            };
            var btnClose = MakeButton("Close", cDark);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Location = new Point(bottom.Width - S(12) - btnClose.Width, S(8));
            btnClose.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(btnClose);
            var btnCsv = MakeButton("Export CSV", cBrandDark);
            btnCsv.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCsv.Location = new Point(btnClose.Left - S(8) - btnCsv.Width, S(8));
            btnCsv.Click += (s, e) => ExportCsv();
            bottom.Controls.Add(btnCsv);
            Controls.Add(bottom);                      // edge after
            CancelButton = btnClose;

            // Main grid columns (typed so Days/dates sort numerically/chronologically).
            AddCol(_gridMain, "File", "File", S(170), typeof(string), null,
                DataGridViewContentAlignment.MiddleLeft);
            AddCol(_gridMain, "Division", "Div", S(70), typeof(string), null,
                DataGridViewContentAlignment.MiddleCenter);
            AddCol(_gridMain, "Released By", "By", S(110), typeof(string), null,
                DataGridViewContentAlignment.MiddleLeft);
            AddCol(_gridMain, "WIP Start", "Start", S(130), typeof(DateTime),
                "MM/dd/yyyy HH:mm", DataGridViewContentAlignment.MiddleLeft);
            AddCol(_gridMain, "Released", "Rel", S(130), typeof(DateTime),
                "MM/dd/yyyy HH:mm", DataGridViewContentAlignment.MiddleLeft);
            AddCol(_gridMain, "Days", "Days", S(70), typeof(double), "0.0",
                DataGridViewContentAlignment.MiddleRight);
            AddCol(_gridMain, "Bounces", "Bnc", S(70), typeof(int), null,
                DataGridViewContentAlignment.MiddleCenter);

            AddCol(_gridRollup, "Group", "G", S(220), typeof(string), null,
                DataGridViewContentAlignment.MiddleLeft);
            AddCol(_gridRollup, "Releases", "N", S(110), typeof(int), null,
                DataGridViewContentAlignment.MiddleCenter);
            AddCol(_gridRollup, "Avg d", "Avg", S(110), typeof(double), "0.0",
                DataGridViewContentAlignment.MiddleCenter);
            AddCol(_gridRollup, "p90 d", "p90", S(110), typeof(double), "0.0",
                DataGridViewContentAlignment.MiddleCenter);
        }

        private void FillMain()
        {
            _gridMain.Rows.Clear();
            // Slowest first by default (most actionable).
            foreach (var r in _records.OrderByDescending(x => x.Days))
                _gridMain.Rows.Add(
                    r.FileName, NA(r.Division), NA(r.ReleasedBy),
                    r.StartTime, r.ReleaseTime, r.Days, r.Bounces);
        }

        private void RebuildRollup()
        {
            _gridRollup.Rows.Clear();
            bool byDiv = _groupBy.SelectedIndex == 0;
            var groups = _records
                .GroupBy(r => NA(byDiv ? r.Division : r.ReleasedBy))
                .Select(g =>
                {
                    var days = g.Select(x => x.Days).OrderBy(x => x).ToList();
                    return new
                    {
                        Group = g.Key,
                        Count = g.Count(),
                        Avg = days.Average(),
                        P90 = AuditReportForm.Percentile(days, 0.90)
                    };
                })
                .OrderByDescending(x => x.Count);
            foreach (var g in groups)
                _gridRollup.Rows.Add(g.Group, g.Count, g.Avg, g.P90);
        }

        private void UpdateSummary()
        {
            int n = _records.Count;
            string line1, line2;
            if (n == 0)
            {
                line1 = _windowLabel + ":  no completed cycles in this window.";
            }
            else
            {
                var days = _records.Select(r => r.Days).OrderBy(x => x).ToList();
                double avg = days.Average();
                double med = days.Count % 2 == 1
                    ? days[days.Count / 2]
                    : (days[days.Count / 2 - 1] + days[days.Count / 2]) / 2.0;
                double p90 = AuditReportForm.Percentile(days, 0.90);
                line1 = string.Format(CultureInfo.InvariantCulture,
                    "{0}:  {1} releases  ·  Avg {2:0.0} d  ·  Median {3:0.0} d  ·  p90 {4:0.0} d",
                    _windowLabel, n, avg, med, p90);
            }

            // Rework (bounce-backs) summary: total + the top requesters.
            int b = _bounces.Count;
            string top = "";
            if (b > 0)
            {
                top = string.Join(", ", _bounces
                    .GroupBy(x => string.IsNullOrEmpty(x.Engineer) ? "(unknown)" : x.Engineer)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => g.Key + " ×" + g.Count()));
            }
            line2 = "Bounce-backs (Master rejections): " + b +
                    (top.Length > 0 ? "   ·   top: " + top : "");

            _summary.Text = line1 + Environment.NewLine + line2;
        }

        // ── Helpers ──────────────────────────────────────────────────
        private static string NA(string s) =>
            string.IsNullOrEmpty(s) ? "(unknown)" : s;

        private Button MakeButton(string text, Color back)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn, Width = S(110), Height = S(26),
                BackColor = back, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                BackgroundColor = cBg, BorderStyle = BorderStyle.None,
                Font = _fGrid, GridColor = cBorder,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, ReadOnly = true,
                RowHeadersVisible = false, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode =
                    DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false, AllowUserToResizeColumns = true
            };
            g.ColumnHeadersHeight = S(26);
            g.RowTemplate.Height = S(22);
            g.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = _fGridBold;
            g.ColumnHeadersDefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleLeft;
            g.AlternatingRowsDefaultCellStyle.BackColor = cRowAlt;
            return g;
        }

        private void AddCol(DataGridView g, string header, string name, int width,
            Type valueType, string format, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = header, Name = name, Width = width,
                ValueType = valueType, SortMode = DataGridViewColumnSortMode.Automatic
            };
            c.DefaultCellStyle.Alignment = align;
            if (format != null) c.DefaultCellStyle.Format = format;
            g.Columns.Add(c);
        }

        // ── CSV export (drill-in rows; RFC-4180 + formula-injection guard) ──
        private void ExportCsv()
        {
            try
            {
                using (var dlg = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    FileName = "CycleTime_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv"
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var sb = new StringBuilder();
                    sb.AppendLine("File,Division,Released By,WIP Start,Released,Days,Bounces");
                    foreach (var r in _records.OrderByDescending(x => x.Days))
                        sb.AppendLine(string.Join(",",
                            Csv(r.FileName), Csv(NA(r.Division)), Csv(NA(r.ReleasedBy)),
                            Csv(r.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                            Csv(r.ReleaseTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                            r.Days.ToString("0.000", CultureInfo.InvariantCulture),
                            r.Bounces.ToString(CultureInfo.InvariantCulture)));
                    System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
                    MessageBox.Show("Exported " + _records.Count + " row(s).",
                        "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed:\n" + ex.Message, "BCore PDM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

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

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fTitle?.Dispose();
                _fLabel?.Dispose();
                _fGrid?.Dispose();
                _fGridBold?.Dispose();
                _fBtn?.Dispose();
            }
        }
    }
}
