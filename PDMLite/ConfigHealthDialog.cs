using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Rule 3.6's per-configuration health dialog (multi-config saves only).
    // Replaces the plain Yes/No warning with an actionable choice: configs
    // whose NAME doesn't match their PartNo can be renamed in one click,
    // right now — the only moment a rename is cheap (before any assembly
    // references the config). Parts whose configurations are driven by a
    // DESIGN TABLE never get the rename button: the table owns the names,
    // and an API rename would desynchronise it (rename in the table instead).
    public sealed class ConfigHealthDialog : Form
    {
        public enum Choice { Cancel, SaveAnyway, Rename }
        public Choice Result { get; private set; } = Choice.Cancel;

        private readonly float _scale;
        private int S(float v) => (int)(v * _scale);

        private readonly Color cBrandDark = Color.FromArgb(44, 85, 128);
        private readonly Color cBrand     = Color.FromArgb(65, 120, 175);
        private readonly Color cBg        = Color.FromArgb(248, 249, 251);
        private readonly Color cTextDark  = Color.FromArgb(25, 30, 40);
        private readonly Color cTextGray  = Color.FromArgb(100, 110, 125);
        private readonly Color cGreen     = Color.FromArgb(60, 140, 95);
        private readonly Color cDark      = Color.FromArgb(75, 80, 90);
        private readonly Color cOrange    = Color.FromArgb(185, 115, 55);

        public ConfigHealthDialog(
            List<string> issueLines,
            List<string> renamePreview,   // "Default  →  FORD.01" lines
            List<string> renameSkipped,   // "X — reason" lines (may be empty)
            bool designTable,
            int parentAsmCount)
        {
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;

            Font fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            Font fBody  = new Font("Segoe UI", 3.7f * _scale);
            Font fBold  = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            Font fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);

            Text            = "BCore PDM — Configuration Check";
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;

            int cW = S(480);
            int x  = S(14);
            int textW = cW - x * 2;

            var titleBar = new Panel
            {
                BackColor = cBrandDark,
                Location  = new Point(0, 0),
                Width     = cW,
                Height    = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text      = "Configurations Need Attention",
                Font      = fTitle,
                ForeColor = Color.White,
                AutoSize  = false,
                Width     = cW,
                Height    = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int y = S(40);
            Label Add(string text, Font font, Color color)
            {
                var l = new Label
                {
                    Text        = text,
                    Font        = font,
                    ForeColor   = color,
                    AutoSize    = true,
                    MaximumSize = new Size(textW, 0),
                    Location    = new Point(x, y)
                };
                Controls.Add(l);
                y = l.Bottom + S(6);
                return l;
            }

            Add(string.Join("\n", issueLines), fBold, cTextDark);
            Add("Config names should match their Part No (per-config " +
                "drawings, search and revision tracking rely on it), and " +
                "every config needs all required properties before the " +
                "release gate will pass.", fBody, cTextGray);

            bool canRename = !designTable && renamePreview.Count > 0;
            if (designTable)
            {
                Add("This part's configurations are driven by a DESIGN " +
                    "TABLE — rename them in the table itself (automatic " +
                    "rename is disabled to keep the table in sync).",
                    fBody, cOrange);
            }
            else if (renamePreview.Count > 0)
            {
                Add("\"Rename && Save\" renames these configs to their " +
                    "Part No now — the only cheap moment (before any " +
                    "assembly references them):", fBody, cTextDark);
                Add(string.Join("\n", renamePreview), fBold, cBrandDark);
                if (renameSkipped.Count > 0)
                    Add("Not auto-renamed (fix manually):\n" +
                        string.Join("\n", renameSkipped), fBody, cOrange);
                if (parentAsmCount > 0)
                    Add("CAUTION: this part is used by " + parentAsmCount +
                        " assembl" + (parentAsmCount == 1 ? "y" : "ies") +
                        " — if any of them reference these configs BY NAME, " +
                        "renaming breaks that reference.", fBody, cOrange);
            }

            int btnH = S(26);
            int btnY = y + S(8);
            int bx   = cW - S(14);

            Button Make(string text, Color back, DialogResult dr)
            {
                var b = new Button
                {
                    Text         = text,
                    Font         = fBtn,
                    Height       = btnH,
                    Width        = S(108),
                    BackColor    = back,
                    ForeColor    = Color.White,
                    FlatStyle    = FlatStyle.Flat,
                    Cursor       = Cursors.Hand,
                    DialogResult = dr
                };
                b.FlatAppearance.BorderSize = 0;
                bx -= b.Width + S(6);
                b.Location = new Point(bx + S(6), btnY);
                Controls.Add(b);
                return b;
            }

            // Right-to-left placement: Cancel, Save Anyway, [Rename & Save]
            var btnCancel = Make("Cancel", cDark, DialogResult.Cancel);
            btnCancel.Click += (s, e) => Result = Choice.Cancel;
            var btnSave = Make("Save Anyway", cBrand, DialogResult.OK);
            btnSave.Click += (s, e) => Result = Choice.SaveAnyway;
            Button btnRename = null;
            if (canRename)
            {
                btnRename = Make("Rename && Save", cGreen, DialogResult.OK);
                btnRename.Click += (s, e) => Result = Choice.Rename;
            }

            AcceptButton = (IButtonControl)btnRename ?? btnSave;
            CancelButton = btnCancel;
            ClientSize   = new Size(cW, btnY + btnH + S(12));
        }
    }
}
