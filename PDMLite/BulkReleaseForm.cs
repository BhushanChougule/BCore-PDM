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
        private readonly Color cOrange   = Color.FromArgb(185, 115, 55);
        private readonly Color cPurple   = Color.FromArgb(105, 100, 165);
        private readonly Color cDark     = Color.FromArgb(75, 80, 90);
        private readonly Color cAmber    = Color.FromArgb(170, 120, 40);

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
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = cBg;
            // Size the CLIENT area (not the whole window) so bottom controls
            // never fall under the OS title bar / borders.
            this.ClientSize = new Size(S(560), S(600));

            int cW = this.ClientSize.Width;
            int cH = this.ClientSize.Height;

            Font fHeader = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fText = new Font("Segoe UI", 3.6f * _scale);
            Font fBtn = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);

            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location = new Point(0, 0),
                Width = cW,
                Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Bulk Release — WIP Files",
                Font = fHeader,
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false,
                Width = cW,
                Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            this.Controls.Add(titleBar);

            int margin = S(12);
            int innerW = cW - margin * 2;

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

            // Bottom buttons (computed from client height so they always fit).
            int btnH = S(30);
            int btnY = cH - margin - btnH;

            Button btnRelease = new Button
            {
                Text = "Release Selected",
                Font = fBtn,
                Location = new Point(margin, btnY),
                Width = innerW - S(96),
                Height = btnH,
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
                Location = new Point(margin + innerW - S(90), btnY),
                Width = S(90),
                Height = btnH,
                BackColor = cDark,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);

            // File list — fills the space between the count row and the buttons.
            int listTop = S(96);
            int listBottom = btnY - S(8);
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
        }

        // Short type tag + colour from the file extension.
        private void TypeTag(string fileName, out string tag, out Color color)
        {
            string ext = System.IO.Path.GetExtension(fileName).ToLower();
            if (ext == ".sldasm") { tag = "SLDASM"; color = cOrange; }
            else if (ext == ".slddrw") { tag = "SLDDRW"; color = cPurple; }
            else { tag = "SLDPRT"; color = cBrand; }
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
            Font fTag  = new Font("Segoe UI", 2.9f * _scale, FontStyle.Bold);
            Font fMeta = new Font("Segoe UI", 3.0f * _scale);

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
            int rowH = S(58);
            int tagW = S(50);

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
                    Location = new Point(S(6), S(20)),
                    Width = S(18),
                    Height = S(18),
                    Tag = f.FilePath
                };
                card.Controls.Add(cb);
                _checks.Add(cb);

                // Name (without extension), leaving room for the type tag.
                card.Controls.Add(new Label
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(f.FileName),
                    Font = fBold,
                    ForeColor = cTextDark,
                    Location = new Point(S(28), S(5)),
                    AutoSize = false,
                    Width = card.Width - S(28) - tagW - S(8),
                    Height = S(16),
                    AutoEllipsis = true
                });

                // Type tag, right-aligned.
                string tag; Color tagColor;
                TypeTag(f.FileName, out tag, out tagColor);
                card.Controls.Add(new Label
                {
                    Text = tag,
                    Font = fTag,
                    ForeColor = Color.White,
                    BackColor = tagColor,
                    Location = new Point(card.Width - tagW - S(6), S(6)),
                    Width = tagW,
                    Height = S(15),
                    TextAlign = ContentAlignment.MiddleCenter
                });

                // PN + description (or a hint when the part number is missing).
                bool hasPn = !string.IsNullOrWhiteSpace(f.PartNumber);
                string sub = hasPn ? "PN: " + f.PartNumber : "(no part number)";
                if (!string.IsNullOrWhiteSpace(f.Description))
                    sub += "   " + f.Description;
                card.Controls.Add(new Label
                {
                    Text = sub,
                    Font = fSub,
                    ForeColor = hasPn ? cTextGray : cAmber,
                    Location = new Point(S(28), S(24)),
                    AutoSize = false,
                    Width = card.Width - S(34),
                    Height = S(14),
                    AutoEllipsis = true
                });

                // Modified by / date (extra context for the Master).
                string meta = "";
                if (!string.IsNullOrWhiteSpace(f.ModifiedBy))
                    meta = "Modified by " + f.ModifiedBy;
                if (f.ModifiedDate != default(DateTime))
                    meta += (meta.Length > 0 ? " · " : "Modified ") +
                            f.ModifiedDate.ToString("dd/MM/yyyy");
                if (meta.Length > 0)
                    card.Controls.Add(new Label
                    {
                        Text = meta,
                        Font = fMeta,
                        ForeColor = cTextLight,
                        Location = new Point(S(28), S(40)),
                        AutoSize = false,
                        Width = card.Width - S(34),
                        Height = S(13),
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
