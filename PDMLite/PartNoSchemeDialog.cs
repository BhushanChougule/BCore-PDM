using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    // The part-number generator + small admin spot for the per-division
    // numbering schemes. Opened from the "Generate" button on the Part Number
    // row of PropertyForm.
    //
    // Pick a division and click "Generate Next Number" — DatabaseManager
    // .GenerateNextPartNo reserves the next {Prefix}{Next zero-padded} value
    // ATOMICALLY under the cross-machine vault lock, so two engineers never
    // collide, and the result is filled into the Part Number field.
    //
    // A Master also sees an "Edit scheme" block (Prefix / Pad / Next) — the
    // small admin spot — gated on the live role (engineers see it read-only via
    // the current-scheme line only).
    //
    // Styled to house convention (cf. DrawingScopeDialog): brand title bar,
    // _scale-based fonts, flat coloured buttons. Fonts are fields disposed in
    // Dispose(bool) (a Font assigned to a control is not owned by it — audit C4).
    internal class PartNoSchemeDialog : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private Font _fTitle, _fBody, _fLabel, _fInput, _fHint, _fBtn;

        private ComboBox _division;
        private Label    _current;
        private TextBox  _prefix, _pad, _next;
        private readonly bool _isMaster;
        private List<NumberingScheme> _schemes;

        // The reserved part number, or null if the user cancelled.
        public string GeneratedPartNo { get; private set; }

        public PartNoSchemeDialog(string autoDivisionKey)
        {
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;

            _isMaster = string.Equals(
                DatabaseManager.GetUserRole(PDMLiteAddin.CurrentUser), "Master",
                StringComparison.OrdinalIgnoreCase);
            try { _schemes = DatabaseManager.GetNumberingSchemes(); }
            catch { _schemes = new List<NumberingScheme>(); }

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fBody  = new Font("Segoe UI", 3.7f * _scale);
            _fLabel = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fInput = new Font("Segoe UI", 3.7f * _scale);
            _fHint  = new Font("Segoe UI", 3.1f * _scale);
            _fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Generate Part Number";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(440), S(_isMaster ? 320 : 210));

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
                Text = "Generate Part Number", Font = _fTitle,
                ForeColor = Color.White, Location = new Point(0, 0),
                AutoSize = false, Width = cW, Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(14);
            int y = S(42);

            Controls.Add(new Label
            {
                Text = "Pick a division and generate the next number. It is " +
                       "reserved immediately, so no two engineers get the same one.",
                Font = _fBody, ForeColor = cTextDark,
                Location = new Point(x, y), AutoSize = false,
                Width = cW - S(28), Height = S(32)
            });
            y += S(38);

            Controls.Add(new Label
            {
                Text = "Division:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(x, y + S(3)), AutoSize = false,
                Width = S(70), Height = S(20)
            });
            _division = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = _fInput,
                Location = new Point(x + S(74), y),
                Width = cW - S(28) - S(74)
            };
            _division.Items.AddRange(DatabaseManager.WipDivisions);
            // Pre-select the file's own division when known.
            int sel = 0;
            if (!string.IsNullOrEmpty(autoDivisionKey))
                for (int i = 0; i < DatabaseManager.WipDivisions.Length; i++)
                    if (string.Equals(
                            DatabaseManager.DivisionKey(DatabaseManager.WipDivisions[i]),
                            autoDivisionKey, StringComparison.OrdinalIgnoreCase))
                    { sel = i; break; }
            if (_division.Items.Count > 0) _division.SelectedIndex = sel;
            _division.SelectedIndexChanged += (s, e) => RefreshScheme();
            Controls.Add(_division);
            y += S(30);

            _current = new Label
            {
                Font = _fHint, ForeColor = cTextGray,
                Location = new Point(x, y), AutoSize = false,
                Width = cW - S(28), Height = S(18)
            };
            Controls.Add(_current);
            y += S(26);

            if (_isMaster)
            {
                Controls.Add(new Panel
                {
                    BackColor = Color.FromArgb(200, 210, 220),
                    Height = Math.Max(1, S(1)), Width = cW - S(28),
                    Location = new Point(x, y)
                });
                y += S(8);

                Controls.Add(new Label
                {
                    Text = "Edit scheme (Master):", Font = _fLabel,
                    ForeColor = cBrandDark, Location = new Point(x, y),
                    AutoSize = true
                });
                y += S(24);

                _prefix = AddEditField("Prefix:", x, ref y);
                _pad    = AddEditField("Pad (digits):", x, ref y);
                _next   = AddEditField("Next number:", x, ref y);

                Button btnSave = new Button
                {
                    Text = "Save Scheme", Font = _fBtn,
                    Location = new Point(x, y),
                    Width = S(120), Height = S(24),
                    BackColor = cBrand, ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
                };
                btnSave.FlatAppearance.BorderSize = 0;
                btnSave.Click += OnSaveScheme;
                Controls.Add(btnSave);
                y += S(30);
            }

            // ── Bottom buttons ────────────────────────────────────────
            int btnH = S(26);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnCancel = new Button
            {
                Text = "Cancel", Font = _fBtn,
                Location = new Point(cW - S(8) - S(96), btnY),
                Width = S(96), Height = btnH,
                BackColor = cDark, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            Button btnGen = new Button
            {
                Text = "Generate Next Number", Font = _fBtn,
                Location = new Point(cW - S(8) - S(96) - S(6) - S(180), btnY),
                Width = S(180), Height = btnH,
                BackColor = cGreen, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            btnGen.FlatAppearance.BorderSize = 0;
            btnGen.Click += OnGenerate;
            Controls.Add(btnGen);

            AcceptButton = btnGen;
            CancelButton = btnCancel;

            RefreshScheme();
        }

        private TextBox AddEditField(string label, int x, ref int y)
        {
            Controls.Add(new Label
            {
                Text = label, Font = _fInput, ForeColor = cTextDark,
                Location = new Point(x, y + S(3)), AutoSize = false,
                Width = S(96), Height = S(20)
            });
            var tb = new TextBox
            {
                Font = _fInput, Location = new Point(x + S(100), y),
                Width = S(120), BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(tb);
            y += S(28);
            return tb;
        }

        private string SelectedKey()
            => DatabaseManager.DivisionKey(_division.SelectedItem?.ToString() ?? "");

        // Refresh the current-scheme line (and prefill the Master edit fields)
        // for the selected division.
        private void RefreshScheme()
        {
            string key = SelectedKey();
            NumberingScheme sc = _schemes.FirstOrDefault(s =>
                string.Equals(s.Division, key, StringComparison.OrdinalIgnoreCase));

            string prefix = sc != null ? sc.Prefix : key;
            int pad  = sc != null ? sc.Pad  : 4;
            int next = sc != null ? sc.Next : 1;

            string sample = prefix + next.ToString(
                System.Globalization.CultureInfo.InvariantCulture).PadLeft(pad, '0');
            _current.Text = sc != null
                ? "Current: prefix \"" + prefix + "\" · pad " + pad +
                  " · next \"" + sample + "\""
                : "New scheme — first number will be \"" + sample + "\"";

            if (_isMaster && _prefix != null)
            {
                _prefix.Text = prefix;
                _pad.Text    = pad.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _next.Text   = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private void OnSaveScheme(object sender, EventArgs e)
        {
            int pad, next;
            if (!int.TryParse((_pad.Text ?? "").Trim(), out pad) || pad < 1)
            {
                MessageBox.Show("Pad (digits) must be a whole number ≥ 1.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse((_next.Text ?? "").Trim(), out next) || next < 1)
            {
                MessageBox.Show("Next number must be a whole number ≥ 1.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                DatabaseManager.SetNumberingScheme(
                    SelectedKey(), (_prefix.Text ?? "").Trim(), pad, next);
                _schemes = DatabaseManager.GetNumberingSchemes();
                RefreshScheme();
                MessageBox.Show("Scheme saved.", "BCore PDM",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the scheme.\n\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnGenerate(object sender, EventArgs e)
        {
            string pn;
            try { pn = DatabaseManager.GenerateNextPartNo(SelectedKey()); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not generate a part number.\n\n" + ex.Message,
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(pn))
            {
                MessageBox.Show(
                    "Could not generate a part number (vault unavailable).\n\n" +
                    "Enter the part number manually.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            GeneratedPartNo = pn;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _fTitle?.Dispose();
                _fBody?.Dispose();
                _fLabel?.Dispose();
                _fInput?.Dispose();
                _fHint?.Dispose();
                _fBtn?.Dispose();
            }
        }
    }
}
