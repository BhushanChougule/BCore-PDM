using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    // Lets a Master choose which configurations of a multi-config file to bump
    // to the next revision. All configs are pre-checked; Master unchecks any
    // whose drawing did not change and should stay at the current revision.
    internal class ConfigRevisionPickerForm : Form
    {
        private readonly float _scale;
        private int S(int v) => (int)(v * _scale);

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

            ClientSize      = new Size(S(440), S(290));
            Text            = "BCore PDM — Select Configurations";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;

            Controls.Add(new Label
            {
                Text      = "Select configurations to bump to the next revision:",
                Location  = new Point(S(12), S(12)),
                Size      = new Size(S(416), S(26)),
                Font      = new Font("Segoe UI", 9f * _scale),
                TextAlign = ContentAlignment.MiddleLeft
            });

            _list = new CheckedListBox
            {
                Location     = new Point(S(12), S(44)),
                Size         = new Size(S(416), S(172)),
                Font         = new Font("Segoe UI", 9.5f * _scale),
                CheckOnClick = true
            };
            for (int i = 0; i < configNames.Count; i++)
            {
                string line = configNames[i] +
                    "   (REV " + currentRevs[i] + "  →  REV " + nextRevs[i] + ")";
                _list.Items.Add(line, isChecked: true);
            }
            Controls.Add(_list);

            var btnAll = new Button
            {
                Text     = "All",
                Location = new Point(S(12), S(228)),
                Size     = new Size(S(58), S(28)),
                Font     = new Font("Segoe UI", 8.5f * _scale)
            };
            btnAll.Click += (s, e) =>
            {
                for (int i = 0; i < _list.Items.Count; i++)
                    _list.SetItemChecked(i, true);
            };
            Controls.Add(btnAll);

            var btnNone = new Button
            {
                Text     = "None",
                Location = new Point(S(76), S(228)),
                Size     = new Size(S(58), S(28)),
                Font     = new Font("Segoe UI", 8.5f * _scale)
            };
            btnNone.Click += (s, e) =>
            {
                for (int i = 0; i < _list.Items.Count; i++)
                    _list.SetItemChecked(i, false);
            };
            Controls.Add(btnNone);

            var btnOk = new Button
            {
                Text         = "OK",
                Location     = new Point(S(258), S(228)),
                Size         = new Size(S(80), S(28)),
                Font         = new Font("Segoe UI", 9f * _scale),
                DialogResult = DialogResult.OK
            };
            btnOk.Click += (s, e) =>
            {
                SelectedConfigs = new List<string>();
                for (int i = 0; i < _list.Items.Count; i++)
                    if (_list.GetItemChecked(i))
                        SelectedConfigs.Add(_configNames[i]);
            };
            Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text         = "Cancel",
                Location     = new Point(S(348), S(228)),
                Size         = new Size(S(80), S(28)),
                Font         = new Font("Segoe UI", 9f * _scale),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
