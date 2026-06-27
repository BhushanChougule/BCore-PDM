using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    // The Engineering Change Request (ECR) review/list surface. TWO modes:
    //  - MASTER review hub (isMaster=true): the Submitted/UnderReview queue with
    //    per-card Accept / Reject / Convert-to-ECO actions, plus a state filter
    //    to browse the whole ECR history.
    //  - ENGINEER "My ECRs" (isMaster=false): the engineer's own ECRs, read-only
    //    (no action buttons) — they raise ECRs via EcrForm and watch their state.
    //
    // Self-contained (own palette, S(), ClearAndDispose, card fonts as disposed
    // fields) per the house one-form-one-file convention. Card rebuild hygiene
    // mirrors PendingRequestsForm: ClearAndDispose tears the column down (a plain
    // Controls.Clear parks controls forever) and card fonts are once-created
    // fields disposed in Dispose(bool). Esc closes. Every DB/UI call that hits
    // the network is wrapped so an N: blip never escapes onto SOLIDWORKS' loop.
    internal class EcrListForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cCard      = Color.White;
        private readonly Color cBorder    = Color.FromArgb(220, 225, 232);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cOrange    = Color.FromArgb(185, 115, 55);
        private readonly Color cPurple    = Color.FromArgb(105, 100, 165);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);
        private readonly Color cRed       = Color.FromArgb(180, 75, 75);
        private readonly Color cMaroon    = Color.FromArgb(140, 60, 60);

        private readonly bool   _isMaster;
        private readonly string _user;

        private ComboBox _filter;
        private Panel     _listPanel;
        private Label     _countLabel;

        private readonly Font _fTitle, _fCardBold, _fCardSub, _fCardMeta,
            _fCardBtn, _fHint, _fFilter;

        // Guard so a click on an action button can't start a second action while
        // the first is pumping its confirm/reason dialogs.
        private bool _busy;

        public EcrListForm(bool isMaster, string user)
        {
            _isMaster = isMaster;
            _user = user ?? "";

            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            _fTitle    = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fCardBold = new Font("Segoe UI", 3.9f * _scale, FontStyle.Bold);
            _fCardSub  = new Font("Segoe UI", 3.4f * _scale);
            _fCardMeta = new Font("Segoe UI", 3.2f * _scale);
            _fCardBtn  = new Font("Segoe UI", 3.4f * _scale, FontStyle.Bold);
            _fHint     = new Font("Segoe UI", 3.2f * _scale);
            _fFilter   = new Font("Segoe UI", 3.6f * _scale);

            Text            = "BCore PDM — " +
                (_isMaster ? "Change Requests (ECR)" : "My Change Requests");
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox     = true;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(600), S(560));
            MinimumSize     = new Size(S(440), S(360));

            BuildUi();
            LoadEcrs();
        }

        private void BuildUi()
        {
            int cW = ClientSize.Width;

            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Dock      = DockStyle.Top,
                Height    = S(34)
            };
            titleBar.Controls.Add(new Label
            {
                Text = _isMaster ? "Engineering Change Requests"
                                 : "My Engineering Change Requests",
                Font = _fTitle, ForeColor = Color.White,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            // Top control row (filter for Master; a hint for the engineer).
            Panel top = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = cBg };
            if (_isMaster)
            {
                top.Controls.Add(new Label
                {
                    Text = "Show:", Font = _fFilter, ForeColor = cTextDark,
                    Location = new Point(S(12), S(11)),
                    AutoSize = false, Width = S(48), Height = S(20)
                });
                _filter = new ComboBox
                {
                    Font = _fFilter,
                    Location = new Point(S(60), S(8)),
                    Width = S(180),
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                _filter.Items.AddRange(new object[]
                {
                    "Open (Submitted / Under Review)", "Submitted", "Under Review",
                    "Accepted", "Rejected", "Converted", "All"
                });
                _filter.SelectedIndex = 0;
                _filter.SelectedIndexChanged += (s, e) => LoadEcrs();
                top.Controls.Add(_filter);
            }
            else
            {
                top.Controls.Add(new Label
                {
                    Text = "Your change requests and their current state.",
                    Font = _fHint, ForeColor = cTextGray,
                    Location = new Point(S(12), S(12)),
                    AutoSize = false, Width = cW - S(24), Height = S(18)
                });
            }
            Controls.Add(top);

            _countLabel = new Label
            {
                Dock = DockStyle.Top, Height = S(22), BackColor = cBg,
                Font = _fHint, ForeColor = cTextGray,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(12), 0, 0, 0)
            };
            Controls.Add(_countLabel);

            _listPanel = new Panel
            {
                Dock = DockStyle.Fill, BackColor = cBg, AutoScroll = true,
                Padding = new Padding(S(8))
            };
            Controls.Add(_listPanel);

            // Bottom bar: "New ECR…" (raise a request for the active doc) + Close.
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = S(44), BackColor = cBg };
            int bH = S(28), bW = S(120);
            Button btnNew = new Button
            {
                Text = "New ECR…", Font = _fCardBtn,
                Location = new Point(S(12), S(8)), Width = bW, Height = bH,
                BackColor = cGreen, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnNew.FlatAppearance.BorderSize = 0;
            btnNew.Click += (s, e) => RaiseNew();
            bottom.Controls.Add(btnNew);

            Button btnClose = new Button
            {
                Text = "Close", Font = _fCardBtn,
                Width = S(90), Height = bH,
                Location = new Point(ClientSize.Width - S(12) - S(90), S(8)),
                BackColor = cDark, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = DialogResult.Cancel
            };
            btnClose.FlatAppearance.BorderSize = 0;
            bottom.Controls.Add(btnClose);
            Controls.Add(bottom);

            // Z-order: WinForms resolves docking by reverse add order — the Fill
            // panel must be sent to back so the top/bottom bars keep their bands
            // (house Fill-resolves-by-z-order convention).
            _listPanel.SendToBack();
            top.BringToFront();
            titleBar.BringToFront();
            bottom.BringToFront();
        }

        // Raise a new ECR for the active document, then refresh the list.
        private void RaiseNew()
        {
            if (_busy) return;
            try
            {
                ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                if (doc == null)
                {
                    MessageBox.Show(
                        "Open the part/assembly you want to raise a change request " +
                        "for, then click New ECR.",
                        "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                VaultManager.RaiseEcr(doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not raise the ECR: " + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { LoadEcrs(); }
        }

        // Private clear-and-dispose so the rebuilt cards don't park their handles
        // (audit-C4 — Controls.Clear alone leaks).
        private void ClearAndDispose(Control parent)
        {
            for (int i = parent.Controls.Count - 1; i >= 0; i--)
            {
                Control c = parent.Controls[i];
                parent.Controls.RemoveAt(i);
                c.Dispose();
            }
        }

        private string FilterState()
        {
            if (!_isMaster) return ""; // engineer mode loads by requester
            switch (_filter?.SelectedIndex ?? 0)
            {
                case 1: return EcrManager.StateSubmitted;
                case 2: return EcrManager.StateUnderReview;
                case 3: return EcrManager.StateAccepted;
                case 4: return EcrManager.StateRejected;
                case 5: return EcrManager.StateConverted;
                case 6: return ""; // All
                default: return "OPEN"; // Submitted + UnderReview
            }
        }

        // Cached query result so a resize re-flows the layout WITHOUT re-reading
        // the ECR store from disk on every pixel of a drag.
        private List<EcrManager.Ecr> _ecrs = new List<EcrManager.Ecr>();

        private void LoadEcrs()
        {
            try
            {
                if (!_isMaster)
                {
                    _ecrs = EcrManager.GetEcrsByRequester(_user);
                }
                else
                {
                    string st = FilterState();
                    if (st == "OPEN")
                    {
                        var open = EcrManager.GetEcrs(EcrManager.StateSubmitted);
                        open.AddRange(EcrManager.GetEcrs(EcrManager.StateUnderReview));
                        _ecrs = open.OrderByDescending(r => r.CreatedDate).ToList();
                    }
                    else
                    {
                        _ecrs = EcrManager.GetEcrs(st);
                    }
                }
            }
            catch
            {
                _ecrs = new List<EcrManager.Ecr>();
                ClearAndDispose(_listPanel);
                _countLabel.Text = "  ECR store unavailable.";
                return;
            }
            RenderCards();
        }

        private void RenderCards()
        {
            ClearAndDispose(_listPanel);
            var ecrs = _ecrs ?? new List<EcrManager.Ecr>();

            _countLabel.Text = "  " + ecrs.Count + " change request" +
                (ecrs.Count == 1 ? "" : "s") + ".";

            if (ecrs.Count == 0)
            {
                _listPanel.Controls.Add(new Label
                {
                    Text = _isMaster
                        ? "No change requests in this view."
                        : "You have not raised any change requests yet.",
                    Font = _fCardSub, ForeColor = cTextGray,
                    Location = new Point(S(12), S(12)),
                    AutoSize = false, Width = _listPanel.ClientSize.Width - S(24),
                    Height = S(40)
                });
                return;
            }

            int y = S(4);
            int cardW = _listPanel.ClientSize.Width - S(24);
            if (cardW < S(300)) cardW = S(300);
            foreach (var ecr in ecrs)
            {
                Panel card = BuildCard(ecr, cardW);
                card.Location = new Point(S(8), y);
                _listPanel.Controls.Add(card);
                y += card.Height + S(8);
            }
        }

        private Panel BuildCard(EcrManager.Ecr ecr, int cardW)
        {
            bool actionable = _isMaster &&
                (string.Equals(ecr.State, EcrManager.StateSubmitted,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ecr.State, EcrManager.StateUnderReview,
                    StringComparison.OrdinalIgnoreCase));

            Panel card = new Panel
            {
                Width = cardW,
                BackColor = cCard,
                BorderStyle = BorderStyle.FixedSingle
            };

            int x = S(10);
            int cy = S(8);
            int innerW = cardW - S(20);

            // Header: number + type + state pill text
            Label hdr = new Label
            {
                Text = ecr.Number + "   ·   " + (ecr.Type ?? "") +
                       "   ·   " + StateLabel(ecr.State),
                Font = _fCardBold, ForeColor = StateColor(ecr.State),
                Location = new Point(x, cy), AutoSize = false,
                Width = innerW, Height = S(20)
            };
            card.Controls.Add(hdr);
            cy += S(22);

            // File + Part No
            string fileName = SafeFileName(ecr.AffectedFilePath);
            string fileLine = "File: " +
                (string.IsNullOrEmpty(fileName) ? "(none)" : fileName) +
                (string.IsNullOrEmpty(ecr.AffectedPartNo) ? ""
                    : "   ·   PN: " + ecr.AffectedPartNo);
            card.Controls.Add(new Label
            {
                Text = fileLine, Font = _fCardSub, ForeColor = cTextDark,
                Location = new Point(x, cy), AutoSize = false,
                Width = innerW, Height = S(18)
            });
            cy += S(20);

            // Description (wraps up to ~3 lines)
            string descText = string.IsNullOrEmpty(ecr.Description)
                ? "(no description)" : ecr.Description;
            int descH = Math.Min(S(54),
                TextRenderer.MeasureText(descText, _fCardSub,
                    new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak).Height + S(2));
            if (descH < S(18)) descH = S(18);
            card.Controls.Add(new Label
            {
                Text = descText, Font = _fCardSub, ForeColor = cTextDark,
                Location = new Point(x, cy), AutoSize = false,
                Width = innerW, Height = descH
            });
            cy += descH + S(4);

            // Requester + created date + age
            string created = ecr.CreatedDate == DateTime.MinValue ? ""
                : ecr.CreatedDate.ToString("MM/dd/yy HH:mm", CultureInfo.InvariantCulture);
            int ageDays = ecr.CreatedDate == DateTime.MinValue ? 0
                : (int)Math.Floor((DateTime.Now - ecr.CreatedDate).TotalDays);
            string meta = "By: " + (ecr.Requester ?? "") +
                (created.Length > 0 ? "   ·   " + created : "") +
                (ageDays > 0 ? "   ·   " + ageDays + "d ago" : "");
            card.Controls.Add(new Label
            {
                Text = meta, Font = _fCardMeta, ForeColor = cTextGray,
                Location = new Point(x, cy), AutoSize = false,
                Width = innerW, Height = S(16)
            });
            cy += S(18);

            // Reviewer / disposition / linked ECO (when reviewed)
            if (!string.IsNullOrEmpty(ecr.Reviewer) ||
                !string.IsNullOrEmpty(ecr.Disposition) ||
                !string.IsNullOrEmpty(ecr.LinkedEcoNumber))
            {
                string rev = "";
                if (!string.IsNullOrEmpty(ecr.Reviewer))
                    rev += "Reviewed by " + ecr.Reviewer;
                if (!string.IsNullOrEmpty(ecr.LinkedEcoNumber))
                    rev += (rev.Length > 0 ? "   ·   " : "") +
                        "→ ECO " + ecr.LinkedEcoNumber;
                if (!string.IsNullOrEmpty(ecr.Disposition))
                    rev += (rev.Length > 0 ? "   ·   " : "") + ecr.Disposition;
                int rH = Math.Min(S(36),
                    TextRenderer.MeasureText(rev, _fCardMeta,
                        new Size(innerW, int.MaxValue),
                        TextFormatFlags.WordBreak).Height + S(2));
                if (rH < S(16)) rH = S(16);
                card.Controls.Add(new Label
                {
                    Text = rev, Font = _fCardMeta, ForeColor = cPurple,
                    Location = new Point(x, cy), AutoSize = false,
                    Width = innerW, Height = rH
                });
                cy += rH + S(2);
            }

            // Action buttons (Master + actionable state only)
            if (actionable)
            {
                cy += S(2);
                int bH = S(24);
                int bW = S(120);
                int bx = x;

                Button accept = MakeBtn("Accept / Convert", cGreen, bx, cy, bW, bH);
                accept.Click += (s, e) => DoConvert(ecr);
                card.Controls.Add(accept);
                bx += bW + S(6);

                Button reject = MakeBtn("Reject", cRed, bx, cy, S(80), bH);
                reject.Click += (s, e) => DoReject(ecr);
                card.Controls.Add(reject);

                cy += bH + S(6);
            }
            else
            {
                cy += S(4);
            }

            card.Height = cy;
            return card;
        }

        private Button MakeBtn(string text, Color back, int x, int y, int w, int h)
        {
            Button b = new Button
            {
                Text = text, Font = _fCardBtn,
                Location = new Point(x, y), Width = w, Height = h,
                BackColor = back, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // Accept the ECR and convert it to an ECO when the ECO layer is present.
        private void DoConvert(EcrManager.Ecr ecr)
        {
            if (_busy) return;
            _busy = true;
            Enabled = false;
            try
            {
                var confirm = MessageBox.Show(
                    "Accept this change request" +
                    "?\n\nIf an ECO module is available it will be converted into " +
                    "an ECO; otherwise it is marked Accepted.\n\n" +
                    ecr.Number + "  —  " + (ecr.Type ?? "") +
                    "\n" + (ecr.Description ?? ""),
                    "BCore PDM — Accept Change Request",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;

                // Optional disposition note.
                string note = Prompt(
                    "Disposition note (optional):",
                    "BCore PDM — Disposition");
                // null = cancelled the note prompt -> still proceed with no note
                if (note == null) note = "";

                string eco;
                try { eco = EcrManager.ConvertToEco(ecr.Id, _user, note); }
                catch { eco = ""; }

                try
                {
                    AuditLogger.Log(string.IsNullOrEmpty(eco) ? "EcrReviewed" : "EcrConverted",
                        _user, SafeFileName(ecr.AffectedFilePath),
                        ecr.AffectedPartNo, "",
                        ecr.Number + " accepted" +
                        (string.IsNullOrEmpty(eco) ? "" : " → ECO " + eco) +
                        (string.IsNullOrEmpty(note) ? "" : " — " + note));
                }
                catch { }

                NotifyEngineer(ecr, "approved", note);

                MessageBox.Show(
                    string.IsNullOrEmpty(eco)
                        ? ecr.Number + " accepted."
                        : ecr.Number + " converted to ECO " + eco + ".",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not process the request: " + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                Enabled = true;
                LoadEcrs();
            }
        }

        private void DoReject(EcrManager.Ecr ecr)
        {
            if (_busy) return;
            _busy = true;
            Enabled = false;
            try
            {
                string reason = Prompt(
                    "Reason for rejecting this change request (sent to the engineer):",
                    "BCore PDM — Reject Change Request");
                if (reason == null) return; // cancelled

                try { EcrManager.SetEcrState(ecr.Id, EcrManager.StateRejected, _user, reason); }
                catch { }

                try
                {
                    AuditLogger.Log("EcrReviewed", _user,
                        SafeFileName(ecr.AffectedFilePath), ecr.AffectedPartNo, "",
                        ecr.Number + " rejected" +
                        (string.IsNullOrEmpty(reason) ? "" : " — " + reason));
                }
                catch { }

                NotifyEngineer(ecr, "rejected", reason);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not reject the request: " + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                Enabled = true;
                LoadEcrs();
            }
        }

        // Best-effort email to the requesting engineer (reuses the request
        // approve/reject notifiers — same intent, distinct ECR subject prefix
        // carried in the requestType). Non-fatal.
        private void NotifyEngineer(EcrManager.Ecr ecr, string outcome, string note)
        {
            try
            {
                string subjType = "ECR " + ecr.Number;
                string fn = SafeFileName(ecr.AffectedFilePath);
                if (outcome == "approved")
                    EmailManager.NotifyRequestApproved(subjType, fn, ecr.Requester);
                else
                    EmailManager.NotifyRequestRejected(subjType, fn, ecr.Requester, note);
            }
            catch { }
        }

        // Small modal text prompt (reused for disposition / rejection reason).
        // Returns null on cancel, else the entered text (possibly empty).
        private string Prompt(string prompt, string title)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.BackColor = cBg;
                f.ClientSize = new Size(S(360), S(180));
                int cW = f.ClientSize.Width;

                using (var fLbl = new Font("Segoe UI", 3.7f * _scale))
                using (var fBtn = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold))
                {
                    var lbl = new Label
                    {
                        Text = prompt, Font = fLbl, ForeColor = cTextDark,
                        Location = new Point(S(14), S(12)), AutoSize = false,
                        Width = cW - S(28), Height = S(40)
                    };
                    f.Controls.Add(lbl);

                    var box = new TextBox
                    {
                        Font = fLbl, Multiline = true, ScrollBars = ScrollBars.Vertical,
                        Location = new Point(S(14), S(54)),
                        Width = cW - S(28), Height = S(70)
                    };
                    f.Controls.Add(box);

                    int bH = S(26), bW = S(90);
                    int bY = f.ClientSize.Height - S(8) - bH;
                    var ok = new Button
                    {
                        Text = "OK", Font = fBtn,
                        Location = new Point(cW - S(8) - bW * 2 - S(6), bY),
                        Width = bW, Height = bH,
                        BackColor = cGreen, ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                        DialogResult = DialogResult.OK
                    };
                    ok.FlatAppearance.BorderSize = 0;
                    f.Controls.Add(ok);
                    var cancel = new Button
                    {
                        Text = "Cancel", Font = fBtn,
                        Location = new Point(cW - S(8) - bW, bY),
                        Width = bW, Height = bH,
                        BackColor = cDark, ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                        DialogResult = DialogResult.Cancel
                    };
                    cancel.FlatAppearance.BorderSize = 0;
                    f.Controls.Add(cancel);

                    f.AcceptButton = ok;
                    f.CancelButton = cancel;

                    return f.ShowDialog(this) == DialogResult.OK
                        ? (box.Text ?? "").Trim() : null;
                }
            }
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private string StateLabel(string state)
        {
            if (string.Equals(state, EcrManager.StateUnderReview,
                StringComparison.OrdinalIgnoreCase)) return "Under Review";
            return string.IsNullOrEmpty(state) ? "Submitted" : state;
        }

        private Color StateColor(string state)
        {
            if (string.Equals(state, EcrManager.StateAccepted, StringComparison.OrdinalIgnoreCase))
                return cGreen;
            if (string.Equals(state, EcrManager.StateConverted, StringComparison.OrdinalIgnoreCase))
                return cBrand;
            if (string.Equals(state, EcrManager.StateRejected, StringComparison.OrdinalIgnoreCase))
                return cMaroon;
            if (string.Equals(state, EcrManager.StateUnderReview, StringComparison.OrdinalIgnoreCase))
                return cOrange;
            return cTextDark; // Submitted
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !_busy) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Re-flow card widths to the new client width — re-render only, no
            // re-query of the ECR store (the cached _ecrs drive the layout).
            if (_listPanel != null && !_busy)
                RenderCards();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle?.Dispose(); _fCardBold?.Dispose(); _fCardSub?.Dispose();
                _fCardMeta?.Dispose(); _fCardBtn?.Dispose(); _fHint?.Dispose();
                _fFilter?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
