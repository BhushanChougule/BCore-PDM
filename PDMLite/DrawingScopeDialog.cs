using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Asked once, at the moment a drawing is CREATED for a multi-config part /
    // assembly that has no drawing yet. Lets the user decide whether the new
    // drawing is shared by every configuration (a common drawing with a config
    // table) or specific to the active configuration only. After this choice the
    // file name on disk carries the decision, so the prompt never repeats.
    internal class DrawingScopeDialog : Form
    {
        public enum Scope { Cancel, Common, PerConfig }

        private readonly float _scale;
        private int S(int v) => (int)(v * _scale);

        public Scope Result { get; private set; } = Scope.Cancel;

        public DrawingScopeDialog(int configCount, string activeConfig)
        {
            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            ClientSize      = new Size(S(440), S(220));
            Text            = "BCore PDM — New Drawing";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;

            Controls.Add(new Label
            {
                Text      = "This file has " + configCount +
                            " configurations and no drawing yet.\n" +
                            "Select how the new drawing should be created:",
                Location  = new Point(S(12), S(12)),
                Size      = new Size(S(416), S(44)),
                Font      = new Font("Segoe UI", 9f * _scale),
                TextAlign = ContentAlignment.MiddleLeft
            });

            var rbCommon = new RadioButton
            {
                Text     = "Common drawing (one drawing for ALL configurations)",
                Location = new Point(S(18), S(66)),
                Size     = new Size(S(410), S(22)),
                Font     = new Font("Segoe UI", 9.5f * _scale),
                Checked  = true
            };
            Controls.Add(rbCommon);

            Controls.Add(new Label
            {
                Text      = "Use a config table or design table to differentiate configurations.",
                Location  = new Point(S(38), S(90)),
                Size      = new Size(S(390), S(18)),
                Font      = new Font("Segoe UI", 8f * _scale),
                ForeColor = System.Drawing.Color.FromArgb(100, 100, 100)
            });

            var rbPerCfg = new RadioButton
            {
                Text     = "This configuration only  (\"" + activeConfig + "\")",
                Location = new Point(S(18), S(120)),
                Size     = new Size(S(410), S(22)),
                Font     = new Font("Segoe UI", 9.5f * _scale)
            };
            Controls.Add(rbPerCfg);

            Controls.Add(new Label
            {
                Text      = "Other configurations can get their own drawings later.",
                Location  = new Point(S(38), S(144)),
                Size      = new Size(S(390), S(18)),
                Font      = new Font("Segoe UI", 8f * _scale),
                ForeColor = System.Drawing.Color.FromArgb(100, 100, 100)
            });

            var btnOk = new Button
            {
                Text         = "OK",
                Location     = new Point(S(248), S(180)),
                Size         = new Size(S(80), S(28)),
                Font         = new Font("Segoe UI", 9f * _scale),
                DialogResult = DialogResult.OK
            };
            btnOk.Click += (s, e) =>
            {
                Result = rbPerCfg.Checked ? Scope.PerConfig : Scope.Common;
            };
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text         = "Cancel",
                Location     = new Point(S(340), S(180)),
                Size         = new Size(S(88), S(28)),
                Font         = new Font("Segoe UI", 9f * _scale),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
