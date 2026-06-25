using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // ADVANCED (property-wide) search popup. Opened by double-clicking the
    // "SEARCH FILES" header in the task pane. The quick task-pane box stays a
    // clean PartNo/Description/FileName quick-find (so typing "ste" never drags
    // in every STEEL part); property search lives HERE behind its own surface.
    //
    // A main search box (same surface as the quick search) plus four refinement
    // controls — Drawn By (text), Material (dropdown), Finish (dropdown) and Part
    // Type (Manufactured|Purchased). Whatever the user fills is AND-combined and
    // matched PER CONFIGURATION by DatabaseManager.SearchFilesAdvanced, so a
    // multi-config part shows only the configs that actually match (e.g. only its
    // STEEL config), with a real per-config Part No on each card. Live search via
    // a debounce timer on every field; results render as the same per-config cards
    // as the task-pane search (status bar, name, PN+REV, description, Open PRT/ASM
    // + Open DRW). Self-contained (own palette / S() / fonts) per the one-form-
    // one-file convention; never writes the vault.
    //
    // Opening: a card button sets FileToOpen / FileToOpenConfig (and OpenDrawing
    // for the create-a-drawing path) and closes; the task-pane caller opens it
    // deferred — mirroring the Vault Dashboard / Where Used deferred-open pattern.
    public class AdvancedSearchForm : Form
    {
        // Result carried out of the modal (the caller opens after it closes).
        public string FileToOpen       { get; private set; }
        public string FileToOpenConfig { get; private set; }
        public bool   OpenDrawing      { get; private set; } // open/create the drawing for FileToOpen+config

        private const string Sentinel  = "-- Select --"; // PropertyForm's list sentinel
        private const string AnyOption = "— Any —";       // our "don't filter on this"
        private const int    MaxCards  = 50;

        private TextBox  _mainBox, _drawnByBox;
        private ComboBox _materialBox, _finishBox, _partTypeBox, _statusBox, _fileTypeBox;
        private Panel    _resultsPanel;
        private Label    _countLabel;
        private Button   _btnExport;
        private Timer    _timer;
        private ContextMenuStrip _cardMenu;   // shared right-click menu for result cards
        private Card     _menuCard;           // the card the menu was opened on

        // EM_SETCUEBANNER — grey placeholder for a single-line TextBox (no
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

        // Grey placeholder for an EDITABLE ComboBox (CB_SETCUEBANNER — the combo
        // equivalent of EM_SETCUEBANNER; shows when the edit portion is empty and
        // unfocused). Only works on a DropDown (editable) combo, not DropDownList.
        private static void SetComboCue(ComboBox cb, string text)
        {
            const int CB_SETCUEBANNER = 0x1703;
            if (cb.IsHandleCreated)
                SendMessage(cb.Handle, CB_SETCUEBANNER, IntPtr.Zero, text);
            else
                cb.HandleCreated += (s, e) =>
                    SendMessage(cb.Handle, CB_SETCUEBANNER, IntPtr.Zero, text);
        }

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        private static readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private static readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private static readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private static readonly Color cCard      = Color.White;
        private static readonly Color cBorder    = Color.FromArgb(220, 225, 232);
        private static readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private static readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private static readonly Color cTextLight = Color.FromArgb(155, 163, 175);
        private static readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private static readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private static readonly Color cMaroon    = Color.FromArgb(140, 60, 60);
        private static readonly Color cObsolete  = Color.FromArgb(120, 120, 120);
        private static readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private Font _fHeader, _fSection, _fLabel, _fInput, _fHint;
        private Font _fCardBold, _fCardPn, _fCardDesc, _fCardBtn, _fBar;
        private StringFormat _sfBarCenter;
        private Pen _penBorder; // 1px card outline (shared — no per-paint alloc)

        // Card thumbnails: extracted SOLIDWORKS preview bitmaps keyed by
        // "filePath|config", loaded LAZILY on a card's first paint (so a 50-card
        // search never fires 50 preview reads up front) and CACHED. The Images
        // are SHARED (a ThumbPanel never owns its image), capped to bound GDI
        // handles, and disposed in Dispose(bool). A null value means "tried, no
        // preview" so a preview-less file is read at most once. Mirrors the
        // task-pane search cards (own copy per the one-form-one-file convention).
        private readonly Dictionary<string, Image> _thumbCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private const int ThumbCacheCap = 400;

        // Thumbnails load via a THROTTLED, deferred queue — never inside a paint
        // (GetPreviewBitmap is a network COM call that pumps the STA loop and froze
        // the popup per card). Panels request on first paint; a Timer (_thumbTimer)
        // loads ONE per tick off the message loop so cards/recents render first;
        // suspended during the large preview.
        private readonly Queue<ThumbPanel> _thumbQueue = new Queue<ThumbPanel>();
        private Timer _thumbTimer;   // throttle: one preview read per tick
        private bool _thumbDraining;
        private bool _thumbLoadsSuspended;

        public AdvancedSearchForm()
        {
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fHeader   = new Font("Segoe UI", 6f   * _scale, FontStyle.Bold);
            _fSection  = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);
            _fLabel    = new Font("Segoe UI", 3.7f * _scale);
            _fInput    = new Font("Segoe UI", 3.7f * _scale);
            _fHint     = new Font("Segoe UI", 3.2f * _scale, FontStyle.Italic);
            _fCardBold = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            _fCardPn   = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);
            _fCardDesc = new Font("Segoe UI", 3.3f * _scale);
            _fCardBtn  = new Font("Segoe UI", 3.4f * _scale, FontStyle.Bold);
            _fBar      = new Font("Segoe UI", 3.1f * _scale, FontStyle.Bold);
            _sfBarCenter = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            _penBorder = new Pen(cBorder);

            BuildUI();
        }

        // House convention (cf. WhereUsedForm/PendingRequestsForm): release the
        // timer + fonts in Dispose(bool), NOT FormClosed (which never fires for a
        // form disposed without being shown). The single caller ShowDialogs inside
        // a using, so this runs reliably.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop(); _timer?.Dispose();
                _thumbTimer?.Stop(); _thumbTimer?.Dispose();
                _fHeader?.Dispose(); _fSection?.Dispose(); _fLabel?.Dispose();
                _fInput?.Dispose(); _fHint?.Dispose();
                _fCardBold?.Dispose(); _fCardPn?.Dispose(); _fCardDesc?.Dispose();
                _fCardBtn?.Dispose(); _fBar?.Dispose();
                _sfBarCenter?.Dispose();
                _penBorder?.Dispose();
                _cardMenu?.Dispose();
                foreach (var img in _thumbCache.Values)
                    try { img?.Dispose(); } catch { }
                _thumbCache.Clear();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void BuildUI()
        {
            this.Text = "BCore PDM — Advanced Search";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            this.KeyPreview = true;
            // Height = header(30) + filters(206) + bottom(42) + a results area
            // sized to EXACTLY 4 card rows of the taller card (no 5th row
            // peeking): 4*cardH(98) + 3*rowGap(6) + top pad(6) = 416, viewport
            // ~420 hides row 5 cleanly (cards grew to S98 for the new layout —
            // file name + description on full-width rows, preview beside PN/REV).
            this.ClientSize = new Size(S(648), S(698));

            int clientW = ClientSize.Width;
            int margin  = S(14);
            int innerW  = clientW - margin * 2;

            // ── Results panel (Fill) — added FIRST so the edge panels dock
            //    around it (house z-order convention). ───────────────────────
            _resultsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = cBg,
                AutoScroll = true
            };
            this.Controls.Add(_resultsPanel);

            // ── Bottom bar (Close) ─────────────────────────────────────────
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom, Height = S(42), BackColor = cBg
            };
            var btnClose = new Button
            {
                Text = "Close", Font = _fCardBtn,
                Width = S(90), Height = S(26),
                BackColor = cDark, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(clientW - margin - S(90), S(8))
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnClose);

            // Export CSV (left) — the shown result rows. Disabled until a search
            // returns rows.
            _btnExport = new Button
            {
                Text = "Export CSV", Font = _fCardBtn,
                Width = S(110), Height = S(26),
                BackColor = cBrand, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Location = new Point(margin, S(8)), Enabled = false
            };
            _btnExport.FlatAppearance.BorderSize = 0;
            _btnExport.Click += (s, e) => ExportCsv();
            bottom.Controls.Add(_btnExport);

            this.Controls.Add(bottom);

            // ── Filters panel (Top) — centred search + a refine grid (Drawn By
            //    + Material, Finish + Part Type, Status) ───────────────────────
            var filters = new Panel
            {
                Dock = DockStyle.Top, Height = S(206), BackColor = cBg
            };

            int y = S(8);
            filters.Controls.Add(MakeSection("SEARCH", margin, ref y, innerW));

            // Centred main search box (narrower than the form so it reads as a
            // search bar, not an edge-to-edge field on the wide popup).
            int mainW = S(396);
            _mainBox = MakeTextBox((clientW - mainW) / 2, y, mainW);
            SetCueBanner(_mainBox, "Part no, drawing no, description or file name");
            filters.Controls.Add(_mainBox);
            y += S(28);

            filters.Controls.Add(new Label
            {
                Text = "Type a term, then refine with the filters below.",
                Font = _fHint, ForeColor = cTextLight,
                Location = new Point(margin, y),
                AutoSize = false, Width = innerW, Height = S(14),
                TextAlign = ContentAlignment.MiddleCenter
            });
            y += S(18);

            filters.Controls.Add(MakeSection("REFINE", margin, ref y, innerW));

            // 2×2 grid: Drawn By + Material on row 1, Finish + Part Type on row 2
            // — half the vertical space of four stacked rows, leaving more room
            // for result cards.
            int colGap2 = S(20);
            int colW    = (innerW - colGap2) / 2;
            int labelW  = S(70);
            int c1LabelX = margin;
            int c1InputX = margin + labelW + S(6);
            int c2LabelX = margin + colW + colGap2;
            int c2InputX = c2LabelX + labelW + S(6);
            int cInputW  = colW - labelW - S(6);

            int ry1 = y;
            int ry2 = y + S(30);

            _drawnByBox = MakeTextBox(c1InputX, ry1, cInputW);
            SetCueBanner(_drawnByBox, "e.g. BC");
            AddRow(filters, "Drawn By", c1LabelX, labelW, ry1, _drawnByBox);

            // Material + Finish are TYPE-TO-NARROW combos (editable + autocomplete
            // over the list) — the material list will grow long, so typing filters
            // the dropdown suggestions; the user still picks a real list value.
            // They start BLANK with a cue-banner placeholder (like the search box),
            // no "— Any —" item — empty = no filter.
            _materialBox = MakeCombo(c2InputX, ry1, cInputW,
                BuildOptions(PropertyForm.MaterialOptions(), includeAny: false),
                typeable: true, cue: "Type or pick a material");
            AddRow(filters, "Material", c2LabelX, labelW, ry1, _materialBox);

            // Row 2: Part Type under Drawn By (left), Finish under Material (right).
            // Part Type is just two values — a plain dropdown list with "— Any —"
            // (a DropDownList has no edit box, so no cue banner / typing).
            _partTypeBox = MakeCombo(c1InputX, ry2, cInputW,
                BuildOptions(PropertyForm.PartTypeOptions(), includeAny: true),
                typeable: false);
            AddRow(filters, "Part Type", c1LabelX, labelW, ry2, _partTypeBox);

            _finishBox = MakeCombo(c2InputX, ry2, cInputW,
                BuildOptions(PropertyForm.FinishTypeOptions(), includeAny: false),
                typeable: true, cue: "Type or pick a finish");
            AddRow(filters, "Finish", c2LabelX, labelW, ry2, _finishBox);

            // Row 3: Status (lifecycle, left) + File Type (Part/Assembly, right)
            // — both plain dropdown lists with "— Any —".
            int ry3 = ry2 + S(30);
            _statusBox = MakeCombo(c1InputX, ry3, cInputW, new[]
                { AnyOption, "WIP", "Released", "Locked", "Obsolete" },
                typeable: false);
            AddRow(filters, "Status", c1LabelX, labelW, ry3, _statusBox);

            _fileTypeBox = MakeCombo(c2InputX, ry3, cInputW, new[]
                { AnyOption, "Part", "Assembly" }, typeable: false);
            AddRow(filters, "File Type", c2LabelX, labelW, ry3, _fileTypeBox);

            y = ry3 + S(30);

            _countLabel = new Label
            {
                Text = "Type a term or pick a filter to search.",
                Font = _fHint, ForeColor = cTextGray,
                Location = new Point(margin, y),
                AutoSize = false, Width = innerW, Height = S(16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            filters.Controls.Add(_countLabel);

            this.Controls.Add(filters);

            // ── Header banner (Top, added LAST so it sits above the filters) ─
            var header = new Panel
            {
                Dock = DockStyle.Top, Height = S(30), BackColor = cBrandDark
            };
            header.Controls.Add(new Label
            {
                Text = "Advanced Search", Font = _fHeader, ForeColor = Color.White,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });
            this.Controls.Add(header);

            // ── Shared right-click menu for result cards (acts on _menuCard) ──
            _cardMenu = new ContextMenuStrip();
            _cardMenu.Items.Add("Copy Part No", null, (s, e) => CardCopyPartNo());
            _cardMenu.Items.Add("Copy File Path", null, (s, e) => CardCopyPath());
            _cardMenu.Items.Add("Open Containing Folder", null,
                (s, e) => CardOpenFolder());
            _cardMenu.Items.Add(new ToolStripSeparator());
            _cardMenu.Items.Add("Where Used…", null, (s, e) => CardWhereUsed());

            // ── Live search wiring (debounced) ──────────────────────────────
            _timer = new Timer { Interval = 450 };
            // Stop FIRST so a queued WM_TIMER can't re-enter; try/catch so an
            // unexpected throw on the tick can't escape onto SOLIDWORKS' loop.
            _timer.Tick += (s, e) => { _timer.Stop(); try { RunSearch(); } catch { } };
            // Throttle preview loads: one per 60ms tick — cards/recents render
            // first, thumbnails trickle in without freezing the popup.
            _thumbTimer = new Timer { Interval = 60 };
            _thumbTimer.Tick += ThumbTick;
            _mainBox.TextChanged    += (s, e) => Schedule();
            _drawnByBox.TextChanged += (s, e) => Schedule();
            // Material/Finish are editable: TextChanged catches typing AND the
            // autocomplete pick; SelectedIndexChanged catches the dropdown arrow.
            _materialBox.SelectedIndexChanged += (s, e) => Schedule();
            _materialBox.TextChanged          += (s, e) => Schedule();
            _finishBox.SelectedIndexChanged   += (s, e) => Schedule();
            _finishBox.TextChanged            += (s, e) => Schedule();
            _statusBox.SelectedIndexChanged   += (s, e) => Schedule();
            _fileTypeBox.SelectedIndexChanged += (s, e) => Schedule();
            _partTypeBox.SelectedIndexChanged += (s, e) => Schedule();

            this.AcceptButton = null; // Enter in a box should not close the form
        }

        private void Schedule()
        {
            _timer.Stop();
            _timer.Start();
        }

        private Label MakeSection(string text, int x, ref int y, int w)
        {
            var lbl = new Label
            {
                Text = text, Font = _fSection, ForeColor = cTextGray,
                Location = new Point(x, y),
                AutoSize = false, Width = w, Height = S(16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            y += S(18);
            return lbl;
        }

        private TextBox MakeTextBox(int x, int y, int w)
        {
            return new TextBox
            {
                Font = _fInput, Width = w, Height = S(22),
                Location = new Point(x, y),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = cCard, ForeColor = cTextDark
            };
        }

        // typeable = an editable combo with autocomplete over its own list items
        // (type to narrow the dropdown, still pick a real value); it starts BLANK
        // (SelectedIndex -1) with the given cue-banner placeholder. Non-typeable
        // is a plain dropdown list defaulting to its first item ("— Any —").
        // FlatStyle is left at the default (Standard) — the old FlatStyle.Flat
        // rendered a tiny dot-sized arrow.
        private ComboBox MakeCombo(int x, int y, int w, string[] items,
            bool typeable, string cue = null)
        {
            var cb = new ComboBox
            {
                Font = _fInput, Width = w,
                Location = new Point(x, y),
                DropDownStyle = typeable
                    ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList
            };
            cb.Items.AddRange(items);
            cb.MaxDropDownItems = 16;
            if (typeable)
            {
                cb.AutoCompleteMode = AutoCompleteMode.Suggest;
                cb.AutoCompleteSource = AutoCompleteSource.ListItems;
                if (cue != null) SetComboCue(cb, cue); // blank, cue placeholder
            }
            else
            {
                cb.SelectedIndex = 0; // "— Any —"
            }
            return cb;
        }

        // Build a dropdown's items: optionally "— Any —" first (no filter), then
        // the source list with PropertyForm's "-- Select --" sentinel removed.
        private static string[] BuildOptions(string[] source, bool includeAny)
        {
            var list = new List<string>();
            if (includeAny) list.Add(AnyOption);
            foreach (var s in source)
                if (!string.Equals(s, Sentinel, StringComparison.Ordinal))
                    list.Add(s);
            return list.ToArray();
        }

        private void AddRow(Panel host, string label, int x, int labelW,
            int y, Control input)
        {
            host.Controls.Add(new Label
            {
                Text = label, Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(x, y + S(3)),
                AutoSize = false, Width = labelW, Height = S(18)
            });
            host.Controls.Add(input);
        }

        // A combo's filter value. "— Any —" or empty means no filter. Reads the
        // TEXT (not SelectedItem) so an editable combo's typed/autocomplete-picked
        // value is honoured. For an editable combo a mid-type PARTIAL ("STEE")
        // that doesn't yet match a real list value is treated as "no filter" — so
        // typing just narrows the dropdown without flashing "no results" until a
        // full value is chosen; the match itself stays exact.
        private static string ComboValue(ComboBox cb)
        {
            string t = (cb.Text ?? "").Trim();
            if (t.Length == 0 ||
                string.Equals(t, AnyOption, StringComparison.Ordinal))
                return "";
            if (cb.DropDownStyle == ComboBoxStyle.DropDown &&
                cb.FindStringExact(t) < 0)
                return ""; // partial mid-type — not a committed filter yet
            return t;
        }

        private void RunSearch()
        {
            ClearAndDispose(_resultsPanel);
            _thumbQueue.Clear(); // discard pending preview loads for the old cards
            if (_btnExport != null) _btnExport.Enabled = false;

            string main     = _mainBox.Text.Trim();
            string drawnBy  = _drawnByBox.Text.Trim();
            string material = ComboValue(_materialBox);
            string finish   = ComboValue(_finishBox);
            string partType = ComboValue(_partTypeBox);
            string status   = ComboValue(_statusBox);
            string fileType = ComboValue(_fileTypeBox);

            if (main.Length == 0 && drawnBy.Length == 0 && material.Length == 0 &&
                finish.Length == 0 && partType.Length == 0 && status.Length == 0 &&
                fileType.Length == 0)
            {
                // No term / filters → show the user's recently-opened files
                // (quick access, like Vault/PDM — moved here off the task pane).
                ShowRecentFiles();
                return;
            }

            bool truncated;
            List<VaultFile> results;
            try
            {
                results = DatabaseManager.SearchFilesAdvanced(
                    main, drawnBy, material, finish, partType, status, fileType,
                    out truncated);
            }
            catch
            {
                _countLabel.ForeColor = cMaroon;
                _countLabel.Text = "Vault unavailable — check the N: drive.";
                return;
            }

            // No matches — report and stop BEFORE the drawing-index load below,
            // so a no-results tick doesn't pay an extra full vault.xml read.
            if (results.Count == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "No matching files.";
                return;
            }

            // Drawing snapshot for the Open DRW wiring (ONE load for the search,
            // not one per card — mirrors the task-pane search).
            DatabaseManager.DrawingIndex drwIndex;
            try { drwIndex = DatabaseManager.BuildDrawingIndex(); }
            catch { drwIndex = null; }

            var cards = new List<Card>();
            foreach (var f in results)
            {
                string baseName = Path.GetFileNameWithoutExtension(
                    string.IsNullOrEmpty(f.FileName)
                        ? Path.GetFileName(f.FilePath) : f.FileName);
                string ext = (Path.GetExtension(string.IsNullOrEmpty(f.FileName)
                    ? f.FilePath : f.FileName) ?? "").ToLowerInvariant();

                // SearchFilesAdvanced already TRIMMED Configurations to the
                // matching configs, so one card per entry is exactly right.
                foreach (var c in f.Configurations)
                {
                    string drawingPath = null;
                    var drws = drwIndex?.DrawingsForConfig(f.FilePath, c.Name);
                    if (drws != null && drws.Count > 0) drawingPath = drws[0];

                    cards.Add(new Card
                    {
                        DisplayName  = baseName,
                        ModelPath    = f.FilePath,
                        ModelExt     = ext,
                        ConfigName   = c.Name,
                        // Per-config value first, then the file-level value,
                        // whitespace-safe — legacy records have empty <Config>
                        // fields (Revision had NO fallback before), so a card
                        // would show a blank PN / no rev even when file-level
                        // values exist.
                        PartNumber   = FirstNonBlank(c.PartNo, f.PartNumber),
                        Description  = FirstNonBlank(c.Description, f.Description),
                        Revision     = FirstNonBlank(c.Revision, f.Revision),
                        Status       = f.Status,
                        SupersededBy = f.SupersededBy,
                        DrawingPath  = drawingPath
                    });
                }
            }

            // Default sort: predictable order by Part No, then file name, then
            // config — beats raw vault-file order (sorted BEFORE the cap so the
            // first N are the alphabetical first N, not the first N on disk).
            cards.Sort(CompareCards);

            if (cards.Count > MaxCards)
            {
                cards = cards.GetRange(0, MaxCards);
                truncated = true;
            }

            if (cards.Count == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "No matching files.";
                return;
            }

            RenderCards(cards);
            if (_btnExport != null) _btnExport.Enabled = true;

            _countLabel.ForeColor = cTextGray;
            _countLabel.Text = truncated
                ? "Showing first " + cards.Count + " — refine to narrow results."
                : cards.Count + " result" + (cards.Count == 1 ? "" : "s") + ".";
        }

        // Empty fields → show the user's recently-opened files (RecentFiles is
        // populated by the task pane on every active saved doc). ONE DB load via
        // GetFilesByPaths; ONE card per recent FILE (its primary/first config) so
        // a multi-config recent doesn't expand into N cards. Recency order is
        // kept (no sort — recency is the point). Export stays disabled (it's for
        // an actual search).
        private void ShowRecentFiles()
        {
            List<string> paths = RecentFiles.Get();
            if (paths.Count == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "Type a term or pick a filter to search.";
                return;
            }
            List<VaultFile> recents;
            try { recents = DatabaseManager.GetFilesByPaths(paths); }
            catch
            {
                _countLabel.ForeColor = cMaroon;
                _countLabel.Text = "Vault unavailable — check the N: drive.";
                return;
            }
            if (recents.Count == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "Type a term or pick a filter to search.";
                return;
            }

            DatabaseManager.DrawingIndex drwIndex;
            try { drwIndex = DatabaseManager.BuildDrawingIndex(); }
            catch { drwIndex = null; }

            var cards = new List<Card>();
            foreach (var f in recents)
            {
                string baseName = Path.GetFileNameWithoutExtension(
                    string.IsNullOrEmpty(f.FileName)
                        ? Path.GetFileName(f.FilePath) : f.FileName);
                string ext = (Path.GetExtension(string.IsNullOrEmpty(f.FileName)
                    ? f.FilePath : f.FileName) ?? "").ToLowerInvariant();
                // One card per recent FILE — its primary (first) config.
                ConfigEntry c = (f.Configurations != null && f.Configurations.Count > 0)
                    ? f.Configurations[0] : null;
                string cfgName = c != null ? c.Name : "";
                string drawingPath = null;
                var drws = drwIndex?.DrawingsForConfig(f.FilePath, cfgName);
                if (drws != null && drws.Count > 0) drawingPath = drws[0];
                cards.Add(new Card
                {
                    DisplayName  = baseName,
                    ModelPath    = f.FilePath,
                    ModelExt     = ext,
                    ConfigName   = cfgName,
                    PartNumber   = FirstNonBlank(c != null ? c.PartNo : null,
                                                 f.PartNumber),
                    Description  = FirstNonBlank(c != null ? c.Description : null,
                                                 f.Description),
                    Revision     = FirstNonBlank(c != null ? c.Revision : null,
                                                 f.Revision),
                    Status       = f.Status,
                    SupersededBy = f.SupersededBy,
                    DrawingPath  = drawingPath
                });
            }
            if (cards.Count == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "Type a term or pick a filter to search.";
                return;
            }
            RenderCards(cards);
            _countLabel.ForeColor = cTextGray;
            _countLabel.Text = "Recently opened (" + cards.Count +
                ") — type a term or pick a filter to search.";
        }

        // On open (empty fields) show recents — RunSearch routes the all-empty
        // case to ShowRecentFiles. Done in OnShown (not the ctor) so the results
        // panel is laid out at its real width before the cards tile.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Show the popup INSTANTLY, then load recents — the recents lookup is
            // a couple of network vault reads, and running it synchronously here
            // froze the window while it opened. Give immediate feedback, then defer
            // the work onto the message loop so the form paints first.
            _countLabel.ForeColor = cTextGray;
            _countLabel.Text = "Loading recent files…";
            try { BeginInvoke((Action)(() => { try { RunSearch(); } catch { } })); }
            catch { try { RunSearch(); } catch { } }
        }

        private void RenderCards(List<Card> cards)
        {
            // THREE-COLUMN grid: full-width cards looked stretched and wasted
            // space, so cards are ~task-pane width and tiled left-to-right,
            // top-to-bottom — each a white tile with a 1px border, separated by
            // gaps, so three sit per row with clear distinction and many fit
            // on screen (the form is sized for 5 rows × 3 = 15 cards).
            //
            // Absolutely-positioned children ignore a panel's Padding, so indent
            // with explicit margins. Reserve the vertical scrollbar width up front:
            // AutoScroll adds the bar AFTER tall content lands, shrinking ClientSize
            // — sizing to the pre-bar width would then trigger a horizontal bar too.
            const int cols = 3;
            int pad = S(6);
            int colGap = S(6);
            int rowGap = S(6);
            int totalW = _resultsPanel.ClientSize.Width
                         - pad * 2 - SystemInformation.VerticalScrollBarWidth;
            int cardW = (totalW - colGap * (cols - 1)) / cols;
            int barW = S(16);
            int cardH = S(98);
            int contentLeft = barW + S(8);
            // SMALL square preview on the RIGHT, beside the part-no + rev rows;
            // the file name + description get their own full-width rows above and
            // below it (mirrors the task-pane search card).
            int thumbW = S(30);
            int thumbX = cardW - thumbW - S(8);
            int thumbY = S(26);
            int fullW  = cardW - contentLeft - S(8);   // file name / desc / buttons
            int textW  = thumbX - contentLeft - S(6);  // part no / rev (stop at preview)

            for (int i = 0; i < cards.Count; i++)
            {
                Card g = cards[i];
                int col = i % cols;
                int row = i / cols;
                int cx = pad + col * (cardW + colGap);
                int cy = pad + row * (cardH + rowGap);

                Color statusColor = StatusColor(g.Status);
                string statusText = (string.IsNullOrEmpty(g.Status)
                                        ? "WIP" : g.Status).ToUpper();

                string modelPath = g.ModelPath;
                string drawingPath = g.DrawingPath;
                string configName = g.ConfigName;
                bool hasModel = !string.IsNullOrEmpty(modelPath);

                Panel card = new Panel
                {
                    Location = new Point(cx, cy), Width = cardW, Height = cardH,
                    BackColor = cCard, BorderStyle = BorderStyle.None
                };
                // 1px outline for clear card-to-card distinction (shared pen).
                card.Paint += (s, e) =>
                {
                    var p = (Panel)s;
                    e.Graphics.DrawRectangle(_penBorder, 0, 0,
                        p.Width - 1, p.Height - 1);
                };

                // ── Rotated status bar ──
                Panel bar = new Panel
                {
                    BackColor = statusColor,
                    Location = new Point(0, 0), Width = barW, Height = cardH
                };
                bar.Paint += (s, e) =>
                {
                    var gr = e.Graphics;
                    gr.SmoothingMode =
                        System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    gr.TextRenderingHint =
                        System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    gr.TranslateTransform(barW / 2f, cardH / 2f);
                    gr.RotateTransform(-90f);
                    gr.DrawString(statusText, _fBar, Brushes.White,
                        0, 0, _sfBarCenter);
                };
                card.Controls.Add(bar);

                // ── File name — FULL-WIDTH top row (a long name uses the whole
                // card; fall back to the part number if somehow blank). ──
                card.Controls.Add(new Label
                {
                    Text = FirstNonBlank(g.DisplayName, g.PartNumber),
                    Font = _fCardBold, ForeColor = cTextDark,
                    Location = new Point(contentLeft, S(7)),
                    AutoSize = false, Width = fullW, Height = S(16),
                    AutoEllipsis = true
                });

                // ── Thumbnail (lazy SOLIDWORKS preview; click = enlarge) ──
                string thumbPath = hasModel ? modelPath : drawingPath;
                string thumbCfg = configName;
                var thumb = new ThumbPanel(
                    () => GetThumbnail(thumbPath, thumbCfg), QueueThumbLoad)
                {
                    Location = new Point(thumbX, thumbY),
                    Width = thumbW, Height = thumbW, BackColor = cCard
                };
                thumb.Click += (s, e) => ShowLargePreview(thumbPath, thumbCfg);
                card.Controls.Add(thumb);

                // ── Part number (own row, ELLIPSIS up to the preview when long) ──
                card.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(g.PartNumber)
                                ? "No Part No" : g.PartNumber,
                    Font = _fCardPn, ForeColor = cTextGray,
                    Location = new Point(contentLeft, S(26)),
                    AutoSize = false, Width = textW, Height = S(14),
                    AutoEllipsis = true
                });

                // ── Revision (own row) ──
                card.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(g.Revision)
                                ? "" : "REV " + g.Revision,
                    Font = _fCardPn, ForeColor = cTextGray,
                    Location = new Point(contentLeft, S(42)),
                    AutoSize = false, Width = textW, Height = S(14),
                    AutoEllipsis = true
                });

                // ── Description (or "→ Superseded by X" for an obsolete card) —
                // FULL-WIDTH row below the preview. ──
                bool obsoleteWithRepl =
                    string.Equals(g.Status, "Obsolete",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(g.SupersededBy);
                card.Controls.Add(new Label
                {
                    Text = obsoleteWithRepl
                        ? "→ Superseded by " + g.SupersededBy
                        : (string.IsNullOrWhiteSpace(g.Description)
                                ? "(no description)" : g.Description),
                    Font = _fCardDesc,
                    ForeColor = obsoleteWithRepl ? cMaroon : cTextLight,
                    Location = new Point(contentLeft, S(58)),
                    AutoSize = false, Width = fullW, Height = S(14),
                    AutoEllipsis = true
                });

                // ── Open PRT/ASM + Open DRW — full width along the bottom ──
                int gap = S(4);
                int btnW = (fullW - gap) / 2;
                int btnY = S(76);
                int btnH = S(18);

                string partLabel = g.ModelExt == ".sldasm" ? "Open ASM" : "Open PRT";
                Button btnModel = new Button
                {
                    Text = partLabel, Font = _fCardBtn,
                    Width = btnW, Height = btnH,
                    Location = new Point(contentLeft, btnY),
                    BackColor = hasModel ? cBrand : cTextLight,
                    ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                    Enabled = hasModel, Cursor = Cursors.Hand
                };
                btnModel.FlatAppearance.BorderSize = 0;
                if (hasModel)
                    btnModel.Click += (s, e) =>
                    {
                        _timer.Stop(); // committing to an action — no late tick
                        FileToOpen = modelPath;
                        FileToOpenConfig = configName;
                        OpenDrawing = false;
                        DialogResult = DialogResult.OK;
                        Close();
                    };
                card.Controls.Add(btnModel);

                Button btnDrawing = new Button
                {
                    Text = "Open DRW", Font = _fCardBtn,
                    Width = btnW, Height = btnH,
                    Location = new Point(contentLeft + btnW + gap, btnY),
                    BackColor = cBrandDark, ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
                };
                btnDrawing.FlatAppearance.BorderSize = 0;
                btnDrawing.Click += (s, e) =>
                {
                    _timer.Stop(); // committing to an action — no late tick
                    if (!string.IsNullOrEmpty(drawingPath))
                    {
                        // Drawing exists — open it directly (no config switch).
                        FileToOpen = drawingPath;
                        FileToOpenConfig = null;
                        OpenDrawing = false;
                    }
                    else
                    {
                        // None yet — open the model on this config + create one.
                        FileToOpen = modelPath;
                        FileToOpenConfig = configName;
                        OpenDrawing = true;
                    }
                    DialogResult = DialogResult.OK;
                    Close();
                };
                card.Controls.Add(btnDrawing);

                // Right-click anywhere on the card (background or any child) opens
                // the shared menu for THIS card. One shared menu + _menuCard (vs a
                // ContextMenuStrip per card) — assigning a menu to a control does
                // not make it owned/disposed, so per-card menus would leak (C4).
                MouseEventHandler showMenu = (s, e) =>
                {
                    if (e.Button != MouseButtons.Right) return;
                    // Stop the debounce timer while the menu is up: a queued tick
                    // would otherwise ClearAndDispose the card the menu sits on
                    // (consistent with the CardWhereUsed / ExportCsv guards).
                    _timer.Stop();
                    _menuCard = g;
                    _cardMenu.Show(Cursor.Position);
                };
                card.MouseUp += showMenu;
                foreach (Control ch in card.Controls) ch.MouseUp += showMenu;

                _resultsPanel.Controls.Add(card);
            }
        }

        // Card sort: Part No, then file name, then config (OrdinalIgnoreCase).
        private static int CompareCards(Card a, Card b)
        {
            int c = string.Compare(a.PartNumber ?? "", b.PartNumber ?? "",
                StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.DisplayName ?? "", b.DisplayName ?? "",
                StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.ConfigName ?? "", b.ConfigName ?? "",
                StringComparison.OrdinalIgnoreCase);
        }

        // ── Result card right-click actions (operate on _menuCard) ───────────
        private void CardCopyPartNo()
        {
            if (_menuCard == null) return;
            string pn = FirstNonBlank(_menuCard.PartNumber, _menuCard.DisplayName);
            try { if (!string.IsNullOrEmpty(pn)) Clipboard.SetText(pn); } catch { }
        }

        private void CardCopyPath()
        {
            if (_menuCard == null || string.IsNullOrEmpty(_menuCard.ModelPath))
                return;
            try { Clipboard.SetText(_menuCard.ModelPath); } catch { }
        }

        private void CardOpenFolder()
        {
            if (_menuCard == null || string.IsNullOrEmpty(_menuCard.ModelPath))
                return;
            string path = _menuCard.ModelPath;
            try
            {
                if (File.Exists(path))
                    System.Diagnostics.Process.Start(
                        "explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        System.Diagnostics.Process.Start(
                            "explorer.exe", "\"" + dir + "\"");
                    else
                        MessageBox.Show("Folder not found:\n" + path, "BCore PDM",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch { }
        }

        private void CardWhereUsed()
        {
            if (_menuCard == null || string.IsNullOrEmpty(_menuCard.ModelPath))
                return;
            var c = _menuCard;
            string fileName = Path.GetFileName(c.ModelPath);
            // Stop the debounce timer: the nested modal pumps the message loop, so
            // a queued tick would otherwise re-enter RunSearch and ClearAndDispose
            // the result cards (one of which owns this modal) mid-show.
            _timer.Stop();
            try
            {
                using (var wu = new WhereUsedForm(
                    c.ModelPath, fileName, c.PartNumber, c.ConfigName))
                {
                    wu.ShowDialog(this);
                    // If the user opened a parent assembly there, cascade that open
                    // up through this modal to the task-pane caller.
                    if (!string.IsNullOrEmpty(wu.FileToOpen))
                    {
                        FileToOpen = wu.FileToOpen;
                        FileToOpenConfig = null;
                        OpenDrawing = false;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Where Used could not open:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Export to CSV with the columns the user asked for. RE-QUERIES uncapped
        // (int.MaxValue) so the export is the FULL matching set, not just the ≤50
        // cards shown on screen (the card list stays capped for UI performance).
        // One row per matching configuration; per-config fields come from the
        // config, with PartNo/Description falling back to the file level.
        private void ExportCsv()
        {
            // Stop the debounce timer: the SaveFileDialog below pumps the message
            // loop, so a queued tick would otherwise rebuild the result cards
            // (ClearAndDispose) underneath the open dialog.
            _timer.Stop();
            string main     = _mainBox.Text.Trim();
            string drawnBy  = _drawnByBox.Text.Trim();
            string material = ComboValue(_materialBox);
            string finish   = ComboValue(_finishBox);
            string partType = ComboValue(_partTypeBox);
            string status   = ComboValue(_statusBox);
            string fileType = ComboValue(_fileTypeBox);
            if (main.Length == 0 && drawnBy.Length == 0 && material.Length == 0 &&
                finish.Length == 0 && partType.Length == 0 && status.Length == 0 &&
                fileType.Length == 0)
                return;

            List<VaultFile> all;
            try
            {
                bool exTrunc;
                all = DatabaseManager.SearchFilesAdvanced(
                    main, drawnBy, material, finish, partType, status, fileType,
                    out exTrunc, int.MaxValue);
            }
            catch
            {
                MessageBox.Show("Vault unavailable — could not export.",
                    "BCore PDM — Export Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (all.Count == 0) return;

            // Flatten to one row per matching config, then sort by Part No (then
            // file name) so the CSV matches the on-screen default order.
            var rows = new List<string[]>();
            foreach (var f in all)
            {
                string fileName = string.IsNullOrEmpty(f.FileName)
                    ? Path.GetFileName(f.FilePath) : f.FileName;
                foreach (var c in f.Configurations)
                    rows.Add(new[]
                    {
                        fileName,
                        string.IsNullOrEmpty(c.PartNo) ? f.PartNumber : c.PartNo,
                        c.DrawingNo ?? "",
                        string.IsNullOrEmpty(c.Description) ? f.Description : c.Description,
                        c.Revision ?? "", c.Material ?? "", c.FinishType ?? "",
                        c.DrawnBy ?? "", c.PartType ?? ""
                    });
            }
            rows.Sort((a, b) =>
            {
                int c1 = string.Compare(a[1], b[1], StringComparison.OrdinalIgnoreCase);
                return c1 != 0 ? c1
                    : string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase);
            });

            using (var sfd = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "AdvancedSearch_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(",", new[]
                    {
                        "File Name", "Part No", "Drawing No", "Description",
                        "Rev", "Material", "Finish", "Drawn By", "Part Type"
                    }));
                    foreach (var r in rows)
                    {
                        for (int i = 0; i < r.Length; i++) r[i] = Csv(r[i]);
                        sb.AppendLine(string.Join(",", r));
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show("Exported " + rows.Count +
                        " row" + (rows.Count == 1 ? "" : "s") + " to:\n" +
                        sfd.FileName, "BCore PDM — Exported",
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

        // RFC-4180 escaping + Excel formula-injection guard (house convention).
        // Excel/LibreOffice execute a cell starting with = + - @ — and ALSO when
        // it leads with a TAB or CR before such a char — so neutralise all six,
        // matching AuditLogger/ExportManager/VaultDashboardForm (not the weaker
        // 4-char guard the older viewers shipped). Vault.xml is engineer-editable,
        // so these fields are not fully trusted.
        private static string Csv(string field)
        {
            field = field ?? "";
            if (field.Length > 0)
            {
                char c0 = field[0];
                if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' ||
                    c0 == '\t' || c0 == '\r')
                    field = "'" + field;
            }
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                field = "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private static Color StatusColor(string status)
        {
            switch (status)
            {
                case "Released": return cGreen;
                case "Locked":   return cOrange;
                case "Obsolete": return cObsolete;
                default:         return cBrand;
            }
        }

        // First non-whitespace value (trimmed), else "". Lets a card fall back
        // from a blank per-config field to the file-level value.
        private static string FirstNonBlank(params string[] vals)
        {
            if (vals != null)
                foreach (var v in vals)
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        // ── Card preview thumbnails (own copy per one-form-one-file; mirrors
        //    the task-pane search cards) ───────────────────────────────────────

        // Converts an OLE IPictureDisp (what ISldWorks.GetPreviewBitmap returns)
        // to a System.Drawing.Image via AxHost's protected static helper.
        private sealed class PictureConverter : System.Windows.Forms.AxHost
        {
            private PictureConverter() : base(
                "59EE46BA-677D-4d20-BF10-8D8067CB8B33") { }
            public static Image ToImage(object iPictureDisp)
            {
                try
                {
                    return iPictureDisp == null
                        ? null : GetPictureFromIPicture(iPictureDisp);
                }
                catch { return null; }
            }
        }

        // A small tile that paints a SHARED cached preview Image (never owned —
        // disposed only by the cache) or a light page-glyph placeholder. The
        // image is fetched LAZILY on first paint (the _loaded flag set BEFORE the
        // call so a re-entrant paint can't recurse).
        private sealed class ThumbPanel : Panel
        {
            private readonly Func<Image> _loader;
            private readonly Action<ThumbPanel> _request; // parent queues a load
            private Image _img;     // shared cached image — NOT owned here
            private bool _loaded, _requested;
            public ThumbPanel(Func<Image> loader, Action<ThumbPanel> request)
            {
                _loader = loader;
                _request = request;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
            }
            // The (slow, network, MESSAGE-PUMPING) preview read — called ONLY by
            // the parent's serialized drain, OUTSIDE any paint (doing it in OnPaint
            // froze the popup per card and let the COM pump re-dispatch clicks).
            public void LoadNow()
            {
                if (_loaded) return;
                _loaded = true;
                try { _img = _loader == null ? null : _loader(); }
                catch { _img = null; }
                if (!IsDisposed) Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Color.White);
                if (_img != null)
                {
                    g.InterpolationMode = System.Drawing.Drawing2D
                        .InterpolationMode.HighQualityBicubic;
                    double s = Math.Min((double)Width / _img.Width,
                                        (double)Height / _img.Height);
                    int w = Math.Max(1, (int)(_img.Width * s));
                    int h = Math.Max(1, (int)(_img.Height * s));
                    try { g.DrawImage(_img, (Width - w) / 2, (Height - h) / 2, w, h); }
                    catch { }
                }
                else
                {
                    int mx = Width / 5, my = Height / 6;
                    var page = new Rectangle(mx, my, Width - 2 * mx, Height - 2 * my);
                    g.FillRectangle(Brushes.WhiteSmoke, page);
                    g.DrawRectangle(Pens.Silver, page);
                }
                g.DrawRectangle(Pens.Gainsboro, 0, 0, Width - 1, Height - 1);
                if (!_loaded && !_requested)
                {
                    _requested = true;
                    _request?.Invoke(this);
                }
            }
        }

        // A ThumbPanel asks (on first paint) for its preview; we enqueue and make
        // sure the throttle timer is running — the COM read happens in ThumbTick,
        // off paint, one per tick.
        private void QueueThumbLoad(ThumbPanel p)
        {
            if (p == null || p.IsDisposed) return;
            _thumbQueue.Enqueue(p);
            if (!_thumbLoadsSuspended && _thumbTimer != null && !_thumbTimer.Enabled)
                _thumbTimer.Start();
        }

        // Load ONE queued thumbnail per tick (throttled). The guard makes a
        // pump-re-dispatched WM_TIMER a no-op; stops the timer when the queue
        // drains; skips disposed panels.
        private void ThumbTick(object sender, EventArgs e)
        {
            if (_thumbDraining || _thumbLoadsSuspended) return;
            _thumbDraining = true;
            try
            {
                while (_thumbQueue.Count > 0)
                {
                    ThumbPanel p = _thumbQueue.Dequeue();
                    if (p != null && !p.IsDisposed) { p.LoadNow(); break; }
                }
            }
            catch { }
            finally
            {
                _thumbDraining = false;
                if (_thumbQueue.Count == 0) _thumbTimer?.Stop();
            }
        }

        // Pause/resume preview loading around the large-preview read+modal.
        private void SuspendThumbLoads(bool suspend)
        {
            _thumbLoadsSuspended = suspend;
            if (suspend) _thumbTimer?.Stop();
            else if (_thumbQueue.Count > 0) _thumbTimer?.Start();
        }

        // Cached small preview for a file (null on failure → placeholder). MUST
        // run on the UI thread (the SW API is not thread-safe). Caches the result
        // (INCLUDING null).
        private Image GetThumbnail(string filePath, string config)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string key = filePath + "|" + (config ?? "");
            Image cached;
            if (_thumbCache.TryGetValue(key, out cached)) return cached;

            Image thumb = null;
            try
            {
                if (PDMLiteAddin.SwApp != null && File.Exists(filePath))
                {
                    object pic = PDMLiteAddin.SwApp.GetPreviewBitmap(
                        filePath, config ?? "");
                    using (Image full = PictureConverter.ToImage(pic))
                    {
                        if (full != null)
                        {
                            // Trim the white margin FIRST so the model fills the
                            // small tile; CropToContent may return `full` itself.
                            Image cropped = CropToContent(full);
                            try { thumb = ResizeToThumb(cropped, S(96)); }
                            finally { if (cropped != full) cropped.Dispose(); }
                        }
                    }
                }
            }
            catch { thumb = null; }

            // Don't Dispose cached Images here — a ThumbPanel in the current view
            // shares (never owns) the cached Image, so disposing mid-session blanks
            // a live tile. Clear() drops our refs; GC reclaims the rest.
            if (_thumbCache.Count >= ThumbCacheCap)
                _thumbCache.Clear();
            _thumbCache[key] = thumb;
            return thumb;
        }

        // Resize to fit within maxPx (preserve aspect, never upscale) into a NEW
        // bitmap; the caller disposes the source.
        private static Image ResizeToThumb(Image src, int maxPx)
        {
            if (src == null) return null;
            double s = Math.Min((double)maxPx / src.Width,
                                (double)maxPx / src.Height);
            if (s > 1) s = 1;
            int w = Math.Max(1, (int)(src.Width * s));
            int h = Math.Max(1, (int)(src.Height * s));
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D
                    .InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        // Trim the near-white / transparent border SOLIDWORKS bakes around a
        // preview so the model FILLS the small tile. Returns the SOURCE unchanged
        // when there is nothing to trim, the image is all background, or anything
        // fails — so the caller disposes the result ONLY when it differs from src.
        private static Image CropToContent(Image src)
        {
            if (src == null) return null;
            Bitmap bmp = null;
            bool ownBmp = false;
            try
            {
                bmp = src as Bitmap;
                if (bmp == null) { bmp = new Bitmap(src); ownBmp = true; }
                int w = bmp.Width, h = bmp.Height;
                if (w < 8 || h < 8) return src;

                var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                int stride = data.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(data);

                // Detect the ACTUAL background colour from 8 border samples
                // (corners + edge midpoints) rather than assuming white — some
                // seats save a light-gray / gradient backdrop where a hardcoded
                // white cutoff trims nothing. Per-channel MEDIAN is robust to a
                // model that reaches one sample point. (BGRA buffer.)
                int[] sx = { 2, w - 3, 2, w - 3, w / 2, w / 2, 2, w - 3 };
                int[] sy = { 2, 2, h - 3, h - 3, 2, h - 3, h / 2, h / 2 };
                byte[] sB = new byte[8], sG = new byte[8], sR = new byte[8];
                for (int k = 0; k < 8; k++)
                {
                    int si = sy[k] * stride + (sx[k] << 2);
                    sB[k] = buf[si]; sG[k] = buf[si + 1]; sR[k] = buf[si + 2];
                }
                Array.Sort(sB); Array.Sort(sG); Array.Sort(sR);
                int bgB = sB[4], bgG = sG[4], bgR = sR[4]; // medians
                const int TOL = 22; // per-channel distance that still counts as bg

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + (x << 2);
                        bool bg = buf[i + 3] < 8 ||
                            (Math.Abs(buf[i]     - bgB) <= TOL &&
                             Math.Abs(buf[i + 1] - bgG) <= TOL &&
                             Math.Abs(buf[i + 2] - bgR) <= TOL);
                        if (bg) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY) return src; // all background

                int padX = Math.Max(1, (maxX - minX + 1) / 20);
                int padY = Math.Max(1, (maxY - minY + 1) / 20);
                minX = Math.Max(0, minX - padX);
                minY = Math.Max(0, minY - padY);
                maxX = Math.Min(w - 1, maxX + padX);
                maxY = Math.Min(h - 1, maxY + padY);
                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                if (cw >= w && ch >= h) return src; // nothing to trim
                if (cw < 4 || ch < 4) return src;   // degenerate

                var crop = new Bitmap(cw, ch,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(crop))
                    g.DrawImage(bmp, new Rectangle(0, 0, cw, ch),
                        new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
                return crop;
            }
            catch { return src; }
            finally { if (ownBmp && bmp != null) bmp.Dispose(); }
        }

        // Click a card thumbnail → show the FULL preview larger in a modal (no
        // crop — SW frames the model centred with even margins, which reads
        // cleaner at this size). Esc / click-the-image closes. Null → info.
        private void ShowLargePreview(string filePath, string config)
        {
            // Modal ShowDialog pumps the message loop; a queued debounce tick would
            // RunSearch → ClearAndDispose the card whose Click is on the stack
            // (ObjectDisposedException, audit-M8). Stop it, like the other modals.
            _timer.Stop();
            // Pause background card-thumbnail loads while we do our own preview
            // read + modal, so two SOLIDWORKS preview reads never overlap.
            SuspendThumbLoads(true);
            try
            {

            Image full = null;
            try
            {
                if (PDMLiteAddin.SwApp != null && File.Exists(filePath))
                    full = PictureConverter.ToImage(
                        PDMLiteAddin.SwApp.GetPreviewBitmap(filePath, config ?? ""));
            }
            catch { full = null; }

            if (full == null)
            {
                MessageBox.Show("No preview available for this file.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (var f = new Form())
                using (var pb = new PictureBox())
                {
                    f.Text = "Preview — " +
                        Path.GetFileNameWithoutExtension(filePath);
                    f.StartPosition = FormStartPosition.CenterScreen;
                    f.BackColor = Color.White;
                    f.ShowInTaskbar = false;
                    int w = Math.Min(Math.Max(full.Width, S(320)), S(900));
                    int h = Math.Min(Math.Max(full.Height, S(320)), S(700));
                    f.ClientSize = new Size(w, h);
                    pb.Dock = DockStyle.Fill;
                    pb.SizeMode = PictureBoxSizeMode.Zoom;
                    pb.Image = full; // PictureBox does not own/dispose its Image
                    pb.Cursor = Cursors.Hand;
                    f.Controls.Add(pb);
                    f.KeyPreview = true;
                    f.KeyDown += (s, e) =>
                    { if (e.KeyCode == Keys.Escape) f.Close(); };
                    pb.Click += (s, e) => f.Close();
                    f.ShowDialog(this);
                }
            }
            finally { full.Dispose(); }

            }
            finally { SuspendThumbLoads(false); }
        }

        // Controls.Clear() re-parents removed controls to the hidden parking
        // window where they keep their GDI/USER handles forever — dispose every
        // child, THEN clear (audit C4). The results panel rebuilds on every
        // debounce tick, so a plain Clear would leak handles per card per search.
        private static void ClearAndDispose(Control container)
        {
            var children = new Control[container.Controls.Count];
            container.Controls.CopyTo(children, 0);
            container.Controls.Clear();
            foreach (Control c in children)
                try { c.Dispose(); } catch { }
        }

        // One result card (a single matching configuration of a model file).
        private class Card
        {
            public string DisplayName;   // base file name, no extension
            public string ModelPath;
            public string ModelExt;
            public string DrawingPath;
            public string ConfigName;
            public string PartNumber;
            public string Description;
            public string Revision;
            public string Status;
            public string SupersededBy;
        }
    }
}
