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

        public PendingRequestsForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadRequests();
        }

        private void BuildForm()
        {
            this.Text = "BCore PDM — Pending Requests";
            this.Width = S(680);
            this.Height = S(500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = cBg;

            Font fHeader = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fSection = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);

            // Title bar
            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, 0),
                Width = this.Width,
                Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Pending Requests",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = this.Width,
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            this.Controls.Add(titleBar);

            int colW = (this.Width - S(40)) / 3;
            int colH = this.Height - S(32) - S(16);
            int[] colX = { S(10), S(10) + colW + S(5), S(10) + (colW + S(5)) * 2 };
            int colY = S(40);

            // Column headers
            string[] types = { "UNLOCK", "REVISION", "RELEASE" };
            Color[] colors = { cPurple, cDark, cGreen };
            Panel[] panels = new Panel[3];
            Label[] headers = new Label[3];

            for (int i = 0; i < 3; i++)
            {
                headers[i] = new Label
                {
                    Text = types[i] + " REQUESTS",
                    Font = fSection,
                    ForeColor = colors[i],
                    Location = new Point(colX[i], colY),
                    AutoSize = false,
                    Width = colW,
                    Height = S(20)
                };
                this.Controls.Add(headers[i]);

                panels[i] = new Panel
                {
                    Location = new Point(colX[i], colY + S(22)),
                    Width = colW,
                    Height = colH - S(22),
                    BackColor = cBg,
                    AutoScroll = true,
                    BorderStyle = BorderStyle.FixedSingle
                };
                this.Controls.Add(panels[i]);
            }

            _unlockHeader   = headers[0];
            _revisionHeader = headers[1];
            _releaseHeader  = headers[2];
            _unlockPanel    = panels[0];
            _revisionPanel  = panels[1];
            _releasePanel   = panels[2];
        }

        public void LoadRequests()
        {
            var all = DatabaseManager.GetPendingRequests();

            var unlockReqs   = all.Where(r => r.RequestType == "Unlock").ToList();
            var revisionReqs = all.Where(r => r.RequestType == "Revision" ||
                                              string.IsNullOrEmpty(r.RequestType)).ToList();
            var releaseReqs  = all.Where(r => r.RequestType == "Release").ToList();

            PopulateSection(_unlockPanel,   unlockReqs,   _unlockHeader,   "UNLOCK");
            PopulateSection(_revisionPanel, revisionReqs, _revisionHeader, "REVISION");
            PopulateSection(_releasePanel,  releaseReqs,  _releaseHeader,  "RELEASE");
        }

        private void PopulateSection(Panel panel, List<RevisionRequest> requests,
                                     Label header, string type)
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

                card.Controls.Add(new Label
                {
                    Text = req.FileName,
                    Font = fBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(8), S(5)),
                    AutoSize = false,
                    Width = rw - S(16),
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
                    if (type == "UNLOCK")
                    {
                        DatabaseManager.ResolveRequest(capturedReq.Id, "Approved");
                        EmailManager.NotifyRequestApproved("Unlock",
                            capturedReq.FileName, capturedReq.RequestedBy);
                        VaultManager.UnlockFile(capturedReq.FilePath);
                    }
                    else if (type == "REVISION" || string.IsNullOrEmpty(capturedReq.RequestType))
                    {
                        VaultManager.ApproveRequest(capturedReq);
                    }
                    else
                    {
                        // Release request — open file and trigger release
                        DatabaseManager.ResolveRequest(capturedReq.Id, "Approved");
                        EmailManager.NotifyRequestApproved("Release",
                            capturedReq.FileName, capturedReq.RequestedBy);
                        ModelDoc2 doc = PDMLiteAddin.SwApp?
                            .GetOpenDocumentByName(capturedReq.FilePath) as ModelDoc2;
                        if (doc != null)
                            VaultManager.ReleaseFile(doc);
                        else
                            MessageBox.Show(
                                "Open the file first, then release it:\n" +
                                capturedReq.FileName,
                                "BCore PDM", MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                    }
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
    }
}
