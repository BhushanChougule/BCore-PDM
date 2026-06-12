using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Asked once, at the moment a drawing is CREATED for a multi-config part /
    // assembly that has no drawing yet. Lets the user decide whether the new
    // drawing is shared by every configuration (a common drawing with a config
    // table) or specific to the active configuration only. After this choice the
    // file name on disk carries the decision, so the prompt never repeats.
    //
    // Styled to match PendingRequestsForm: brand title bar, small _scale-based
    // fonts (3–6f range), flat coloured buttons.
    internal class DrawingScopeDialog : Form
    {
        public enum Scope { Cancel, Common, PerConfig }

        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        public Scope Result { get; private set; } = Scope.Cancel;

        // sharedExists=false: the part has NO drawing yet (creation-time
        // choice). sharedExists=true: a COMMON drawing exists but the active
        // config has none of its own — Open the common one, or create a
        // separate drawing for this config (the learned-scope prompt; see
        // VaultManager.OpenOrCreateDrawing).
        public DrawingScopeDialog(int configCount, string activeConfig,
            bool sharedExists = false)
        {
            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            Font fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fBody  = new Font("Segoe UI", 3.7f * _scale);
            Font fOpt   = new Font("Segoe UI", 3.9f * _scale, FontStyle.Bold);
            Font fHint  = new Font("Segoe UI", 3.1f * _scale);
            Font fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = sharedExists ? "BCore PDM — Drawing Scope"
                                           : "BCore PDM — New Drawing";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            // The sharedExists variant carries longer option labels — give
            // it a wider client area so nothing clips (found in PR testing).
            ClientSize      = new Size(S(sharedExists ? 436 : 380), S(232));

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
                Text      = sharedExists ? "Open Drawing" : "New Drawing",
                Font      = fTitle,
                ForeColor = Color.White,
                Location  = new Point(0, 0),
                AutoSize  = false,
                Width     = cW,
                Height    = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(14);
            int y = S(42);

            Controls.Add(new Label
            {
                Text      = sharedExists
                    ? "This file has " + configCount + " configurations and " +
                      "a COMMON drawing.\n\"" + activeConfig +
                      "\" has no drawing of its own:"
                    : "This file has " + configCount +
                      " configurations and no drawing yet.\n" +
                      "Select how the new drawing should be created:",
                Font      = fBody,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(28),
                Height    = S(34)
            });
            y += S(40);

            var rbCommon = new RadioButton
            {
                Text      = sharedExists
                    ? "Open the common drawing  (covers ALL configs)"
                    : "Common drawing  (one for ALL configurations)",
                Font      = fOpt,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(28),
                Height    = S(20),
                Checked   = true
            };
            Controls.Add(rbCommon);
            y += S(20);

            Controls.Add(new Label
            {
                Text      = sharedExists
                    ? "Remembered for this configuration — never asked again."
                    : "Differentiate configurations with a config / design table.",
                Font      = fHint,
                ForeColor = cTextGray,
                Location  = new Point(x + S(16), y),
                AutoSize  = false,
                Width     = cW - S(44),
                Height    = S(16)
            });
            y += S(24);

            var rbPerCfg = new RadioButton
            {
                Text      = (sharedExists ? "Create a separate drawing for  \""
                                          : "This configuration only  (\"") +
                            activeConfig + "\"",
                Font      = fOpt,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(28),
                Height    = S(20)
            };
            Controls.Add(rbPerCfg);
            y += S(20);

            Controls.Add(new Label
            {
                Text      = sharedExists
                    ? "Creates a drawing named after this configuration; remembered."
                    : "Other configurations can get their own drawings later.",
                Font      = fHint,
                ForeColor = cTextGray,
                Location  = new Point(x + S(16), y),
                AutoSize  = false,
                Width     = cW - S(44),
                Height    = S(16)
            });

            // ── Bottom buttons (flat, anchored to client bottom) ──────
            int btnH = S(26);
            int btnW = S(96);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnCancel = new Button
            {
                Text         = "Cancel",
                Font         = fBtn,
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

            Button btnOk = new Button
            {
                Text         = "OK",
                Font         = fBtn,
                Location     = new Point(cW - S(8) - btnW * 2 - S(6), btnY),
                Width        = btnW,
                Height       = btnH,
                BackColor    = cGreen,
                ForeColor    = Color.White,
                FlatStyle    = FlatStyle.Flat,
                Cursor       = Cursors.Hand,
                DialogResult = DialogResult.OK
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                Result = rbPerCfg.Checked ? Scope.PerConfig : Scope.Common;
            };
            Controls.Add(btnOk);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
