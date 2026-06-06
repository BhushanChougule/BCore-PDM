using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
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
        private Button _btnLock;
        private Button _btnRelease;
        private Button _btnUnlock;
        private Button _btnNewRev;
        private Button _btnRollback;
        private Button _btnReqUnlock;
        private Button _btnReqRevision;
        private Button _btnReqRelease;
        private Button _btnUpdateDrawings;
        private Button _btnMyRequests;
        private TextBox _searchBox;
        private Panel _resultsPanel;
        private Button _btnRequests;
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
        private readonly Color cSwRed = Color.FromArgb(190, 55, 50); // muted SOLIDWORKS red

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

            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            // ── Master Actions ────────────────────────────────────────
            Label masterLbl = MakeSectionHeader("MASTER ACTIONS", fSection, x, y, w);
            masterLbl.Visible = isMaster;
            this.Controls.Add(masterLbl);
            y += S(20);

            _btnLock    = MakeActionButton("Lock File",          cOrange, fBtn, x, w, ref y, isMaster);
            _btnRelease = MakeActionButton("Release File",        cGreen,  fBtn, x, w, ref y, isMaster);
            _btnUnlock  = MakeActionButton("Unlock File",         cPurple, fBtn, x, w, ref y, isMaster);
            _btnNewRev  = MakeActionButton("New Revision",        cDark,   fBtn, x, w, ref y, isMaster);
            _btnRollback = MakeActionButton("Rollback Revision",  cMaroon, fBtn, x, w, ref y, isMaster);

            _btnLock.Click    += (s, e) => DoAction("lock");
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
            _btnUpdateDrawings = MakeActionButton("Update Drawings",  cBrand,  fBtn, x, w, ref engY, !isMaster);
            _btnMyRequests    = MakeActionButton("My Requests",       cPurple, fBtn, x, w, ref engY, !isMaster);

            _btnReqUnlock.Click      += (s, e) => DoAction("requnlock");
            _btnReqRevision.Click    += (s, e) => DoAction("requestrev");
            _btnReqRelease.Click     += (s, e) => DoAction("reqrelease");
            _btnUpdateDrawings.Click += (s, e) => DoAction("updatedrawings");
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
            this.Controls.Add(Divider(x, y, w));
            y += S(10);

            _btnRequests = new Button
            {
                Text = "PENDING REQUESTS",
                Font = fSection,
                Width = w,
                Height = S(26),
                Location = new Point(x, y),
                BackColor = cBrandDark,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(6), 0, 0, 0),
                Visible = isMaster
            };
            _btnRequests.FlatAppearance.BorderSize = 0;
            _btnRequests.Click += (s, e) => OpenRequestsPopup();
            this.Controls.Add(_btnRequests);
            if (isMaster) y += S(30);

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

            // ── Remove from Vault (Master only) ───────────────────────
            // Retires the active file: moves its WIP copy, RELEASED snapshot and
            // exports to SCRAP and deletes the vault record (blocked while
            // Released). Orphans (file already deleted on disk) are auto-purged
            // by search, so there's no separate cleanup button.
            Button btnRemove = new Button
            {
                Text = "Remove from Vault",
                Font = fBtn,
                Width = w,
                Height = S(24),
                Location = new Point(x, y),
                BackColor = cSwRed,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Visible = isMaster
            };
            btnRemove.FlatAppearance.BorderSize = 0;
            btnRemove.Click += (s, e) => DoAction("remove");
            this.Controls.Add(btnRemove);
            if (isMaster) y += S(28);

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
            if (_btnRequests == null) return;
            bool isMaster = DatabaseManager.GetUserRole(
                PDMLiteAddin.CurrentUser) == "Master";
            if (!isMaster) return;

            var count = DatabaseManager.GetPendingRequests().Count;
            _btnRequests.Text = count > 0
                ? $"PENDING REQUESTS  ({count})"
                : "PENDING REQUESTS";
        }

        private void OpenRequestsPopup()
        {
            var form = new PendingRequestsForm(_scale);
            form.ShowDialog(this);
            RefreshRequests();
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
            _resultsPanel.Controls.Clear();

            if (string.IsNullOrEmpty(term))
            {
                _resultsPanel.Height = 0;
                return;
            }

            bool truncated;
            List<VaultFile> results = DatabaseManager.SearchFiles(term, out truncated);

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

            // Show first N only — prompt the user to narrow a broad search.
            if (truncated)
            {
                _resultsPanel.Controls.Add(new Label
                {
                    Text = "Showing first " + results.Count +
                           " — refine your search to narrow results.",
                    Font = new Font("Segoe UI", 3.3f * _scale, FontStyle.Italic),
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
                PopulateHistoryPanel(null);
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

            // File History
            var history = DatabaseManager.GetFileHistory(filePath);
            PopulateHistoryPanel(history);

            // Refresh pending requests for masters
            RefreshRequests();
        }

        // ── History Panel ─────────────────────────────────────────────
        private void PopulateHistoryPanel(List<HistoryEntry> history)
        {
            _historyPanel.Controls.Clear();
            Font fHistBold = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            Font fHistSub  = new Font("Segoe UI", 3.3f * _scale);

            if (history == null || history.Count == 0)
            {
                _historyPanel.Controls.Add(new Label
                {
                    Text = "No history yet",
                    Font = fHistSub,
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
                    dateStr = dt.ToString("dd/MM/yy HH:mm");

                _historyPanel.Controls.Add(new Label
                {
                    Text = "● " + entry.Status,
                    Font = fHistBold,
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
                    Font = fHistSub,
                    ForeColor = cTextGray,
                    Location = new Point(S(4), hy),
                    AutoSize = false,
                    Width = _historyPanel.Width - S(20),
                    Height = S(14)
                });
                hy += S(15);

                if (!string.IsNullOrEmpty(entry.ChangeNote))
                {
                    _historyPanel.Controls.Add(new Label
                    {
                        Text = entry.ChangeNote,
                        Font = fHistSub,
                        ForeColor = cTextLight,
                        Location = new Point(S(4), hy),
                        AutoSize = false,
                        Width = _historyPanel.Width - S(20),
                        Height = S(14),
                        AutoEllipsis = true
                    });
                    hy += S(15);
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
            else if (action == "remove") VaultManager.RemoveFromVault(doc);
            else if (action == "requestrev") VaultManager.RequestRevision(doc);
            else if (action == "requnlock") VaultManager.RequestUnlock(doc);
            else if (action == "reqrelease") VaultManager.RequestRelease(doc);
            else if (action == "updatedrawings") VaultManager.OpenOrCreateDrawing(doc);
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
                Font fTitle = new Font("Segoe UI", 10f * _scale, FontStyle.Bold);
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
                    Font = new Font("Segoe UI", 3.8f * _scale),
                    ForeColor = cTextGray, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(16)
                });
                y += S(18);

                // "Richards-Wilcox" — Georgia Bold Italic
                form.Controls.Add(new Label {
                    Text = "Richards-Wilcox",
                    Font = new Font("Georgia", 7f * _scale, FontStyle.Bold | FontStyle.Italic),
                    ForeColor = cTextDark, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(22)
                });
                y += S(30);

                // "Designed, Developed and Maintained by"
                form.Controls.Add(new Label {
                    Text = "Designed, Developed and Maintained by",
                    Font = new Font("Segoe UI", 3.8f * _scale),
                    ForeColor = cTextGray, TextAlign = ContentAlignment.MiddleCenter,
                    Location  = new Point(0, y),
                    AutoSize  = false, Width = fw, Height = S(16)
                });
                y += S(18);

                // "Bhushan Chougule" — Georgia Bold Italic, same cBrand as "BC"
                form.Controls.Add(new Label {
                    Text = "Bhushan Chougule",
                    Font = new Font("Georgia", 7f * _scale, FontStyle.Bold | FontStyle.Italic),
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
                    Font = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold),
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
                default: return cBrand;
            }
        }
    }
}