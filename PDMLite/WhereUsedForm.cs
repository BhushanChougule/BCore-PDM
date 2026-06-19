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
    // TWO MODES (radio toggle): SINGLE LEVEL (default) = the assemblies that
    // directly contain this file; ALL LEVELS = the full usage chain up to the
    // top-level assemblies (a part inside a sub-assembly that sits inside a
    // bigger assembly shows the whole path), rendered as an indented tree. The
    // all-levels walk is computed lazily on first switch (one disk pass) and
    // cached.
    //
    // Double-clicking a row sets FileToOpen and closes; the dashboard caller
    // then OpenDeferred()s it (opens the canonical WIP copy via OpenByPath),
    // mirroring the dashboard's own double-click and BaselineViewerForm.
    public class WhereUsedForm : Form
    {
        private readonly string _filePath;
        private readonly string _fileName;

        private List<WhereUsedEntry> _directParents = new List<WhereUsedEntry>();
        private List<WhereUsedEntry> _allLevels;        // null until first needed
        private List<WhereUsedEntry> _parents;          // the active list
        private bool _multi;                            // current mode

        // Set on double-click; the dashboard opens it after this modal closes.
        public string FileToOpen { get; private set; }

        private Panel _top, _bottom;
        private Label _fileLabel, _subtitle, _hint, _countLabel;
        private RadioButton _rbDirect, _rbAll;
        private DataGridView _grid;
        private Button _btnExport, _btnClose;

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        private static readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private static readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private static readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private static readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private static readonly Color cMaroon    = Color.FromArgb(140, 60, 60);
        private static readonly Color cTextDark  = Color.FromArgb(60, 64, 72);
        private static readonly Color cSubText   = Color.FromArgb(110, 116, 126);
        private static readonly Color cHintText  = Color.FromArgb(135, 141, 151);
        private static readonly Color cBg         = Color.FromArgb(245, 247, 250);

        private Font _fHeader, _fSub, _fMeta, _fHint, _fBtn, _fGrid, _fGridHead;

        public WhereUsedForm(string filePath, string fileName)
        {
            _filePath = filePath;
            _fileName = fileName;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fSub      = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _fMeta     = new Font("Segoe UI", 3.5f * _scale);
            _fHint     = new Font("Segoe UI", 3.2f * _scale, FontStyle.Italic);
            _fBtn      = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridHead = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            // Default mode (Single Level) is cheap — one walk; the multi-level
            // walk is deferred until the user asks for it.
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
            this.ClientSize = new Size(S(520), S(500));
            this.MinimumSize = new Size(S(420), S(340));

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

            // ── Top block (centred, like the other BCore forms) ──────────────
            _top = new Panel { Dock = DockStyle.Top, Height = S(98), BackColor = cBg };
            _fileLabel = new Label
            {
                Text = string.IsNullOrEmpty(_fileName)
                    ? Path.GetFileName(_filePath ?? "") : _fileName,
                Font = _fSub, ForeColor = cTextDark,
                Location = new Point(S(14), S(8)),
                AutoSize = false, Height = S(20), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _subtitle = new Label
            {
                Font = _fMeta, ForeColor = cSubText,
                Location = new Point(S(14), S(30)),
                AutoSize = false, Height = S(16), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _rbDirect = new RadioButton
            {
                Text = "Single Level", Font = _fMeta,
                ForeColor = cTextDark, AutoSize = true, Checked = true,
                Location = new Point(S(14), S(52))
            };
            _rbAll = new RadioButton
            {
                Text = "All Levels (Incl. Sub-assemblies)", Font = _fMeta,
                ForeColor = cTextDark, AutoSize = true,
                Location = new Point(S(150), S(52))
            };
            _rbDirect.CheckedChanged += (s, e) => { if (_rbDirect.Checked) SetMode(false); };
            _rbAll.CheckedChanged    += (s, e) => { if (_rbAll.Checked)    SetMode(true);  };
            _hint = new Label
            {
                Text = "Tip: double-click a row to open that assembly.",
                Font = _fHint, ForeColor = cHintText,
                Location = new Point(S(14), S(78)),
                AutoSize = false, Height = S(15), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _top.Controls.Add(_fileLabel);
            _top.Controls.Add(_subtitle);
            _top.Controls.Add(_rbDirect);
            _top.Controls.Add(_rbAll);
            _top.Controls.Add(_hint);
            _top.Resize += (s, e) => LayoutTop();

            // ── Footer (count line + buttons) ────────────────────────────────
            _bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = cBg };
            _countLabel = new Label
            {
                Font = _fMeta, ForeColor = cTextDark,
                Location = new Point(S(14), S(13)),
                AutoSize = false, AutoEllipsis = true,
                Height = S(18), Width = S(240),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _bottom.Controls.Add(_countLabel);

            _btnClose = MakeButton("Close", Color.FromArgb(220, 220, 220), cTextDark);
            _btnClose.Click += (s, e) => this.Close();
            _btnExport = MakeButton("Export CSV", cBrand, Color.White);
            _btnExport.Click += (s, e) => ExportCsv();
            _bottom.Controls.Add(_btnExport);
            _bottom.Controls.Add(_btnClose);
            _bottom.Resize += (s, e) => LayoutBottom();

            // ── Grid ─────────────────────────────────────────────────────────
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
            _grid.CellMouseDoubleClick += Grid_CellMouseDoubleClick;

            // Fill control added FIRST (resolved last → takes the middle), then
            // edge panels; the inner top panel before the header bar so the
            // header docks outermost (house z-order convention).
            this.Controls.Add(_grid);
            this.Controls.Add(_bottom);
            this.Controls.Add(_top);
            this.Controls.Add(headerBar);

            LayoutTop();
            LayoutBottom();

            this.FormClosed += (s, e) =>
            {
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fHint?.Dispose(); _fBtn?.Dispose(); _fGrid?.Dispose();
                _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
        }

        // Centre the filename / subtitle / hint (full-width, centre-aligned text)
        // and the radio PAIR (as one unit) — matches the centred top blocks of
        // the other BCore forms.
        private void LayoutTop()
        {
            if (_top == null) return;
            int w = _top.Width;
            int margin = S(14);
            int innerW = Math.Max(S(40), w - 2 * margin);
            _fileLabel.Location = new Point(margin, _fileLabel.Top); _fileLabel.Width = innerW;
            _subtitle.Location  = new Point(margin, _subtitle.Top);  _subtitle.Width  = innerW;
            _hint.Location      = new Point(margin, _hint.Top);      _hint.Width      = innerW;

            // PreferredSize measures text+glyph reliably even before the form is
            // shown (an AutoSize control's .Width may not be finalised yet).
            int wDirect = _rbDirect.PreferredSize.Width;
            int wAll    = _rbAll.PreferredSize.Width;
            int gap = S(28);
            int total = wDirect + gap + wAll;
            int x0 = Math.Max(margin, (w - total) / 2);
            _rbDirect.Location = new Point(x0, _rbDirect.Top);
            _rbAll.Location    = new Point(x0 + wDirect + gap, _rbAll.Top);
        }

        // Right-align the buttons and bound the count label so its text can never
        // run under them (the overlap/clip bug) at any width or DPI.
        private void LayoutBottom()
        {
            if (_bottom == null) return;
            _btnClose.Location  = new Point(_bottom.Width - _btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(_btnClose.Left - _btnExport.Width - S(8), S(9));
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

        // Switch between Single Level and All Levels. The all-levels walk is
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
                _countLabel.Text = total == 1
                    ? "Used by 1 assembly."
                    : "Used by " + total + " assemblies.";
            }
            else
            {
                int direct = _parents.Count(p => p.Level == 0);
                _countLabel.Text = "Used by " + total + " reference" +
                    (total == 1 ? "" : "s") + "  ·  " + direct + " direct.";
            }
            if (_btnExport != null) _btnExport.Enabled = total > 0;
            LayoutBottom(); // re-bound the label width to the current panel width
        }

        // Double-click a row → open that assembly (deferred via the dashboard
        // caller so it opens after this nested modal closes). Mirrors the
        // dashboard's own double-click-to-open.
        private void Grid_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _parents.Count) return;
            string path = _parents[e.RowIndex].Path;
            if (string.IsNullOrEmpty(path)) return;
            FileToOpen = path;
            this.Close();
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
                        Csv(_multi ? "All levels" : "Single level"));
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
