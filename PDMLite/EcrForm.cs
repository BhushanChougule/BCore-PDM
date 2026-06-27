using System;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Engineer-facing dialog to RAISE an Engineering Change Request (ECR) — the
    // formal front of the change-control workflow. The affected file (path +
    // part number) is AUTO-FILLED from the active document by the caller and
    // shown read-only; the engineer picks a change TYPE + a categorised reason
    // CODE and types a description of the requested change.
    //
    // The dialog ENFORCES "complete": Submit is disabled until a description is
    // typed (a reason code is pre-selected to a sensible default but a real
    // description is required — an empty ECR is worthless to the reviewer). A
    // non-OK close is a cancel/abort.
    //
    // DPI-aware (S(v)=v*_scale), house-styled (brand title bar, flat buttons),
    // matching ReasonForChangeForm / SupersededByPickerForm. Fonts are fields
    // disposed in Dispose(bool) (a Font assigned to a control is not owned by it).
    internal class EcrForm : Form
    {
        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);

        private readonly ComboBox _type;
        private readonly ComboBox _reason;
        private readonly TextBox  _desc;
        private readonly Button   _btnOk;
        private readonly Font _fTitle, _fBody, _fLabel, _fInput, _fHint, _fBtn, _fMeta;

        // Results, read by the caller when DialogResult == OK.
        public string EcrType    { get; private set; }
        public string ReasonCode { get; private set; }
        // The full description: "ReasonCode — typed text" (or the typed text
        // alone for "Other"), so the reviewer sees the categorised reason and
        // the detail together.
        public string Description { get; private set; }

        // type can be pre-seeded (e.g. a "Revision" ECR raised alongside a
        // Revision request); pass "" for the General default.
        public EcrForm(string fileName, string partNo, string seedType)
        {
            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fBody  = new Font("Segoe UI", 3.7f * _scale);
            _fLabel = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fInput = new Font("Segoe UI", 3.7f * _scale);
            _fHint  = new Font("Segoe UI", 3.1f * _scale);
            _fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);
            _fMeta  = new Font("Segoe UI", 3.4f * _scale);

            Text            = "BCore PDM — New Change Request";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(460), S(380));

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
                Text      = "New Engineering Change Request",
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

            // ── Affected file (read-only, auto-filled) ─────────────────
            string fileMeta = "Affected file: " +
                (string.IsNullOrEmpty(fileName) ? "(unsaved)" : fileName) +
                (string.IsNullOrEmpty(partNo) ? "" : "   ·   Part No: " + partNo);
            Controls.Add(new Label
            {
                Text      = fileMeta,
                Font      = _fMeta,
                ForeColor = cTextGray,
                Location  = new Point(x, y),
                AutoSize  = false,
                Width     = cW - S(32),
                Height    = S(20)
            });
            y += S(26);

            // ── Change type ───────────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Change type:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(x, y + S(4)),
                AutoSize = false, Width = S(92), Height = S(22)
            });
            _type = new ComboBox
            {
                Font = _fInput,
                Location = new Point(x + S(96), y),
                Width = cW - S(96) - S(32),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _type.Items.AddRange(new object[]
            {
                EcrManager.TypeGeneral, EcrManager.TypeRevision,
                EcrManager.TypeRelease, EcrManager.TypeUnlock
            });
            _type.SelectedIndex = IndexOfType(seedType);
            Controls.Add(_type);
            y += S(32);

            // ── Reason code ───────────────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Reason:", Font = _fLabel, ForeColor = cTextDark,
                Location = new Point(x, y + S(4)),
                AutoSize = false, Width = S(92), Height = S(22)
            });
            _reason = new ComboBox
            {
                Font = _fInput,
                Location = new Point(x + S(96), y),
                Width = cW - S(96) - S(32),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var c in EcrManager.ReasonCodes()) _reason.Items.Add(c);
            if (_reason.Items.Count > 0) _reason.SelectedIndex = 0;
            Controls.Add(_reason);
            y += S(36);

            // ── Description (required) ────────────────────────────────
            Controls.Add(new Label
            {
                Text = "Describe the change requested:", Font = _fLabel,
                ForeColor = cTextDark, Location = new Point(x, y),
                AutoSize = false, Width = cW - S(32), Height = S(18)
            });
            y += S(20);

            _desc = new TextBox
            {
                Font = _fInput,
                Location = new Point(x, y),
                Width = cW - S(32),
                Height = S(96),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            _desc.TextChanged += (s, e) => UpdateOk();
            Controls.Add(_desc);
            y += S(100);

            Controls.Add(new Label
            {
                Text = "A reviewer (Master) will accept, reject, or convert this " +
                       "to an ECO. A description is required.",
                Font = _fHint, ForeColor = cTextGray,
                Location = new Point(x, y), AutoSize = false,
                Width = cW - S(32), Height = S(28)
            });

            // ── Bottom buttons ────────────────────────────────────────
            int btnH = S(26);
            int btnW = S(110);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnCancel = new Button
            {
                Text = "Cancel", Font = _fBtn,
                Location = new Point(cW - S(8) - btnW, btnY),
                Width = btnW, Height = btnH,
                BackColor = cDark, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(btnCancel);

            _btnOk = new Button
            {
                Text = "Submit ECR", Font = _fBtn,
                Location = new Point(cW - S(8) - btnW * 2 - S(6), btnY),
                Width = btnW, Height = btnH,
                BackColor = cGreen, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                DialogResult = DialogResult.OK
            };
            _btnOk.FlatAppearance.BorderSize = 0;
            _btnOk.Click += (s, e) => { Capture(); };
            Controls.Add(_btnOk);

            AcceptButton = _btnOk;
            CancelButton = btnCancel;
            UpdateOk();
        }

        private static int IndexOfType(string seedType)
        {
            if (string.Equals(seedType, EcrManager.TypeRevision,
                StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(seedType, EcrManager.TypeRelease,
                StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(seedType, EcrManager.TypeUnlock,
                StringComparison.OrdinalIgnoreCase)) return 3;
            return 0; // General
        }

        private bool HasDesc => !string.IsNullOrWhiteSpace(_desc.Text);

        private void UpdateOk()
        {
            _btnOk.Enabled = HasDesc;
        }

        private void Capture()
        {
            EcrType = _type.SelectedItem as string ?? EcrManager.TypeGeneral;
            string code = _reason.SelectedItem as string ?? "";
            ReasonCode = code;
            string detail = (_desc.Text ?? "").Trim();
            // Lead with the reason code so the description is scannable, except
            // for "Other" which commits the typed text alone (free-form).
            if (string.IsNullOrEmpty(code) ||
                code.Equals("Other", StringComparison.OrdinalIgnoreCase))
                Description = detail;
            else
                Description = detail.Length > 0 ? code + " — " + detail : code;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle?.Dispose(); _fBody?.Dispose(); _fLabel?.Dispose();
                _fInput?.Dispose(); _fHint?.Dispose();  _fBtn?.Dispose();
                _fMeta?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
