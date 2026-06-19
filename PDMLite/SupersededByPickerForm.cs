using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PDMLite
{
    // Optional "superseded by" picker shown when a Master marks a file Obsolete:
    // choose the file that REPLACES it (the supersession link), so "→ superseded
    // by X" can be shown wherever the obsolete file appears. Skipping is allowed
    // (no replacement recorded) — this dialog never aborts the obsolete flow (the
    // Master has already confirmed and given a reason); it only sets or skips the
    // replacement.
    //
    // Searches the vault by part number / description / file name via the same
    // DatabaseManager.SearchFiles the task pane uses (debounced ≥2 chars). The
    // file being obsoleted is excluded from the results so it can't supersede
    // itself.
    //
    // DPI-aware (S(v)=v*_scale), house-styled (brand title bar, flat buttons);
    // fonts + the debounce timer are disposed in Dispose(bool).
    internal class SupersededByPickerForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private readonly string _excludePath;   // the file being obsoleted
        private readonly TextBox  _search;
        private readonly ListBox  _list;
        private readonly Button   _btnSet;
        private readonly Timer    _debounce;
        private readonly Font _fTitle, _fBody, _fInput, _fList, _fHint, _fBtn;

        private readonly List<VaultFile> _results = new List<VaultFile>();

        // The chosen replacement, or null if the Master skipped.
        public VaultFile Selected { get; private set; }

        public SupersededByPickerForm(string excludePath)
        {
            _excludePath = excludePath ?? "";

            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fBody  = new Font("Segoe UI", 3.7f * _scale);
            _fInput = new Font("Segoe UI", 3.7f * _scale);
            _fList  = new Font("Segoe UI", 3.6f * _scale);
            _fHint  = new Font("Segoe UI", 3.1f * _scale);
            _fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Superseded By";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(440), S(388));

            int cW = ClientSize.Width;

            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location  = new Point(0, 0),
                Width     = cW,
                Height    = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Superseded By", Font = _fTitle, ForeColor = Color.White,
                Location = new Point(0, 0), AutoSize = false,
                Width = cW, Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(16);
            int y = S(42);

            Controls.Add(new Label
            {
                Text = "Which file REPLACES this one? Search and pick the " +
                       "replacement, or Skip if there is none.",
                Font = _fBody, ForeColor = cTextDark,
                Location = new Point(x, y), AutoSize = false,
                Width = cW - S(32), Height = S(32)
            });
            y += S(38);

            _search = new TextBox
            {
                Font = _fInput,
                Location = new Point(x, y),
                Width = cW - S(32),
                Height = S(22)
            };
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            Controls.Add(_search);
            y += S(30);

            _list = new ListBox
            {
                Font = _fList,
                Location = new Point(x, y),
                Width = cW - S(32),
                Height = S(196),
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            _list.SelectedIndexChanged += (s, e) => UpdateSet();
            _list.DoubleClick += (s, e) => { if (_list.SelectedIndex >= 0) Commit(); };
            Controls.Add(_list);
            y += S(202);

            Controls.Add(new Label
            {
                Text = "Type at least 2 characters to search by part number, " +
                       "description or file name.",
                Font = _fHint, ForeColor = cTextGray,
                Location = new Point(x, y), AutoSize = false,
                Width = cW - S(32), Height = S(16)
            });

            int btnH = S(26);
            int btnW = S(120);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnSkip = new Button
            {
                Text = "Skip (no replacement)", Font = _fBtn,
                Location = new Point(x, btnY), Width = btnW + S(20), Height = btnH,
                BackColor = cDark, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel // Selected stays null
            };
            btnSkip.FlatAppearance.BorderSize = 0;
            Controls.Add(btnSkip);

            _btnSet = new Button
            {
                Text = "Set Replacement", Font = _fBtn,
                Location = new Point(cW - S(8) - btnW, btnY), Width = btnW, Height = btnH,
                BackColor = cGreen, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Enabled = false
            };
            _btnSet.FlatAppearance.BorderSize = 0;
            _btnSet.Click += (s, e) => { if (_list.SelectedIndex >= 0) Commit(); };
            Controls.Add(_btnSet);

            CancelButton = btnSkip; // Esc / close = Skip (no replacement)

            _debounce = new Timer { Interval = 400 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); RunSearch(); };
        }

        private void UpdateSet()
        {
            // Enable only on a REAL result row — the "(no matches)" /
            // "(vault unavailable)" placeholders are in _list but not _results,
            // so a selected placeholder index is >= _results.Count and must not
            // light up the Set button (clicking it would no-op).
            int i = _list.SelectedIndex;
            _btnSet.Enabled = i >= 0 && i < _results.Count;
        }

        private void Commit()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _results.Count) return;
            Selected = _results[i];
            DialogResult = DialogResult.OK;
            Close();
        }

        private void RunSearch()
        {
            string term = (_search.Text ?? "").Trim();
            _list.Items.Clear();
            _results.Clear();
            UpdateSet();
            if (term.Length < 2) return;

            List<VaultFile> hits;
            try
            {
                bool truncated;
                hits = DatabaseManager.SearchFiles(term, out truncated);
            }
            catch
            {
                _list.Items.Add("(vault unavailable)");
                return;
            }

            foreach (var f in hits)
            {
                // Don't let a file supersede itself.
                if (!string.IsNullOrEmpty(_excludePath) &&
                    string.Equals(f.FilePath, _excludePath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                _results.Add(f);
                string pn = string.IsNullOrEmpty(f.PartNumber)
                    ? "(no part no)" : f.PartNumber;
                string nm = Path.GetFileNameWithoutExtension(
                    f.FileName ?? Path.GetFileName(f.FilePath ?? ""));
                _list.Items.Add(pn + "   —   " + nm +
                    (string.IsNullOrEmpty(f.Status) ? "" : "   (" + f.Status + ")"));
            }
            if (_list.Items.Count == 0)
                _list.Items.Add("(no matches)"); // not selectable as a result
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _debounce?.Stop();
                _debounce?.Dispose();
                _fTitle?.Dispose(); _fBody?.Dispose(); _fInput?.Dispose();
                _fList?.Dispose();  _fHint?.Dispose(); _fBtn?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
