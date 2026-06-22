using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PDMLite
{
    public class TaskPaneControl : UserControl
    {
        private Label _fileNameLbl;
        private Label _statusVal;
        private Label _partNoVal;
        private Label _revVal;
        private Label _lockedVal;
        private Panel _historyPanel;
        private Button _btnOpenLinkedMaster;
        private Button _btnRelease;
        private Button _btnUnlock;
        private Button _btnNewRev;
        private Button _btnRollback;
        private Button _btnReqUnlock;
        private Button _btnReqRevision;
        private Button _btnReqRelease;
        private Button _btnOpenLinkedEng;
        private Button _btnMyRequests;
        private TextBox _searchBox;
        private Panel _resultsPanel;
        private Button _btnRequests;
        private int _pendingCount;
        private Timer _searchTimer;
        private ToolTip _searchHeaderTip;

        private float _scale = 1f;

        private readonly Color cBrand = Color.FromArgb(65, 120, 175);
        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg = Color.FromArgb(248, 249, 251);
        private readonly Color cCard = Color.White;
        private readonly Color cBorder = Color.FromArgb(220, 225, 232);
        private readonly Color cTextDark = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray = Color.FromArgb(100, 110, 125);
        private readonly Color cTextLight = Color.FromArgb(155, 163, 175);
        private readonly Color cGreen = Color.FromArgb(60, 140, 95);
        private readonly Color cOrange = Color.FromArgb(185, 115, 55);
        private readonly Color cPurple = Color.FromArgb(105, 100, 165);
        private readonly Color cDark = Color.FromArgb(75, 80, 90);
        private readonly Color cRed = Color.FromArgb(180, 75, 75);
        private readonly Color cMaroon = Color.FromArgb(140, 60, 60);
        private readonly Color cSwRed = Color.FromArgb(190, 55, 50); // muted SOLIDWORKS red

        // Fonts for the REBUILT panels (search result cards + file history).
        // Created ONCE (ctor) and disposed in Dispose(bool). These panels are
        // torn down and rebuilt constantly — every search keystroke and every
        // doc/config switch — and a Font assigned to a control is NOT owned
        // by it, so the old per-card "new Font(...)" leaked a GDI handle per
        // label per rebuild, marching SOLIDWORKS toward the 10,000-handle
        // ceiling over a multi-day session (audit C4).
        private Font _fBold38, _fBold35, _fBold34, _fBold31;
        private Font _fReg35, _fReg33, _fItalic33;
        // Paint-invariant StringFormat for the cards' rotated status bar —
        // shared like the fonts (was allocated on every WM_PAINT).
        private StringFormat _sfBarCenter;

        // Card thumbnails: extracted SOLIDWORKS preview bitmaps, keyed by
        // "filePath|config". Loaded LAZILY (only when a card first paints — see
        // ThumbPanel) so a 50-card search never fires 50 network preview reads
        // up front, and CACHED so repeated searches reuse them. The cached
        // Images are SHARED (a ThumbPanel never owns its image), capped to bound
        // GDI handles, and disposed in Dispose(bool). A null value means "tried,
        // no preview" — so a missing-preview file isn't re-read every search.
        private readonly Dictionary<string, Image> _thumbCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private const int ThumbCacheCap = 400;

        // Thumbnails load via a THROTTLED, deferred queue — NEVER inside a paint
        // (GetPreviewBitmap is a network COM call that pumps the STA loop). Panels
        // request a load on first paint; a Timer (_thumbTimer) loads ONE per tick
        // off the message loop, so ALL cards render first and previews trickle in
        // gently without freezing the pane. _thumbLoadsSuspended pauses loading
        // during a doc-open / large-preview so a heavy SW op never races a read.
        private readonly Queue<ThumbPanel> _thumbQueue = new Queue<ThumbPanel>();
        private Timer _thumbTimer;   // throttle: one preview read per tick
        private bool _thumbDraining;
        private bool _thumbLoadsSuspended;

        // Shared hover tooltip (one instance, no per-card alloc) + shared
        // right-click menu for result/recent cards; _menuCard is the card the
        // menu was opened on (captured on right-click).
        private ToolTip _cardTip;
        private ContextMenuStrip _cardMenu;
        private SearchGroup _menuCard;

        public TaskPaneControl()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;

            _fBold38 = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            _fBold35 = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);
            _fBold34 = new Font("Segoe UI", 3.4f * _scale, FontStyle.Bold);
            _fBold31 = new Font("Segoe UI", 3.1f * _scale, FontStyle.Bold);
            _fReg35 = new Font("Segoe UI", 3.5f * _scale);
            _fReg33 = new Font("Segoe UI", 3.3f * _scale);
            _fItalic33 = new Font("Segoe UI", 3.3f * _scale, FontStyle.Italic);
            _sfBarCenter = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            _cardTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 500,
                                     ReshowDelay = 200, ShowAlways = true };
            _cardMenu = BuildCardMenu();

            BuildUI();
        }

        // base.Dispose disposes child CONTROLS, but not fonts (a control does
        // not own its Font) and not the timer (created without a container).
        // Order matters: tear the children down FIRST, then release the fonts
        // they were painting with — the reverse leaves a window where a
        // re-entrant repaint could draw with a disposed Font.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _searchTimer?.Stop();
                _searchTimer?.Dispose();
                _thumbTimer?.Stop();
                _thumbTimer?.Dispose();
                _searchHeaderTip?.Dispose();
                _fBold38?.Dispose();
                _fBold35?.Dispose();
                _fBold34?.Dispose();
                _fBold31?.Dispose();
                _fReg35?.Dispose();
                _fReg33?.Dispose();
                _fItalic33?.Dispose();
                _sfBarCenter?.Dispose();
                _cardTip?.Dispose();
                _cardMenu?.Dispose();
                foreach (var img in _thumbCache.Values)
                    try { img?.Dispose(); } catch { }
                _thumbCache.Clear();
            }
        }

        // Converts an OLE IPictureDisp (what ISldWorks.GetPreviewBitmap returns)
        // to a System.Drawing.Image. AxHost.GetPictureFromIPicture is protected
        // static, so we expose it via a tiny derived class (the standard interop
        // pattern). Never instantiated — only the inherited static is called.
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

        // A small thumbnail tile that paints a SHARED cached preview Image (never
        // owned — disposed only by the cache), or a light page-glyph placeholder
        // when none exists. The image is fetched LAZILY on the first paint via
        // the supplied loader, with the _loaded flag set BEFORE the call so a
        // re-entrant paint (a COM read can pump messages) can't recurse into it.
        private sealed class ThumbPanel : Panel
        {
            private readonly Func<Image> _loader;          // SW-API fallback read
            private readonly Action<ThumbPanel> _request;  // parent resolves the load
            private Image _img;     // shared cached image — NOT owned here
            private bool _loaded, _requested;
            public readonly string FilePath;  // for the shell load + cache key
            public readonly string Config;
            public ThumbPanel(string filePath, string config,
                Func<Image> loader, Action<ThumbPanel> request)
            {
                FilePath = filePath;
                Config = config;
                _loader = loader;
                _request = request;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
            }
            // Set the resolved tile (from the shell loader OR the SW-API fallback).
            // null leaves the placeholder but marks loaded so we don't re-request.
            public void SetImage(Image img)
            {
                _img = img;
                _loaded = true;
                if (!IsDisposed) Invalidate();
            }
            // SW-API FALLBACK read (only when the shell has no thumbnail). Runs on
            // the UI thread via the throttle (ThumbTick), OUTSIDE any paint — doing
            // a SW preview read inside OnPaint blocked the UI per card and let the
            // COM pump re-dispatch a click mid-paint / re-enter a doc-open (crash).
            public void LoadNow()
            {
                if (_loaded) return;
                Image img = null;
                try { img = _loader == null ? null : _loader(); }
                catch { img = null; }
                SetImage(img);
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
                    try
                    {
                        g.DrawImage(_img, (Width - w) / 2, (Height - h) / 2, w, h);
                    }
                    catch { }
                }
                else
                {
                    // Placeholder: a simple centred "page" glyph.
                    int mx = Width / 5, my = Height / 6;
                    var page = new Rectangle(mx, my, Width - 2 * mx, Height - 2 * my);
                    g.FillRectangle(Brushes.WhiteSmoke, page);
                    g.DrawRectangle(Pens.Silver, page);
                }
                g.DrawRectangle(Pens.Gainsboro, 0, 0, Width - 1, Height - 1);
                // Request the preview load OFF the paint path (see LoadNow). The
                // placeholder shows instantly; the image fills in when the drain
                // reaches this panel.
                if (!_loaded && !_requested)
                {
                    _requested = true;
                    _request?.Invoke(this);
                }
            }
        }

        // Owner-drawn Label for the Active File name. The stock Label's
        // paint path can be broken by in-process OLE in-place machinery —
        // observed on the one part with a DESIGN TABLE (embedded Excel OLE):
        // Text/bounds/font all correct, yet nothing painted. TextRenderer
        // goes through raw GDI ExtTextOut, the most glitch-resistant text
        // path in WinForms, and NoPrefix also neutralises mnemonic quirks.
        private sealed class PaintedLabel : Label
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor); // no per-paint brush (audit C4)
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    ClientRectangle, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine);
            }
        }

        // Controls.Clear() does NOT dispose the removed controls — WinForms
        // re-parents them to a hidden "parking window" where they keep their
        // USER/GDI handles alive forever. The task pane lives for the whole
        // SOLIDWORKS session and these panels rebuild constantly, so plain
        // Clear() marched the process toward the 10,000-handle ceiling (at
        // which point SOLIDWORKS stops painting and dies). Dispose every
        // child, THEN clear (audit C4).
        private static void ClearAndDispose(Control container)
        {
            var children = new Control[container.Controls.Count];
            container.Controls.CopyTo(children, 0);
            container.Controls.Clear();
            foreach (Control c in children)
                try { c.Dispose(); } catch { }
        }

        private int S(float v) => (int)(v * _scale);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter && _searchBox != null && _searchBox.Focused)
            {
                RunSearch();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void BuildUI()
        {
            this.BackColor = cBg;
            this.AutoScroll = true;

            int x = S(10);
            int w = S(188);
            int y = 0;

            // ── Your exact font settings ──────────────────────────────
            Font fHeader = new Font("Segoe UI", 7f * _scale, FontStyle.Bold);
            Font fSection = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            Font fLabel = new Font("Segoe UI", 4f * _scale);
            Font fValue = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            Font fBtn = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);

            bool isMaster = DatabaseManager.GetUserRole(
                PDMLiteAddin.CurrentUser) == "Master";

            // ── Header Banner ─────────────────────────────────────────
            Panel headerBanner = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, y),
                Width = S(210),
                Height = S(32),
                Cursor = Cursors.Hand
            };
            headerBanner.Click += (s, e) => ShowAboutDialog();
            this.Controls.Add(headerBanner);
            var headerLbl = new Label
            {
                Text = "BCore PDM",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = S(210),
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            headerLbl.Click += (s, e) => ShowAboutDialog();
            headerBanner.Controls.Add(headerLbl);
            y += S(32);

            // ── Search Section ────────────────────────────────────────
            y += S(8);
            // The quick search box below is a clean PartNo/Description/FileName
            // quick-find. Property-wide search (Material/Finish/DrawnBy/PartType)
            // lives behind a popup so it never floods this box with category hits
            // — DOUBLE-CLICK this header to open it (tooltip advertises it).
            Label searchHeader =
                MakeSectionHeader("SEARCH FILES", fSection, x, y, w);
            searchHeader.Cursor = Cursors.Hand;
            searchHeader.DoubleClick += (s, e) => OpenAdvancedSearch();
            _searchHeaderTip = new ToolTip();
            _searchHeaderTip.SetToolTip(searchHeader,
                "Double-click for Advanced Search (Material, Finish, Drawn By, Part Type)");
            this.Controls.Add(searchHeader);
            y += S(20);

            Panel searchCard = new Panel
            {
                BackColor = cCard,
                Location = new Point(x, y),
                Width = w,
                Height = S(24),
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(searchCard);

            // Full width — search is BUTTON-LESS by design: auto-search fires
            // on ≥2 chars via the 600ms timer and clearing the box stops it
            // (both handled in TextChanged below), so there is no Clear/Search
            // button. S(2) margins each side inside searchCard.
            _searchBox = new TextBox
            {
                Font = fLabel,
                Width = w - S(4),
                Height = S(22),
                Location = new Point(S(2), S(2)),
                BorderStyle = BorderStyle.None,
                BackColor = cCard,
                ForeColor = cTextDark
            };
            _searchTimer = new Timer { Interval = 600 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RunSearch(); };
            // Throttle preview loads: one per 60ms tick, so cards render first and
            // thumbnails trickle in without freezing the pane.
            _thumbTimer = new Timer { Interval = 60 };
            _thumbTimer.Tick += ThumbTick;
            _searchBox.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                if (_searchBox.Text.Length >= 2) _searchTimer.Start();
                else if (_searchBox.Text.Length == 0)
                {
                    ClearAndDispose(_resultsPanel);
                    _cardTip.RemoveAll(); // clearing the box is a rebuild too
                    _thumbQueue.Clear();
                    _resultsPanel.Height = 0;
                }
            };
            searchCard.Controls.Add(_searchBox);

            // No Clear/Search buttons by design (auto-search + clear-on-empty,
            // see the search-box note above). Advance past the search row.
            y += S(28);

            // Clickable hint that doubles as the entry point to the quick-access
            // popup (Saved searches / Recent / Favorites). Kept a popup because
            // this pane's layout is fixed-position — an inline list would overlap
            // the Active File section below.
            var hintLink = new Label
            {
                Text = "Search part no / description   ·   ★ Saved & Recent",
                Font = new Font("Segoe UI", 3.2f * _scale),
                ForeColor = cBrand,
                Location = new Point(x, y),
                AutoSize = false,
                Width = w,
                Height = S(14),
                Cursor = Cursors.Hand
            };
            hintLink.Click += (s, e) => OpenQuickAccess();
            this.Controls.Add(hintLink);
            y += S(16);

            _resultsPanel = new Panel
            {
                Location = new Point(x, y),
                Width = w,
                Height = 0,
                BackColor = cBg,
                AutoScroll = false
            };
            this.Controls.Add(_resultsPanel);
            y += S(6);

            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── Active File Section ───────────────────────────────────
            this.Controls.Add(MakeSectionHeader("ACTIVE FILE", fSection, x, y, w));
            y += S(20);

            Panel fileCard = new Panel
            {
                BackColor = cCard,
                Location = new Point(x, y),
                Width = w,
                Height = S(26),
                BorderStyle = BorderStyle.None
            };
            fileCard.Controls.Add(new Panel
            {
                BackColor = cBrand,
                Location = new Point(0, 0),
                Width = S(3),
                Height = S(26)
            });
            _fileNameLbl = new PaintedLabel
            {
                Text = "No file open",
                Font = fValue,
                ForeColor = cTextDark,
                Location = new Point(S(7), S(5)),
                AutoSize = false,
                Width = w - S(10),
                Height = S(16),
                AutoEllipsis = true
            };
            fileCard.Controls.Add(_fileNameLbl);
            this.Controls.Add(fileCard);
            y += S(30);

            Panel infoCard = new Panel
            {
                BackColor = cCard,
                Location = new Point(x, y),
                Width = w,
                Height = S(88),
                BorderStyle = BorderStyle.None
            };
            this.Controls.Add(infoCard);

            int iy = S(6);
            _statusVal = MakeInfoRowInCard(infoCard, "Status", fLabel, fValue, S(6), w - S(12), ref iy);
            _partNoVal = MakeInfoRowInCard(infoCard, "Part No", fLabel, fValue, S(6), w - S(12), ref iy);
            _revVal = MakeInfoRowInCard(infoCard, "Revision", fLabel, fValue, S(6), w - S(12), ref iy);
            _lockedVal = MakeInfoRowInCard(infoCard, "Locked By", fLabel, fValue, S(6), w - S(12), ref iy);
            y += S(92);

            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── Master Actions ────────────────────────────────────────
            Label masterLbl = MakeSectionHeader("MASTER ACTIONS", fSection, x, y, w);
            masterLbl.Visible = isMaster;
            this.Controls.Add(masterLbl);
            y += S(20);

            _btnOpenLinkedMaster = MakeActionButton("Open Drawing", cBrand, fBtn, x, w, ref y, isMaster);
            _btnRelease = MakeActionButton("Release File",        cGreen,  fBtn, x, w, ref y, isMaster);
            _btnUnlock  = MakeActionButton("Unlock File",         cPurple, fBtn, x, w, ref y, isMaster);
            _btnNewRev  = MakeActionButton("New Revision",        cDark,   fBtn, x, w, ref y, isMaster);
            _btnRollback = MakeActionButton("Rollback Revision",  cMaroon, fBtn, x, w, ref y, isMaster);

            _btnOpenLinkedMaster.Click += (s, e) => DoAction("openlinked");
            _btnRelease.Click += (s, e) => DoAction("release");
            _btnUnlock.Click  += (s, e) => DoAction("unlock");
            _btnNewRev.Click  += (s, e) => DoAction("newrev");
            _btnRollback.Click += (s, e) => DoAction("rollback");

            // ── Engineer Actions (same y-position, shown for non-masters) ─
            int engY = y - S(5 * 28); // start at same position as master buttons
            Label engLbl = MakeSectionHeader("ENGINEER ACTIONS", fSection, x, engY - S(20), w);
            engLbl.Visible = !isMaster;
            this.Controls.Add(engLbl);

            _btnReqUnlock     = MakeActionButton("Request Unlock",    cOrange, fBtn, x, w, ref engY, !isMaster);
            _btnReqRevision   = MakeActionButton("Request Revision",  cDark,   fBtn, x, w, ref engY, !isMaster);
            _btnReqRelease    = MakeActionButton("Request Release",   cGreen,  fBtn, x, w, ref engY, !isMaster);
            _btnOpenLinkedEng = MakeActionButton("Open Drawing",      cBrand,  fBtn, x, w, ref engY, !isMaster);
            _btnMyRequests    = MakeActionButton("My Requests",       cPurple, fBtn, x, w, ref engY, !isMaster);

            _btnReqUnlock.Click      += (s, e) => DoAction("requnlock");
            _btnReqRevision.Click    += (s, e) => DoAction("requestrev");
            _btnReqRelease.Click     += (s, e) => DoAction("reqrelease");
            _btnOpenLinkedEng.Click  += (s, e) => DoAction("openlinked");
            _btnMyRequests.Click     += (s, e) => DoAction("myrequests");

            y += S(4);
            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── File History ──────────────────────────────────────────
            this.Controls.Add(MakeSectionHeader("FILE HISTORY", fSection, x, y, w));
            y += S(20);

            _historyPanel = new Panel
            {
                Location = new Point(x, y),
                Width = w,
                Height = S(260),
                BackColor = cBg,
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };
            this.Controls.Add(_historyPanel);
            y += S(266);

            // ── Pending Requests (Master only) ────────────────────────
            // Divider + button are BOTH Master-only: engineers don't get the
            // Pending Requests button, so its divider must be gated too (else
            // engineers see an orphan line above the Vault Dashboard button).
            if (isMaster)
            {
                this.Controls.Add(Divider(x, y, w));
                y += S(10);
            }

            _btnRequests = new Button
            {
                Text = "",   // drawn manually in Paint so we can mix alignments
                Font = fSection,
                Width = w,
                Height = S(26),
                Location = new Point(x, y),
                BackColor = cBrandDark,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Visible = isMaster
            };
            _btnRequests.FlatAppearance.BorderSize = 0;
            _btnRequests.Paint += (s, pe) =>
            {
                var btn = (Button)s;
                var g = pe.Graphics;
                // "Pending Requests" — centered
                TextRenderer.DrawText(g, "Pending Requests", btn.Font,
                    new Rectangle(0, 0, btn.Width, btn.Height), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine);
                // count badge — right-aligned, only when > 0
                if (_pendingCount > 0)
                {
                    TextRenderer.DrawText(g, "(" + _pendingCount + ")", btn.Font,
                        new Rectangle(0, 0, btn.Width - S(8), btn.Height), Color.White,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine);
                }
            };
            _btnRequests.Click += (s, e) => OpenRequestsPopup();
            this.Controls.Add(_btnRequests);
            if (isMaster) y += S(30);

            // ── Vault Dashboard (all users) ───────────────────────────
            // Read-only whole-vault view. Engineers get it too: it only opens
            // files (OpenByPath, which respects every vault rule) — no Master
            // actions — so self-service status visibility carries no risk.
            Button btnDashboard = new Button
            {
                Text = "Vault Dashboard",
                Font = fBtn,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnDashboard.FlatAppearance.BorderSize = 0;
            btnDashboard.Click += (s, e) => OpenDashboard();
            this.Controls.Add(btnDashboard);
            y += S(28);

            // (The Audit Report is reached by switching from inside the Vault
            // Dashboard — see OpenDashboard's view-switch loop — so it needs no
            // separate task-pane button, keeping the pane uncluttered.)

            // ── Where Used (all users) ────────────────────────────────
            // Read-only impact analysis: which assemblies reference a part/sub-
            // assembly. Seeds with the ACTIVE file (a drawing resolves to the model
            // it documents — a drawing isn't a component), and the form's Find box
            // can switch to any tracked file without reopening. Engineers get it too
            // (read-only; it only opens files via the vault rules).
            Button btnWhereUsed = new Button
            {
                Text = "Where Used",
                Font = fBtn,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnWhereUsed.FlatAppearance.BorderSize = 0;
            btnWhereUsed.Click += (s, e) => OpenWhereUsed();
            this.Controls.Add(btnWhereUsed);
            y += S(28);

            // ── Send Test Email (all users) ───────────────────────────
            Button btnTestEmail = new Button
            {
                Text = "Send Test Email",
                Font = fBtn,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnTestEmail.FlatAppearance.BorderSize = 0;
            btnTestEmail.Click += (s, e) => SendTestEmail();
            this.Controls.Add(btnTestEmail);
            y += S(28);

            // (Remove from Vault moved to the Vault Dashboard row right-click —
            // "Remove from Vault…", Masters only. It acts BY PATH so the file need
            // not be the active document, and sits next to the Mark Obsolete /
            // Reinstate lifecycle actions where retirement logically belongs.)

            // 1px sentinel — pins the AutoScroll virtual bottom to remove gap
            this.Controls.Add(new Panel
            {
                BackColor = cBg,
                Location = new Point(0, y + S(2)),
                Width = S(210),
                Height = 1
            });
        }

        // ── Refresh Pending Requests button count (Master only) ──────
        private void RefreshRequests()
        {
            try
            {
                if (_btnRequests == null) return;
                bool isMaster = DatabaseManager.GetUserRole(
                    PDMLiteAddin.CurrentUser) == "Master";
                if (!isMaster) return;

                _pendingCount = DatabaseManager.GetPendingRequests().Count;
                _btnRequests.Invalidate(); // Paint redraws with latest count
            }
            catch { } // DB unreachable — keep the last known count
        }

        private void OpenRequestsPopup()
        {
            using (var form = new PendingRequestsForm(_scale))
                form.ShowDialog(this);
            RefreshRequests();
        }

        // Open the full-screen Vault Dashboard (all users), with a SINGLE-WINDOW
        // view switch between it and the Audit Report: each form has a button that
        // closes it asking to switch to the other, and this loop reopens the
        // other one (so only ever one window is up at a time — no modal stacking).
        // The loop ends when a form is closed normally (Close/Esc) or — for the
        // dashboard — with a file to open, which is deferred until after the modal
        // closes (OpenByPath opens the canonical WIP copy, read-only when Released).
        private void OpenDashboard()
        {
            bool showAudit = false;
            while (true)
            {
                if (showAudit)
                {
                    using (var form = new AuditReportForm(_scale))
                    {
                        form.ShowDialog(this);
                        if (form.SwitchToDashboard) { showAudit = false; continue; }
                    }
                    break; // closed normally
                }

                using (var form = new VaultDashboardForm(_scale))
                {
                    form.ShowDialog(this);
                    if (form.SwitchToAudit) { showAudit = true; continue; }

                    if (!string.IsNullOrEmpty(form.FileToOpen))
                    {
                        try
                        {
                            VaultManager.OpenByPath(form.FileToOpen);
                            // "Open Model" on a config-specific drawing lands the
                            // model on that drawing's configuration (best-effort).
                            if (!string.IsNullOrEmpty(form.FileToOpenConfig))
                            {
                                ModelDoc2 doc = PDMLiteAddin.SwApp
                                    ?.GetOpenDocumentByName(form.FileToOpen) as ModelDoc2;
                                if (doc != null)
                                    doc.ShowConfiguration2(form.FileToOpenConfig);
                            }
                            // API open from the dashboard — same as the search
                            // card, drive the obsolete-components warning for the
                            // opened file (deferred + forced past the guard).
                            DeferObsoleteWarning(form.FileToOpen);
                        }
                        catch { }
                    }
                }
                break; // closed normally / file opened
            }
        }

        // Open the read-only Where Used viewer, seeded with the ACTIVE file. A
        // drawing isn't a component, so resolve it to the model it documents and
        // show the MODEL's where-used (the design decision). With no active file
        // the viewer opens empty and the user searches via its Find box. If the
        // user picks a parent assembly to open, do so after the modal closes.
        private void OpenWhereUsed()
        {
            string seedPath = null, seedName = null, seedPartNo = null, seedConfig = null;
            try
            {
                ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                if (doc != null)
                {
                    if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        string model = VaultManager.GetDrawingReferencedModel(doc);
                        if (!string.IsNullOrEmpty(model))
                        {
                            seedPath = model; seedName = Path.GetFileName(model);
                            // The config the drawing documents drives its Part No
                            // (a multi-config model has a different PN per config).
                            seedPartNo = VaultManager.GetDrawingPartNo(doc);
                            seedConfig = VaultManager.GetDrawingPrimaryConfig(doc);
                        }
                    }
                    else
                    {
                        string p = doc.GetPathName();
                        if (!string.IsNullOrEmpty(p))
                        {
                            seedPath = p; seedName = Path.GetFileName(p);
                            // Seed from the ACTIVE configuration (its own Part No),
                            // not the file's primary/last-saved config.
                            seedPartNo = PropertyValidator.GetProperty(doc, "PartNo");
                            try
                            {
                                var cfg = doc.GetActiveConfiguration()
                                    as SolidWorks.Interop.sldworks.Configuration;
                                if (cfg != null) seedConfig = cfg.Name;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                string toOpen = null;
                using (var v = new WhereUsedForm(seedPath, seedName, seedPartNo, seedConfig))
                {
                    v.ShowDialog(this);
                    toOpen = v.FileToOpen;
                }
                if (!string.IsNullOrEmpty(toOpen)) OpenFile(toOpen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Where Used:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── Send a diagnostic test email and show the result ──────────
        private void SendTestEmail()
        {
            bool success;
            string result = EmailManager.SendTestEmail(out success);
            MessageBox.Show(result, "BCore PDM — Test Email",
                MessageBoxButtons.OK,
                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // ── Search ────────────────────────────────────────────────────
        private void RunSearch()
        {
            string term = _searchBox.Text.Trim();
            ClearAndDispose(_resultsPanel);
            // Drop the previous render's tooltip registrations on EVERY rebuild
            // (audit-C4): a WinForms ToolTip keeps every SetToolTip'd control in
            // its internal table until RemoveAll/Dispose — disposing the control
            // does NOT remove it. Done here (not in RenderCards) so the empty /
            // no-results / DB-error paths reset it too, not just populated renders.
            _cardTip.RemoveAll();
            _thumbQueue.Clear(); // discard pending preview loads for the old cards

            if (string.IsNullOrEmpty(term))
            {
                _resultsPanel.Height = 0;
                return;
            }

            bool truncated;
            List<VaultFile> results;
            try
            {
                results = DatabaseManager.SearchFiles(term, out truncated);
            }
            catch
            {
                // DB unreachable (network down, or vault.xml missing with an
                // unrestorable backup — LoadOrCreate now THROWS rather than
                // bootstrapping an empty vault). This runs on the search
                // timer's tick: an uncaught throw here would be an unhandled
                // exception on SOLIDWORKS' message loop.
                _resultsPanel.Controls.Add(new Label
                {
                    Text = "Vault unavailable — check the N: drive.",
                    Font = _fReg35,
                    ForeColor = cRed,
                    Location = new Point(0, S(4)),
                    AutoSize = false,
                    Width = S(188),
                    Height = S(20)
                });
                _resultsPanel.Height = S(28);
                return;
            }

            if (results.Count == 0)
            {
                _resultsPanel.Controls.Add(new Label
                {
                    Text = "No files found for: " + term,
                    Font = _fReg35,
                    ForeColor = cTextLight,
                    Location = new Point(0, S(4)),
                    AutoSize = false,
                    Width = S(188),
                    Height = S(20)
                });
                _resultsPanel.Height = S(28);
                return;
            }

            // Build ONE card per matching configuration. Config name = Part No by
            // convention, so a multi-config part yields a card per config, each
            // showing THAT config's own PartNo / Description / Revision (never the
            // active config's). A drawing result maps back to its model and is
            // skipped if that model also matched (it is expanded there instead).
            var cards = BuildConfigCards(results, term.ToLowerInvariant());
            RenderCards(cards, truncated, null);
        }

        // Render the search-result cards into the results panel. headerText is
        // an optional caption row above the cards (currently always null — the
        // quick box clears on empty; RECENTLY OPENED lives in Advanced Search).
        private void RenderCards(List<SearchGroup> cards, bool truncated,
            string headerText)
        {
            int ry = 0;
            int rw = S(188);

            if (!string.IsNullOrEmpty(headerText))
            {
                _resultsPanel.Controls.Add(new Label
                {
                    Text = headerText,
                    Font = _fItalic33,
                    ForeColor = cTextLight,
                    Location = new Point(S(2), ry + S(2)),
                    AutoSize = false,
                    Width = rw,
                    Height = S(16)
                });
                ry += S(18);
            }

            // Cap the rendered cards. SearchFiles caps at 50 FILES, but a
            // multi-config part expands to ONE card per configuration, so 50
            // files could still explode into hundreds of cards and freeze the
            // panel at scale. Trim to MaxCards and flag truncation so the
            // "refine your search" hint shows below.
            const int MaxCards = 50;
            if (cards.Count > MaxCards)
            {
                cards = cards.GetRange(0, MaxCards);
                truncated = true;
            }

            int barW = S(15);
            // Compact card: the FILE NAME and the DESCRIPTION each get their own
            // FULL-WIDTH row (so a long name/description is never crowded by the
            // preview); the part number and revision sit beside a SMALL preview.
            int cardH = S(98);
            int contentLeft = barW + S(6);
            // SMALL square preview tile on the RIGHT, beside the part-no + rev
            // rows ONLY (between the full-width file-name row above and the
            // full-width description row below) — keeps the card compact.
            int thumbW = S(30);
            int thumbX = rw - thumbW - S(6);
            int thumbY = S(26);
            // The file name + description use the FULL width; the part no / rev
            // rows sit to the LEFT of the preview (textW stops before it) and
            // ELLIPSIS at the preview edge rather than vanishing when long.
            int fullW = rw - contentLeft - S(6);
            int textW = thumbX - contentLeft - S(6);
            int btnLeft = contentLeft;
            int btnFullW = rw - btnLeft - S(3);

            foreach (SearchGroup g in cards)
            {
                // Route through the shared StatusColor helper (single source of
                // truth) — the old inline Released/Locked/else mapping here had no
                // Obsolete case, so an Obsolete card's bar fell through to cBrand
                // (the same blue as WIP). StatusColor returns green/orange/grey/
                // blue for Released/Locked/Obsolete/default, identical otherwise.
                Color statusColor = StatusColor(g.Status);
                string statusText = (string.IsNullOrEmpty(g.Status)
                                        ? "WIP" : g.Status).ToUpper();

                string modelPath = g.ModelPath;
                string drawingPath = g.DrawingPath;
                string configName = g.ConfigName;
                bool hasModel = !string.IsNullOrEmpty(modelPath);

                Panel card = new Panel
                {
                    Location = new Point(0, ry),
                    Width = rw,
                    Height = cardH,
                    BackColor = cCard,
                    BorderStyle = BorderStyle.None
                };

                // ── Thick status bar with vertical (rotated) status text ──
                Panel bar = new Panel
                {
                    BackColor = statusColor,
                    Location = new Point(0, 0),
                    Width = barW,
                    Height = cardH
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
                    // Brushes.White is the framework's cached brush (never
                    // dispose it); font + format are the shared fields.
                    gr.DrawString(statusText, _fBold31, Brushes.White,
                        0, 0, _sfBarCenter);
                };
                card.Controls.Add(bar);

                // ── Thumbnail (lazy SOLIDWORKS preview; click = enlarge) ──
                // Use the model's preview when there is one, else the drawing's.
                string thumbPath = hasModel ? modelPath : drawingPath;
                string thumbCfg = configName;
                var thumb = new ThumbPanel(thumbPath, thumbCfg,
                    () => GetThumbnail(thumbPath, thumbCfg), RequestThumb)
                {
                    Location = new Point(thumbX, thumbY),
                    Width = thumbW,
                    Height = thumbW, // square, top-right corner
                    BackColor = cCard
                };
                thumb.Click += (s, e) => ShowLargePreview(thumbPath, thumbCfg);
                card.Controls.Add(thumb);

                // ── File name (no extension) — FULL-WIDTH top row, so a long
                // name uses the whole card and is never crowded by the preview.
                // Fall back to the part number if the name is somehow blank, so
                // the top row is never empty. ──────────────────────────────
                card.Controls.Add(new Label
                {
                    Text = FirstNonBlank(g.DisplayName, g.PartNumber),
                    Font = _fBold38,
                    ForeColor = cTextDark,
                    Location = new Point(contentLeft, S(7)),
                    AutoSize = false,
                    Width = fullW,
                    Height = S(16),
                    AutoEllipsis = true
                });

                // ── Part number (own row) ─────────────────────────────────
                // PartNumber is the config's own PN (= config name) with a
                // file-level fallback; whitespace-safe so a blank/space-only
                // value still shows the honest "No Part No" placeholder rather
                // than an empty line.
                card.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(g.PartNumber)
                                ? "No Part No" : g.PartNumber,
                    Font = _fBold35,
                    ForeColor = cTextGray,
                    Location = new Point(contentLeft, S(26)),
                    AutoSize = false,
                    Width = textW,
                    Height = S(14),
                    AutoEllipsis = true
                });

                // ── Revision (own row) ────────────────────────────────────
                card.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(g.Revision)
                                ? "" : "REV " + g.Revision,
                    Font = _fBold35,
                    ForeColor = cTextGray,
                    Location = new Point(contentLeft, S(42)),
                    AutoSize = false,
                    Width = textW,
                    Height = S(14),
                    AutoEllipsis = true
                });

                // ── Description row — repurposed for the two states an engineer
                // most needs to see at a glance: an Obsolete card shows
                // "→ Superseded by X" (the replacement), a Locked card shows
                // "Locked by X" (the owner — who has it, the PDM check-out cue),
                // else the description. The full description is always in the
                // hover tooltip. Reusing this row keeps the fixed card height. ──
                bool obsoleteWithRepl =
                    string.Equals(g.Status, "Obsolete",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(g.SupersededBy);
                bool lockedWithOwner =
                    string.Equals(g.Status, "Locked",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(g.LockedBy);
                card.Controls.Add(new Label
                {
                    Text = obsoleteWithRepl
                        ? "→ Superseded by " + g.SupersededBy
                        : lockedWithOwner
                            ? "Locked by " + g.LockedBy
                            : (string.IsNullOrWhiteSpace(g.Description)
                                    ? "(no description)" : g.Description),
                    Font = _fReg33,
                    ForeColor = obsoleteWithRepl ? cMaroon
                              : lockedWithOwner ? cOrange : cTextLight,
                    Location = new Point(contentLeft, S(58)),
                    AutoSize = false,
                    Width = fullW,
                    Height = S(14),
                    AutoEllipsis = true
                });

                // ── Two buttons: Open Part/Assembly + Open Drawing ────────
                // Full width along the bottom (btnLeft/btnFullW), below the
                // thumbnail and the text rows, at their original size.
                int gap = S(4);
                int btnW = (btnFullW - gap) / 2;
                int btnY = S(76);
                int btnH = S(18);

                string partLabel = g.ModelExt == ".sldasm"
                                    ? "Open ASM" : "Open PRT";
                Button btnModel = new Button
                {
                    Text = partLabel,
                    Font = _fBold34,
                    Width = btnW,
                    Height = btnH,
                    Location = new Point(btnLeft, btnY),
                    BackColor = hasModel ? cBrand : cTextLight,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Enabled = hasModel,
                    Cursor = Cursors.Hand
                };
                btnModel.FlatAppearance.BorderSize = 0;
                if (hasModel)
                    btnModel.Click += (s, e) => OpenFileConfig(modelPath, configName);
                card.Controls.Add(btnModel);

                Button btnDrawing = new Button
                {
                    Text = "Open DRW",
                    Font = _fBold34,
                    Width = btnW,
                    Height = btnH,
                    Location = new Point(btnLeft + btnW + gap, btnY),
                    BackColor = cBrandDark,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnDrawing.FlatAppearance.BorderSize = 0;
                btnDrawing.Click += (s, e) =>
                    OpenDrawingResult(modelPath, drawingPath, configName);
                card.Controls.Add(btnDrawing);

                // ── Divider ───────────────────────────────────────────────
                card.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(0, cardH - S(1)),
                    Width = rw,
                    Height = S(1)
                });

                // ── Hover tooltip with the FULL metadata the compact card
                // ellipsises (full name / PN / rev / description) plus status,
                // lock owner, superseded-by and modified-by/date — the detail
                // industry tools surface on hover. One SHARED ToolTip (no
                // per-card alloc); set on the card AND every child so a hover
                // anywhere on the tile shows it. ──
                string tip = BuildCardTooltip(g);
                _cardTip.SetToolTip(card, tip);

                // ── Right-click menu (Copy Part No / Copy File Path / Open
                // Containing Folder / Where Used) — parity with the Advanced
                // Search cards and every industry PDM. ONE shared menu assigned
                // via the ContextMenuStrip PROPERTY on the card AND every child
                // (so a right-click anywhere on the tile works AND consumes
                // WM_CONTEXTMENU — a MouseUp+Show let SOLIDWORKS' own menu still
                // fire). Tag = this card's SearchGroup; the menu's Opening reads
                // SourceControl.Tag → _menuCard. ──
                card.Tag = g;
                card.ContextMenuStrip = _cardMenu;
                foreach (Control ch in card.Controls)
                {
                    _cardTip.SetToolTip(ch, tip);
                    ch.Tag = g;
                    ch.ContextMenuStrip = _cardMenu;
                }

                _resultsPanel.Controls.Add(card);
                ry += cardH + S(2);
            }

            // Show first N only — prompt the user to narrow a broad search.
            if (truncated)
            {
                _resultsPanel.Controls.Add(new Label
                {
                    Text = "Showing first " + cards.Count +
                           " — refine your search to narrow results.",
                    Font = _fItalic33,
                    ForeColor = cTextLight,
                    Location = new Point(0, ry + S(2)),
                    AutoSize = false,
                    Width = rw,
                    Height = S(22)
                });
                ry += S(26);
            }

            _resultsPanel.Height = ry;
        }

        // Multi-line hover tooltip: the FULL metadata the compact card
        // ellipsises (name / PN / rev / description) + status, lock owner,
        // superseded-by and modified-by/date — the detail industry tools surface
        // on hover. Invariant date so a non-US locale can't write "06.13.2026".
        private string BuildCardTooltip(SearchGroup g)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(FirstNonBlank(g.DisplayName, g.PartNumber));
            sb.Append("\nPart No: ").Append(
                string.IsNullOrWhiteSpace(g.PartNumber) ? "—" : g.PartNumber);
            if (!string.IsNullOrWhiteSpace(g.Revision))
                sb.Append("    REV ").Append(g.Revision);
            if (g.TotalConfigs > 1)
                sb.Append("    (").Append(g.TotalConfigs).Append(" configs)");
            if (!string.IsNullOrWhiteSpace(g.Description))
                sb.Append("\n").Append(g.Description);
            sb.Append("\nStatus: ").Append(
                string.IsNullOrEmpty(g.Status) ? "WIP" : g.Status);
            if (string.Equals(g.Status, "Locked", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(g.LockedBy))
                sb.Append("  ·  Locked by ").Append(g.LockedBy);
            if (!string.IsNullOrWhiteSpace(g.SupersededBy))
                sb.Append("\n→ Superseded by ").Append(g.SupersededBy);
            if (!string.IsNullOrWhiteSpace(g.ModifiedBy) ||
                g.ModifiedDate > DateTime.MinValue)
            {
                sb.Append("\nModified");
                if (!string.IsNullOrWhiteSpace(g.ModifiedBy))
                    sb.Append(" by ").Append(g.ModifiedBy);
                if (g.ModifiedDate > DateTime.MinValue)
                    sb.Append(" · ").Append(g.ModifiedDate.ToString(
                        "MM/dd/yyyy HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Shared right-click menu for result/recent cards. The menu is assigned
        // to each card control via the ContextMenuStrip PROPERTY (not a MouseUp
        // handler) — only that consumes Windows' WM_CONTEXTMENU, so SOLIDWORKS'
        // own task-pane menu ("Enable CommandManager / Toolbars / Customize")
        // never shows. _menuCard is resolved from the right-clicked control's Tag.
        private ContextMenuStrip BuildCardMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Copy Part No", null, (s, e) => CardCopyPartNo());
            m.Items.Add("Copy File Path", null, (s, e) => CardCopyPath());
            m.Items.Add("Open Containing Folder", null, (s, e) => CardOpenFolder());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Where Used…", null, (s, e) => CardWhereUsed());
            m.Opening += (s, e) =>
            {
                _menuCard = m.SourceControl?.Tag as SearchGroup;
                if (_menuCard == null) { e.Cancel = true; return; }
                _searchTimer.Stop(); // a queued tick would dispose this card
            };
            return m;
        }

        // The on-disk path a card represents (the model, else the drawing).
        private static string CardFilePath(SearchGroup g)
        {
            if (g == null) return null;
            return !string.IsNullOrEmpty(g.ModelPath) ? g.ModelPath : g.DrawingPath;
        }

        private void CardCopyPartNo()
        {
            if (_menuCard == null) return;
            string pn = FirstNonBlank(_menuCard.PartNumber, _menuCard.DisplayName);
            try { if (!string.IsNullOrEmpty(pn)) Clipboard.SetText(pn); } catch { }
        }

        private void CardCopyPath()
        {
            string p = CardFilePath(_menuCard);
            try { if (!string.IsNullOrEmpty(p)) Clipboard.SetText(p); } catch { }
        }

        private void CardOpenFolder()
        {
            string p = CardFilePath(_menuCard);
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                if (File.Exists(p))
                    System.Diagnostics.Process.Start(
                        "explorer.exe", "/select,\"" + p + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(p);
                    if (Directory.Exists(dir))
                        System.Diagnostics.Process.Start(
                            "explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { }
        }

        private void CardWhereUsed()
        {
            if (_menuCard == null) return;
            // Where-used is a MODEL question — a drawing is never a component, so
            // querying a drawing path always reports "not used". For an orphan-
            // drawing card (no model record) resolve the model from the drawing;
            // if there is none, say so rather than running a misleading query.
            string path   = _menuCard.ModelPath;
            string partNo = _menuCard.PartNumber;
            string config = _menuCard.ConfigName;
            if (string.IsNullOrEmpty(path))
            {
                VaultFile model = null;
                try { model = DatabaseManager.GetModelForDrawing(_menuCard.DrawingPath); }
                catch { }
                if (model == null || string.IsNullOrEmpty(model.FilePath))
                {
                    MessageBox.Show(
                        "No part/assembly is linked to this drawing, so Where Used " +
                        "has nothing to trace.",
                        "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                path   = model.FilePath;
                partNo = model.PartNumber;
                config = null; // model's primary config
            }
            try
            {
                string toOpen = null;
                using (var v = new WhereUsedForm(path, Path.GetFileName(path),
                    partNo, config))
                {
                    v.ShowDialog(this);
                    toOpen = v.FileToOpen;
                }
                if (!string.IsNullOrEmpty(toOpen)) OpenFile(toOpen);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open Where Used:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private const int ShellThumbPx = 256; // shell request size, downscaled to the tile

        // A card asks (on its first paint) for its preview. Try the Windows SHELL
        // thumbnail on a BACKGROUND thread FIRST (zero UI cost — see ShellThumbnail
        // / ShellThumbnailLoader); a cache hit short-circuits; a shell MISS falls
        // back to the throttled SW-API read (QueueThumbLoad → ThumbTick → the
        // GetThumbnail path), so a machine without the shell handler is no worse
        // than before.
        private void RequestThumb(ThumbPanel p)
        {
            if (p == null || p.IsDisposed) return;
            string key = p.FilePath + "|" + (p.Config ?? "");
            Image cached;
            if (_thumbCache.TryGetValue(key, out cached)) { p.SetImage(cached); return; }
            try
            {
                ShellThumbnailLoader.Request(this, () => p != null && !p.IsDisposed,
                    p.FilePath, ShellThumbPx, raw => OnShellThumb(p, key, raw));
            }
            catch { QueueThumbLoad(p); } // loader unavailable → SW fallback
        }

        // A background shell result arrives on the UI thread. Build + cache the
        // tile and show it; a shell MISS (null) falls back to the throttled SW read.
        private void OnShellThumb(ThumbPanel p, string key, Bitmap raw)
        {
            if (raw == null) { if (p != null && !p.IsDisposed) QueueThumbLoad(p); return; }
            Image tile = null;
            try { tile = MakeTile(raw); } catch { tile = null; }
            finally { try { raw.Dispose(); } catch { } }
            if (tile == null) { if (p != null && !p.IsDisposed) QueueThumbLoad(p); return; }
            StoreThumb(key, tile);
            if (p != null && !p.IsDisposed) p.SetImage(tile);
        }

        // A ThumbPanel that missed the shell asks for its preview via the SW API.
        // We enqueue and make sure the throttle timer is running — the COM read
        // happens in ThumbTick, off the paint path, one per tick.
        private void QueueThumbLoad(ThumbPanel p)
        {
            if (p == null || p.IsDisposed) return;
            _thumbQueue.Enqueue(p);
            if (!_thumbLoadsSuspended && _thumbTimer != null && !_thumbTimer.Enabled)
                _thumbTimer.Start();
        }

        // Load ONE queued thumbnail per tick (throttled so cards stay snappy and
        // the UI breathes between slow network reads). The re-entrancy guard
        // matters: a load's COM call pumps the STA loop, which can re-dispatch the
        // timer's WM_TIMER; the guard makes that nested tick a no-op. Stops the
        // timer when the queue drains. Skips disposed panels (stale cards).
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

        // Pause/resume preview loading around a heavy SW operation (doc open,
        // large preview) so a queued read never races it.
        private void SuspendThumbLoads(bool suspend)
        {
            _thumbLoadsSuspended = suspend;
            if (suspend) _thumbTimer?.Stop();
            else if (_thumbQueue.Count > 0) _thumbTimer?.Start();
        }

        // Get a small cached thumbnail for a file's SOLIDWORKS preview (null on
        // failure → the card draws a placeholder). MUST run on the UI thread
        // (the SOLIDWORKS API is not thread-safe — reached only via the serialized
        // ThumbTick, which runs on the UI thread, NEVER inside a paint). Caches
        // the result (INCLUDING null, so a preview-less file is read at most once);
        // caps the cache to bound GDI handles.
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
                        thumb = MakeTile(full);
                }
            }
            catch { thumb = null; }

            StoreThumb(key, thumb);
            return thumb;
        }

        // Crop the preview's baked background, then shrink to the card-tile size.
        // CropToContent may return `full` itself (nothing to trim) — only dispose a
        // NEW crop. Shared by the shell path (OnShellThumb) + SW path (GetThumbnail).
        private Image MakeTile(Image full)
        {
            if (full == null) return null;
            Image cropped = CropToContent(full);
            try { return ResizeToThumb(cropped, S(96)); }
            finally { if (cropped != full) cropped.Dispose(); }
        }

        // Cache a resolved tile under "path|config". Crude bound: when full, drop
        // the cache (do NOT dispose — live tiles SHARE the cached Image; GC
        // reclaims the unreferenced ones once their panels go away).
        private void StoreThumb(string key, Image img)
        {
            if (_thumbCache.Count >= ThumbCacheCap) _thumbCache.Clear();
            _thumbCache[key] = img;
        }

        // Resize to fit within maxPx (preserve aspect, never upscale) into a NEW
        // bitmap; the source is disposed by the caller.
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

        // Trim the near-white (or transparent) border SOLIDWORKS bakes around a
        // preview so the model FILLS the (small) tile instead of floating in
        // white. Scans the bounding box of non-background pixels and returns a
        // crop with a small margin. Returns the SOURCE unchanged when there is
        // nothing to trim, the image is all background, or anything fails — so a
        // caller must dispose the result ONLY when it differs from the source.
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
                // (corners + edge midpoints) rather than assuming white — SW's
                // preview is usually white but some seats save a light-gray /
                // gradient backdrop where a hardcoded white cutoff trims nothing.
                // The per-channel MEDIAN is robust to a model that happens to
                // reach one sample point. (BGRA buffer.)
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

                // ~5% margin so the model is not flush against the tile edge.
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

        // Click a card thumbnail → show the full preview bitmap larger in a
        // modal. Re-extracts the un-resized preview; null → friendly info.
        private void ShowLargePreview(string filePath, string config)
        {
            // This opens a modal (ShowDialog) that pumps the message loop; a queued
            // debounce tick would then RunSearch → ClearAndDispose the very card
            // whose Click is on the stack (ObjectDisposedException, audit-M8). Stop
            // it, exactly like the context-menu / Open-button paths.
            _searchTimer.Stop();
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
            // NOTE: the large popup shows the FULL preview (NOT CropToContent) —
            // SOLIDWORKS already frames the model centred with even margins, so
            // the natural whitespace reads cleaner and more uniform at this size.
            // The tight crop is reserved for the small card tiles, where every
            // pixel counts.
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
                    // Esc closes (KeyPreview so the form sees the key before the
                    // PictureBox) — the modal had no keyboard escape; a click on
                    // the image closes it too (common preview-popup UX).
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

        // Quick-access popup: Saved searches / Recent files / Favorites. Opened
        // from the search hint link. Acts AFTER the modal closes (deferred, like
        // the Advanced Search / dashboard popups): a saved search re-runs the
        // quick search; a recent/favorite file opens.
        private void OpenQuickAccess()
        {
            try
            {
                string term = (_searchBox.Text ?? "").Trim();
                using (var f = new QuickAccessPopup(term))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    if (!string.IsNullOrEmpty(f.FileToOpen))
                        OpenFile(f.FileToOpen);
                    else if (!string.IsNullOrEmpty(f.TermToRun))
                    {
                        _searchBox.Text = f.TermToRun;
                        _searchTimer.Stop();   // setting Text armed the debounce —
                        RunSearch();           // run once, now, instead
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Quick access could not open:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFile(string filePath)
        {
            // If a thumbnail preview read is on the stack (its COM call pumped the
            // loop and dispatched this click), don't open the doc NESTED inside
            // that read — re-post so the open runs once the read unwinds. The
            // re-posted call sees _thumbDraining=false and proceeds normally.
            if (_thumbDraining)
            {
                try { BeginInvoke((Action)(() => OpenFile(filePath))); return; }
                catch { /* no handle — fall through and open inline */ }
            }

            _searchBox.Text = "";
            ClearAndDispose(_resultsPanel);
            _thumbQueue.Clear(); // the cards (and any pending preview reads) are gone
            _resultsPanel.Height = 0;

            if (!File.Exists(filePath))
            {
                MessageBox.Show("File not found:\n" + filePath,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Record in the per-user Recent list (local prefs, non-fatal).
            try { UserPrefs.AddRecent(filePath); } catch { }

            try
            {
                ModelDoc2 existingDoc = PDMLiteAddin.SwApp
                    .GetOpenDocumentByName(filePath) as ModelDoc2;

                if (existingDoc != null)
                {
                    int errors = 0;
                    PDMLiteAddin.SwApp.ActivateDoc3(
                        filePath, false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                        ref errors);
                    return;
                }

                string ext = Path.GetExtension(filePath).ToLower();
                int docType = ext == ".sldasm"
                                 ? (int)swDocumentTypes_e.swDocASSEMBLY
                               : ext == ".slddrw"
                                 ? (int)swDocumentTypes_e.swDocDRAWING
                               : (int)swDocumentTypes_e.swDocPART;

                int errs = 0, warnings = 0;
                PDMLiteAddin.SwApp.OpenDoc6(
                    filePath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "", ref errs, ref warnings);
            }
            catch
            {
                MessageBox.Show("Could not open file:\n" + filePath,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Open Drawing from a search card. If the drawing exists, open it; if
        // none exists yet, open the part/assembly (switched to the card's
        // configuration) and create one from it — same behaviour as the
        // task-pane Open Drawing button.
        private void OpenDrawingResult(string modelPath, string drawingPath,
            string configName)
        {
            if (!string.IsNullOrEmpty(drawingPath))
            {
                OpenFile(drawingPath);
                return;
            }

            if (string.IsNullOrEmpty(modelPath))
            {
                MessageBox.Show(
                    "No part or assembly is associated with this drawing.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // No drawing yet — open the model on the right config, then make one.
            OpenFileConfig(modelPath, configName);
            ModelDoc2 model = PDMLiteAddin.SwApp
                ?.GetOpenDocumentByName(modelPath) as ModelDoc2;
            if (model != null) VaultManager.OpenOrCreateDrawing(model);
        }

        // Advanced (property-wide) search popup — opened by double-clicking the
        // "SEARCH FILES" header. The popup is modal and self-contained; it sets a
        // file to open and closes, and we open it AFTER the modal closes (deferred
        // open, mirroring the Vault Dashboard / Where Used pattern) so the open
        // doesn't race the modal teardown.
        private void OpenAdvancedSearch()
        {
            try
            {
                using (var f = new AdvancedSearchForm())
                {
                    if (f.ShowDialog() != DialogResult.OK) return;
                    if (f.OpenDrawing)
                        // Open the model on its config, then open/create its drawing.
                        OpenDrawingResult(f.FileToOpen, null, f.FileToOpenConfig);
                    else if (!string.IsNullOrEmpty(f.FileToOpen))
                        OpenFileConfig(f.FileToOpen, f.FileToOpenConfig);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Advanced Search could not complete:\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Open (or activate, if already open) a model and switch it to the
        // requested configuration. Config name = Part No, so this lands the
        // engineer on the exact config they clicked in the search card.
        private void OpenFileConfig(string filePath, string configName)
        {
            OpenFile(filePath);
            if (!string.IsNullOrEmpty(configName))
            {
                try
                {
                    ModelDoc2 doc = PDMLiteAddin.SwApp
                        ?.GetOpenDocumentByName(filePath) as ModelDoc2;
                    if (doc != null) doc.ShowConfiguration2(configName);
                }
                catch { }
            }
            // Opening from a search card is a synchronous API open inside this
            // click handler — drive the obsolete-components warning for the file
            // we just opened, DEFERRED + forced (see DeferObsoleteWarning).
            DeferObsoleteWarning(filePath);
        }

        // Show the obsolete-components warning for a file opened in-app, AFTER the
        // current open settles. Deferred via BeginInvoke so it runs once the click
        // handler returns and SOLIDWORKS can display a dialog. PATH-BASED + forced:
        // SW fires its own open notification DURING the nested OpenDoc6, which runs
        // the hook and consumes the once-per-open guard — but that message is
        // suppressed while SW is mid-open — so a plain re-call would be guarded out
        // and ActiveDoc may not be the assembly. WarnObsoleteForPath(path, force:true)
        // checks the exact path and re-shows past the guard, so the in-app open
        // warns exactly once (File > Open already warns via its own notification).
        private void DeferObsoleteWarning(string path)
        {
            try
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)(() =>
                    {
                        try { PDMLiteAddin.Instance?.WarnObsoleteForPath(path, true); }
                        catch { }
                    }));
                else
                    PDMLiteAddin.Instance?.WarnObsoleteForPath(path, true);
            }
            catch { }
        }

        // Expand the flat search results into per-configuration cards. A model
        // file yields one card per configuration that matches the term (or every
        // config when the file matched by name / is single-config). A drawing
        // result maps back to its model; if that model also matched it is skipped
        // (expanded under the model), else the model's matching configs are shown,
        // or a drawing-only card for a true orphan with no model.
        private List<SearchGroup> BuildConfigCards(List<VaultFile> results,
            string termL)
        {
            var cards = new List<SearchGroup>();

            // ONE drawing snapshot for the WHOLE search. Each config card needs
            // the drawings for its model+config, and the old per-card
            // GetDrawingsForConfig was up to ~50 full vault.xml SMB loads per
            // search (audit M3). Resolve every card against this in-memory
            // index instead.
            DatabaseManager.DrawingIndex drwIndex;
            try { drwIndex = DatabaseManager.BuildDrawingIndex(); }
            catch { drwIndex = null; } // search still renders; cards just lack the drawing wiring

            var modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in results)
            {
                string ext = Path.GetExtension(f.FilePath).ToLower();
                if (ext == ".sldprt" || ext == ".sldasm")
                    modelPaths.Add(f.FilePath);
            }

            foreach (var f in results)
            {
                string ext = Path.GetExtension(f.FilePath).ToLower();
                if (ext == ".slddrw")
                {
                    VaultFile model =
                        DatabaseManager.GetModelForDrawing(f.FilePath);
                    if (model != null && modelPaths.Contains(model.FilePath))
                        continue; // model matched too — expanded under it
                    if (model != null)
                        AddModelConfigCards(cards, model, termL, drwIndex);
                    else
                        cards.Add(new SearchGroup
                        {
                            DisplayName = Path.GetFileNameWithoutExtension(f.FileName),
                            DrawingPath = f.FilePath,
                            Status = f.Status,
                            SupersededBy = f.SupersededBy,
                            PartNumber = "",
                            Description = ""
                        });
                }
                else
                {
                    AddModelConfigCards(cards, f, termL, drwIndex);
                }
            }
            return cards;
        }

        // Append one card per matching configuration of a model file.
        private void AddModelConfigCards(List<SearchGroup> cards,
            VaultFile model, string termL, DatabaseManager.DrawingIndex drwIndex)
        {
            string fileName = string.IsNullOrEmpty(model.FileName)
                ? Path.GetFileName(model.FilePath) : model.FileName;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            bool nameMatched = baseName.ToLowerInvariant().Contains(termL);

            var configs = model.Configurations;
            if (configs == null || configs.Count == 0)
                configs = new List<ConfigEntry> { new ConfigEntry {
                    Name = model.PartNumber, PartNo = model.PartNumber,
                    Description = model.Description, Revision = model.Revision } };

            int total = configs.Count;
            var shown = new List<ConfigEntry>();
            foreach (var c in configs)
            {
                bool match = total <= 1 || nameMatched
                          || (c.PartNo ?? "").ToLowerInvariant().Contains(termL)
                          || (c.Description ?? "").ToLowerInvariant().Contains(termL);
                if (match) shown.Add(c);
            }
            if (shown.Count == 0) shown = configs; // never drop a matched file

            foreach (var c in shown)
            {
                // This config's drawing — config-specific, or a shared
                // config-table drawing that covers all configs. Resolved
                // against the ONE shared snapshot (no per-card DB load).
                string drawingPath = null;
                var drws = drwIndex?.DrawingsForConfig(model.FilePath, c.Name);
                if (drws != null && drws.Count > 0) drawingPath = drws[0];

                cards.Add(new SearchGroup
                {
                    DisplayName  = baseName,
                    ModelPath    = model.FilePath,
                    ModelExt     = ext,
                    ConfigName   = c.Name,
                    // Per-config value first, then the file-level value. Legacy
                    // records (saved before per-config PN/Desc/Rev indexing) have
                    // empty <Config> fields, so a config card showed a blank PN /
                    // no description / NO revision (Revision had no fallback at
                    // all) even when the file-level fields were populated. Fall
                    // back, whitespace-safe, so those records render their data.
                    PartNumber   = FirstNonBlank(c.PartNo, model.PartNumber),
                    Description  = FirstNonBlank(c.Description, model.Description),
                    Revision     = FirstNonBlank(c.Revision, model.Revision),
                    Status       = model.Status,
                    SupersededBy = model.SupersededBy,
                    DrawingPath  = drawingPath,
                    TotalConfigs = total,
                    // For the hover tooltip + the Locked-card owner line.
                    ModifiedBy   = model.ModifiedBy,
                    ModifiedDate = model.ModifiedDate,
                    LockedBy     = model.LockedBy
                });
            }
        }

        // First non-whitespace value (trimmed), else "". Lets a config card fall
        // back from a blank per-config field to the file-level value.
        private static string FirstNonBlank(params string[] vals)
        {
            if (vals != null)
                foreach (var v in vals)
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }

        // One search card. With multi-config support a card represents a single
        // configuration of a model (or a drawing-only orphan), so it carries the
        // config name, that config's revision, and the file's total config count.
        private class SearchGroup
        {
            public string DisplayName;   // base filename, no extension
            public string ModelPath;     // part/assembly path, or null
            public string ModelExt;      // ".sldprt" / ".sldasm", or null
            public string DrawingPath;   // .slddrw path, or null
            public string ConfigName;    // configuration this card represents
            public string PartNumber;    // this config's PartNo (= config name)
            public string Description;   // this config's description
            public string Revision;      // this config's revision
            public string Status;        // file-level status
            public string SupersededBy;  // replacement Part No (Obsolete files only)
            public int    TotalConfigs;  // number of configs in the file
            public string ModifiedBy;    // last saver (tooltip)
            public DateTime ModifiedDate;// last save time (tooltip)
            public string LockedBy;      // lock owner (shown when Status==Locked)
        }

        // ── Refresh ───────────────────────────────────────────────────
        public void Refresh(ModelDoc2 doc)
        {
            if (doc == null)
            {
                _fileNameLbl.Text = "No file open";
                _statusVal.Text = "—";
                _partNoVal.Text = "—";
                _revVal.Text = "—";
                _lockedVal.Text = "—";
                SetOpenLinkedLabel("Open Drawing");
                PopulateHistoryPanel(null);
                RefreshRequests();
                return;
            }

            // HARDENED name resolution (one real part rendered a BLANK name
            // box, surviving Save As — so the cause is what SOLIDWORKS
            // reports, not the file's bytes). Two failure modes covered:
            // a path with an illegal character makes Path.GetFileName THROW
            // on .NET Framework (silently aborting the whole refresh), and
            // an embedded NUL renders as EMPTY text in GDI. Derive the name
            // defensively and fall back to the document TITLE so the box
            // always shows something identifiable.
            string filePath = "";
            try { filePath = doc.GetPathName() ?? ""; } catch { }
            // Track this as a recently-opened file (any saved doc that becomes
            // active — opened via search, dashboard, or File>Open). Shown when
            // the search box is empty. No-op for an unsaved doc (empty path).
            try { if (!string.IsNullOrEmpty(filePath)) RecentFiles.Add(filePath); }
            catch { }
            string fileName;
            try { fileName = Path.GetFileName(filePath); }
            catch
            {
                int cut = filePath.LastIndexOfAny(new[] { '\\', '/' });
                fileName = cut >= 0 ? filePath.Substring(cut + 1) : filePath;
            }
            if (fileName.IndexOf('\0') >= 0)
                fileName = fileName.Replace("\0", "");
            fileName = fileName.Trim();
            if (string.IsNullOrEmpty(fileName) &&
                !string.IsNullOrEmpty(filePath))
            {
                // Saved doc with an unusable path string — show the title.
                try { fileName = (doc.GetTitle() ?? "").Trim(); } catch { }
            }

            // ONE DB load for status + lock + history (was three separate
            // vault.xml loads on every doc/config switch — audit M3).
            ActiveFileInfo info;
            try { info = DatabaseManager.GetActiveFileInfo(filePath); }
            catch
            {
                // DB unreachable (network down / unrestorable vault) — this
                // runs from BeginInvoke'd deferred refreshes and DoAction,
                // where an uncaught throw is an unhandled message-loop
                // exception inside SOLIDWORKS. Show a dash and stop; the
                // dashboards and ValidateSave surface the error loudly.
                _statusVal.Text = "—";
                _partNoVal.Text = "—";
                _revVal.Text = "—";
                _lockedVal.Text = "—";
                PopulateHistoryPanel(null);
                return;
            }
            string status = info.Status;
            var lockInfo = info.Lock;

            string partNo = PropertyValidator.GetProperty(doc, "PartNo");

            bool isMaster = false;
            try
            {
                isMaster = DatabaseManager.GetUserRole(
                    PDMLiteAddin.CurrentUser) == "Master";
            }
            catch { }
            bool isDrawing = doc.GetType() ==
                (int)swDocumentTypes_e.swDocDRAWING;
            // A drawing's revision is DRIVEN BY THE MODEL it documents (the part is
            // the master), and a rollback leaves the drawing's OWN Revision property
            // stale — so read it from the model like the Part No. A part/assembly
            // shows its active config's own Revision.
            string rev = isDrawing
                ? VaultManager.GetDrawingRevision(doc)
                : PropertyValidator.GetProperty(doc, "Revision");

            // Multi-config indicator. Config name = Part No by convention, so a
            // part/assembly with several configs shows "(N configs)" after the
            // file name. The ActiveConfigChangePostNotify hook re-runs Refresh on
            // every config switch, so the Part No / Revision below always reflect
            // the config the engineer is currently looking at.
            int configCount = 0;
            if (!isDrawing)
            {
                try { configCount = PropertyValidator.GetConfigNames(doc).Count; }
                catch { }
            }

            _fileNameLbl.Text = string.IsNullOrEmpty(fileName)
                ? "Unsaved new file"
                : (configCount > 1 ? fileName + "  (" + configCount + " configs)"
                                   : fileName);

            if (!string.IsNullOrEmpty(status))
            {
                _statusVal.Text = status;
                _statusVal.ForeColor = StatusColor(status);
            }
            else if (string.IsNullOrEmpty(filePath))
            {
                // Brand-new doc, never saved — becomes WIP on first save.
                _statusVal.Text = "WIP";
                _statusVal.ForeColor = StatusColor("");
            }
            else
            {
                // On disk but no vault record: saved outside the vault, or
                // QUARANTINED — first save under a taken name (the post-save
                // guard refuses to track a duplicate). Say so instead of
                // implying the file is a tracked WIP. The duplicate flag came
                // from the SAME load as the status (no extra DB call).
                bool dup = info.HasDuplicateRival;
                // "DUPLICATE" / "Untracked" — the value column fits ~10 chars
                // before clipping (sized for "Not Locked").
                _statusVal.Text = dup ? "DUPLICATE" : "Untracked";
                _statusVal.ForeColor = dup ? cSwRed : cDark;
            }
            // Drawings share the PartNo of the part/assembly they document —
            // pull it from the referenced model so the card matches the part.
            if (isDrawing)
            {
                string drwPartNo = VaultManager.GetDrawingPartNo(doc);
                _partNoVal.Text = string.IsNullOrEmpty(drwPartNo)
                    ? "Drawing" : drwPartNo;
            }
            else
            {
                _partNoVal.Text = string.IsNullOrEmpty(partNo) ? "—" : partNo;
            }
            _revVal.Text = string.IsNullOrEmpty(rev) ? "—" : "REV " + rev;
            _lockedVal.Text = lockInfo.IsLocked ? lockInfo.LockedBy : "Not Locked";
            _lockedVal.ForeColor = lockInfo.IsLocked ? cRed : cGreen;

            // Context-aware Open button: a drawing opens its model, a part/
            // assembly opens its drawing. Both role variants stay in sync.
            SetOpenLinkedLabel(isDrawing
                ? VaultManager.GetDrawingOpenLabel(doc)
                : "Open Drawing");

            // File History — from the same combined load above.
            PopulateHistoryPanel(info.History);

            // Refresh pending requests for masters
            RefreshRequests();
        }

        // Keep both role variants of the context-aware Open button labelled
        // the same (only one is visible at a time, per role).
        private void SetOpenLinkedLabel(string text)
        {
            if (_btnOpenLinkedMaster != null) _btnOpenLinkedMaster.Text = text;
            if (_btnOpenLinkedEng != null) _btnOpenLinkedEng.Text = text;
        }

        // ── History Panel ─────────────────────────────────────────────
        private void PopulateHistoryPanel(List<HistoryEntry> history)
        {
            ClearAndDispose(_historyPanel);

            if (history == null || history.Count == 0)
            {
                _historyPanel.Controls.Add(new Label
                {
                    Text = "No history yet",
                    Font = _fReg33,
                    ForeColor = cTextGray,
                    Location = new Point(S(4), S(6)),
                    AutoSize = false,
                    Width = _historyPanel.Width - S(20),
                    Height = S(18)
                });
                return;
            }

            int hy = S(2);
            foreach (var entry in history)
            {
                string dateStr = "—";
                if (DateTime.TryParse(entry.ChangedDate, out DateTime dt))
                    // MM/dd like everywhere else — the panel rendered dd/MM,
                    // contradicting the project-wide MM/dd/yyyy convention.
                    dateStr = dt.ToString("MM/dd/yy HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture);

                _historyPanel.Controls.Add(new Label
                {
                    Text = "● " + entry.Status,
                    Font = _fBold38,
                    ForeColor = StatusColor(entry.Status),
                    Location = new Point(S(4), hy),
                    AutoSize = false,
                    Width = _historyPanel.Width - S(20),
                    Height = S(16)
                });
                hy += S(17);

                _historyPanel.Controls.Add(new Label
                {
                    Text = dateStr + "  " + entry.ChangedBy,
                    Font = _fReg33,
                    ForeColor = cTextGray,
                    Location = new Point(S(4), hy),
                    AutoSize = false,
                    Width = _historyPanel.Width - S(20),
                    Height = S(14)
                });
                hy += S(15);

                if (!string.IsNullOrEmpty(entry.ChangeNote))
                {
                    // WRAP the note (up to ~3 lines) instead of hard-truncating
                    // on one line — a reason-for-change can be a full sentence,
                    // and a single ellipsised line hid it in the narrow panel.
                    int noteW = _historyPanel.Width - S(20);
                    int oneLine = S(14);
                    int noteH = TextRenderer.MeasureText(
                        entry.ChangeNote, _fReg33,
                        new Size(noteW, int.MaxValue),
                        TextFormatFlags.WordBreak).Height;
                    if (noteH < oneLine) noteH = oneLine;
                    if (noteH > oneLine * 3) noteH = oneLine * 3; // cap at 3 lines
                    _historyPanel.Controls.Add(new Label
                    {
                        Text = entry.ChangeNote,
                        Font = _fReg33,
                        ForeColor = cTextLight,
                        Location = new Point(S(4), hy),
                        AutoSize = false,
                        Width = noteW,
                        Height = noteH,
                        AutoEllipsis = true
                    });
                    hy += noteH + S(1);
                }

                _historyPanel.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(S(4), hy),
                    Width = _historyPanel.Width - S(20),
                    Height = 1
                });
                hy += S(7);
            }
        }

        // ── Button Actions ────────────────────────────────────────────
        // The single choke point for every task-pane button. The whole
        // dispatch is guarded: VaultManager / DatabaseManager calls do raw
        // network I/O, and an N:-drive blip mid-click would otherwise throw
        // unhandled on SOLIDWORKS' message loop (the search and refresh paths
        // are guarded individually; this covers the action buttons).
        private void DoAction(string action)
        {
            try { DoActionCore(action); }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The action could not be completed — the vault may be " +
                    "unavailable.\n\nCheck the N: drive and try again.\n\n" +
                    "Detail: " + ex.Message,
                    "BCore PDM — Vault Unavailable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void DoActionCore(string action)
        {
            ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
            if (doc == null)
            {
                MessageBox.Show("Please open a SOLIDWORKS file first.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path = doc.GetPathName();

            if (action == "openlinked")
            {
                // Context-aware: from a drawing → open the referenced part/
                // assembly; from a part/assembly → open (or create) its drawing.
                if (doc.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
                    VaultManager.OpenReferencedModel(doc);
                else
                    VaultManager.OpenOrCreateDrawing(doc);
                // Defer refresh — ActiveDocChangeNotify fires during NewDocument
                // before InsertModelInPredefinedView runs, so the initial refresh
                // sees a blank drawing. BeginInvoke queues AFTER the full creation
                // completes, so the views are set up and part no / label are correct.
                BeginInvoke((Action)(() =>
                    Refresh(PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2)));
                return;
            }
            else if (action == "release") VaultManager.ReleaseFile(doc);
            else if (action == "unlock") VaultManager.UnlockFile(path);
            else if (action == "newrev")
            {
                VaultManager.StartNewRevision(doc);
                // StartNewRevision closes and reopens the file (and may also
                // close/reopen the drawing). Defer the refresh so SOLIDWORKS
                // fully settles the reopened docs before we read properties.
                BeginInvoke((Action)(() =>
                    Refresh(PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2)));
                return;
            }
            else if (action == "rollback") VaultManager.RollbackRevision(doc);
            else if (action == "requestrev") VaultManager.RequestRevision(doc);
            else if (action == "requnlock") VaultManager.RequestUnlock(doc);
            else if (action == "reqrelease") VaultManager.RequestRelease(doc);
            else if (action == "myrequests") { VaultManager.ViewMyRequests(); return; }

            // Unlock also closes and reopens — use current active doc for refresh.
            Refresh(PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2);
        }

        // ── About Dialog ─────────────────────────────────────────────
        private void ShowAboutDialog()
        {
            int fw = S(320);
            int fh = S(260);

            using (var form = new Form())
            {
                form.Text = "About BCore PDM";
                form.Width  = fw;
                form.Height = fh;
                form.StartPosition    = FormStartPosition.CenterScreen;
                form.FormBorderStyle  = FormBorderStyle.FixedDialog;
                form.MaximizeBox      = false;
                form.MinimizeBox      = false;
                form.BackColor        = Color.White;

                // Top accent bar
                form.Controls.Add(new Panel {
                    BackColor = cBrandDark,
                    Location  = new Point(0, 0),
                    Width     = fw,
                    Height    = S(5)
                });

                int y = S(16);

                // ── "BCore PDM" — "BC" in cBrand, "ore PDM" in cTextDark ──
                // Paint-based panel avoids Label padding causing gap/clipping issues
                // Dialog-local fonts: disposing the form disposes its controls
                // but NOT their fonts, so release them when the dialog closes.
                Font fTitle = new Font("Segoe UI", 10f * _scale, FontStyle.Bold);
                Font fAboutSub = new Font("Segoe UI", 3.8f * _scale);
                Font fAboutName = new Font("Georgia", 7f * _scale,
                    FontStyle.Bold | FontStyle.Italic);
                Font fAboutVer = new Font("Segoe UI", 3.8f * _scale,
                    FontStyle.Bold);
                form.FormClosed += (s, e) =>
                {
                    fTitle.Dispose();
                    fAboutSub.Dispose();
                    fAboutName.Dispose();
                    fAboutVer.Dispose();
                };
                var titlePanel = new Panel {
                    Location  = new Point(0, y),
                    Width     = fw,
                    Height    = S(44),
                    BackColor = Color.White
                };
                titlePanel.Paint += (s, pe) => {
                    var bcSz  = TextRenderer.MeasureText(pe.Graphics, "BC",      fTitle, new Size(fw, S(44)), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    var oreSz = TextRenderer.MeasureText(pe.Graphics, "ore PDM", fTitle, new Size(fw, S(44)), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    int startX = (fw - bcSz.Width - oreSz.Width) / 2;
                    int textY  = (titlePanel.Height - bcSz.Height) / 2;
                    TextRenderer.DrawText(pe.Graphics, "BC",      fTitle, new Point(startX,              textY), cBrand,    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(pe.Graphics, "ore PDM", fTitle, new Point(startX + bcSz.Width, textY), cTextDark, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                };
                form.Controls.Add(titlePanel);
                y += S(48);

                // Divider
                form.Controls.Add(new Panel {
                    BackColor = cBorder,
                    Location  = new Point(S(30), y),
                    Width     = fw - S(60), Height = 1
                });
                y += S(12);

                // "A Product Data Management Solution for"
                form.Controls.Add(new Label {
                    Text = "A Product Data Management Solution for",
                    Font = fAboutSub,
                    ForeColor = cTextGray, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(16)
                });
                y += S(18);

                // "Richards-Wilcox" — Georgia Bold Italic
                form.Controls.Add(new Label {
                    Text = "Richards-Wilcox",
                    Font = fAboutName,
                    ForeColor = cTextDark, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(22)
                });
                y += S(30);

                // "Designed, Developed and Maintained by"
                form.Controls.Add(new Label {
                    Text = "Designed, Developed and Maintained by",
                    Font = fAboutSub,
                    ForeColor = cTextGray, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(16)
                });
                y += S(18);

                // "Bhushan Chougule" — Georgia Bold Italic, same cBrand as "BC"
                form.Controls.Add(new Label {
                    Text = "Bhushan Chougule",
                    Font = fAboutName,
                    ForeColor = cBrand, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(22)
                });
                y += S(30);

                // Divider
                form.Controls.Add(new Panel {
                    BackColor = cBorder,
                    Location  = new Point(S(30), y),
                    Width     = fw - S(60), Height = 1
                });
                y += S(12);

                // "Release Version 1.0"
                form.Controls.Add(new Label {
                    Text = "Release Version 1.0",
                    Font = fAboutVer,
                    ForeColor = cTextLight, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(16)
                });

                form.ShowDialog(this);
            }
        }

        // ── UI Helpers ────────────────────────────────────────────────
        private Label MakeSectionHeader(string text, Font font,
                                         int x, int y, int w)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = cTextGray,
                Location = new Point(x, y),
                AutoSize = false,
                Width = w,
                Height = S(18)
            };
        }

        private Label MakeInfoRowInCard(Panel card, string labelText,
                                         Font labelFont, Font valueFont,
                                         int x, int w, ref int y)
        {
            card.Controls.Add(new Label
            {
                Text = labelText + ":",
                Font = labelFont,
                ForeColor = cTextLight,
                Location = new Point(x, y),
                AutoSize = false,
                Width = S(55),
                Height = S(18)
            });

            Label val = new Label
            {
                Text = "—",
                Font = valueFont,
                ForeColor = cTextDark,
                Location = new Point(x + S(58), y),
                AutoSize = false,
                Width = w - S(58),
                Height = S(18)
            };
            card.Controls.Add(val);
            y += S(20);
            return val;
        }

        private Button MakeActionButton(string text, Color color,
                                         Font font, int x, int w,
                                         ref int y, bool visible)
        {
            Button btn = new Button
            {
                Text = text,
                Font = font,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = color,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0),
                Visible = visible
            };
            btn.FlatAppearance.BorderSize = 0;
            this.Controls.Add(btn);
            y += S(28);
            return btn;
        }

        private Panel Divider(int x, int y, int w)
        {
            return new Panel
            {
                BackColor = cBorder,
                Location = new Point(x, y),
                Width = w,
                Height = S(1)
            };
        }

        private Color StatusColor(string status)
        {
            switch (status)
            {
                case "Released": return cGreen;
                case "Locked": return cOrange;
                case "Obsolete": return Color.FromArgb(120, 120, 120);
                default: return cBrand;
            }
        }
    }

    // Recently-opened vault files, persisted per-user to
    // %LOCALAPPDATA%\BCorePDM\recent.txt (most-recent first, capped). Populated
    // from TaskPaneControl.Refresh whenever a saved file becomes active; READ by
    // AdvancedSearchForm to show recents when its fields are empty. Top-level
    // (not nested) so both surfaces can reach it. All I/O is swallowed — a
    // missing / locked recents file never disrupts either surface.
    internal static class RecentFiles
    {
        private const int Cap = 12;
        private static readonly object _gate = new object();
        // recent.txt is per-user but shared by every SOLIDWORKS instance that user
        // runs in their interactive session, so the in-process _gate alone can't
        // stop two instances clobbering each other's read-modify-write. A
        // session-local named Mutex serialises the Add across those processes.
        // Add runs on the SW UI thread (from Refresh), so the wait is SHORT
        // (50ms): under no contention it acquires instantly; if a peer is stuck
        // we proceed unsynchronised rather than stall a doc switch — the write is
        // best-effort and skipping it is harmless.
        private const string MutexName = "BCorePDM.RecentFiles";

        private static string FilePathOf()
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData),
                "BCorePDM");
            return Path.Combine(dir, "recent.txt");
        }

        public static List<string> Get()
        {
            var list = new List<string>();
            try
            {
                string f = FilePathOf();
                if (!File.Exists(f)) return list;
                foreach (var line in File.ReadAllLines(f))
                {
                    string p = (line ?? "").Trim();
                    if (p.Length == 0) continue;
                    if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                        list.Add(p);
                    if (list.Count >= Cap) break;
                }
            }
            catch { }
            return list;
        }

        public static void Add(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                lock (_gate)
                using (var mtx = new System.Threading.Mutex(false, MutexName))
                {
                    bool held = false;
                    try { held = mtx.WaitOne(50); }
                    catch (System.Threading.AbandonedMutexException) { held = true; }
                    try
                    {
                        var list = Get();
                        // Skip the write when it is already the most-recent entry.
                        if (list.Count > 0 && string.Equals(
                                list[0], filePath, StringComparison.OrdinalIgnoreCase))
                            return;
                        list.RemoveAll(p => string.Equals(
                            p, filePath, StringComparison.OrdinalIgnoreCase));
                        list.Insert(0, filePath);
                        if (list.Count > Cap) list.RemoveRange(Cap, list.Count - Cap);
                        string f = FilePathOf();
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        File.WriteAllLines(f, list);
                    }
                    finally { if (held) try { mtx.ReleaseMutex(); } catch { } }
                }
            }
            catch { }
        }
    }

    // Fetches a file's Windows SHELL thumbnail — the same preview Explorer shows,
    // produced by SOLIDWORKS' registered thumbnail handler. Unlike ISldWorks
    // .GetPreviewBitmap (which must run on SW's STA UI thread and BLOCKS it), the
    // shell image factory is a SEPARATE COM object SAFE to call on a background
    // thread, so card previews load with zero UI-thread cost. Returns null on ANY
    // failure (no thumbnail / handler not registered / error) → caller falls back
    // to the SW-API read. Minimal P/Invoke surface (one interface, one method) to
    // keep the native risk small; Image.FromHbitmap gives an OPAQUE copy, which is
    // fine — SW previews sit on a solid background and the card crop trims it.
    internal static class ShellThumbnail
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int SIIGBF_THUMBNAILONLY = 0x08; // a thumbnail, never an icon
        private static readonly Guid IID_IShellItemImageFactory =
            new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        // HandleProcessCorruptedStateExceptions + SecurityCritical opt THIS method
        // in to catching corrupted-state exceptions (a valid per-method opt-in in
        // full-trust .NET 4.x — the legacy config switch is an alternative, not a
        // requirement), so an AccessViolation from a hypothetical bad P/Invoke is
        // caught by the catch below → null → SW-API fallback instead of tearing
        // down SOLIDWORKS. A BEST-EFFORT backstop, NOT an absolute guarantee (some
        // CSE kinds, e.g. StackOverflow, are never catchable); the signatures are
        // standard, so this path isn't expected to fire.
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public static Bitmap TryGet(string path, int size)
        {
            if (string.IsNullOrEmpty(path) || size <= 0) return null;
            try { if (!File.Exists(path)) return null; } catch { return null; }
            object obj = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero,
                    IID_IShellItemImageFactory, out obj);
                var factory = obj as IShellItemImageFactory;
                if (factory == null) return null;
                SIZE sz; sz.cx = size; sz.cy = size;
                int hr = factory.GetImage(sz, SIIGBF_THUMBNAILONLY, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                using (var tmp = Image.FromHbitmap(hbm))
                    return new Bitmap(tmp); // copy so the HBITMAP can be freed
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) { try { DeleteObject(hbm); } catch { } }
                if (obj != null) { try { Marshal.ReleaseComObject(obj); } catch { } }
            }
        }
    }

    // Runs ShellThumbnail.TryGet on ONE dedicated background STA thread and
    // marshals each result back to the requesting control's UI thread — so card
    // preview reads never touch the UI thread. IsBackground, so it dies with the
    // process (no shutdown needed). Best-effort: a disposed owner / failed marshal
    // just drops the bitmap, and a card whose Alive() probe is false is skipped
    // before the read. Shared by TaskPaneControl + AdvancedSearchForm.
    // LIMITATIONS (accepted — both low-risk, see PR review): (1) ONE worker with
    // no per-read timeout, so a pathological/wedged thumbnail handler could stall
    // the queue (SW's handler is in-proc/well-behaved and GetImage is an OUTGOING
    // COM call, which the STA's own call machinery pumps, so no Application.Run
    // pump is needed); (2) if the owner is disposed in the microsecond window
    // AFTER the IsDisposed check but BEFORE the posted BeginInvoke is pumped, that
    // one bitmap leaks (WinForms drops the undelivered post) — at most one per
    // teardown-race, negligible.
    internal static class ShellThumbnailLoader
    {
        private sealed class Req
        {
            public Control Owner; public Func<bool> Alive; public string Path;
            public int Size; public Action<Bitmap> Done;
        }
        private static readonly object _gate = new object();
        private static readonly Queue<Req> _q = new Queue<Req>();
        private static readonly System.Threading.AutoResetEvent _signal =
            new System.Threading.AutoResetEvent(false);
        private static System.Threading.Thread _worker;

        // owner = the control to marshal the result back through (stable for the
        // session); alive = a cheap liveness probe for the actual card, so a read
        // is SKIPPED when the card is already gone (see WorkerLoop).
        public static void Request(Control owner, Func<bool> alive, string path,
            int size, Action<Bitmap> done)
        {
            if (owner == null || string.IsNullOrEmpty(path) || done == null) return;
            lock (_gate)
            {
                _q.Enqueue(new Req { Owner = owner, Alive = alive, Path = path,
                                     Size = size, Done = done });
                if (_worker == null)
                {
                    _worker = new System.Threading.Thread(WorkerLoop)
                    { IsBackground = true, Name = "BCorePDM.ShellThumbs" };
                    _worker.SetApartmentState(System.Threading.ApartmentState.STA);
                    _worker.Start();
                }
            }
            _signal.Set();
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Req r = null;
                lock (_gate) { if (_q.Count > 0) r = _q.Dequeue(); }
                if (r == null) { _signal.WaitOne(); continue; }
                // Skip the (network) shell read if the requesting card is already
                // gone — under fast typing the old card set is disposed while its
                // requests sit here, so reading them is wasted N: I/O whose result
                // would be dropped anyway. Cuts the churn + transient retention.
                if (r.Alive != null) { try { if (!r.Alive()) continue; } catch { } }

                Bitmap bmp = null;
                try { bmp = ShellThumbnail.TryGet(r.Path, r.Size); }
                catch { bmp = null; }

                try
                {
                    Control owner = r.Owner;
                    if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
                    {
                        Bitmap captured = bmp;
                        Action<Bitmap> done = r.Done;
                        owner.BeginInvoke((Action)(() => done(captured)));
                        bmp = null; // ownership handed to the UI callback
                    }
                }
                catch { }
                finally { if (bmp != null) { try { bmp.Dispose(); } catch { } } }
            }
        }
    }
}