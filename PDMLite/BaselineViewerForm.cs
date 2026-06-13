using System;
using System.Collections.Generic;
using System.Drawing;
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
        private Button _btnCompare;

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

            // ── Top panel: assembly name + release picker + meta ──────
            var top = new Panel { Dock = DockStyle.Top, Height = S(86), BackColor = cBg };

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
                Width = S(220)
            };
            foreach (var b in _baselines)
                _revPicker.Items.Add("REV " + b.Revision +
                    (string.IsNullOrEmpty(b.Config) ? "" : "   (" + b.Config + ")") +
                    "   —   " + b.ReleasedDate);
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

            // "Compare…" — diff two releases. Needs at least two captured
            // baselines; greyed otherwise.
            _btnCompare = MakeButton("Compare…", cBrandDark, Color.White);
            _btnCompare.Enabled = _baselines.Count >= 2;
            _btnCompare.Click += (s, e) => OpenCompare();

            // Anchor to the bottom-right; lay out on resize.
            bottom.Controls.Add(_btnExport);
            bottom.Controls.Add(_btnCompare);
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
            _grid.Rows.Clear();

            var b = Selected();
            if (b == null)
            {
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
                "   ·   Released by " + b.ReleasedBy + " on " + b.ReleasedDate;

            // Stable order: parts then sub-assemblies, alphabetically.
            var ordered = b.Components
                .OrderBy(c => (Path.GetExtension(c.Name ?? "") ?? "")
                    .Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var c in ordered)
            {
                _grid.Rows.Add(
                    Path.GetFileNameWithoutExtension(c.Name ?? ""),
                    c.PartNo,
                    c.Revision,
                    c.Status,
                    c.Qty);
            }

            int total = ordered.Sum(c => Math.Max(c.Qty, 0));
            _countLabel.Text = ordered.Count + " unique component" +
                (ordered.Count == 1 ? "" : "s") + "  ·  " + total + " total";
            if (_btnExport != null) _btnExport.Enabled = ordered.Count > 0;
        }

        // Colour the Status cell to match the rest of the app.
        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].HeaderText != "Status")
                return;
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
                        Csv(b.ReleasedBy), Csv(b.ReleasedDate)
                    }));
                    sb.AppendLine();
                    sb.AppendLine("Component,PartNo,Revision,Status,Qty");
                    foreach (var c in b.Components)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv(Path.GetFileNameWithoutExtension(c.Name ?? "")),
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

        // Right-align Close, then Export, then Compare (right→left).
        private void LayoutBottomButtons(Panel bottom, Button btnClose)
        {
            btnClose.Location = new Point(
                bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(
                btnClose.Left - _btnExport.Width - S(8), S(9));
            _btnCompare.Location = new Point(
                _btnExport.Left - _btnCompare.Width - S(8), S(9));
        }

        // Open the two-release comparison (nested-modal on top of the viewer).
        private void OpenCompare()
        {
            try
            {
                using (var c = new BaselineCompareForm(_asmPath, _asmName))
                    c.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the comparison:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
