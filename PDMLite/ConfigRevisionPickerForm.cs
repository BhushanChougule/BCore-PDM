using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Lets a Master choose which configurations of a multi-config file to bump
    // to the next revision. All configs are pre-checked; the Master unchecks any
    // whose drawing did not change and should stay at the current revision.
    //
    // Styled to match DrawingScopeDialog / PendingRequestsForm: brand title bar,
    // small _scale-based fonts (3–6f range), flat coloured buttons. Earlier this
    // form used 8.5–9.5f base fonts, which rendered oversized and clipped the
    // text + button labels — the house convention keeps everything proportional.
    internal class ConfigRevisionPickerForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private readonly CheckedListBox _list;
        private readonly List<string>   _configNames;

        // Populated when the user clicks OK; null if the user cancelled.
        public List<string> SelectedConfigs { get; private set; }

        public ConfigRevisionPickerForm(
            List<string> configNames,
            List<string> currentRevs,
            List<string> nextRevs)
        {
            _configNames = configNames;

            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            Font fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fBody  = new Font("Segoe UI", 3.7f * _scale);
            Font fList  = new Font("Segoe UI", 3.7f * _scale);
            Font fSmall = new Font("Segoe UI", 3.3f * _scale, FontStyle.Bold);
            Font fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Select Configurations";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(440), S(330));

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
                Text      = "Select Configurations",
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
                Text      = "Select the configurations to bump to the next revision:",
                Font      = fBody,
                ForeColor = cTextDark,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(28),
                Height    = S(20)
            });
            y += S(26);

            // Bottom button row geometry — the list fills the space up to it.
            int btnH = S(26);
            int btnY = ClientSize.Height - S(8) - btnH;

            _list = new CheckedListBox
            {
                Location       = new Point(x, y),
                Size           = new Size(cW - S(28), btnY - y - S(8)),
                Font           = fList,
                ForeColor      = cTextDark,
                BackColor      = Color.White,
                BorderStyle    = BorderStyle.FixedSingle,
                IntegralHeight = false,
                CheckOnClick   = true
            };
            for (int i = 0; i < configNames.Count; i++)
            {
                string line = configNames[i] +
                    "   (REV " + currentRevs[i] + "  →  REV " + nextRevs[i] + ")";
                _list.Items.Add(line, isChecked: true);
            }
            Controls.Add(_list);

            // ── All / None (left) ──────────────────────────────────────
            var btnAll = MakeButton("All", cBrand, fSmall,
                new Point(x, btnY), S(58), btnH);
            btnAll.Click += (s, e) => SetAll(true);
            Controls.Add(btnAll);

            var btnNone = MakeButton("None", cDark, fSmall,
                new Point(x + S(64), btnY), S(58), btnH);
            btnNone.Click += (s, e) => SetAll(false);
            Controls.Add(btnNone);

            // ── OK / Cancel (right) ────────────────────────────────────
            int btnW = S(96);
            var btnCancel = MakeButton("Cancel", cDark, fBtn,
                new Point(cW - S(8) - btnW, btnY), btnW, btnH);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            var btnOk = MakeButton("OK", cGreen, fBtn,
                new Point(cW - S(8) - btnW * 2 - S(6), btnY), btnW, btnH);
            btnOk.DialogResult = DialogResult.OK;
            btnOk.Click += (s, e) =>
            {
                SelectedConfigs = new List<string>();
                for (int i = 0; i < _list.Items.Count; i++)
                    if (_list.GetItemChecked(i))
                        SelectedConfigs.Add(_configNames[i]);
            };
            Controls.Add(btnOk);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void SetAll(bool check)
        {
            for (int i = 0; i < _list.Items.Count; i++)
                _list.SetItemChecked(i, check);
        }

        private Button MakeButton(string text, Color back, Font font,
            Point loc, int width, int height)
        {
            var b = new Button
            {
                Text      = text,
                Font      = font,
                Location  = loc,
                Width     = width,
                Height    = height,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
