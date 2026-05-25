using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PDMLite
{
    public class RollbackDialog : Form
    {
        public string SelectedFile { get; private set; }
        public string SelectedRevision { get; private set; }
        public bool Confirmed { get; private set; } = false;

        private float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        public RollbackDialog(string[] archivedFiles, string currentRev)
        {
            using (var g = this.CreateGraphics())
                _scale = g.DpiX / 96f;
            BuildUI(archivedFiles, currentRev);
        }

        private void BuildUI(string[] archivedFiles, string currentRev)
        {
            this.Text = "BCore PDM — Rollback Revision";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(245, 247, 250);
            this.Width = S(300);

            Font fHeader = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fSub = new Font("Segoe UI", 4f * _scale);
            Font fLabel = new Font("Segoe UI", 4f * _scale);
            Font fRevBold = new Font("Segoe UI", 4.5f * _scale, FontStyle.Bold);
            Font fFile = new Font("Segoe UI", 3.5f * _scale);
            Font fBtn = new Font("Segoe UI", 4f * _scale, FontStyle.Bold);

            int x = S(12);
            int w = S(245);
            int y = S(12);

            // ── Header ────────────────────────────────────────────────
            this.Controls.Add(new Panel
            {
                BackColor = Color.FromArgb(44, 85, 128),
                Location = new Point(0, 0),
                Width = S(300),
                Height = S(28)
            });

            this.Controls.Add(new Label
            {
                Text = "Rollback Revision",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = S(300),
                Height = S(28),
                TextAlign = ContentAlignment.MiddleCenter
            });
            y = S(36);

            // ── Current revision warning ──────────────────────────────
            this.Controls.Add(new Label
            {
                Text = "Current REV " + currentRev + " will be archived",
                Font = fSub,
                ForeColor = Color.FromArgb(180, 50, 50),
                Location = new Point(x, y),
                AutoSize = false,
                Width = w,
                Height = S(20)
            });
            y += S(22);

            // ── Divider ───────────────────────────────────────────────
            this.Controls.Add(new Panel
            {
                BackColor = Color.FromArgb(200, 210, 220),
                Height = S(1),
                Width = w,
                Location = new Point(x, y)
            });
            y += S(10);

            // ── Section label ─────────────────────────────────────────
            this.Controls.Add(new Label
            {
                Text = "Select revision to restore:",
                Font = fLabel,
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(x, y),
                AutoSize = true
            });
            y += S(22);

            // ── Sort files — most recent first ────────────────────────
            var sorted = new List<string>(archivedFiles);
            sorted.Sort();
            sorted.Reverse();

            foreach (string file in sorted)
            {
                string fileName = Path.GetFileName(file);
                string rev = ExtractRevision(fileName);

                // Card
                Panel card = new Panel
                {
                    BackColor = Color.White,
                    Location = new Point(x, y),
                    Width = w,
                    Height = S(46),
                    BorderStyle = BorderStyle.None
                };

                // Left accent
                card.Controls.Add(new Panel
                {
                    BackColor = Color.FromArgb(44, 85, 128),
                    Location = new Point(0, 0),
                    Width = S(4),
                    Height = S(46)
                });

                // REV label
                card.Controls.Add(new Label
                {
                    Text = "REV " + rev,
                    Font = fRevBold,
                    ForeColor = Color.FromArgb(30, 30, 30),
                    Location = new Point(S(8), S(5)),
                    AutoSize = true
                });

                // Filename
                card.Controls.Add(new Label
                {
                    Text = fileName,
                    Font = fFile,
                    ForeColor = Color.FromArgb(120, 120, 120),
                    Location = new Point(S(8), S(24)),
                    AutoSize = false,
                    Width = w - S(75),
                    Height = S(16),
                    AutoEllipsis = true
                });

                // Restore button
                string capturedFile = file;
                string capturedRev = rev;
                Button btnRestore = new Button
                {
                    Text = "Restore",
                    Font = fBtn,
                    Width = S(65),
                    Height = S(28),
                    Location = new Point(w - S(66), S(9)),
                    BackColor = Color.FromArgb(44, 85, 128),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnRestore.FlatAppearance.BorderSize = 0;
                btnRestore.Click += (s, e) =>
                {
                    SelectedFile = capturedFile;
                    SelectedRevision = "REV " + capturedRev;
                    Confirmed = true;
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                };
                card.Controls.Add(btnRestore);

                this.Controls.Add(card);
                y += S(52);
            }

            y += S(10);

            // ── Cancel button ─────────────────────────────────────────
            Button btnCancel = new Button
            {
                Text = "Cancel",
                Font = fBtn,
                Width = S(75),
                Height = S(28),
                Location = new Point(x, y),
                BackColor = Color.FromArgb(220, 220, 220),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };
            this.Controls.Add(btnCancel);

            this.Height = y + S(70);
        }

        private string ExtractRevision(string fileName)
        {
            int idx = fileName.ToUpper().LastIndexOf(" REV ");
            if (idx < 0) return "?";
            string after = fileName.Substring(idx + 5);
            int dot = after.IndexOf('.');
            return dot >= 0 ? after.Substring(0, dot) : after;
        }
    }
}