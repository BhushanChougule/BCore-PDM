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

            ClientSize      = new Size(S(460), S(232));
            Text            = "BCore PDM — New Drawing";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;

            Controls.Add(new Label
            {
                Text      = "This file has " + configCount + " configurations and no " +
                            "drawing yet.\nHow should the new drawing be created?",
                Location  = new Point(S(16), S(14)),
                Size      = new Size(S(428), S(48)),
                Font      = new Font("Segoe UI", 9.5f * _scale),
                TextAlign = ContentAlignment.MiddleLeft
            });

            var btnCommon = new Button
            {
                Text     = "Common drawing\n(one drawing for ALL configurations)",
                Location = new Point(S(16), S(72)),
                Size     = new Size(S(428), S(52)),
                Font     = new Font("Segoe UI", 9.5f * _scale),
                TextAlign = ContentAlignment.MiddleCenter
            };
            btnCommon.Click += (s, e) =>
            {
                Result = Scope.Common;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(btnCommon);

            var btnPerCfg = new Button
            {
                Text     = "This configuration only\n(\"" + activeConfig + "\")",
                Location = new Point(S(16), S(132)),
                Size     = new Size(S(428), S(52)),
                Font     = new Font("Segoe UI", 9.5f * _scale),
                TextAlign = ContentAlignment.MiddleCenter
            };
            btnPerCfg.Click += (s, e) =>
            {
                Result = Scope.PerConfig;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(btnPerCfg);

            var btnCancel = new Button
            {
                Text         = "Cancel",
                Location     = new Point(S(364), S(194)),
                Size         = new Size(S(80), S(28)),
                Font         = new Font("Segoe UI", 9f * _scale),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnCancel);

            CancelButton = btnCancel;
        }
    }
}
