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

        // EM_SETCUEBANNER — shows grey placeholder text in a single-line TextBox
        // until the user types. Available since the placeholder property does not
        // exist on .NET Framework 4.8's TextBox.
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            if (box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            else
                box.HandleCreated += (s, e) =>
                    SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        }

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
        private Timer _searchTimer;
        private readonly List<CheckBox> _checks = new List<CheckBox>();

        // Guards the two-way sync between "Select all" and the card checkboxes.
        private bool _syncing;

        // Card fonts, created once: LoadFiles reruns on every 600ms search
        // debounce tick, and a Font assigned to a control is not owned by it —
        // per-call fonts leaked a GDI handle each (audit C4). Disposed in
        // Dispose(bool) (with the search timer).
        private readonly Font _fCardBold, _fCardSub, _fCardSubBold,
            _fCardTag, _fCardMeta;

        public BulkReleaseForm(float scale)
        {
            _scale = scale;

            _fCardBold    = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fCardSub     = new Font("Segoe UI", 3.2f * _scale);
            _fCardSubBold = new Font("Segoe UI", 3.2f * _scale, FontStyle.Bold);
            _fCardTag     = new Font("Segoe UI", 2.9f * _scale, FontStyle.Bold);
            _fCardMeta    = new Font("Segoe UI", 3.0f * _scale);

            BuildForm();
            LoadFiles();
        }

        // Timer + fonts are released here rather than on FormClosed:
        // FormClosed never fires for a form that is disposed without having
        // been shown (e.g. an exception out of ShowDialog) — which would have
        // left a LIVE timer ticking LoadFiles against a disposed form — while
        // the caller's using disposes unconditionally. Controls go first
        // (base), then the timer and their fonts.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _searchTimer?.Stop();
                _searchTimer?.Dispose();
                _fCardBold?.Dispose();
                _fCardSub?.Dispose();
                _fCardSubBold?.Dispose();
                _fCardTag?.Dispose();
                _fCardMeta?.Dispose();
            }
        }

        // Controls.Clear() does NOT dispose the removed controls — they get
        // re-parented to the hidden WinForms parking window and keep their
        // USER/GDI handles until the process dies. The list rebuilds on every
        // search tick, so dispose every child, THEN clear (audit C4).
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

            // Dynamic search box — full width, auto-searches after 600 ms of
            // inactivity (≥2 chars) or immediately clears back to all files when
            // the box is empty. Mirrors the task-pane search pattern.
            _filter = new TextBox
            {
                Font = fText,
                Location = new Point(margin, S(42)),
                Width = innerW
            };
            // Grey cue banner (placeholder) — Win32, works on .NET Framework 4.8.
            SetCueBanner(_filter, "Search by part number, description or filename…");

            _searchTimer = new Timer { Interval = 600 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); LoadFiles(); };
            _filter.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                if (string.IsNullOrWhiteSpace(_filter.Text))
                    LoadFiles();          // clear → show all immediately
                else if (_filter.Text.Length >= 2)
                    _searchTimer.Start(); // wait for pause before querying
            };
            this.Controls.Add(_filter);

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
                if (_syncing) return;
                _syncing = true;
                foreach (var cb in _checks) cb.Checked = _selectAll.Checked;
                _syncing = false;
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

        // Adds a bold "label" + regular "value" side by side on one row and
        // returns the X where the value ends (so the next pair can follow).
        // fill=false → value auto-sizes to its text (so a following pair butts
        // up against it); fill=true → value takes the remaining width with an
        // ellipsis (use for the last pair on the row).
        private int AddInlinePair(Panel card, string label, string value,
            Font fLabel, Font fValue, int x, int y, int rightEdge, bool fill)
        {
            var lbl = new Label
            {
                Text = label,
                Font = fLabel,
                ForeColor = cTextDark,
                Location = new Point(x, y),
                AutoSize = true
            };
            card.Controls.Add(lbl);
            int labelW = TextRenderer.MeasureText(label, fLabel).Width;

            int valX = x + labelW + S(3);
            int valW = TextRenderer.MeasureText(value, fValue).Width;
            var val = new Label
            {
                Text = value,
                Font = fValue,
                ForeColor = cTextGray,
                Location = new Point(valX, y),
                AutoSize = !fill,
                AutoEllipsis = fill
            };
            if (fill)
            {
                val.Width = Math.Max(S(10), rightEdge - valX);
                val.Height = S(14);
                valW = Math.Min(valW, val.Width);
            }
            card.Controls.Add(val);
            return valX + valW;
        }

        private void LoadFiles()
        {
            _checks.Clear();
            _selectAll.Checked = false;
            ClearAndDispose(_listPanel);

            bool truncated;
            List<VaultFile> files =
                DatabaseManager.GetReleasableFiles(_filter.Text, out truncated);

            _countLabel.Text = files.Count + " WIP file" +
                (files.Count == 1 ? "" : "s") + (truncated ? " (first 50)" : "");

            if (files.Count == 0)
            {
                _listPanel.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(_filter.Text)
                        ? "No WIP files to release."
                        : "No WIP files match \"" + _filter.Text + "\".",
                    Font = _fCardSub,
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
                cb.CheckedChanged += (s, e) =>
                {
                    if (_syncing) return;
                    _syncing = true;
                    _selectAll.Checked = _checks.Count > 0 && _checks.All(c => c.Checked);
                    _syncing = false;
                };
                card.Controls.Add(cb);
                _checks.Add(cb);

                // Name (without extension), leaving room for the type tag.
                card.Controls.Add(new Label
                {
                    Text = System.IO.Path.GetFileNameWithoutExtension(f.FileName),
                    Font = _fCardBold,
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
                    Font = _fCardTag,
                    ForeColor = Color.White,
                    BackColor = tagColor,
                    Location = new Point(card.Width - tagW - S(6), S(6)),
                    Width = tagW,
                    Height = S(15),
                    TextAlign = ContentAlignment.MiddleCenter
                });

                // PN + description on one line: the "PN:" / "DESC:" labels are
                // bold so they stand out from their values. Built from a row of
                // side-by-side AutoSize labels, positioned by measured width.
                // Drawings carry no properties of their own — they inherit PN +
                // Description from the part/assembly of the same base filename.
                string pn = f.PartNumber;
                string desc = f.Description;
                if (string.Equals(System.IO.Path.GetExtension(f.FileName),
                        ".slddrw", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(pn))
                {
                    var model = DatabaseManager.GetModelForDrawing(f.FilePath);
                    if (model != null)
                    {
                        pn = model.PartNumber;
                        desc = model.Description;
                    }
                }

                bool hasPn = !string.IsNullOrWhiteSpace(pn);
                bool hasDesc = !string.IsNullOrWhiteSpace(desc);
                int subY = S(24);
                int subX = S(28);
                int subRight = card.Width - S(6);

                if (!hasPn)
                {
                    // No part number — single amber hint, no bold label.
                    card.Controls.Add(new Label
                    {
                        Text = "(no part number)",
                        Font = _fCardSub,
                        ForeColor = cAmber,
                        Location = new Point(subX, subY),
                        AutoSize = true
                    });
                }
                else
                {
                    subX = AddInlinePair(card, "PN:", pn,
                        _fCardSubBold, _fCardSub, subX, subY, subRight, false);
                    if (hasDesc)
                    {
                        subX += S(10); // gap between the PN and DESC pairs
                        AddInlinePair(card, "DESC:", desc,
                            _fCardSubBold, _fCardSub, subX, subY, subRight, true);
                    }
                }

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
                        Font = _fCardMeta,
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
