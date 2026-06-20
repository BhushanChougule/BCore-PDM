using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
        private ComboBox _materialBox, _finishBox, _partTypeBox;
        private Panel    _resultsPanel;
        private Label    _countLabel;
        private Timer    _timer;

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
                _fHeader?.Dispose(); _fSection?.Dispose(); _fLabel?.Dispose();
                _fInput?.Dispose(); _fHint?.Dispose();
                _fCardBold?.Dispose(); _fCardPn?.Dispose(); _fCardDesc?.Dispose();
                _fCardBtn?.Dispose(); _fBar?.Dispose();
                _sfBarCenter?.Dispose();
                _penBorder?.Dispose();
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
            this.ClientSize = new Size(S(648), S(664));

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
            this.Controls.Add(bottom);

            // ── Filters panel (Top) — centred search + a 2×2 refine grid ─────
            var filters = new Panel
            {
                Dock = DockStyle.Top, Height = S(176), BackColor = cBg
            };

            int y = S(8);
            filters.Controls.Add(MakeSection("SEARCH", margin, ref y, innerW));

            // Centred main search box (narrower than the form so it reads as a
            // search bar, not an edge-to-edge field on the wide popup).
            int mainW = S(440);
            _mainBox = MakeTextBox((clientW - mainW) / 2, y, mainW);
            SetCueBanner(_mainBox, "Part number, description or file name");
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

            _materialBox = MakeCombo(c2InputX, ry1, cInputW,
                BuildOptions(PropertyForm.MaterialOptions()));
            AddRow(filters, "Material", c2LabelX, labelW, ry1, _materialBox);

            _finishBox = MakeCombo(c1InputX, ry2, cInputW,
                BuildOptions(PropertyForm.FinishTypeOptions()));
            AddRow(filters, "Finish", c1LabelX, labelW, ry2, _finishBox);

            _partTypeBox = MakeCombo(c2InputX, ry2, cInputW,
                BuildOptions(PropertyForm.PartTypeOptions()));
            AddRow(filters, "Part Type", c2LabelX, labelW, ry2, _partTypeBox);

            y = ry2 + S(30);

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

            // ── Live search wiring (debounced) ──────────────────────────────
            _timer = new Timer { Interval = 450 };
            _timer.Tick += (s, e) => { _timer.Stop(); RunSearch(); };
            _mainBox.TextChanged    += (s, e) => Schedule();
            _drawnByBox.TextChanged += (s, e) => Schedule();
            _materialBox.SelectedIndexChanged += (s, e) => Schedule();
            _finishBox.SelectedIndexChanged   += (s, e) => Schedule();
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

        private ComboBox MakeCombo(int x, int y, int w, string[] items)
        {
            var cb = new ComboBox
            {
                Font = _fInput, Width = w,
                Location = new Point(x, y),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            cb.Items.AddRange(items);
            cb.SelectedIndex = 0; // "— Any —"
            cb.MaxDropDownItems = 16;
            return cb;
        }

        // Build a dropdown's items: "— Any —" first (no filter), then the source
        // list with PropertyForm's "-- Select --" sentinel removed.
        private static string[] BuildOptions(string[] source)
        {
            var list = new List<string> { AnyOption };
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

        // A combo's filter value: "— Any —" (index 0) means no filter.
        private static string ComboValue(ComboBox cb)
        {
            if (cb.SelectedIndex <= 0) return "";
            return cb.SelectedItem?.ToString() ?? "";
        }

        private void RunSearch()
        {
            ClearAndDispose(_resultsPanel);

            string main     = _mainBox.Text.Trim();
            string drawnBy  = _drawnByBox.Text.Trim();
            string material = ComboValue(_materialBox);
            string finish   = ComboValue(_finishBox);
            string partType = ComboValue(_partTypeBox);

            if (main.Length == 0 && drawnBy.Length == 0 && material.Length == 0 &&
                finish.Length == 0 && partType.Length == 0)
            {
                _countLabel.ForeColor = cTextGray;
                _countLabel.Text = "Type a term or pick a filter to search.";
                return;
            }

            bool truncated;
            List<VaultFile> results;
            try
            {
                results = DatabaseManager.SearchFilesAdvanced(
                    main, drawnBy, material, finish, partType, out truncated);
            }
            catch
            {
                _countLabel.ForeColor = cMaroon;
                _countLabel.Text = "Vault unavailable — check the N: drive.";
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
                string ext = (Path.GetExtension(f.FileName) ?? "").ToLowerInvariant();

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
                        PartNumber   = string.IsNullOrEmpty(c.PartNo)
                                          ? f.PartNumber : c.PartNo,
                        Description  = string.IsNullOrEmpty(c.Description)
                                          ? f.Description : c.Description,
                        Revision     = c.Revision,
                        Status       = f.Status,
                        SupersededBy = f.SupersededBy,
                        DrawingPath  = drawingPath
                    });
                }
            }

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

            _countLabel.ForeColor = cTextGray;
            _countLabel.Text = truncated
                ? "Showing first " + cards.Count + " — refine to narrow results."
                : cards.Count + " result" + (cards.Count == 1 ? "" : "s") + ".";
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
            int cardH = S(74);
            int contentLeft = barW + S(8);
            int contentW = cardW - contentLeft - S(8);

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

                // ── File name ──
                card.Controls.Add(new Label
                {
                    Text = g.DisplayName, Font = _fCardBold, ForeColor = cTextDark,
                    Location = new Point(contentLeft, S(6)),
                    AutoSize = false, Width = contentW, Height = S(14),
                    AutoEllipsis = true
                });

                // ── Part number (+ revision) ──
                string pnText = string.IsNullOrEmpty(g.PartNumber)
                                    ? "No Part No" : g.PartNumber;
                if (!string.IsNullOrEmpty(g.Revision))
                    pnText += "   REV " + g.Revision;
                card.Controls.Add(new Label
                {
                    Text = pnText, Font = _fCardPn, ForeColor = cTextGray,
                    Location = new Point(contentLeft, S(21)),
                    AutoSize = false, Width = contentW, Height = S(13),
                    AutoEllipsis = true
                });

                // ── Description (or "→ Superseded by X" for an obsolete card) ──
                bool obsoleteWithRepl =
                    string.Equals(g.Status, "Obsolete",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(g.SupersededBy);
                card.Controls.Add(new Label
                {
                    Text = obsoleteWithRepl
                        ? "→ Superseded by " + g.SupersededBy
                        : (string.IsNullOrEmpty(g.Description)
                                ? "(no description)" : g.Description),
                    Font = _fCardDesc,
                    ForeColor = obsoleteWithRepl ? cMaroon : cTextLight,
                    Location = new Point(contentLeft, S(34)),
                    AutoSize = false, Width = contentW, Height = S(13),
                    AutoEllipsis = true
                });

                // ── Open PRT/ASM + Open DRW ──
                int gap = S(4);
                int btnW = (contentW - gap) / 2;
                int btnY = S(52);
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

                _resultsPanel.Controls.Add(card);
            }
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
            public string DisplayName;
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
