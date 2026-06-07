using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    // Master-only picker that lists WIP (releasable) vault files with checkboxes
    // and releases the ticked ones in one pass via VaultManager.BulkRelease.
    // Distinct from the request-based approve flow: these files need not have
    // any pending request.
    public class BulkReleaseForm : Form
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
        private readonly Color cDark     = Color.FromArgb(75, 80, 90);

        private TextBox _filter;
        private Panel _listPanel;
        private CheckBox _selectAll;
        private Label _countLabel;
        private readonly List<CheckBox> _checks = new List<CheckBox>();

        public BulkReleaseForm(float scale)
        {
            _scale = scale;
            BuildForm();
            LoadFiles();
        }

        private void BuildForm()
        {
            this.Text = "BCore PDM — Bulk Release";
            this.Width = S(560);
            this.Height = S(560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = cBg;

            Font fHeader = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fText = new Font("Segoe UI", 3.6f * _scale);
            Font fBtn = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);

            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, 0),
                Width = this.Width,
                Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Bulk Release — WIP Files",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = this.Width,
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            this.Controls.Add(titleBar);

            int margin = S(12);
            int innerW = this.Width - margin * 2 - S(6);

            // Filter box + button
            _filter = new TextBox
            {
                Font = fText,
                Location = new Point(margin, S(42)),
                Width = innerW - S(72),
                Height = S(26)
            };
            _filter.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadFiles(); }
            };
            this.Controls.Add(_filter);

            Button btnFilter = new Button
            {
                Text = "Filter",
                Font = fBtn,
                Location = new Point(margin + innerW - S(66), S(42)),
                Width = S(66),
                Height = S(26),
                BackColor = cDark,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnFilter.FlatAppearance.BorderSize = 0;
            btnFilter.Click += (s, e) => LoadFiles();
            this.Controls.Add(btnFilter);

            // Select-all + count row
            _selectAll = new CheckBox
            {
                Text = "Select all",
                Font = fText,
                ForeColor = cTextGray,
                Location = new Point(margin, S(74)),
                AutoSize = true
            };
            _selectAll.CheckedChanged += (s, e) =>
            {
                foreach (var cb in _checks) cb.Checked = _selectAll.Checked;
            };
            this.Controls.Add(_selectAll);

            _countLabel = new Label
            {
                Font = fText,
                ForeColor = cTextLight,
                Location = new Point(margin + S(120), S(75)),
                AutoSize = false,
                Width = innerW - S(120),
                Height = S(18),
                TextAlign = ContentAlignment.MiddleRight
            };
            this.Controls.Add(_countLabel);

            // File list
            int listTop = S(96);
            int listBottom = this.Height - S(56);
            _listPanel = new Panel
            {
                Location = new Point(margin, listTop),
                Width = innerW,
                Height = listBottom - listTop,
                BackColor = cBg,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(_listPanel);

            // Bottom buttons
            Button btnRelease = new Button
            {
                Text = "Release Selected",
                Font = fBtn,
                Location = new Point(margin, this.Height - S(48)),
                Width = innerW - S(96),
                Height = S(30),
                BackColor = cGreen,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnRelease.FlatAppearance.BorderSize = 0;
            btnRelease.Click += (s, e) => ReleaseSelected();
            this.Controls.Add(btnRelease);

            Button btnClose = new Button
            {
                Text = "Close",
                Font = fBtn,
                Location = new Point(margin + innerW - S(90), this.Height - S(48)),
                Width = S(90),
                Height = S(30),
                BackColor = cDark,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);
        }

        private void LoadFiles()
        {
            _checks.Clear();
            _selectAll.Checked = false;
            _listPanel.Controls.Clear();

            bool truncated;
            List<VaultFile> files =
                DatabaseManager.GetReleasableFiles(_filter.Text, out truncated);

            Font fBold = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            Font fSub  = new Font("Segoe UI", 3.2f * _scale);

            _countLabel.Text = files.Count + " WIP file" +
                (files.Count == 1 ? "" : "s") + (truncated ? " (first 50)" : "");

            if (files.Count == 0)
            {
                _listPanel.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(_filter.Text)
                        ? "No WIP files to release."
                        : "No WIP files match \"" + _filter.Text + "\".",
                    Font = fSub,
                    ForeColor = cTextLight,
                    Location = new Point(S(8), S(10)),
                    AutoSize = true
                });
                return;
            }

            int ry = S(4);
            int rw = _listPanel.Width - S(6);
            int rowH = S(44);

            foreach (var f in files)
            {
                Panel card = new Panel
                {
                    BackColor = cCard,
                    Location = new Point(S(2), ry),
                    Width = rw - S(4),
                    Height = rowH
                };

                CheckBox cb = new CheckBox
                {
                    Location = new Point(S(6), S(14)),
                    Width = S(18),
                    Height = S(18),
                    Tag = f.FilePath
                };
                card.Controls.Add(cb);
                _checks.Add(cb);

                card.Controls.Add(new Label
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(f.FileName),
                    Font = fBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(28), S(5)),
                    AutoSize = false,
                    Width = rw - S(36),
                    Height = S(16),
                    AutoEllipsis = true
                });

                string sub = string.IsNullOrEmpty(f.PartNumber) ? "" : "PN: " + f.PartNumber;
                if (!string.IsNullOrEmpty(f.Description))
                    sub += (sub.Length > 0 ? "   " : "") + f.Description;
                card.Controls.Add(new Label
                {
                    Text = sub,
                    Font = fSub,
                    ForeColor = cTextGray,
                    Location = new Point(S(28), S(23)),
                    AutoSize = false,
                    Width = rw - S(36),
                    Height = S(14),
                    AutoEllipsis = true
                });

                card.Controls.Add(new Panel
                {
                    BackColor = cBorder,
                    Location = new Point(0, rowH - S(1)),
                    Width = rw - S(4),
                    Height = S(1)
                });

                _listPanel.Controls.Add(card);
                ry += rowH + S(3);
            }
        }

        private void ReleaseSelected()
        {
            var paths = _checks.Where(cb => cb.Checked)
                               .Select(cb => (string)cb.Tag)
                               .ToList();

            if (paths.Count == 0)
            {
                MessageBox.Show("Tick at least one file to release.",
                    "BCore PDM — Bulk Release",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    "Release " + paths.Count + " selected file" +
                    (paths.Count == 1 ? "" : "s") + "?\n\n" +
                    "Files are released parts → assemblies → drawings. " +
                    "Anything that fails validation is skipped and reported.",
                    "BCore PDM — Bulk Release",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var result = VaultManager.BulkRelease(paths);

            MessageBox.Show(result.BuildSummary("Bulk Release complete."),
                "BCore PDM — Batch Result",
                MessageBoxButtons.OK,
                result.Skipped.Count > 0 ? MessageBoxIcon.Warning
                                         : MessageBoxIcon.Information);

            LoadFiles(); // released files drop out (no longer WIP)
        }
    }
}
