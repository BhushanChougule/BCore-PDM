using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Text;
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
        private Label _historyContent;
        private Button _btnLock;
        private Button _btnRelease;
        private Button _btnUnlock;
        private Button _btnNewRev;
        private Button _btnRollback;
        private Button _btnRequestRev;
        private TextBox _searchBox;
        private Panel _resultsPanel;
        private Panel _requestsPanel;
        private Label _requestsHeader;
        private Timer _searchTimer;

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

        public TaskPaneControl()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;
            BuildUI();
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
            Font fHistory = new Font("Segoe UI", 3.5f * _scale);

            bool isMaster = DatabaseManager.GetUserRole(
                PDMLiteAddin.CurrentUser) == "Master";

            // ── Header Banner ─────────────────────────────────────────
            Panel headerBanner = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, y),
                Width = S(210),
                Height = S(32)
            };
            this.Controls.Add(headerBanner);
            headerBanner.Controls.Add(new Label
            {
                Text = "BCore PDM",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = S(210),
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            y += S(32);

            // ── Search Section ────────────────────────────────────────
            y += S(8);
            this.Controls.Add(MakeSectionHeader("SEARCH FILES", fSection, x, y, w));
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

            _searchBox = new TextBox
            {
                Font = fLabel,
                Width = w - S(2) - S(53),
                Height = S(22),
                Location = new Point(S(2), S(2)),
                BorderStyle = BorderStyle.None,
                BackColor = cCard,
                ForeColor = cTextDark
            };
            _searchTimer = new Timer { Interval = 600 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RunSearch(); };
            _searchBox.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                if (_searchBox.Text.Length >= 2) _searchTimer.Start();
                else if (_searchBox.Text.Length == 0)
                {
                    _resultsPanel.Controls.Clear();
                    _resultsPanel.Height = 0;
                }
            };
            searchCard.Controls.Add(_searchBox);

            Button btnClear = new Button
            {
                Text = "✖",
                Font = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold),
                Width = S(22),
                Height = S(24),
                Location = new Point(x + w - S(52), y),
                BackColor = cRed,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += (s, e) =>
            {
                _searchBox.Text = "";
                _resultsPanel.Controls.Clear();
                _resultsPanel.Height = 0;
            };
            this.Controls.Add(btnClear);

            Button btnSearch = new Button
            {
                Text = "Search",
                Font = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold),
                Width = S(48),
                Height = S(24),
                Location = new Point(x + w - S(48), y),
                BackColor = cBrand,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnSearch.FlatAppearance.BorderSize = 0;
            btnSearch.Click += (s, e) => RunSearch();
            this.Controls.Add(btnSearch);
            y += S(28);

            this.Controls.Add(new Label
            {
                Text = "Search by part number or description",
                Font = new Font("Segoe UI", 3.2f * _scale),
                ForeColor = cTextLight,
                Location = new Point(x, y),
                AutoSize = false,
                Width = w,
                Height = S(14)
            });
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
            _fileNameLbl = new Label
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

            // ── Engineer: Request Revision button ─────────────────────
            // Visible only to engineers when file is Released
            _btnRequestRev = new Button
            {
                Text = "Request Revision",
                Font = fBtn,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = Color.FromArgb(100, 130, 170),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Visible = false // shown dynamically in Refresh
            };
            _btnRequestRev.FlatAppearance.BorderSize = 0;
            _btnRequestRev.Click += (s, e) => DoAction("requestrev");
            this.Controls.Add(_btnRequestRev);
            y += S(28);

            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── Master Actions ────────────────────────────────────────
            Label masterLbl = MakeSectionHeader("MASTER ACTIONS", fSection, x, y, w);
            masterLbl.Visible = isMaster;
            this.Controls.Add(masterLbl);
            y += S(20);

            _btnLock = MakeActionButton("Lock File", cOrange, fBtn, x, w, ref y, isMaster);
            _btnRelease = MakeActionButton("Release File", cGreen, fBtn, x, w, ref y, isMaster);
            _btnUnlock = MakeActionButton("Unlock File", cPurple, fBtn, x, w, ref y, isMaster);
            _btnNewRev = MakeActionButton("New Revision", cDark, fBtn, x, w, ref y, isMaster);
            _btnRollback = MakeActionButton("Rollback Revision", cMaroon, fBtn, x, w, ref y, isMaster);

            _btnLock.Click += (s, e) => DoAction("lock");
            _btnRelease.Click += (s, e) => DoAction("release");
            _btnUnlock.Click += (s, e) => DoAction("unlock");
            _btnNewRev.Click += (s, e) => DoAction("newrev");
            _btnRollback.Click += (s, e) => DoAction("rollback");

            _btnLock.Visible = isMaster;
            _btnRelease.Visible = isMaster;
            _btnUnlock.Visible = isMaster;
            _btnNewRev.Visible = isMaster;
            _btnRollback.Visible = isMaster;

            y += S(4);
            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── File History ──────────────────────────────────────────
            this.Controls.Add(MakeSectionHeader("FILE HISTORY", fSection, x, y, w));
            y += S(20);

            _historyContent = new Label
            {
                Text = "No history yet",
                Font = fHistory,
                ForeColor = cTextGray,
                Location = new Point(x, y),
                AutoSize = false,
                Width = w,
                Height = S(300)
            };
            this.Controls.Add(_historyContent);
            y += S(305);

            // ── Pending Requests (Master only) ────────────────────────
            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            _requestsHeader = MakeSectionHeader("PENDING REQUESTS", fSection, x, y, w);
            _requestsHeader.Visible = isMaster;
            this.Controls.Add(_requestsHeader);
            y += S(20);

            _requestsPanel = new Panel
            {
                Location = new Point(x, y),
                Width = w,
                Height = 0,
                BackColor = cBg,
                Visible = isMaster
            };
            this.Controls.Add(_requestsPanel);
        }

        // ── Refresh Pending Requests (Master only) ────────────────────
        private void RefreshRequests()
        {
            if (_requestsPanel == null) return;
            bool isMaster = DatabaseManager.GetUserRole(
                PDMLiteAddin.CurrentUser) == "Master";
            if (!isMaster) return;

            _requestsPanel.Controls.Clear();
            var requests = DatabaseManager.GetPendingRequests();

            _requestsHeader.Text = requests.Count > 0
                ? $"PENDING REQUESTS  ({requests.Count})"
                : "PENDING REQUESTS";

            if (requests.Count == 0)
            {
                _requestsPanel.Controls.Add(new Label
                {
                    Text = "No pending requests",
                    Font = new Font("Segoe UI", 3.5f * _scale),
                    ForeColor = cTextLight,
                    Location = new Point(0, S(4)),
                    AutoSize = false,
                    Width = S(188),
                    Height = S(18)
                });
                _requestsPanel.Height = S(24);
                return;
            }

            int ry = 0;
            int rw = S(188);
            Font fReqBold = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            Font fReqSub = new Font("Segoe UI", 3.3f * _scale);
            Font fReqBtn = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold);

            foreach (var req in requests)
            {
                bool hasNote = !string.IsNullOrEmpty(req.Note);
                int btnY = hasNote ? S(68) : S(56);
                int cardHeight = hasNote ? S(96) : S(84);

                Panel card = new Panel
                {
                    BackColor = cCard,
                    Location = new Point(0, ry),
                    Width = rw,
                    Height = cardHeight,
                    BorderStyle = BorderStyle.None
                };

                // Orange left bar
                card.Controls.Add(new Panel
                {
                    BackColor = cOrange,
                    Location = new Point(0, 0),
                    Width = S(4),
                    Height = cardHeight
                });

                // File name
                card.Controls.Add(new Label
                {
                    Text = req.FileName,
                    Font = fReqBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(8), S(5)),
                    AutoSize = false,
                    Width = rw - S(12),
                    Height = S(15),
                    AutoEllipsis = true
                });

                // Requested by
                card.Controls.Add(new Label
                {
                    Text = "By: " + req.RequestedBy,
                    Font = fReqSub,
                    ForeColor = cTextGray,
                    Location = new Point(S(8), S(22)),
                    AutoSize = true
                });

                // Date
                string dateStr = "—";
                if (DateTime.TryParse(req.RequestDate, out DateTime dt))
                    dateStr = dt.ToString("dd/MM/yy HH:mm");
                card.Controls.Add(new Label
                {
                    Text = dateStr,
                    Font = fReqSub,
                    ForeColor = cTextLight,
                    Location = new Point(S(8), S(40)),
                    AutoSize = true
                });

                // Note (only if exists)
                if (hasNote)
                {
                    card.Controls.Add(new Label
                    {
                        Text = "Note: " + req.Note,
                        Font = fReqSub,
                        ForeColor = cTextGray,
                        Location = new Point(S(8), S(48)),
                        AutoSize = false,
                        Width = rw - S(12),
                        Height = S(13),
                        AutoEllipsis = true
                    });
                }

                // Approve button
                RevisionRequest capturedReq = req;
                Button btnApprove = new Button
                {
                    Text = "Approve",
                    Font = fReqBtn,
                    Width = S(72),
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
                    VaultManager.ApproveRequest(capturedReq);
                    RefreshRequests();
                    ModelDoc2 activeDoc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                    if (activeDoc != null) Refresh(activeDoc);
                };
                card.Controls.Add(btnApprove);

                // Reject button
                Button btnReject = new Button
                {
                    Text = "Reject",
                    Font = fReqBtn,
                    Width = S(62),
                    Height = S(22),
                    Location = new Point(S(84), btnY),
                    BackColor = cRed,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnReject.FlatAppearance.BorderSize = 0;
                btnReject.Click += (s, e) =>
                {
                    VaultManager.RejectRequest(capturedReq);
                    RefreshRequests();
                    ModelDoc2 activeDoc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                    if (activeDoc != null) Refresh(activeDoc);
                };
                card.Controls.Add(btnReject);

                // Bottom border
                card.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(0, cardHeight - S(1)),
                    Width = rw,
                    Height = S(1)
                });

                _requestsPanel.Controls.Add(card);
                ry += cardHeight + S(4);
            }

            _requestsPanel.Height = ry;
        }

        // ── Search ────────────────────────────────────────────────────
        private void RunSearch()
        {
            string term = _searchBox.Text.Trim();
            _resultsPanel.Controls.Clear();

            if (string.IsNullOrEmpty(term))
            {
                _resultsPanel.Height = 0;
                return;
            }

            List<VaultFile> results = DatabaseManager.SearchFiles(term);

            if (results.Count == 0)
            {
                _resultsPanel.Controls.Add(new Label
                {
                    Text = "No files found for: " + term,
                    Font = new Font("Segoe UI", 3.5f * _scale),
                    ForeColor = cTextLight,
                    Location = new Point(0, S(4)),
                    AutoSize = false,
                    Width = S(188),
                    Height = S(20)
                });
                _resultsPanel.Height = S(28);
                return;
            }

            int ry = 0;
            int rw = S(188);

            foreach (var file in results)
            {
                Color statusColor = file.Status == "Released" ? cGreen
                                  : file.Status == "Locked" ? cOrange
                                  : cBrand;

                Panel card = new Panel
                {
                    Location = new Point(0, ry),
                    Width = rw,
                    Height = S(68),
                    BackColor = cCard,
                    BorderStyle = BorderStyle.None
                };

                card.Controls.Add(new Panel
                {
                    BackColor = statusColor,
                    Location = new Point(0, 0),
                    Width = S(4),
                    Height = S(68)
                });

                card.Controls.Add(new Label
                {
                    Text = Path.GetFileName(file.FilePath),
                    Font = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold),
                    ForeColor = cTextDark,
                    Location = new Point(S(8), S(5)),
                    AutoSize = false,
                    Width = rw - S(12),
                    Height = S(15),
                    AutoEllipsis = true
                });

                card.Controls.Add(new Label
                {
                    Text = file.Status ?? "WIP",
                    Font = new Font("Segoe UI", 3.2f * _scale, FontStyle.Bold),
                    ForeColor = statusColor,
                    Location = new Point(S(8), S(21)),
                    AutoSize = true
                });

                card.Controls.Add(new Label
                {
                    Text = string.IsNullOrEmpty(file.PartNumber)
                                ? "No Part No" : file.PartNumber,
                    Font = new Font("Segoe UI", 3.5f * _scale),
                    ForeColor = cTextGray,
                    Location = new Point(S(8), S(33)),
                    AutoSize = false,
                    Width = rw - S(12),
                    Height = S(13)
                });

                string filePath = file.FilePath;
                Button btnOpen = new Button
                {
                    Text = "Open in SOLIDWORKS",
                    Font = new Font("Segoe UI", 3.5f * _scale, FontStyle.Bold),
                    Width = rw - S(8),
                    Height = S(18),
                    Location = new Point(S(4), S(50)),
                    BackColor = cBrand,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnOpen.FlatAppearance.BorderSize = 0;
                btnOpen.Click += (s, e) => OpenFile(filePath);
                card.Controls.Add(btnOpen);

                card.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(0, S(67)),
                    Width = rw,
                    Height = S(1)
                });

                _resultsPanel.Controls.Add(card);
                ry += S(70);
            }

            _resultsPanel.Height = ry;
        }

        private void OpenFile(string filePath)
        {
            _searchBox.Text = "";
            _resultsPanel.Controls.Clear();
            _resultsPanel.Height = 0;

            if (!File.Exists(filePath))
            {
                MessageBox.Show("File not found:\n" + filePath,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

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
                _historyContent.Text = "No history yet";
                _btnRequestRev.Visible = false;
                RefreshRequests();
                return;
            }

            string filePath = doc.GetPathName();
            string fileName = Path.GetFileName(filePath);
            string status = DatabaseManager.GetFileStatusByName(filePath);
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");
            string rev = PropertyValidator.GetProperty(doc, "Revision");
            var lockInfo = DatabaseManager.GetLockInfo(filePath);

            bool isMaster = DatabaseManager.GetUserRole(
                PDMLiteAddin.CurrentUser) == "Master";
            bool isDrawing = doc.GetType() ==
                (int)swDocumentTypes_e.swDocDRAWING;

            _fileNameLbl.Text = string.IsNullOrEmpty(fileName)
                ? "Unsaved new file" : fileName;

            _statusVal.Text = string.IsNullOrEmpty(status) ? "WIP" : status;
            _statusVal.ForeColor = StatusColor(status);
            _partNoVal.Text = isDrawing ? "Drawing" :
                (string.IsNullOrEmpty(partNo) ? "—" : partNo);
            _revVal.Text = string.IsNullOrEmpty(rev) ? "—" : "REV " + rev;
            _lockedVal.Text = lockInfo.IsLocked ? lockInfo.LockedBy : "Not Locked";
            _lockedVal.ForeColor = lockInfo.IsLocked ? cRed : cGreen;

            // Show Request Revision button only for engineers on Released files
            _btnRequestRev.Visible = !isMaster && status == "Released";

            // File History
            var history = DatabaseManager.GetFileHistory(filePath);
            if (history.Count == 0)
            {
                _historyContent.Text = "No history yet";
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var entry in history.Take(5))
                {
                    string dateStr = "—";
                    if (DateTime.TryParse(entry.ChangedDate, out DateTime dt))
                        dateStr = dt.ToString("dd/MM/yy HH:mm");
                    sb.AppendLine($"● {entry.Status}");
                    sb.AppendLine($"  {dateStr}  {entry.ChangedBy}");
                    if (!string.IsNullOrEmpty(entry.ChangeNote))
                        sb.AppendLine($"  {entry.ChangeNote}");
                    sb.AppendLine("  ─────────────");
                }
                _historyContent.Text = sb.ToString().TrimEnd();
            }

            // Refresh pending requests for masters
            RefreshRequests();
        }

        // ── Button Actions ────────────────────────────────────────────
        private void DoAction(string action)
        {
            ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
            if (doc == null)
            {
                MessageBox.Show("Please open a SOLIDWORKS file first.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path = doc.GetPathName();

            if (action == "lock") VaultManager.LockFile(path);
            else if (action == "release") VaultManager.ReleaseFile(doc);
            else if (action == "unlock") VaultManager.UnlockFile(path);
            else if (action == "newrev") VaultManager.StartNewRevision(doc);
            else if (action == "rollback") VaultManager.RollbackRevision(doc);
            else if (action == "requestrev") VaultManager.RequestRevision(doc);

            Refresh(doc);
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
                default: return cBrand;
            }
        }
    }
}