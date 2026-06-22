using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    public class PendingRequestsForm : Form
    {
        private float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrand    = Color.FromArgb(65, 120, 175);
        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg       = Color.FromArgb(248, 249, 251);
        private readonly Color cCard     = Color.White;
        private readonly Color cBorder   = Color.FromArgb(220, 225, 232);
        private readonly Color cTextDark = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray = Color.FromArgb(100, 110, 125);
        private readonly Color cTextLight = Color.FromArgb(155, 163, 175);
        private readonly Color cGreen    = Color.FromArgb(60, 140, 95);
        private readonly Color cOrange   = Color.FromArgb(185, 115, 55);
        private readonly Color cPurple   = Color.FromArgb(105, 100, 165);
        private readonly Color cDark     = Color.FromArgb(75, 80, 90);
        private readonly Color cRed      = Color.FromArgb(180, 75, 75);

        private Panel _unlockPanel;
        private Panel _revisionPanel;
        private Panel _releasePanel;
        private Label _unlockHeader;
        private Label _revisionHeader;
        private Label _releaseHeader;
        private CheckBox _unlockAll;
        private CheckBox _revisionAll;
        private CheckBox _releaseAll;

        // Per-column checkboxes (Tag = RevisionRequest) for the current cards.
        private readonly List<CheckBox> _unlockChecks   = new List<CheckBox>();
        private readonly List<CheckBox> _revisionChecks = new List<CheckBox>();
        private readonly List<CheckBox> _releaseChecks  = new List<CheckBox>();

        // Guards the two-way sync between a column's "All" box and its cards so
        // programmatic checks don't recurse.
        private bool _syncing;

        // Re-entrancy guard for the approve/reject/batch actions. A running
        // batch pumps messages through its dialogs (confirm boxes, blocker
        // popups, SOLIDWORKS work), so without this a second click on any
        // action button started a SECOND batch inside the first one.
        private bool _busy;

        // Run one action exclusively: ignore clicks while a previous action is
        // still pumping, and grey the form so the busy state is visible. Modal
        // dialogs opened by the action still work (they don't need the form
        // enabled).
        private void RunExclusive(Action action)
        {
            if (_busy) return;
            _busy = true;
            this.Enabled = false;
            try { action(); }
            catch (Exception ex)
            {
                // The action runs raw DB/VaultManager I/O (BulkApprove,
                // RejectRequest, BulkRelease) from a Click handler on
                // SOLIDWORKS' message loop — an N: blip mid-batch would
                // otherwise throw unhandled here. One choke point covers every
                // action button (audit H12).
                MessageBox.Show(
                    "The request could not be completed — the vault may be " +
                    "unavailable.\n\nCheck the N: drive and try again.\n\n" +
                    "Detail: " + ex.Message,
                    "BCore PDM — Vault Unavailable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                this.Enabled = true;
                _busy = false;
            }
        }

        // Card fonts, created once: PopulateSection runs 3× per LoadRequests
        // (and LoadRequests after every approve/reject), and a Font assigned
        // to a control is not owned by it — per-call fonts leaked a GDI
        // handle each (audit C4). Disposed in Dispose(bool).
        private readonly Font _fCardBold, _fCardSub, _fCardPn, _fCardBtn;

        public PendingRequestsForm(float scale)
        {
            _scale = scale;

            _fCardBold = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            _fCardSub  = new Font("Segoe UI", 3.3f * _scale);
            _fCardPn   = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);
            _fCardBtn  = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            BuildForm();
            LoadRequests();
        }

        // Fonts are released here rather than on FormClosed: FormClosed never
        // fires for a form that is disposed without having been shown (e.g.
        // an exception out of ShowDialog), while the caller's using disposes
        // unconditionally. Controls go first (base), then their fonts.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fCardBold?.Dispose();
                _fCardSub?.Dispose();
                _fCardPn?.Dispose();
                _fCardBtn?.Dispose();
            }
        }

        // Esc closes the popup. ProcessCmdKey fires before any child control
        // handles the key, so it works regardless of focus (the cards/buttons
        // would otherwise swallow it — the form had no keyboard escape at all).
        // Ignored while a batch is running (_busy): RunExclusive greys the form
        // during a pumping batch, and closing mid-batch would tear down the
        // cards it is still iterating.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !_busy)
            {
                this.DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Controls.Clear() does NOT dispose the removed controls — they get
        // re-parented to the hidden WinForms parking window and keep their
        // USER/GDI handles until the process dies. The columns rebuild after
        // every approve/reject, so dispose every child, THEN clear (audit C4).
        private static void ClearAndDispose(Control container)
        {
            var children = new Control[container.Controls.Count];
            container.Controls.CopyTo(children, 0);
            container.Controls.Clear();
            foreach (Control c in children)
                try { c.Dispose(); } catch { }
        }

        private void BuildForm()
        {
            this.Text = "BCore PDM — Pending Requests";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            // Size the CLIENT area so bottom controls never fall under the OS
            // title bar / borders.
            this.ClientSize = new Size(S(680), S(560));

            int cW = this.ClientSize.Width;
            int cH = this.ClientSize.Height;

            Font fHeader = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fSection = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);
            Font fSmall = new Font("Segoe UI", 3.1f * _scale);
            Font fBtn = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            // Title bar
            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, 0),
                Width = cW,
                Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Pending Requests",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = cW,
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            this.Controls.Add(titleBar);

            int colW = (cW - S(40)) / 3;
            int[] colX = { S(10), S(10) + colW + S(5), S(10) + (colW + S(5)) * 2 };
            int colY = S(40);

            // Bottom buttons anchored from client bottom — no dead space.
            int bMargin      = S(8);
            int row2H        = S(28);
            int row1H        = S(26);
            int rowGap       = S(6);
            int row2Y        = cH - bMargin - row2H;      // Approve All + Bulk Release
            int row1Y        = row2Y - rowGap - row1H;    // per-column Approve Selected
            int bottomBarTop = row1Y - rowGap;            // list panels end here

            int panelTop = colY + S(22);
            int panelH = bottomBarTop - panelTop - S(6);

            string[] titles = { "Unlock", "Revision", "Release" };
            Color[] colors = { cPurple, cDark, cGreen };
            Panel[] panels = new Panel[3];
            Label[] headers = new Label[3];
            CheckBox[] allChecks = new CheckBox[3];

            for (int i = 0; i < 3; i++)
            {
                headers[i] = new Label
                {
                    Text = titles[i] + " Requests",
                    Font = fSection,
                    ForeColor = colors[i],
                    Location = new Point(colX[i], colY),
                    AutoSize = false,
                    Width = colW - S(46),
                    Height = S(20)
                };
                this.Controls.Add(headers[i]);

                // "All" select-all checkbox at the right of each column header.
                allChecks[i] = new CheckBox
                {
                    Text = "All",
                    Font = fSmall,
                    ForeColor = cTextGray,
                    Location = new Point(colX[i] + colW - S(44), colY - S(1)),
                    AutoSize = true
                };
                this.Controls.Add(allChecks[i]);

                panels[i] = new Panel
                {
                    Location = new Point(colX[i], panelTop),
                    Width = colW,
                    Height = panelH,
                    BackColor = cBg,
                    AutoScroll = true,
                    BorderStyle = BorderStyle.FixedSingle
                };
                this.Controls.Add(panels[i]);

                // Per-column "Approve Selected" button.
                Button btnSel = new Button
                {
                    Text = "Approve Selected",
                    Font = fBtn,
                    Location = new Point(colX[i], row1Y),
                    Width = colW,
                    Height = row1H,
                    BackColor = colors[i],
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnSel.FlatAppearance.BorderSize = 0;
                int idx = i;
                btnSel.Click += (s, e) =>
                    RunExclusive(() => ApproveSelectedColumn(idx));
                this.Controls.Add(btnSel);
            }

            _unlockHeader   = headers[0];
            _revisionHeader = headers[1];
            _releaseHeader  = headers[2];
            _unlockPanel    = panels[0];
            _revisionPanel  = panels[1];
            _releasePanel   = panels[2];
            _unlockAll      = allChecks[0];
            _revisionAll    = allChecks[1];
            _releaseAll     = allChecks[2];

            _unlockAll.CheckedChanged   += (s, e) => SetAll(_unlockChecks, _unlockAll.Checked);
            _revisionAll.CheckedChanged += (s, e) => SetAll(_revisionChecks, _revisionAll.Checked);
            _releaseAll.CheckedChanged  += (s, e) => SetAll(_releaseChecks, _releaseAll.Checked);

            // ── Bottom row: Approve All Pending (green) + Bulk Release (blue) ──
            // Equal halves of the full column span, with the same S(5) gap the
            // three columns use between them so they look uniform.
            int totalColSpan = colX[2] + colW - colX[0];
            int halfW        = (totalColSpan - S(5)) / 2;

            Button btnApproveAll = new Button
            {
                Text = "Approve All Pending",
                Font = fBtn,
                Location = new Point(colX[0], row2Y),
                Width = halfW,
                Height = row2H,
                BackColor = cGreen,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnApproveAll.FlatAppearance.BorderSize = 0;
            btnApproveAll.Click += (s, e) => RunExclusive(ApproveAllPending);
            this.Controls.Add(btnApproveAll);

            Button btnBulkRelease = new Button
            {
                Text = "Bulk Release - WIP",
                Font = fBtn,
                Location = new Point(colX[0] + halfW + S(5), row2Y),
                Width = totalColSpan - halfW - S(5),
                Height = row2H,
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnBulkRelease.FlatAppearance.BorderSize = 0;
            btnBulkRelease.Click += (s, e) => RunExclusive(() =>
            {
                using (var f = new BulkReleaseForm(_scale)) f.ShowDialog(this);
                LoadRequests();
            });
            this.Controls.Add(btnBulkRelease);
        }

        // "All" box → set every card in the column.
        private void SetAll(List<CheckBox> boxes, bool value)
        {
            if (_syncing) return;
            _syncing = true;
            foreach (var cb in boxes) cb.Checked = value;
            _syncing = false;
        }

        // Card toggled → keep the column's "All" box in step (checked only when
        // every card is ticked, cleared the moment one is unticked).
        private void SyncAllCheckbox(List<CheckBox> boxes, CheckBox allBox)
        {
            if (_syncing || allBox == null) return;
            _syncing = true;
            allBox.Checked = boxes.Count > 0 && boxes.All(b => b.Checked);
            _syncing = false;
        }

        public void LoadRequests()
        {
            _unlockChecks.Clear();
            _revisionChecks.Clear();
            _releaseChecks.Clear();
            if (_unlockAll != null)   _unlockAll.Checked = false;
            if (_revisionAll != null) _revisionAll.Checked = false;
            if (_releaseAll != null)  _releaseAll.Checked = false;

            List<RevisionRequest> all;
            try
            {
                all = DatabaseManager.GetPendingRequests();
            }
            catch
            {
                // DB unreachable (N: blip / unrestorable vault) — this runs
                // from Click handlers on SOLIDWORKS' message loop, where an
                // uncaught throw is an unhandled exception dialog. Say so
                // instead of rendering empty columns that read as "no work".
                all = new List<RevisionRequest>();
                MessageBox.Show(
                    "Could not read pending requests — the vault is " +
                    "unavailable.\n\nCheck the N: drive and reopen this window.",
                    "BCore PDM — Vault Unavailable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            var unlockReqs   = all.Where(r => r.RequestType == "Unlock").ToList();
            var revisionReqs = all.Where(r => r.RequestType == "Revision" ||
                                              string.IsNullOrEmpty(r.RequestType)).ToList();
            var releaseReqs  = all.Where(r => r.RequestType == "Release").ToList();

            PopulateSection(_unlockPanel,   unlockReqs,   _unlockHeader,   "UNLOCK",   _unlockChecks,   _unlockAll);
            PopulateSection(_revisionPanel, revisionReqs, _revisionHeader, "REVISION", _revisionChecks, _revisionAll);
            PopulateSection(_releasePanel,  releaseReqs,  _releaseHeader,  "RELEASE",  _releaseChecks,  _releaseAll);
        }

        private void PopulateSection(Panel panel, List<RevisionRequest> requests,
                                     Label header, string type, List<CheckBox> checks,
                                     CheckBox allBox)
        {
            ClearAndDispose(panel);
            string display = type == "UNLOCK"   ? "Unlock"
                           : type == "REVISION" ? "Revision"
                           : "Release";
            header.Text = display + " Requests" +
                (requests.Count > 0 ? $"  ({requests.Count})" : "");

            Color barColor = type == "UNLOCK"   ? cPurple
                           : type == "REVISION" ? cDark
                           : cGreen;

            if (requests.Count == 0)
            {
                panel.Controls.Add(new Label
                {
                    Text = "No pending requests",
                    Font = _fCardSub,
                    ForeColor = cTextLight,
                    Location = new Point(S(6), S(8)),
                    AutoSize = true
                });
                return;
            }

            int ry = S(4);
            int rw = panel.Width - S(4);

            foreach (var req in requests)
            {
                bool hasNote = !string.IsNullOrEmpty(req.Note);

                // PN + Revision live on the File record, not the request. For a
                // drawing (no props of its own) fall back to its model. Guarded:
                // these are per-card DB reads — a network blip mid-populate must
                // degrade to a card without the PN line, not an unhandled throw.
                string pn = "", rev = "";
                try
                {
                    var rec = DatabaseManager.GetFileRecord(req.FilePath);
                    pn = rec?.PartNumber ?? "";
                    rev = rec?.Revision ?? "";
                    if (string.IsNullOrWhiteSpace(pn) &&
                        req.FileName != null &&
                        req.FileName.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    {
                        var model = DatabaseManager.GetModelForDrawing(req.FilePath);
                        if (model != null) { pn = model.PartNumber; rev = model.Revision; }
                    }
                }
                catch { }
                bool hasPnLine = !string.IsNullOrWhiteSpace(pn) ||
                                 !string.IsNullOrWhiteSpace(rev);

                // Lay the card out top-down with a running cursor so heights
                // adapt to the optional PN/Rev and Note lines.
                int rowH = S(15);
                int cur = S(5);
                int yFile = cur;            cur += S(16);
                int yPn   = hasPnLine ? cur : -1; if (hasPnLine) cur += rowH;
                int yBy   = cur;            cur += rowH;
                int yDate = cur;            cur += rowH;
                int yNote = hasNote ? cur : -1;   if (hasNote) cur += rowH;
                int btnY  = cur + S(3);
                int cardH = btnY + S(22) + S(6);

                Panel card = new Panel
                {
                    BackColor = cCard,
                    Location = new Point(S(2), ry),
                    Width = rw - S(4),
                    Height = cardH
                };

                card.Controls.Add(new Panel
                {
                    BackColor = barColor,
                    Location = new Point(0, 0),
                    Width = S(4),
                    Height = cardH
                });

                // Selection checkbox, top-right corner of the card.
                CheckBox cb = new CheckBox
                {
                    Location = new Point(card.Width - S(20), S(4)),
                    Width = S(16),
                    Height = S(16),
                    Tag = req
                };
                cb.CheckedChanged += (s, e) => SyncAllCheckbox(checks, allBox);
                card.Controls.Add(cb);
                checks.Add(cb);

                card.Controls.Add(new Label
                {
                    Text = req.FileName,
                    Font = _fCardBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(8), yFile),
                    AutoSize = false,
                    Width = rw - S(36),
                    Height = S(16),
                    AutoEllipsis = true
                });

                if (hasPnLine)
                {
                    string pnRev = "";
                    if (!string.IsNullOrWhiteSpace(pn)) pnRev = "PN: " + pn;
                    if (!string.IsNullOrWhiteSpace(rev))
                        pnRev += (pnRev.Length > 0 ? "    " : "") + "Rev: " + rev;
                    card.Controls.Add(new Label
                    {
                        Text = pnRev,
                        Font = _fCardPn,
                        ForeColor = cBrandDark,
                        Location = new Point(S(8), yPn),
                        AutoSize = false,
                        Width = rw - S(16),
                        Height = S(14),
                        AutoEllipsis = true
                    });
                }

                card.Controls.Add(new Label
                {
                    Text = "By: " + req.RequestedBy,
                    Font = _fCardSub,
                    ForeColor = cTextGray,
                    Location = new Point(S(8), yBy),
                    AutoSize = true
                });

                string dateStr = "—";
                if (DateTime.TryParse(req.RequestDate, out DateTime dt))
                    // MM/dd like everywhere else — the card rendered dd/MM,
                    // contradicting the project-wide MM/dd/yyyy convention.
                    dateStr = dt.ToString("MM/dd/yy HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture);
                card.Controls.Add(new Label
                {
                    Text = dateStr,
                    Font = _fCardSub,
                    ForeColor = cTextLight,
                    Location = new Point(S(8), yDate),
                    AutoSize = true
                });

                if (hasNote)
                {
                    card.Controls.Add(new Label
                    {
                        Text = "Note: " + req.Note,
                        Font = _fCardSub,
                        ForeColor = cTextGray,
                        Location = new Point(S(8), yNote),
                        AutoSize = false,
                        Width = rw - S(16),
                        Height = S(14),
                        AutoEllipsis = true
                    });
                }

                RevisionRequest capturedReq = req;

                Button btnApprove = new Button
                {
                    Text = type == "UNLOCK" ? "Approve Unlock" : "Approve",
                    Font = _fCardBtn,
                    Width = S(90),
                    Height = S(22),
                    Location = new Point(S(8), btnY),
                    BackColor = cGreen,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnApprove.FlatAppearance.BorderSize = 0;
                btnApprove.Click += (s, e) => RunExclusive(() =>
                {
                    ApproveSingle(capturedReq, type);
                    LoadRequests();
                });
                card.Controls.Add(btnApprove);

                Button btnReject = new Button
                {
                    Text = "Reject",
                    Font = _fCardBtn,
                    Width = S(58),
                    Height = S(22),
                    Location = new Point(S(102), btnY),
                    BackColor = cRed,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnReject.FlatAppearance.BorderSize = 0;
                btnReject.Click += (s, e) => RunExclusive(() =>
                {
                    VaultManager.RejectRequest(capturedReq);
                    LoadRequests();
                });
                card.Controls.Add(btnReject);

                card.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(0, cardH - S(1)),
                    Width = rw - S(4),
                    Height = S(1)
                });

                panel.Controls.Add(card);
                ry += cardH + S(4);
            }
        }

        // Single-card Approve. Confirms first, then routes through the same
        // BulkApprove engine the batch buttons use, so it auto-opens the file
        // and ONLY resolves the request when the action actually succeeded — a
        // blocked release/revision stays pending instead of vanishing.
        private void ApproveSingle(RevisionRequest req, string type)
        {
            string verb = type == "UNLOCK"  ? "Unlock"
                        : type == "RELEASE" ? "Release"
                        : "Start a new revision on";

            if (MessageBox.Show(
                    verb + " \"" + req.FileName + "\"?\n\n" +
                    "Requested by: " + req.RequestedBy,
                    "BCore PDM — Approve Request",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var result = VaultManager.BulkApprove(new[] { req });
            ShowSummary("Approve complete.", result);
        }

        // Approve all CHECKED requests in one column via the batch engine.
        private void ApproveSelectedColumn(int col)
        {
            var checks = col == 0 ? _unlockChecks
                       : col == 1 ? _revisionChecks
                       : _releaseChecks;

            var picked = checks.Where(cb => cb.Checked)
                               .Select(cb => (RevisionRequest)cb.Tag)
                               .ToList();

            if (picked.Count == 0)
            {
                MessageBox.Show("Tick at least one request first.",
                    "BCore PDM — Bulk Approve",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string[] verbs = { "unlock", "start revision on", "release" };
            if (MessageBox.Show(
                    "Approve " + picked.Count + " " +
                    (picked.Count == 1 ? "request" : "requests") + " (" +
                    verbs[col] + ")?",
                    "BCore PDM — Bulk Approve",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var result = VaultManager.BulkApprove(picked);
            ShowSummary("Bulk Approve complete.", result);
            LoadRequests();
        }

        // Approve every pending request across all three columns.
        private void ApproveAllPending()
        {
            List<RevisionRequest> all;
            try
            {
                all = DatabaseManager.GetPendingRequests();
            }
            catch
            {
                MessageBox.Show(
                    "Could not read pending requests — the vault is " +
                    "unavailable.\n\nCheck the N: drive and try again.",
                    "BCore PDM — Vault Unavailable",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (all.Count == 0)
            {
                MessageBox.Show("There are no pending requests.",
                    "BCore PDM — Approve All",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int nUnlock   = all.Count(r => r.RequestType == "Unlock");
            int nRevision = all.Count(r => r.RequestType == "Revision" ||
                                           string.IsNullOrEmpty(r.RequestType));
            int nRelease  = all.Count(r => r.RequestType == "Release");

            if (MessageBox.Show(
                    "Approve ALL pending requests?\n\n" +
                    "  • " + nUnlock + " unlock\n" +
                    "  • " + nRevision + " revision\n" +
                    "  • " + nRelease + " release\n\n" +
                    "Each file is unlocked / revised / released accordingly.",
                    "BCore PDM — Approve All Pending",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var result = VaultManager.BulkApprove(all);
            ShowSummary("Approve All complete.", result);
            LoadRequests();
        }

        private void ShowSummary(string heading, BatchResult result)
        {
            MessageBox.Show(result.BuildSummary(heading),
                "BCore PDM — Batch Result",
                MessageBoxButtons.OK,
                result.Skipped.Count > 0 ? MessageBoxIcon.Warning
                                         : MessageBoxIcon.Information);
        }
    }
}
