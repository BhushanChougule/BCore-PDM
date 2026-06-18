using System;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // A categorised "reason for change" capture dialog — the SOLIDWORKS-PDM /
    // ECO reason-code pattern: a dropdown of standard reason codes for the
    // operation plus an optional free-text detail. Reused by Release, Rollback
    // and Remove from Vault; each caller passes its own title, prompt and
    // category list. The committed string LEADS with the chosen code so it is
    // scannable in File History and groupable in the Audit Report; an optional
    // detail follows ("Code — detail"). The "Other" code REQUIRES a detail and
    // commits the detail text alone (the free-form escape hatch), so picking a
    // code never forces an artificial label onto a genuinely free-text reason.
    //
    // The dialog itself enforces "reason required": OK is disabled until a real
    // code is chosen (and, for "Other", a detail typed), so callers no longer
    // need their own re-prompt loop — a non-OK result means cancel/abort.
    //
    // DPI-aware (S(v)=v*_scale), styled to the house convention (brand title
    // bar, 3.1–6f fonts, flat coloured buttons), matching DrawingScopeDialog /
    // ConfigHealthDialog. Fonts are fields disposed in Dispose(bool) (a Font
    // assigned to a control is not owned by it — house audit-C4 discipline).
    internal class ReasonForChangeForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private const string Sentinel = "-- Select --";
        private const string Other    = "Other";

        private readonly ComboBox _category;
        private readonly TextBox  _detail;
        private readonly Button   _btnOk;
        private readonly Font _fTitle, _fBody, _fLabel, _fInput, _fHint, _fBtn;

        // The committed reason, or null if the user cancelled.
        public string Reason { get; private set; }

        public ReasonForChangeForm(string title, string prompt, string[] categories)
        {
            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fBody  = new Font("Segoe UI", 3.7f * _scale);
            _fLabel = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fInput = new Font("Segoe UI", 3.7f * _scale);
            _fHint  = new Font("Segoe UI", 3.1f * _scale);
            _fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — " + title;
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(440), S(258));

            int cW = ClientSize.Width;

            // ── Brand title bar ───────────────────────────────────────
            Panel titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location  = new Point(0, 0),
                Width     = cW,
                Height    = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text      = title,
                Font      = _fTitle,
                ForeColor = Color.White,
                Location  = new Point(0, 0),
                AutoSize  = false,
                Width     = cW,
                Height    = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(16);
            int y = S(42);

            // ── Prompt ────────────────────────────────────────────────
            Controls.Add(new Label
            {
                Text      = prompt,
                Font      = _fBody,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(32),
                Height    = S(34)
            });
            y += S(40);

            // ── Reason category (required) ─────────────────────────────
            Controls.Add(new Label
            {
                Text      = "Reason:",
                Font      = _fLabel,
                ForeColor = cTextDark,
                Location  = new Point(x, y + S(4)),
                AutoSize  = false,
                Width     = S(70),
                Height    = S(22)
            });
            _category = new ComboBox
            {
                Font          = _fInput,
                Location      = new Point(x + S(74), y),
                Width         = cW - S(74) - S(32),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _category.Items.Add(Sentinel);
            if (categories != null)
                foreach (var c in categories) _category.Items.Add(c);
            _category.SelectedIndex = 0;
            _category.SelectedIndexChanged += (s, e) => UpdateOk();
            Controls.Add(_category);
            y += S(32);

            // ── Optional free-text detail ──────────────────────────────
            Controls.Add(new Label
            {
                Text      = "Details (optional):",
                Font      = _fLabel,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(32),
                Height    = S(18)
            });
            y += S(20);

            _detail = new TextBox
            {
                Font       = _fInput,
                Location   = new Point(x, y),
                Width      = cW - S(32),
                Height     = S(48),
                Multiline  = true,
                ScrollBars = ScrollBars.Vertical
            };
            _detail.TextChanged += (s, e) => UpdateOk();
            Controls.Add(_detail);
            y += S(52);

            Controls.Add(new Label
            {
                Text      = "Pick a standard reason; add detail if helpful. " +
                            "\"Other\" requires a detail.",
                Font      = _fHint,
                ForeColor = cTextGray,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(32),
                Height    = S(16)
            });

            // ── Bottom buttons (flat, anchored to client bottom) ──────
            int btnH = S(26);
            int btnW = S(96);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnCancel = new Button
            {
                Text         = "Cancel",
                Font         = _fBtn,
                Location     = new Point(cW - S(8) - btnW, btnY),
                Width        = btnW,
                Height       = btnH,
                BackColor    = cDark,
                ForeColor    = Color.White,
                FlatStyle    = FlatStyle.Flat,
                Cursor       = Cursors.Hand,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            _btnOk = new Button
            {
                Text         = "OK",
                Font         = _fBtn,
                Location     = new Point(cW - S(8) - btnW * 2 - S(6), btnY),
                Width        = btnW,
                Height       = btnH,
                BackColor    = cGreen,
                ForeColor    = Color.White,
                FlatStyle    = FlatStyle.Flat,
                Cursor       = Cursors.Hand,
                DialogResult = DialogResult.OK
            };
            _btnOk.FlatAppearance.BorderSize = 0;
            _btnOk.Click += (s, e) => { Reason = Compose(); };
            Controls.Add(_btnOk);

            AcceptButton = _btnOk;
            CancelButton = btnCancel;
            UpdateOk();
        }

        private bool HasDetail => !string.IsNullOrWhiteSpace(_detail.Text);

        private string SelectedCategory =>
            _category.SelectedIndex > 0 ? (string)_category.SelectedItem : "";

        // OK enabled only with a real reason: a code chosen, and — for the
        // free-form "Other" — a detail typed. Mirrors the "reason required"
        // gate the callers expect (a non-OK close is a cancel/abort).
        private void UpdateOk()
        {
            string cat = SelectedCategory;
            _btnOk.Enabled = cat.Length > 0 &&
                (!cat.Equals(Other, StringComparison.OrdinalIgnoreCase) || HasDetail);
        }

        private string Compose()
        {
            string cat = SelectedCategory;
            string detail = (_detail.Text ?? "").Trim();
            if (cat.Equals(Other, StringComparison.OrdinalIgnoreCase))
                return detail;                                  // free-form escape hatch
            return detail.Length > 0 ? cat + " — " + detail : cat;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle?.Dispose(); _fBody?.Dispose(); _fLabel?.Dispose();
                _fInput?.Dispose(); _fHint?.Dispose();  _fBtn?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
