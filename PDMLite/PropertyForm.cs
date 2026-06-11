using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    public class PropertyForm : Form
    {
        public bool PropertiesSaved { get; private set; } = false;

        private ModelDoc2 _doc;
        private List<string> _fieldsToFill;                     // active-config mode
        private Dictionary<string, List<string>> _configIssues; // multi-config mode (null otherwise)

        // Composite input registry. Key = "{config}|{field}" ("" = active
        // config). _inputTargets maps the key back to [configOrNull, field] so
        // OnSaveClick writes each value to the right configuration.
        private Dictionary<string, Control> _inputControls = new Dictionary<string, Control>();
        private Dictionary<string, string[]> _inputTargets = new Dictionary<string, string[]>();

        // ── Fixed sizes that work well together ───────────────────────
        private const int FormWidthPx = 1200;
        private const int LabelWidth = 380;
        private const int InputWidth = 480;
        private const int RowHeight = 62;
        private const int LeftMargin = 30;
        private const int InputLeft = 410;
        private const int StartY = 210;

        private Font _labelFont;
        private Font _inputFont;

        private static readonly Dictionary<string, string> FieldLabels =
            new Dictionary<string, string>
        {
            { "PartNo",      "Part Number"    },
            { "DrawingNo",   "Drawing Number" },
            { "Description", "Description"    },
            { "DrawnBy",     "Drawn By"       },
            { "DrawnDate",   "Drawn Date"     },
            // Property name is Material1 (linked to the drawing template);
            // display label stays "Material".
            { "Material1",   "Material"       },
            { "FinishType",  "Finish Type"    },
            { "Revision",    "Revision"       },
            { "PartType",    "Part Type"      }
        };

        private static readonly Dictionary<string, string[]> Dropdowns =
            new Dictionary<string, string[]>
        {
            // Finish list mirrors the drawing template's Finish Type options.
            // ALL CAPS — everything on the drawing uses uppercase.
            { "FinishType", new[] {
                "-- Select --",
                "NONE",
                "PAINTED",
                "ZINC PLATE",
                "BLACK ZINC",
                "HOT DIPPED GALV.",
                "FNC",
                "SEE TABLE",
                "BLACK OXIDE",
                "PASSIVATE"
            }},
            // Material1 property (linked to drawing template). "BOM" = material
            // is called out in the BOM/table rather than on the part itself.
            // ALL CAPS — everything on the drawing uses uppercase.
            { "Material1", new[] {
                "-- Select --",
                "BOM",
                "ALUMINUM 6061-T6",
                "ALUMINUM 7075-T6",
                "ALUMINUM 5052-H32",
                "STEEL 1018",
                "STEEL 1045",
                "STAINLESS 304",
                "STAINLESS 316",
                "MILD STEEL A36",
                "HDPE",
                "NYLON 6/6",
                "POLYCARBONATE",
                "ACETAL (DELRIN)",
                "TITANIUM GRADE 5",
                "BRASS C360",
                "COPPER 110"
            }},
            { "Revision", new[] {
                "-- Select --",
                "A","B","C","D","E","F","G","H","J","K","L","M",
                "N","P","R","T","U","V","W","Y","Z"
            }},
            // PartType has NO "-- Select --" sentinel: index 0 (Manufactured)
            // is a valid default so the field never blocks a save by default.
            { "PartType", new[] {
                "Manufactured",
                "Purchased"
            }}
        };

        // Active-config mode (save-time Rules 3/3.5): one row per missing
        // field; values are written to the ACTIVE configuration.
        public PropertyForm(ModelDoc2 doc, List<string> emptyFields)
        {
            _doc = doc;
            _fieldsToFill = emptyFields;
            BuildUI();
        }

        // Multi-config mode (release gate): ONE dialog showing every
        // configuration's missing fields, grouped BY FIELD with one row per
        // configuration under each field header — so the user fills e.g.
        // Material for every config in one pass, without switching the active
        // configuration and re-running the release once per config. Values are
        // written to each row's own configuration; they may differ per config.
        public PropertyForm(ModelDoc2 doc,
            Dictionary<string, List<string>> configIssues)
        {
            _doc = doc;
            _configIssues = configIssues;
            BuildUI();
        }

        private void BuildUI()
        {
            bool multiCfg = _configIssues != null;

            _labelFont = new Font("Segoe UI", 11f);
            _inputFont = new Font("Segoe UI", 11f);
            Font headerFont = new Font("Segoe UI", 12f, FontStyle.Bold);
            Font sectionFont = new Font("Segoe UI", 11f, FontStyle.Bold);
            Font buttonFont = new Font("Segoe UI", 11f, FontStyle.Bold);

            this.Text = "BCore PDM — Complete Required Properties";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(245, 247, 250);
            this.Width = FormWidthPx;
            this.AutoScroll = false;

            // ── Header ────────────────────────────────────────────────────
            Label headerLbl = new Label
            {
                Text = "Required properties are missing",
                Font = headerFont,
                ForeColor = Color.FromArgb(180, 50, 50),
                AutoSize = false,
                Width = FormWidthPx - 40,
                Height = 52,
                Location = new Point(LeftMargin, 45)
            };
            this.Controls.Add(headerLbl);

            Label subLbl = new Label
            {
                Text = multiCfg
                    ? "Complete all fields below. Each configuration missing a " +
                      "field has its own row — values may differ per configuration."
                    : "Complete all fields below. File cannot be saved until all fields are filled.",
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoSize = false,
                Width = FormWidthPx - 40,
                Height = 44,
                Location = new Point(LeftMargin, 128)
            };
            this.Controls.Add(subLbl);

            // ── Divider ───────────────────────────────────────────────────
            Panel divider = new Panel
            {
                BackColor = Color.FromArgb(200, 210, 220),
                Height = 2,
                Width = FormWidthPx - 50,
                Location = new Point(LeftMargin, 128)
            };
            this.Controls.Add(divider);

            // ── Rows live in a scrollable panel so any number of configs ×
            //    fields fits on screen (buttons stay fixed below the panel) ──
            Panel rowsPanel = new Panel
            {
                Location = new Point(0, StartY),
                Width = FormWidthPx - 20,
                AutoScroll = true,
                BackColor = this.BackColor
            };

            int y = 0;
            if (!multiCfg)
            {
                // One row per empty field, active configuration.
                foreach (string field in _fieldsToFill)
                {
                    if (!FieldLabels.ContainsKey(field)) continue;
                    y = AddFieldRow(rowsPanel, field,
                        FieldLabels[field] + " *", null, y);
                }
            }
            else
            {
                // Group by FIELD in the canonical required-property order;
                // under each field header, one row per configuration missing
                // it (in the document's configuration order).
                var cfgOrder = PropertyValidator.GetConfigNames(_doc)
                    .Where(c => _configIssues.ContainsKey(c))
                    .ToList();
                foreach (var c in _configIssues.Keys)
                    if (!cfgOrder.Contains(c, StringComparer.OrdinalIgnoreCase))
                        cfgOrder.Add(c);

                foreach (string field in PropertyValidator.RequiredProperties)
                {
                    if (!FieldLabels.ContainsKey(field)) continue;
                    var needy = cfgOrder.Where(c => _configIssues[c]
                        .Contains(field, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (needy.Count == 0) continue;

                    Label fieldHdr = new Label
                    {
                        Text = FieldLabels[field] + " *",
                        Font = sectionFont,
                        AutoSize = false,
                        Width = FormWidthPx - 60,
                        Height = 36,
                        Location = new Point(LeftMargin, y + 8),
                        ForeColor = Color.FromArgb(44, 85, 128)
                    };
                    rowsPanel.Controls.Add(fieldHdr);
                    y += 46;

                    foreach (string cfg in needy)
                        y = AddFieldRow(rowsPanel, field,
                            "      " + cfg, cfg, y);
                }
            }

            // Cap the rows area to the screen so long lists scroll instead of
            // pushing the buttons off-screen.
            int maxRows = Screen.PrimaryScreen.WorkingArea.Height - StartY - 260;
            if (maxRows < RowHeight) maxRows = RowHeight;
            rowsPanel.Height = Math.Min(y + 10, maxRows);
            this.Controls.Add(rowsPanel);

            int btnY = StartY + rowsPanel.Height + 20;

            // ── Buttons ───────────────────────────────────────────────────
            Button btnSave = new Button
            {
                Text = "Save Properties",
                Font = buttonFont,
                Width = 300,
                Height = 44,
                Location = new Point(InputLeft, btnY),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += OnSaveClick;
            this.Controls.Add(btnSave);

            Button btnCancel = new Button
            {
                Text = "Cancel  ",
                Font = buttonFont,
                Width = 160,
                Height = 44,
                Location = new Point(InputLeft + 320, btnY),
                BackColor = Color.FromArgb(220, 220, 220),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.Click += (s, e) => { PropertiesSaved = false; this.Close(); };
            this.Controls.Add(btnCancel);

            // ── Set form height to fit panel + buttons ────────────────────
            this.Height = btnY + 160;
        }

        // Adds one label+input row for `field` targeting `configName` (null =
        // active configuration). Returns the next row's y position.
        private int AddFieldRow(Control parent, string field, string labelText,
            string configName, int y)
        {
            Label fieldLbl = new Label
            {
                Text = labelText,
                Font = _labelFont,
                AutoSize = false,
                Width = LabelWidth,
                Height = 50,
                Location = new Point(LeftMargin, y + 4),
                ForeColor = Color.FromArgb(50, 50, 50)
            };
            parent.Controls.Add(fieldLbl);

            string existing = configName == null
                ? PropertyValidator.GetProperty(_doc, field)
                : PropertyValidator.GetProperty(_doc, field, configName);
            Control input;

            if (Dropdowns.ContainsKey(field))
            {
                ComboBox combo = new ComboBox
                {
                    Font = _inputFont,
                    Width = InputWidth,
                    Height = 50,
                    Location = new Point(InputLeft, y),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.White
                };
                combo.Items.AddRange(Dropdowns[field]);
                combo.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(existing))
                {
                    int idx = combo.FindStringExact(existing);
                    if (idx >= 0) combo.SelectedIndex = idx;
                }
                input = combo;
            }
            else if (field == "DrawnDate" || field == "CheckedDate")
            {
                DateTimePicker dtp = new DateTimePicker
                {
                    Font = _inputFont,
                    Width = InputWidth,
                    Height = 50,
                    Location = new Point(InputLeft, y),
                    Format = DateTimePickerFormat.Short,
                    Value = DateTime.Today
                };
                if (DateTime.TryParse(existing, out DateTime existDate))
                    dtp.Value = existDate;
                input = dtp;
            }
            else
            {
                // DrawnBy defaults to the current user's initials (first two
                // letters of the username, uppercased — e.g. bchougule → BC,
                // rkramarz → RK), matching the CheckedBy convention. The
                // engineer can edit it; only pre-fill when it's empty.
                string defaultText = existing;
                if (field == "DrawnBy" && string.IsNullOrEmpty(existing))
                    defaultText = UserInitials();

                TextBox tb = new TextBox
                {
                    Font = _inputFont,
                    Width = InputWidth,
                    Height = 50,
                    Location = new Point(InputLeft, y),
                    Text = defaultText,
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    CharacterCasing = CharacterCasing.Upper
                };
                input = tb;
            }

            parent.Controls.Add(input);
            string key = (configName ?? "") + "|" + field;
            _inputControls[key] = input;
            _inputTargets[key] = new[] { configName, field };
            return y + RowHeight;
        }

        // Current user's initials: first two letters of the Windows username,
        // uppercased (bchougule → BC, rkramarz → RK). Same rule as CheckedBy.
        private static string UserInitials()
        {
            string u = PDMLiteAddin.CurrentUser ?? "";
            return u.Length >= 2 ? u.Substring(0, 2).ToUpper() : u.ToUpper();
        }

        // Reads a ComboBox's value. Dropdowns whose first item is the
        // "-- Select --" sentinel treat index 0 as "not chosen" (empty).
        // Dropdowns without the sentinel (e.g. PartType) have a valid value
        // at index 0, so we return the selected item directly.
        private static string ComboValue(ComboBox cb)
        {
            bool hasSentinel = cb.Items.Count > 0 &&
                string.Equals(cb.Items[0]?.ToString(), "-- Select --",
                    StringComparison.Ordinal);
            if (hasSentinel && cb.SelectedIndex <= 0) return "";
            return cb.SelectedItem?.ToString() ?? "";
        }

        private static string InputValue(Control ctrl)
        {
            if (ctrl is TextBox tb) return tb.Text.Trim();
            if (ctrl is ComboBox cb) return ComboValue(cb);
            if (ctrl is DateTimePicker dtp) return dtp.Value.ToString("MM/dd/yyyy");
            return "";
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            var stillEmpty = new List<string>();

            foreach (var kvp in _inputControls)
            {
                string[] target = _inputTargets[kvp.Key];
                string cfgName = target[0];
                string field = target[1];
                Control ctrl = kvp.Value;
                string value = InputValue(ctrl);

                if (string.IsNullOrEmpty(value))
                {
                    string label = FieldLabels.ContainsKey(field)
                        ? FieldLabels[field] : field;
                    if (cfgName != null) label += "  (config \"" + cfgName + "\")";
                    stillEmpty.Add(label);
                    ctrl.BackColor = Color.FromArgb(255, 220, 220);
                }
                else
                {
                    ctrl.BackColor = Color.White;
                }
            }

            if (stillEmpty.Count > 0)
            {
                MessageBox.Show(
                    "Please complete these fields:\n\n• " +
                    string.Join("\n• ", stillEmpty),
                    "Incomplete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            foreach (var kvp in _inputControls)
            {
                string[] target = _inputTargets[kvp.Key];
                string cfgName = target[0];
                string field = target[1];
                string value = InputValue(kvp.Value);

                if (cfgName == null)
                    PropertyValidator.SetProperty(_doc, field, value);
                else
                    PropertyValidator.SetProperty(_doc, field, value, cfgName);
            }

            PropertiesSaved = true;
            this.Close();
        }
    }
}
