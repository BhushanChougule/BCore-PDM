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
    // given part/sub-assembly, with each parent's Part No, Revision, Qty and
    // Status. Computed on demand from disk (VaultManager.GetWhereUsed /
    // GetWhereUsedTree / GetWhereUsedTopLevel — the same dependency-walk
    // primitive the release gate uses), so it needs no persisted index and is
    // always current. Opened from the Vault Dashboard row right-click ("Where
    // Used"), nested-modal on top of the dashboard. Self-contained (own palette
    // / S() / CSV escaping) per the one-form-one-file convention. Never writes
    // the vault.
    //
    // THREE MODES (radio toggle): SINGLE LEVEL (default) = the assemblies that
    // directly contain this file; ALL LEVELS = the full usage chain up to the
    // top-level assemblies (indented tree); TOP LEVEL ONLY = just the root/final
    // assemblies that ultimately contain it. The All/Top walks are computed
    // lazily on first switch (one disk pass each) and cached.
    //
    // Qty = how many instances of the row's immediate child the parent contains,
    // read from the parent's latest as-released baseline ("—" when the parent
    // has never been released, so there's no baseline to read).
    //
    // A live FILTER box narrows the rows; a RIGHT-CLICK menu opens the assembly,
    // climbs further up ("Where Used from here"), copies the path or opens the
    // folder. Double-clicking a row (or "Open Assembly") sets FileToOpen and
    // closes; the dashboard caller then OpenDeferred()s it (canonical WIP copy).
    public class WhereUsedForm : Form
    {
        private enum Mode { Single, All, Top }

        private readonly string _filePath;
        private readonly string _fileName;

        private List<WhereUsedEntry> _single = new List<WhereUsedEntry>();
        private List<WhereUsedEntry> _allLevels;     // null until first needed
        private List<WhereUsedEntry> _topLevel;      // null until first needed
        private List<WhereUsedEntry> _parents;       // the active mode's list
        private readonly List<WhereUsedEntry> _displayed = new List<WhereUsedEntry>();
        private Mode _mode = Mode.Single;

        // Set on open/double-click; the dashboard opens it after this modal closes.
        public string FileToOpen { get; private set; }

        private Panel _top, _filterPanel, _bottom;
        private Label _fileLabel, _subtitle, _hint, _countLabel, _filterLabel;
        private RadioButton _rbSingle, _rbAll, _rbTop;
        private TextBox _filterBox;
        private DataGridView _grid;
        private ContextMenuStrip _rowMenu;
        private WhereUsedEntry _menuEntry;       // the right-clicked row
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

            // Default mode (Single Level) is cheap — one walk; the All/Top walks
            // are deferred until the user asks for them.
            try { _single = VaultManager.GetWhereUsed(_filePath); }
            catch { _single = new List<WhereUsedEntry>(); }
            _parents = _single;

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
            this.ClientSize = new Size(S(560), S(516));
            this.MinimumSize = new Size(S(470), S(360));

            var headerBar = new Panel
            {
                BackColor = cBrandDark, Dock = DockStyle.Top, Height = S(30)
            };
            headerBar.Controls.Add(new Label
            {
                Text = "Where Used", Font = _fHeader, ForeColor = Color.White,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });

            // ── Top block (centred, like the other BCore forms) ──────────────
            _top = new Panel { Dock = DockStyle.Top, Height = S(100), BackColor = cBg };
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
            _rbSingle = MakeRadio("Single Level", true);
            _rbAll    = MakeRadio("All Levels (Incl. Sub-assemblies)", false);
            _rbTop    = MakeRadio("Top Level Only", false);
            _rbSingle.Location = new Point(S(14), S(52));
            _rbAll.Location    = new Point(S(120), S(52));
            _rbTop.Location    = new Point(S(300), S(52));
            _rbSingle.CheckedChanged += (s, e) => { if (_rbSingle.Checked) SetMode(Mode.Single); };
            _rbAll.CheckedChanged    += (s, e) => { if (_rbAll.Checked)    SetMode(Mode.All);    };
            _rbTop.CheckedChanged    += (s, e) => { if (_rbTop.Checked)    SetMode(Mode.Top);    };
            _hint = new Label
            {
                Text = "Tip: double-click or right-click a row to open / drill up.",
                Font = _fHint, ForeColor = cHintText,
                Location = new Point(S(14), S(78)),
                AutoSize = false, Height = S(15), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _top.Controls.Add(_fileLabel);
            _top.Controls.Add(_subtitle);
            _top.Controls.Add(_rbSingle);
            _top.Controls.Add(_rbAll);
            _top.Controls.Add(_rbTop);
            _top.Controls.Add(_hint);
            _top.Resize += (s, e) => LayoutTop();

            // ── Filter row ───────────────────────────────────────────────────
            _filterPanel = new Panel { Dock = DockStyle.Top, Height = S(34), BackColor = cBg };
            _filterLabel = new Label
            {
                Text = "Filter:", Font = _fMeta, ForeColor = cTextDark,
                AutoSize = true, Location = new Point(S(14), S(9))
            };
            _filterBox = new TextBox
            {
                Font = _fMeta, Width = S(220), Location = new Point(S(60), S(6))
            };
            _filterBox.TextChanged += (s, e) => LoadRows();
            _filterPanel.Controls.Add(_filterLabel);
            _filterPanel.Controls.Add(_filterBox);
            _filterPanel.Resize += (s, e) => LayoutFilter();

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
            AddCol("Assembly", 0.42f, false);
            AddCol("Part No", 0.22f, false);
            AddCol("Rev", 0.10f, true);
            AddCol("Qty", 0.10f, true);
            AddCol("Status", 0.16f, false);
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellMouseDoubleClick += Grid_CellMouseDoubleClick;
            _grid.MouseDown += Grid_MouseDown;

            // Row right-click menu (open / drill up / copy / folder).
            _rowMenu = new ContextMenuStrip { Font = _fMeta, ShowImageMargin = false };
            _rowMenu.Items.Add("Open Assembly", null, (s, e) =>
            {
                if (_menuEntry != null && !string.IsNullOrEmpty(_menuEntry.Path))
                { FileToOpen = _menuEntry.Path; this.Close(); }
            });
            _rowMenu.Items.Add("Where Used from here", null,
                (s, e) => WhereUsedFromHere(_menuEntry));
            _rowMenu.Items.Add(new ToolStripSeparator());
            _rowMenu.Items.Add("Copy File Path", null, (s, e) => CopyPath(_menuEntry));
            _rowMenu.Items.Add("Open Containing Folder", null,
                (s, e) => OpenContainingFolder(_menuEntry?.Path));

            // Fill control added FIRST (resolved last → middle), then edge panels;
            // header docks outermost (house z-order convention).
            this.Controls.Add(_grid);
            this.Controls.Add(_bottom);
            this.Controls.Add(_filterPanel);
            this.Controls.Add(_top);
            this.Controls.Add(headerBar);

            LayoutTop();
            LayoutFilter();
            LayoutBottom();

            this.FormClosed += (s, e) =>
            {
                _rowMenu?.Dispose();
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fHint?.Dispose(); _fBtn?.Dispose(); _fGrid?.Dispose();
                _fGridHead?.Dispose();
            };
            this.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) this.Close(); };
        }

        private RadioButton MakeRadio(string text, bool check)
        {
            return new RadioButton
            {
                Text = text, Font = _fMeta, ForeColor = cTextDark,
                AutoSize = true, Checked = check
            };
        }

        // Centre the filename / subtitle / hint (full-width, centre-aligned text)
        // and the radio TRIO (as one unit) — matches the centred top blocks of
        // the other BCore forms.
        private void LayoutTop()
        {
            if (_top == null) return;
            int w = _top.Width, margin = S(14);
            int innerW = Math.Max(S(40), w - 2 * margin);
            _fileLabel.Location = new Point(margin, _fileLabel.Top); _fileLabel.Width = innerW;
            _subtitle.Location  = new Point(margin, _subtitle.Top);  _subtitle.Width  = innerW;
            _hint.Location      = new Point(margin, _hint.Top);      _hint.Width      = innerW;

            // PreferredSize measures text+glyph reliably even before the form is
            // shown (an AutoSize control's .Width may not be finalised yet).
            int w1 = _rbSingle.PreferredSize.Width;
            int w2 = _rbAll.PreferredSize.Width;
            int w3 = _rbTop.PreferredSize.Width;
            int gap = S(22);
            int total = w1 + gap + w2 + gap + w3;
            int x0 = Math.Max(margin, (w - total) / 2);
            int y = _rbSingle.Top;
            _rbSingle.Location = new Point(x0, y);
            _rbAll.Location    = new Point(x0 + w1 + gap, y);
            _rbTop.Location    = new Point(x0 + w1 + gap + w2 + gap, y);
        }

        // Centre the "Filter:" label + box as one unit.
        private void LayoutFilter()
        {
            if (_filterPanel == null) return;
            int labW = _filterLabel.PreferredSize.Width;
            int total = labW + S(6) + _filterBox.Width;
            int x0 = Math.Max(S(14), (_filterPanel.Width - total) / 2);
            _filterLabel.Location = new Point(x0, _filterLabel.Top);
            _filterBox.Location   = new Point(x0 + labW + S(6), _filterBox.Top);
        }

        // Right-align the buttons and bound the count label so its text can never
        // run under them at any width or DPI.
        private void LayoutBottom()
        {
            if (_bottom == null) return;
            _btnClose.Location  = new Point(_bottom.Width - _btnClose.Width - S(14), S(9));
            _btnExport.Location = new Point(_btnClose.Left - _btnExport.Width - S(8), S(9));
            if (_countLabel != null)
                _countLabel.Width = Math.Max(S(40),
                    _btnExport.Left - _countLabel.Left - S(10));
        }

        private void AddCol(string header, float weight, bool centre)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = weight * 100f,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            };
            if (centre)
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(col);
        }

        // Switch mode. The All/Top walks are computed once (one disk pass each)
        // on first request and cached.
        private void SetMode(Mode mode)
        {
            if (mode == _mode && _parents != null) return;
            _mode = mode;
            if (mode == Mode.All && _allLevels == null)
                _allLevels = ComputeWithWait(() => VaultManager.GetWhereUsedTree(_filePath));
            else if (mode == Mode.Top && _topLevel == null)
                _topLevel = ComputeWithWait(() => VaultManager.GetWhereUsedTopLevel(_filePath));
            _parents = mode == Mode.All ? _allLevels
                     : mode == Mode.Top ? _topLevel
                     : _single;
            UpdateSubtitle();
            LoadRows();
        }

        private List<WhereUsedEntry> ComputeWithWait(Func<List<WhereUsedEntry>> f)
        {
            var old = this.Cursor;
            this.Cursor = Cursors.WaitCursor;
            try { return f() ?? new List<WhereUsedEntry>(); }
            catch { return new List<WhereUsedEntry>(); }
            finally { this.Cursor = old; }
        }

        private void UpdateSubtitle()
        {
            if (_subtitle == null) return;
            switch (_mode)
            {
                case Mode.All:
                    _subtitle.Text = "Full usage chain — direct parents and the assemblies that contain them:";
                    break;
                case Mode.Top:
                    _subtitle.Text = "Top-level assemblies that ultimately contain this file:";
                    break;
                default:
                    _subtitle.Text = "Assemblies that directly reference this file:";
                    break;
            }
        }

        private void LoadRows()
        {
            string filter = (_filterBox?.Text ?? "").Trim();
            _grid.Rows.Clear();
            _displayed.Clear();

            foreach (var p in _parents)
            {
                if (filter.Length > 0 && !Matches(p, filter)) continue;
                string nm = Path.GetFileNameWithoutExtension(p.Name ?? "");
                if (_mode == Mode.All && p.Level > 0)
                    nm = new string(' ', p.Level * 4) + "↳ " + nm;
                string qty = p.Qty.HasValue ? p.Qty.Value.ToString() : "—";
                _grid.Rows.Add(nm, p.PartNo, p.Revision, qty, p.Status);
                _displayed.Add(p);
            }

            UpdateCount(_parents.Count, _displayed.Count, filter.Length > 0);
            if (_btnExport != null) _btnExport.Enabled = _displayed.Count > 0;
            LayoutBottom(); // re-bound the label width to the current panel width
        }

        private static bool Matches(WhereUsedEntry p, string term)
        {
            term = term.ToLowerInvariant();
            return (Path.GetFileNameWithoutExtension(p.Name ?? "")).ToLowerInvariant().Contains(term)
                || (p.PartNo   ?? "").ToLowerInvariant().Contains(term)
                || (p.Revision ?? "").ToLowerInvariant().Contains(term)
                || (p.Status   ?? "").ToLowerInvariant().Contains(term);
        }

        private void UpdateCount(int total, int shown, bool filtered)
        {
            string baseText;
            if (total == 0)
            {
                baseText = _mode == Mode.Top
                    ? "Not contained in any tracked top-level assembly."
                    : "Not used by any tracked assembly.";
                _countLabel.Text = baseText;
                return;
            }
            switch (_mode)
            {
                case Mode.All:
                    int direct = _parents.Count(p => p.Level == 0);
                    baseText = "Used by " + total + " reference" + (total == 1 ? "" : "s") +
                        "  ·  " + direct + " direct.";
                    break;
                case Mode.Top:
                    baseText = "Contained in " + total + " top-level assembl" +
                        (total == 1 ? "y." : "ies.");
                    break;
                default:
                    baseText = total == 1
                        ? "Used by 1 assembly."
                        : "Used by " + total + " assemblies.";
                    break;
            }
            _countLabel.Text = filtered
                ? "Showing " + shown + " of " + total + "  —  " + baseText
                : baseText;
        }

        // Double-click a row → open that assembly (deferred via the dashboard so
        // it opens after this nested modal closes).
        private void Grid_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _displayed.Count) return;
            string path = _displayed[e.RowIndex].Path;
            if (string.IsNullOrEmpty(path)) return;
            FileToOpen = path;
            this.Close();
        }

        private void Grid_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0 || hit.RowIndex >= _displayed.Count) return;
            _grid.ClearSelection();
            _grid.Rows[hit.RowIndex].Selected = true;
            _menuEntry = _displayed[hit.RowIndex];
            _rowMenu.Show(_grid, e.Location);
        }

        // "Where Used from here": climb further up by opening a nested Where Used
        // on the clicked parent. If the user opens a file from THERE, bubble it
        // up (set our FileToOpen + close) so it cascades to the dashboard.
        private void WhereUsedFromHere(WhereUsedEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path)) return;
            try
            {
                string toOpen = null;
                using (var v = new WhereUsedForm(entry.Path, entry.Name))
                {
                    v.ShowDialog(this);
                    toOpen = v.FileToOpen;
                }
                if (!string.IsNullOrEmpty(toOpen)) { FileToOpen = toOpen; this.Close(); }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Where Used:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyPath(WhereUsedEntry entry)
        {
            try
            {
                if (entry != null && !string.IsNullOrEmpty(entry.Path))
                    Clipboard.SetText(entry.Path);
            }
            catch { /* clipboard busy — swallow */ }
        }

        private void OpenContainingFolder(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe",
                        "/select,\"" + path + "\"");
                    return;
                }
                string dir = Path.GetDirectoryName(path ?? "");
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    MessageBox.Show("Folder not found:\n" + path, "BCore PDM",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open folder:\n" + ex.Message, "BCore PDM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

            // Mute the Assembly name of deeper tree levels so direct parents stand
            // out (all-levels mode only).
            if (col == "Assembly" && _mode == Mode.All && e.RowIndex < _displayed.Count
                && _displayed[e.RowIndex].Level > 0)
                e.CellStyle.ForeColor = cSubText;
        }

        private void ExportCsv()
        {
            if (_displayed.Count == 0) return;
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
                    string modeName = _mode == Mode.All ? "All levels"
                                    : _mode == Mode.Top ? "Top level only"
                                    : "Single level";
                    var sb = new StringBuilder();
                    sb.AppendLine("File,Path,Mode");
                    sb.AppendLine(Csv(_fileName) + "," + Csv(_filePath) + "," + Csv(modeName));
                    sb.AppendLine();
                    sb.AppendLine("Level,Assembly,PartNo,Revision,Qty,Status,Path");
                    foreach (var p in _displayed)
                        sb.AppendLine(string.Join(",", new[]
                        {
                            Csv((p.Level + 1).ToString()),
                            Csv(Path.GetFileNameWithoutExtension(p.Name ?? "")),
                            Csv(p.PartNo), Csv(p.Revision),
                            Csv(p.Qty.HasValue ? p.Qty.Value.ToString() : ""),
                            Csv(p.Status), Csv(p.Path)
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
