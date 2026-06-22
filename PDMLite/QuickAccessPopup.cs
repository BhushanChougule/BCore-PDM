using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PDMLite
{
    // The quick-access surface for Saved searches / Recent files / Favourites,
    // opened from the task-pane search hint link. Kept as a popup (not inline in
    // the task pane) because that panel's layout is fixed-position — a growing
    // inline list would overlap the Active File section below it.
    //
    // The caller acts AFTER the modal closes (deferred, like the dashboard /
    // advanced-search popups): TermToRun → re-run the quick search; FileToOpen →
    // open that file. Favourite toggles + saved-search add/delete mutate
    // UserPrefs in place and rebuild the lists without closing.
    //
    // House styling: brand title bar, _scale fonts, flat buttons; fonts are
    // fields disposed in Dispose(bool).
    internal class QuickAccessPopup : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);
        private readonly Color cRed       = Color.FromArgb(180, 75, 75);

        private Font _fTitle, _fHdr, _fList, _fBtn;

        private readonly string _currentTerm;
        private ListBox _lbSaved, _lbRecent, _lbFav;
        private List<UserPrefs.SavedSearch> _saved = new List<UserPrefs.SavedSearch>();
        private List<string> _recentPaths = new List<string>();
        private List<string> _favPaths = new List<string>();

        // Set on close: run this saved-search term, or open this file (one or
        // neither). The caller checks both.
        public string TermToRun  { get; private set; }
        public string FileToOpen { get; private set; }

        public QuickAccessPopup(string currentTerm)
        {
            _currentTerm = (currentTerm ?? "").Trim();
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fHdr   = new Font("Segoe UI", 3.8f * _scale, FontStyle.Bold);
            _fList  = new Font("Segoe UI", 3.6f * _scale);
            _fBtn   = new Font("Segoe UI", 3.4f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Saved · Recent · Favorites";
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(420), S(560));

            int cW = ClientSize.Width;

            Panel titleBar = new Panel
            {
                BackColor = cBrandDark, Location = new Point(0, 0),
                Width = cW, Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = "Saved · Recent · Favorites", Font = _fTitle,
                ForeColor = Color.White, Location = new Point(0, 0),
                AutoSize = false, Width = cW, Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(12);
            int listW = cW - S(24);
            int y = S(42);

            // ── Save current search ───────────────────────────────────
            var btnSaveCur = MakeButton(
                string.IsNullOrEmpty(_currentTerm)
                    ? "Save current search (type a search first)"
                    : "Save current search:  \"" + _currentTerm + "\"",
                cGreen, x, y, listW, S(26));
            btnSaveCur.Enabled = !string.IsNullOrEmpty(_currentTerm);
            btnSaveCur.Click += OnSaveCurrent;
            Controls.Add(btnSaveCur);
            y += S(34);

            // ── Saved searches ─────────────────────────────────────────
            y = AddHeader("SAVED SEARCHES", x, y);
            _lbSaved = MakeList(x, y, listW, S(92));
            _lbSaved.DoubleClick += (s, e) => RunSelectedSaved();
            Controls.Add(_lbSaved);
            y += _lbSaved.Height + S(4);
            y = AddRowButtons(x, y, listW,
                "Run", (s, e) => RunSelectedSaved(),
                "Delete", cRed, (s, e) => DeleteSelectedSaved());

            // ── Recent ─────────────────────────────────────────────────
            y = AddHeader("RECENT", x, y);
            _lbRecent = MakeList(x, y, listW, S(92));
            _lbRecent.DoubleClick += (s, e) => OpenSelected(_lbRecent, _recentPaths);
            Controls.Add(_lbRecent);
            y += _lbRecent.Height + S(4);
            y = AddRowButtons(x, y, listW,
                "Open", (s, e) => OpenSelected(_lbRecent, _recentPaths),
                "★ Favorite", cBrand, (s, e) => FavoriteSelectedRecent());

            // ── Favorites ───────────────────────────────────────────────
            y = AddHeader("FAVORITES", x, y);
            _lbFav = MakeList(x, y, listW, S(92));
            _lbFav.DoubleClick += (s, e) => OpenSelected(_lbFav, _favPaths);
            Controls.Add(_lbFav);
            y += _lbFav.Height + S(4);
            y = AddRowButtons(x, y, listW,
                "Open", (s, e) => OpenSelected(_lbFav, _favPaths),
                "Remove ★", cDark, (s, e) => RemoveSelectedFavorite());

            var btnClose = MakeButton("Close", cDark, cW - S(12) - S(96), y + S(2),
                S(96), S(26));
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);
            CancelButton = btnClose;

            RebuildLists();
        }

        // Adds a section header label and returns the y just below it.
        private int AddHeader(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text, Font = _fHdr, ForeColor = cBrandDark,
                Location = new Point(x, y), AutoSize = true
            });
            return y + S(20);
        }

        private ListBox MakeList(int x, int y, int w, int h)
        {
            return new ListBox
            {
                Font = _fList, Location = new Point(x, y),
                Width = w, Height = h, IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private Button MakeButton(string text, Color back, int x, int y,
            int w, int h)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn, Location = new Point(x, y),
                Width = w, Height = h, BackColor = back, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private int AddRowButtons(int x, int y, int w,
            string t1, EventHandler h1, string t2, Color c2, EventHandler h2)
        {
            int bw = (w - S(6)) / 2;
            var b1 = MakeButton(t1, cBrand, x, y, bw, S(22));
            b1.Click += h1; Controls.Add(b1);
            var b2 = MakeButton(t2, c2, x + bw + S(6), y, bw, S(22));
            b2.Click += h2; Controls.Add(b2);
            return y + S(30);
        }

        private void RebuildLists()
        {
            _saved = UserPrefs.GetSavedSearches();
            _recentPaths = UserPrefs.GetRecent();
            _favPaths = UserPrefs.GetFavorites();

            _lbSaved.Items.Clear();
            foreach (var s in _saved)
                _lbSaved.Items.Add(s.Name + "   —   \"" + s.Term + "\"");
            if (_lbSaved.Items.Count == 0) _lbSaved.Items.Add("(none yet)");

            _lbRecent.Items.Clear();
            foreach (var p in _recentPaths)
                _lbRecent.Items.Add(Display(p));
            if (_lbRecent.Items.Count == 0) _lbRecent.Items.Add("(none yet)");

            _lbFav.Items.Clear();
            foreach (var p in _favPaths)
                _lbFav.Items.Add(Display(p));
            if (_lbFav.Items.Count == 0) _lbFav.Items.Add("(none yet)");
        }

        private static string Display(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); }
            catch { return path; }
        }

        private void OnSaveCurrent(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentTerm)) return;
            string name = Prompt("Save search", "Name this search:", _currentTerm);
            if (string.IsNullOrWhiteSpace(name)) return;
            UserPrefs.SaveSearch(name, _currentTerm);
            RebuildLists();
        }

        private void RunSelectedSaved()
        {
            int i = _lbSaved.SelectedIndex;
            if (i < 0 || i >= _saved.Count) return;   // guards the "(none yet)" row
            TermToRun = _saved[i].Term;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DeleteSelectedSaved()
        {
            int i = _lbSaved.SelectedIndex;
            if (i < 0 || i >= _saved.Count) return;
            UserPrefs.DeleteSavedSearch(_saved[i].Name);
            RebuildLists();
        }

        private void OpenSelected(ListBox lb, List<string> paths)
        {
            int i = lb.SelectedIndex;
            if (i < 0 || i >= paths.Count) return;     // guards the "(none yet)" row
            FileToOpen = paths[i];
            DialogResult = DialogResult.OK;
            Close();
        }

        private void FavoriteSelectedRecent()
        {
            int i = _lbRecent.SelectedIndex;
            if (i < 0 || i >= _recentPaths.Count) return;
            UserPrefs.ToggleFavorite(_recentPaths[i]);
            RebuildLists();
        }

        private void RemoveSelectedFavorite()
        {
            int i = _lbFav.SelectedIndex;
            if (i < 0 || i >= _favPaths.Count) return;
            UserPrefs.ToggleFavorite(_favPaths[i]); // toggling an existing fav removes it
            RebuildLists();
        }

        // Minimal name prompt (.NET has no InputBox). House-styled, modal.
        private string Prompt(string title, string prompt, string def)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.BackColor = cBg;
                f.ClientSize = new Size(S(320), S(120));
                var lbl = new Label
                {
                    Text = prompt, Font = _fList, ForeColor = cTextDark,
                    Location = new Point(S(12), S(12)), AutoSize = true
                };
                f.Controls.Add(lbl);
                var tb = new TextBox
                {
                    Font = _fList, Location = new Point(S(12), S(38)),
                    Width = S(296), Text = def ?? "",
                    BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(tb);
                var ok = MakeButton("OK", cGreen, S(320) - S(12) - S(160) - S(6),
                    S(80), S(80), S(26));
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);
                var cancel = MakeButton("Cancel", cDark, S(320) - S(12) - S(80),
                    S(80), S(80), S(26));
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fTitle?.Dispose();
                _fHdr?.Dispose();
                _fList?.Dispose();
                _fBtn?.Dispose();
            }
        }
    }
}
