using SolidWorks.Interop.sldworks;
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

        public PendingRequestsForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadRequests();
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

            // Bottom action area reserved below the columns.
            int bottomBarTop = cH - S(96);
            int panelTop = colY + S(22);
            int panelH = bottomBarTop - panelTop - S(6);

            string[] types = { "UNLOCK", "REVISION", "RELEASE" };
            Color[] colors = { cPurple, cDark, cGreen };
            Panel[] panels = new Panel[3];
            Label[] headers = new Label[3];
            CheckBox[] allChecks = new CheckBox[3];

            for (int i = 0; i < 3; i++)
            {
                headers[i] = new Label
                {
                    Text = types[i] + " REQUESTS",
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
                    Location = new Point(colX[i], bottomBarTop + S(2)),
                    Width = colW,
                    Height = S(26),
                    BackColor = colors[i],
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnSel.FlatAppearance.BorderSize = 0;
                int idx = i;
                btnSel.Click += (s, e) => ApproveSelectedColumn(idx);
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
            int row2Y = bottomBarTop + S(32);
            int greenW = colX[1] + colW - colX[0];   // spans columns 1+2

            Button btnApproveAll = new Button
            {
                Text = "Approve All Pending",
                Font = fBtn,
                Location = new Point(colX[0], row2Y),
                Width = greenW,
                Height = S(28),
                BackColor = cGreen,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnApproveAll.FlatAppearance.BorderSize = 0;
            btnApproveAll.Click += (s, e) => ApproveAllPending();
            this.Controls.Add(btnApproveAll);

            Button btnBulkRelease = new Button
            {
                Text = "Bulk Release…",
                Font = fBtn,
                Location = new Point(colX[2], row2Y),
                Width = colW,
                Height = S(28),
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnBulkRelease.FlatAppearance.BorderSize = 0;
            btnBulkRelease.Click += (s, e) =>
            {
                using (var f = new BulkReleaseForm(_scale)) f.ShowDialog(this);
                LoadRequests();
            };
            this.Controls.Add(btnBulkRelease);
        }

        private static void SetAll(List<CheckBox> boxes, bool value)
        {
            foreach (var cb in boxes) cb.Checked = value;
        }

        public void LoadRequests()
        {
            _unlockChecks.Clear();
            _revisionChecks.Clear();
            _releaseChecks.Clear();
            if (_unlockAll != null)   _unlockAll.Checked = false;
            if (_revisionAll != null) _revisionAll.Checked = false;
            if (_releaseAll != null)  _releaseAll.Checked = false;

            var all = DatabaseManager.GetPendingRequests();

            var unlockReqs   = all.Where(r => r.RequestType == "Unlock").ToList();
            var revisionReqs = all.Where(r => r.RequestType == "Revision" ||
                                              string.IsNullOrEmpty(r.RequestType)).ToList();
            var releaseReqs  = all.Where(r => r.RequestType == "Release").ToList();

            PopulateSection(_unlockPanel,   unlockReqs,   _unlockHeader,   "UNLOCK",   _unlockChecks);
            PopulateSection(_revisionPanel, revisionReqs, _revisionHeader, "REVISION", _revisionChecks);
            PopulateSection(_releasePanel,  releaseReqs,  _releaseHeader,  "RELEASE",  _releaseChecks);
        }

        private void PopulateSection(Panel panel, List<RevisionRequest> requests,
                                     Label header, string type, List<CheckBox> checks)
        {
            panel.Controls.Clear();
            header.Text = type + " REQUESTS" +
                (requests.Count > 0 ? $"  ({requests.Count})" : "");

            Font fBold  = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            Font fSub   = new Font("Segoe UI", 3.3f * _scale);
            Font fBtn   = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);
            Color barColor = type == "UNLOCK"   ? cPurple
                           : type == "REVISION" ? cDark
                           : cGreen;

            if (requests.Count == 0)
            {
                panel.Controls.Add(new Label
                {
                    Text = "No pending requests",
                    Font = fSub,
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
                int cardH = hasNote ? S(112) : S(96);

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
                card.Controls.Add(cb);
                checks.Add(cb);

                card.Controls.Add(new Label
                {
                    Text = req.FileName,
                    Font = fBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(8), S(5)),
                    AutoSize = false,
                    Width = rw - S(36),
                    Height = S(16),
                    AutoEllipsis = true
                });

                card.Controls.Add(new Label
                {
                    Text = "By: " + req.RequestedBy,
                    Font = fSub,
                    ForeColor = cTextGray,
                    Location = new Point(S(8), S(23)),
                    AutoSize = true
                });

                string dateStr = "—";
                if (DateTime.TryParse(req.RequestDate, out DateTime dt))
                    dateStr = dt.ToString("dd/MM/yy HH:mm");
                card.Controls.Add(new Label
                {
                    Text = dateStr,
                    Font = fSub,
                    ForeColor = cTextLight,
                    Location = new Point(S(8), S(38)),
                    AutoSize = true
                });

                if (hasNote)
                {
                    card.Controls.Add(new Label
                    {
                        Text = "Note: " + req.Note,
                        Font = fSub,
                        ForeColor = cTextGray,
                        Location = new Point(S(8), S(53)),
                        AutoSize = false,
                        Width = rw - S(16),
                        Height = S(14),
                        AutoEllipsis = true
                    });
                }

                int btnY = hasNote ? S(72) : S(58);
                RevisionRequest capturedReq = req;

                Button btnApprove = new Button
                {
                    Text = type == "UNLOCK" ? "Approve Unlock" : "Approve",
                    Font = fBtn,
                    Width = S(90),
                    Height = S(22),
                    Location = new Point(S(8), btnY),
                    BackColor = cGreen,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnApprove.FlatAppearance.BorderSize = 0;
                btnApprove.Click += (s, e) =>
                {
                    ApproveSingle(capturedReq, type);
                    LoadRequests();
                };
                card.Controls.Add(btnApprove);

                Button btnReject = new Button
                {
                    Text = "Reject",
                    Font = fBtn,
                    Width = S(58),
                    Height = S(22),
                    Location = new Point(S(102), btnY),
                    BackColor = cRed,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnReject.FlatAppearance.BorderSize = 0;
                btnReject.Click += (s, e) =>
                {
                    VaultManager.RejectRequest(capturedReq);
                    LoadRequests();
                };
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

        // Single-card Approve (unchanged behaviour, routed by type).
        private void ApproveSingle(RevisionRequest req, string type)
        {
            if (type == "UNLOCK")
            {
                DatabaseManager.ResolveRequest(req.Id, "Approved");
                EmailManager.NotifyRequestApproved("Unlock", req.FileName, req.RequestedBy);
                VaultManager.UnlockFile(req.FilePath);
            }
            else if (type == "REVISION" || string.IsNullOrEmpty(req.RequestType))
            {
                VaultManager.ApproveRequest(req);
            }
            else
            {
                DatabaseManager.ResolveRequest(req.Id, "Approved");
                EmailManager.NotifyRequestApproved("Release", req.FileName, req.RequestedBy);
                ModelDoc2 doc = PDMLiteAddin.SwApp?
                    .GetOpenDocumentByName(req.FilePath) as ModelDoc2;
                if (doc != null)
                    VaultManager.ReleaseFile(doc);
                else
                    MessageBox.Show(
                        "Open the file first, then release it:\n" + req.FileName,
                        "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
            var all = DatabaseManager.GetPendingRequests();
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
