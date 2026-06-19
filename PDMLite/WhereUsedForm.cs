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

        // The SUBJECT of the where-used query. NOT readonly: the "Find" box lets
        // the user search the vault and switch the subject in-place (the task-pane
        // entry point opens the form seeded with the active file, but any tracked
        // part/assembly can be loaded without reopening the form). A multi-config
        // part is ONE file; _subjectConfig / _subjectPartNo identify WHICH config
        // the query is about (config name == Part No by convention) so the header,
        // the Find autofill and a picked drawing all reflect that config — not the
        // file's primary/active-at-save config.
        private string _filePath;
        private string _fileName;
        private string _subjectConfig;   // configuration this where-used is about (null = file-level)
        private string _subjectPartNo;   // Part No shown in the header + Find box

        // EM_SETCUEBANNER — grey placeholder in a single-line TextBox (no
        // PlaceholderText on .NET Framework 4.8). Mirrors the other BCore forms.
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            else
                box.HandleCreated += (s, e) =>
                    SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        }

        private List<WhereUsedEntry> _single = new List<WhereUsedEntry>();
        private List<WhereUsedEntry> _allLevels;     // null until first needed
        private List<WhereUsedEntry> _topLevel;      // null until first needed
        private List<WhereUsedEntry> _parents;       // the active mode's list
        private readonly List<WhereUsedEntry> _displayed = new List<WhereUsedEntry>();
        private Mode _mode = Mode.Single;

        // Set on open/double-click; the dashboard opens it after this modal closes.
        public string FileToOpen { get; private set; }

        private Panel _top, _findPanel, _filterPanel, _bottom;
        private Label _fileLabel, _subtitle, _hint, _countLabel, _filterLabel, _findLabel;
        private RadioButton _rbSingle, _rbAll, _rbTop;
        private TextBox _filterBox, _findBox;
        private DataGridView _grid;
        private ContextMenuStrip _rowMenu;
        private WhereUsedEntry _menuEntry;       // the right-clicked row
        private Button _btnExport, _btnExportAll, _btnClose;

        // FIND box (search the vault to switch the subject). The results drop down
        // is a ListBox parented to the FORM (an autocomplete overlay above the
        // grid), shown on a debounced search and dismissed on commit / Esc / click
        // away. _findHits is the parallel FindHit list behind the rendered items.
        private ListBox _findResults;
        private List<FindHit> _findHits = new List<FindHit>();
        private Timer _findTimer;
        private bool _suppressFind;              // set while seeding the box text

        // One Find dropdown row. A multi-config part expands to one hit PER
        // matching configuration (config name == Part No), so the user picks the
        // exact config — not the file's primary PN. Path/Name/Config identify the
        // subject; Display is the rendered text.
        private class FindHit
        {
            public string Path;     // model (or orphan-drawing) path
            public string Name;     // file name
            public string Config;   // configuration (null = file-level / single config)
            public string PartNo;   // the config's Part No
            public string Display;  // dropdown text
        }

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

        public WhereUsedForm(string filePath, string fileName,
            string subjectPartNo = null, string subjectConfig = null)
        {
            _filePath = filePath;
            _fileName = fileName;
            _subjectPartNo = subjectPartNo;
            _subjectConfig = subjectConfig;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fSub      = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            _fMeta     = new Font("Segoe UI", 3.5f * _scale);
            _fHint     = new Font("Segoe UI", 3.2f * _scale, FontStyle.Italic);
            _fBtn      = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fGrid     = new Font("Segoe UI", 3.5f * _scale);
            _fGridHead = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            // Resolve the subject Part No (config-specific) from the DB if the
            // caller didn't supply one, so the header + Find autofill are right
            // before the UI is built.
            EnsureSubjectPartNo();

            // Default mode (Single Level) is cheap — one walk; the All/Top walks
            // are deferred until the user asks for them.
            try { _single = VaultManager.GetWhereUsed(_filePath); }
            catch { _single = new List<WhereUsedEntry>(); }
            _parents = _single;

            BuildUI();
            UpdateSubtitle();
            LoadRows();
            SeedFindBox();   // autofill the Find box with the subject's Part No
        }

        private void BuildUI()
        {
            this.Text = "BCore PDM — Where Used";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.KeyPreview = true;
            // Height accommodates the Find row added above the Filter row so the
            // grid keeps its ~14 visible rows.
            this.ClientSize = new Size(S(560), S(552));
            this.MinimumSize = new Size(S(470), S(396));

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
                Text = SubjectLabelText(),
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

            // ── Find row (search the vault → switch the SUBJECT) ─────────────
            // Distinct from Filter below: Find CHANGES which part/assembly's
            // where-used is shown; Filter narrows the rows already shown.
            _findPanel = new Panel { Dock = DockStyle.Top, Height = S(34), BackColor = cBg };
            _findLabel = new Label
            {
                Text = "Find:", Font = _fMeta, ForeColor = cTextDark,
                AutoSize = true, Location = new Point(S(14), S(9))
            };
            _findBox = new TextBox
            {
                Font = _fMeta, Width = S(260), Location = new Point(S(60), S(6))
            };
            SetCueBanner(_findBox, "Part no, description or file name…");
            _findTimer = new Timer { Interval = 400 };
            _findTimer.Tick += (s, e) => { _findTimer.Stop(); RunFind(); };
            _findBox.TextChanged += (s, e) =>
            {
                if (_suppressFind) return;
                _findTimer.Stop();
                if ((_findBox.Text ?? "").Trim().Length >= 2) _findTimer.Start();
                else HideFindResults();
            };
            _findBox.KeyDown += FindBox_KeyDown;
            _findPanel.Controls.Add(_findLabel);
            _findPanel.Controls.Add(_findBox);
            _findPanel.Resize += (s, e) => LayoutFind();

            // The autocomplete overlay — parented to the FORM (so it floats above
            // the grid), hidden until a search returns hits.
            _findResults = new ListBox
            {
                Font = _fMeta, Visible = false, IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            // Commit only on a click that lands on a real item (IndexFromPoint
            // guards against a stray click on the scrollbar committing the
            // currently-selected row).
            _findResults.MouseClick += (s, e) =>
            {
                int i = _findResults.IndexFromPoint(e.Location);
                if (i >= 0) { _findResults.SelectedIndex = i; CommitSelectedFind(); }
            };
            _findResults.KeyDown += FindResults_KeyDown;

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
            _btnExportAll = MakeButton("Export All Levels", cBrandDark, Color.White);
            _btnExportAll.Width = S(130);
            _btnExportAll.Click += (s, e) => ExportAllLevels();
            // Used nowhere at the direct level ⇒ nothing at any level ⇒ nothing to
            // export. Independent of the active mode/filter (this dumps all three).
            _btnExportAll.Enabled = _single != null && _single.Count > 0;
            _bottom.Controls.Add(_btnExportAll);
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
            // header docks outermost (house z-order convention). For same-edge Top
            // docking the LAST added sits closest to the edge, so the visual order
            // top→down is: headerBar · _top · _findPanel · _filterPanel · grid.
            this.Controls.Add(_grid);
            this.Controls.Add(_bottom);
            this.Controls.Add(_filterPanel);
            this.Controls.Add(_findPanel);
            this.Controls.Add(_top);
            this.Controls.Add(headerBar);
            // Float the autocomplete overlay above the grid (positioned on demand).
            this.Controls.Add(_findResults);
            _findResults.BringToFront();

            LayoutTop();
            LayoutFind();
            LayoutFilter();
            LayoutBottom();

            this.FormClosed += (s, e) =>
            {
                _findTimer?.Stop(); _findTimer?.Dispose();
                _rowMenu?.Dispose();
                _fHeader?.Dispose(); _fSub?.Dispose(); _fMeta?.Dispose();
                _fHint?.Dispose(); _fBtn?.Dispose(); _fGrid?.Dispose();
                _fGridHead?.Dispose();
            };
            // Esc dismisses the Find dropdown first (if up), only then closes.
            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                if (_findResults != null && _findResults.Visible)
                { HideFindResults(); e.Handled = true; return; }
                this.Close();
            };
            // The overlay floats over the grid — dismiss it when the window is
            // resized/moved or loses focus so it can never linger out of place.
            this.Resize += (s, e) => HideFindResults();
            this.Deactivate += (s, e) => HideFindResults();
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

        // Centre the "Find:" label + box as one unit (matches the Filter row).
        private void LayoutFind()
        {
            if (_findPanel == null) return;
            int labW = _findLabel.PreferredSize.Width;
            int total = labW + S(6) + _findBox.Width;
            int x0 = Math.Max(S(14), (_findPanel.Width - total) / 2);
            _findLabel.Location = new Point(x0, _findLabel.Top);
            _findBox.Location   = new Point(x0 + labW + S(6), _findBox.Top);
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
            _btnClose.Location     = new Point(_bottom.Width - _btnClose.Width - S(14), S(9));
            _btnExport.Location    = new Point(_btnClose.Left - _btnExport.Width - S(8), S(9));
            _btnExportAll.Location = new Point(_btnExport.Left - _btnExportAll.Width - S(8), S(9));
            if (_countLabel != null)
                _countLabel.Width = Math.Max(S(40),
                    _btnExportAll.Left - _countLabel.Left - S(10));
        }

        // The subject shown in the header: filename · Part No (or a prompt when no
        // subject is loaded yet — the task-pane entry point can open empty).
        private string SubjectLabelText()
        {
            if (string.IsNullOrEmpty(_fileName) && string.IsNullOrEmpty(_filePath))
                return "(no file — use Find to choose a part or assembly)";
            string nm = !string.IsNullOrEmpty(_fileName)
                ? _fileName : Path.GetFileName(_filePath);
            return string.IsNullOrEmpty(_subjectPartNo)
                ? nm : nm + "   ·   " + _subjectPartNo;
        }

        // Resolve the subject's Part No (config-specific) when the caller didn't
        // supply one: the matching config's Pn from the DB record, else the
        // record's primary PN, else the config name itself (config == PN), else
        // left blank. Cached in _subjectPartNo so the header + seed agree.
        private void EnsureSubjectPartNo()
        {
            if (!string.IsNullOrEmpty(_subjectPartNo)) return;
            try
            {
                if (!string.IsNullOrEmpty(_filePath))
                {
                    var rec = DatabaseManager.GetFileRecord(_filePath);
                    if (rec != null)
                    {
                        if (!string.IsNullOrEmpty(_subjectConfig) &&
                            rec.Configurations != null)
                            foreach (var c in rec.Configurations)
                                if (string.Equals(c.Name, _subjectConfig,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrEmpty(c.PartNo))
                                { _subjectPartNo = c.PartNo; return; }
                        if (!string.IsNullOrEmpty(rec.PartNumber))
                        { _subjectPartNo = rec.PartNumber; return; }
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(_subjectPartNo) &&
                !string.IsNullOrEmpty(_subjectConfig))
                _subjectPartNo = _subjectConfig;   // config name == Part No
        }

        // Switch the SUBJECT of the where-used query in place (Find committed a new
        // file/config). Recompute the cheap single-level walk now and invalidate
        // the cached All/Top walks; reset the view to Single Level.
        private void SetSubject(string path, string name, string config, string partNo)
        {
            _filePath = path;
            _fileName = name;
            _subjectConfig = config;
            _subjectPartNo = partNo;
            _allLevels = null;
            _topLevel = null;
            EnsureSubjectPartNo();
            try { _single = VaultManager.GetWhereUsed(_filePath); }
            catch { _single = new List<WhereUsedEntry>(); }
            _mode = Mode.Single;
            _parents = _single;

            if (_fileLabel != null) _fileLabel.Text = SubjectLabelText();
            if (_btnExportAll != null)
                _btnExportAll.Enabled = _single != null && _single.Count > 0;
            // Keep the radios honest (the CheckedChanged → SetMode is a no-op here
            // because _mode/_parents are already Single).
            if (_rbSingle != null && !_rbSingle.Checked) _rbSingle.Checked = true;

            UpdateSubtitle();
            LoadRows();
        }

        // Autofill the Find box with the subject's (config-specific) Part No — the
        // design decision: Part No over file name; file name only as a last resort.
        private void SeedFindBox()
        {
            if (_findBox == null) return;
            EnsureSubjectPartNo();
            string seed = _subjectPartNo ?? "";
            if (string.IsNullOrEmpty(seed) && !string.IsNullOrEmpty(_fileName))
                seed = Path.GetFileNameWithoutExtension(_fileName);

            _suppressFind = true;        // seeding must not trigger a search
            _findBox.Text = seed ?? "";
            try { _findBox.SelectionStart = _findBox.Text.Length; } catch { }
            _suppressFind = false;
        }

        // Debounced vault search → fill the overlay. A multi-config part expands to
        // one row PER matching configuration (config name == Part No), so the user
        // picks the exact config rather than the file's primary PN. Silent on a
        // vault outage, like the task-pane search (this runs on a timer tick).
        private void RunFind()
        {
            string term = (_findBox.Text ?? "").Trim();
            if (term.Length < 2) { HideFindResults(); return; }

            List<VaultFile> results;
            try { results = DatabaseManager.SearchFiles(term); }
            catch { HideFindResults(); return; }
            _findHits = BuildFindHits(results ?? new List<VaultFile>(),
                term.ToLowerInvariant());

            _findResults.BeginUpdate();
            _findResults.Items.Clear();
            foreach (var h in _findHits) _findResults.Items.Add(h.Display);
            if (_findHits.Count == 0)
                _findResults.Items.Add("(no matches for \"" + term + "\")");
            _findResults.EndUpdate();

            PositionFindResults();
            if (_findHits.Count > 0) _findResults.SelectedIndex = 0;
            _findResults.Visible = true;
            _findResults.BringToFront();
        }

        // Expand the flat search results into per-config Find rows (mirrors the
        // task-pane search cards). A drawing maps back to its model and is folded
        // into that model's config rows (skipped if the model also matched); a true
        // orphan drawing (no model) gets its own row.
        private readonly HashSet<string> _findExpanded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private List<FindHit> BuildFindHits(List<VaultFile> results, string termL)
        {
            var hits = new List<FindHit>();
            _findExpanded.Clear();   // dedupe model expansion within one search
            var modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in results)
            {
                string ext = (Path.GetExtension(f.FilePath) ?? "").ToLowerInvariant();
                if (ext == ".sldprt" || ext == ".sldasm") modelPaths.Add(f.FilePath);
            }
            foreach (var f in results)
            {
                string ext = (Path.GetExtension(f.FilePath) ?? "").ToLowerInvariant();
                if (ext == ".slddrw")
                {
                    VaultFile model = null;
                    try { model = DatabaseManager.GetModelForDrawing(f.FilePath); }
                    catch { }
                    if (model != null && modelPaths.Contains(model.FilePath))
                        continue;                       // folded under the model below
                    if (model != null)
                        AddModelHits(hits, model, termL);
                    else
                        hits.Add(new FindHit
                        {
                            Path = f.FilePath, Name = f.FileName, Config = null,
                            PartNo = "",
                            Display = Path.GetFileNameWithoutExtension(f.FileName ?? "")
                                + "   (" + (f.FileName ?? "") + ")"
                        });
                }
                else
                {
                    AddModelHits(hits, f, termL);
                }
            }
            return hits;
        }

        private void AddModelHits(List<FindHit> hits, VaultFile model, string termL)
        {
            // Expand each model at most once per search (two matching drawings can
            // both resolve to the same model).
            if (!string.IsNullOrEmpty(model.FilePath) &&
                !_findExpanded.Add(model.FilePath)) return;

            string fileName = string.IsNullOrEmpty(model.FileName)
                ? Path.GetFileName(model.FilePath) : model.FileName;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            bool nameMatched = baseName.ToLowerInvariant().Contains(termL);

            var configs = model.Configurations;
            if (configs == null || configs.Count == 0)
                configs = new List<ConfigEntry> { new ConfigEntry {
                    Name = model.PartNumber, PartNo = model.PartNumber,
                    Description = model.Description } };

            int total = configs.Count;
            var shown = new List<ConfigEntry>();
            foreach (var c in configs)
            {
                bool match = total <= 1 || nameMatched
                          || (c.PartNo ?? "").ToLowerInvariant().Contains(termL)
                          || (c.Description ?? "").ToLowerInvariant().Contains(termL);
                if (match) shown.Add(c);
            }
            if (shown.Count == 0) shown = configs;   // never drop a matched file

            foreach (var c in shown)
            {
                string pn = string.IsNullOrEmpty(c.PartNo) ? model.PartNumber : c.PartNo;
                string desc = string.IsNullOrEmpty(c.Description)
                    ? model.Description : c.Description;
                hits.Add(new FindHit
                {
                    Path = model.FilePath, Name = fileName, Config = c.Name,
                    PartNo = pn,
                    Display = (string.IsNullOrEmpty(pn) ? baseName : pn)
                        + (string.IsNullOrEmpty(desc) ? "" : "  —  " + desc)
                        + "   (" + fileName + ")"
                });
            }
        }

        // Anchor the overlay just under the Find box (PointToClient handles the
        // docked-panel offsets robustly at any DPI / window size).
        private void PositionFindResults()
        {
            if (_findBox == null || _findResults == null) return;
            Point p = this.PointToClient(_findBox.PointToScreen(Point.Empty));
            int rows = Math.Min(Math.Max(_findResults.Items.Count, 1), 7);
            int h = rows * _findResults.ItemHeight + S(4);
            _findResults.Bounds = new Rectangle(
                p.X, p.Y + _findBox.Height + S(1), _findBox.Width, h);
        }

        private void HideFindResults()
        {
            if (_findResults != null) _findResults.Visible = false;
        }

        private void CommitSelectedFind()
        {
            int i = _findResults.SelectedIndex;
            if (i < 0 || i >= _findHits.Count) return;   // "(no matches)" / none
            CommitFind(_findHits[i]);
        }

        // Load the chosen Find row as the new subject. Model/config hits set the
        // subject directly (BuildFindHits already resolved drawings to their model
        // + config). A bare drawing row is a TRUE orphan (no tracked model) — a
        // drawing isn't a component, so there's no where-used to show.
        private void CommitFind(FindHit h)
        {
            HideFindResults();
            if (h == null || string.IsNullOrEmpty(h.Path)) return;

            string ext = (Path.GetExtension(h.Path) ?? "").ToLowerInvariant();
            if (ext == ".slddrw")
            {
                MessageBox.Show(
                    "A drawing has no \"where used\" — it isn't a component.\n\n" +
                    "No tracked model was found for it; pick the part or " +
                    "assembly instead.",
                    "BCore PDM — Where Used",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetSubject(h.Path, h.Name, h.Config, h.PartNo);
            SeedFindBox();   // reflect the loaded subject's (config) Part No
        }

        private void FindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && _findResults != null
                && _findResults.Visible && _findResults.Items.Count > 0)
            {
                _findResults.Focus();
                if (_findResults.SelectedIndex < 0) _findResults.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                _findTimer.Stop();
                if (_findResults != null && _findResults.Visible) CommitSelectedFind();
                else RunFind();
                e.Handled = true;
                e.SuppressKeyPress = true;   // no Windows ding
            }
        }

        private void FindResults_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitSelectedFind();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideFindResults();
                try { _findBox.Focus(); } catch { }
                e.Handled = true;
            }
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
                if (string.IsNullOrEmpty(_filePath))
                    baseText = "Use Find above to choose a part or assembly.";
                else
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
            HideFindResults();   // any click on the grid dismisses the Find overlay
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
                using (var v = new WhereUsedForm(entry.Path, entry.Name, entry.PartNo))
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

        // Export ALL THREE modes into one .xlsx (a sheet each), like the baseline
        // viewer's "Export All Revs". Full data, UNFILTERED (the per-mode Export
        // CSV covers the current filtered view). Computes the All/Top walks if
        // they haven't been viewed yet. xlsx cells are inline strings, so no CSV
        // formula-injection guard is needed (Excel never executes them).
        private void ExportAllLevels()
        {
            var all = _allLevels ?? (_allLevels =
                ComputeWithWait(() => VaultManager.GetWhereUsedTree(_filePath)));
            var top = _topLevel ?? (_topLevel =
                ComputeWithWait(() => VaultManager.GetWhereUsedTopLevel(_filePath)));

            using (var sfd = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(_fileName ?? "file"))
                    + "_WHERE-USED.xlsx"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sheets = new List<XlsxWriter.Sheet>
                    {
                        BuildSheet("Single Level", _single),
                        BuildSheet("All Levels",   all),
                        BuildSheet("Top Level",    top)
                    };
                    XlsxWriter.Write(sfd.FileName, sheets);
                    MessageBox.Show(
                        "Where-used exported (Single / All / Top Level sheets) to:\n"
                        + sfd.FileName,
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

        private XlsxWriter.Sheet BuildSheet(string tab, List<WhereUsedEntry> rows)
        {
            var sh = new XlsxWriter.Sheet(tab);
            sh.Add("File", _fileName ?? "");
            sh.Add("Path", _filePath ?? "");
            sh.AddBlank();
            sh.Add("Level", "Assembly", "Part No", "Revision", "Qty", "Status", "Path");
            if (rows != null)
                foreach (var p in rows)
                    sh.Add(
                        (p.Level + 1).ToString(),
                        Path.GetFileNameWithoutExtension(p.Name ?? ""),
                        p.PartNo ?? "", p.Revision ?? "",
                        p.Qty.HasValue ? p.Qty.Value.ToString() : "",
                        p.Status ?? "", p.Path ?? "");
            if (rows == null || rows.Count == 0)
                sh.Add("(none)");
            return sh;
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
