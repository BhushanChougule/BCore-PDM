using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // Read-only "Where Used" viewer: the tracked assemblies that reference a
    // given part/sub-assembly, with each parent's Part No, Revision and Status.
    // Computed on demand from disk (VaultManager.GetWhereUsed / GetWhereUsedTree
    // — the same dependency-walk primitive the release gate uses), so it needs
    // no persisted index and is always current. Opened from the Vault Dashboard
    // row right-click ("Where Used"), nested-modal on top of the dashboard.
    // Self-contained (own palette / S() / CSV escaping) per the one-form-one-
    // file convention. Never writes the vault.
    //
    // TWO MODES (radio toggle): DIRECT (default, 1 level) = the assemblies that
    // directly contain this file; ALL LEVELS = the full usage chain up to the
    // top-level assemblies (a part inside a sub-assembly that sits inside a
    // bigger assembly shows the whole path), rendered as an indented tree. The
    // all-levels walk is computed lazily on first switch (one disk pass) and
    // cached.
    public class WhereUsedForm : Form
    {
        private readonly string _filePath;
        private readonly string _fileName;

        private List<WhereUsedEntry> _directParents = new List<WhereUsedEntry>();
        private List<WhereUsedEntry> _allLevels;        // null until first needed
        private List<WhereUsedEntry> _parents;          // the active list
        private bool _multi;                            // current mode

        private DataGridView _grid;
        private Label _subtitle;
        private Label _countLabel;
        private RadioButton _rbDirect, _rbAll;
        private Button _btnExport;

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        private static readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private static readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private static readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private static readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private static readonly Color cMaroon    = Color.FromArgb(140, 60, 60);
        private static readonly Color cTextDark  = Color.FromArgb(60, 64, 72);
        private static readonly Color cSubText   = Color.FromArgb(110, 116, 126);
        private static readonly Color cBg         = Color.FromArgb(245, 247, 250);

        private Font _fHeader, _fSub, _fMeta, _fBtn, _fGrid, _fGridHead;

        public WhereUsedForm(string filePath, string fileName)
        {
            _filePath = filePath;
            _fileName = fileName;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fSub      = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _fMeta     = new Font("Segoe UI", 3.5f * _scale);
            _fBtn      = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridHead = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            // Default mode (Direct) is cheap — one walk; the multi-level walk is
            // deferred until the user asks for it.
            try { _directParents = VaultManager.GetWhereUsed(_filePath); }
            catch { _directParents = new List<WhereUsedEntry>(); }
            _parents = _directParents;

            BuildUI();
            UpdateSubtitle();
            LoadRows();
        }

        private void BuildUI()
        {
            this.Text = "BCore PDM — Where Used";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.KeyPreview = true;
            this.ClientSize = new Size(S(520), S(486));
            this.MinimumSize = new Size(S(420), S(320));

            var headerBar = new Panel
            {
                BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(30)
            };
            headerBar.Controls.Add(new Label
            {
                Text = "Where Used",
                Font = _fHeader,
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });

            var top = new Panel { Dock = DockStyle.Top, Height = S(82), BackColor = cBg };
            top.Controls.Add(new Label
            {
                Text = string.IsNullOrEmpty(_fileName)
                    ? Path.GetFileName(_filePath ?? "") : _fileName,
                Font = _fSub,
                ForeColor = cTextDark,
                Location = new Point(S(14), S(8)),
                AutoSize = false, Width = S(480), Height = S(20),
                AutoEllipsis = true
            });
            _subtitle = new Label
            {
                Font = _fMeta,
                ForeColor = cSubText,
                Location = new Point(S(14), S(30)),
                AutoSize = false, Width = S(480), Height = S(16),
                AutoEllipsis = true
            };
            top.Controls.Add(_subtitle);

            // Mode toggle: Direct (1 level) vs All levels (full usage chain).
            _rbDirect = new RadioButton
            {
                Text = "Direct parents (1 level)", Font = _fMeta,
                ForeColor = cTextDark, AutoSize = true, Checked = true,
                Location = new Point(S(14), S(52))
            };
            _rbAll = new RadioButton
            {
                Text = "All levels (incl. sub-assemblies)", Font = _fMeta,
                ForeColor = cTextDark, AutoSize = true,
                Location = new Point(S(190), S(52))
            };
            _rbDirect.CheckedChanged += (s, e) => { if (_rbDirect.Checked) SetMode(false); };
            _rbAll.CheckedChanged    += (s, e) => { if (_rbAll.Checked)    SetMode(true);  };
            top.Controls.Add(_rbDirect);
            top.Controls.Add(_rbAll);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = cBg };
            _countLabel = new Label
            {
                Font = _fMeta, ForeColor = cTextDark,
                Location = new Point(S(14), S(13)),
                AutoSize = false, AutoEllipsis = true,
                Height = S(18), Width = S(240),
                TextAlign = ContentAlignment.MiddleLeft
            };
            bottom.Controls.Add(_countLabel);

            var btnClose = MakeButton("Close", Color.FromArgb(220, 220, 220), cTextDark);
            btnClose.Click += (s, e) => this.Close();
            _btnExport = MakeButton("Export CSV", cBrand, Color.White);
            _btnExport.Click += (s, e) => ExportCsv();
            bottom.Controls.Add(_btnExport);
            bottom.Controls.Add(btnClose);
            bottom.Resize += (s, e) => LayoutBottom(bottom, btnClose);

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
                AllowUserToResizeColumns = true,
                EnableHeadersVisualStyles = false
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = _fGridHead;
            _grid.ColumnHeadersHeight = S(26);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 251);
            _grid.RowTemplate.Height = S(22);
            AddCol("Assembly", 0.46f);
            AddCol("Part No", 0.24f);
            AddCol("Rev", 0.12f);
            AddCol("Status", 0.18f);
            _grid.CellFormatting += Grid_CellFormatting;

            // Fill control added FIRST (resolved last → takes the middle), then
            // edge panels; the inner top panel before the header bar so the
            // header docks outermost (house z-order convention).
            this.Controls.Add(_grid);
            this.Controls.Add(bottom);
            this.Controls.Add(top);
            this.Controls.Add(headerBar);

            LayoutBottom(bottom, btnClose);

            this.FormClosed += (s, e) =>
            {
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fBtn?.Dispose(); _fGrid?.Dispose(); _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
        }

        // Right-align the buttons and bound the count label so its text can never
        // run under them (the overlap bug) at any width or DPI.
        private void LayoutBottom(Panel bottom, Button btnClose)
        {
            btnClose.Location = new Point(bottom.Width - btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(btnClose.Left - _btnExport.Width - S(8), S(9));
            if (_countLabel != null)
                _countLabel.Width = Math.Max(S(40),
                    _btnExport.Left - _countLabel.Left - S(10));
        }

        private void AddCol(string header, float weight)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = weight * 100f,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        }

        // Switch between Direct (1 level) and All levels. The all-levels walk is
        // computed once (one disk pass) on first request and cached.
        private void SetMode(bool multi)
        {
            if (multi == _multi && _parents != null) return;
            _multi = multi;
            if (multi && _allLevels == null)
            {
                var old = this.Cursor;
                this.Cursor = Cursors.WaitCursor;
                try { _allLevels = VaultManager.GetWhereUsedTree(_filePath); }
                catch { _allLevels = new List<WhereUsedEntry>(); }
                finally { this.Cursor = old; }
            }
            _parents = multi ? _allLevels : _directParents;
            UpdateSubtitle();
            LoadRows();
        }

        private void UpdateSubtitle()
        {
            if (_subtitle == null) return;
            _subtitle.Text = _multi
                ? "Full usage chain — direct parents and the assemblies that contain them:"
                : "Assemblies that directly reference this file:";
        }

        private void LoadRows()
        {
            _grid.Rows.Clear();
            foreach (var p in _parents)
            {
                string nm = Path.GetFileNameWithoutExtension(p.Name ?? "");
                // Indent deeper levels so the usage chain reads as a tree.
                if (_multi && p.Level > 0)
                    nm = new string(' ', p.Level * 4) + "↳ " + nm;
                _grid.Rows.Add(nm, p.PartNo, p.Revision, p.Status);
            }

            int total = _parents.Count;
            if (total == 0)
            {
                _countLabel.Text = "Not used by any tracked assembly.";
            }
            else if (!_multi)
            {
                _countLabel.Text = "Used by " + total + " assembly" +
                    (total == 1 ? "" : "(ies)") + ".";
            }
            else
            {
                int direct = _parents.Count(p => p.Level == 0);
                _countLabel.Text = "Used by " + total + " reference" +
                    (total == 1 ? "" : "s") + " across the usage chain  ·  " +
                    direct + " direct.";
            }
            if (_btnExport != null) _btnExport.Enabled = total > 0;
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string col = _grid.Columns[e.ColumnIndex].HeaderText;

            if (col == "Status")
            {
                string s = (e.Value as string) ?? "";
                if (s.Equals("Released", StringComparison.OrdinalIgnoreCase))
                    e.CellStyle.ForeColor = cGreen;
                else if (s.Equals("Locked", StringComparison.OrdinalIgnoreCase))
                    e.CellStyle.ForeColor = cMaroon;
                else if (s.Equals("WIP", StringComparison.OrdinalIgnoreCase))
                    e.CellStyle.ForeColor = cOrange;
                else if (s.Equals("Obsolete", StringComparison.OrdinalIgnoreCase))
                    e.CellStyle.ForeColor = Color.FromArgb(120, 120, 120);
                else
                    e.CellStyle.ForeColor = Color.FromArgb(140, 140, 140);
                return;
            }

            // Mute the Assembly name of deeper levels so the direct parents stand
            // out in the tree.
            if (col == "Assembly" && _multi && e.RowIndex < _parents.Count
                && _parents[e.RowIndex].Level > 0)
                e.CellStyle.ForeColor = cSubText;
        }

        private void ExportCsv()
        {
            if (_parents.Count == 0) return;
            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(_fileName ?? "file"))
                    + "_WHERE-USED.csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("File,Path,Mode");
                    sb.AppendLine(Csv(_fileName) + "," + Csv(_filePath) + "," +
                        Csv(_multi ? "All levels" : "Direct (1 level)"));
                    sb.AppendLine();
                    sb.AppendLine("Level,Assembly,PartNo,Revision,Status,Path");
                    foreach (var p in _parents)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv((p.Level + 1).ToString()),
                            Csv(Path.GetFileNameWithoutExtension(p.Name ?? "")),
                            Csv(p.PartNo), Csv(p.Revision), Csv(p.Status), Csv(p.Path)
                        }));
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show("Where-used list exported to:\n" + sfd.FileName,
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

        // RFC-4180 escaping + Excel formula-injection guard.
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
            if (string.IsNullOrEmpty(name)) return "file";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(
                c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        private Button MakeButton(string text, Color back, Color fore)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn,
                Width = S(96), Height = S(26),
                BackColor = back, ForeColor = fore,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
