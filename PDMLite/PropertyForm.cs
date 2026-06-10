using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PDMLite
{
    public class PropertyForm : Form
    {
        public bool PropertiesSaved { get; private set; } = false;

        private ModelDoc2 _doc;
        private List<string> _fieldsToFill;
        private Dictionary<string, Control> _inputControls = new Dictionary<string, Control>();

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

        public PropertyForm(ModelDoc2 doc, List<string> emptyFields)
        {
            _doc = doc;
            _fieldsToFill = emptyFields;
            BuildUI();
        }

        private void BuildUI()
        {
            // ── Fixed sizes that work well together ───────────────────────
            int formWidth = 1200;
            int labelWidth = 380;
            int inputWidth = 480;
            int inputHeight = 46;
            int rowHeight = 62;
            int leftMargin = 30;
            int inputLeft = 410;
            int startY = 210;

            Font labelFont = new Font("Segoe UI", 11f);
            Font inputFont = new Font("Segoe UI", 11f);
            Font headerFont = new Font("Segoe UI", 12f, FontStyle.Bold);
            Font buttonFont = new Font("Segoe UI", 11f, FontStyle.Bold);

            this.Text = "BCore PDM — Complete Required Properties";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(245, 247, 250);
            this.Width = formWidth;
            this.AutoScroll = false;

            // ── Header ────────────────────────────────────────────────────
            Label headerLbl = new Label
            {
                Text = "Required properties are missing",
                Font = headerFont,
                ForeColor = Color.FromArgb(180, 50, 50),
                AutoSize = false,
                Width = formWidth - 40,
                Height = 52,
                Location = new Point(leftMargin, 45)
            };
            this.Controls.Add(headerLbl);

            Label subLbl = new Label
            {
                Text = "Complete all fields below. File cannot be saved until all fields are filled.",
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoSize = false,
                Width = formWidth - 40,
                Height = 44,
                Location = new Point(leftMargin, 128)
            };
            this.Controls.Add(subLbl);

            // ── Divider ───────────────────────────────────────────────────
            Panel divider = new Panel
            {
                BackColor = Color.FromArgb(200, 210, 220),
                Height = 2,
                Width = formWidth - 50,
                Location = new Point(leftMargin, 128)
            };
            this.Controls.Add(divider);

            // ── One row per empty field ───────────────────────────────────
            int y = startY;
            foreach (string field in _fieldsToFill)
            {
                if (!FieldLabels.ContainsKey(field)) continue;

                // Field label
                Label fieldLbl = new Label
                {
                    Text = FieldLabels[field] + " *",
                    Font = labelFont,
                    AutoSize = false,
                    Width = labelWidth,
                    Height = 50,
                    Location = new Point(leftMargin, y + 4),
                    ForeColor = Color.FromArgb(50, 50, 50)
                };
                this.Controls.Add(fieldLbl);

                string existing = PropertyValidator.GetProperty(_doc, field);
                Control input;

                if (Dropdowns.ContainsKey(field))
                {
                    ComboBox combo = new ComboBox
                    {
                        Font = inputFont,
                        Width = inputWidth,
                        Height = 50,
                        Location = new Point(inputLeft, y),
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
                        Font = inputFont,
                        Width = inputWidth,
                        Height = 50,
                        Location = new Point(inputLeft, y),
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
                        Font = inputFont,
                        Width = inputWidth,
                        Height = 50,
                        Location = new Point(inputLeft, y),
                        Text = defaultText,
                        BackColor = Color.White,
                        BorderStyle = BorderStyle.FixedSingle,
                        CharacterCasing = CharacterCasing.Upper
                    };
                    input = tb;
                }

                this.Controls.Add(input);
                _inputControls[field] = input;
                y += rowHeight;
            }

            y += 20;

            // ── Buttons ───────────────────────────────────────────────────
            Button btnSave = new Button
            {
                Text = "Save Properties",
                Font = buttonFont,
                Width = 300,
                Height = 44,
                Location = new Point(inputLeft, y),
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
                Location = new Point(inputLeft + 320, y),
                BackColor = Color.FromArgb(220, 220, 220),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.Click += (s, e) => { PropertiesSaved = false; this.Close(); };
            this.Controls.Add(btnCancel);

            // ── Set form height to fit all rows + buttons ─────────────────
            this.Height = y + 340;
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

        private void OnSaveClick(object sender, EventArgs e)
        {
            var stillEmpty = new List<string>();

            foreach (var kvp in _inputControls)
            {
                string field = kvp.Key;
                Control ctrl = kvp.Value;
                string value = "";

                if (ctrl is TextBox tb)
                    value = tb.Text.Trim();
                else if (ctrl is ComboBox cb)
                    value = ComboValue(cb);
                else if (ctrl is DateTimePicker dtp)
                    value = dtp.Value.ToString("MM/dd/yyyy");

                if (string.IsNullOrEmpty(value))
                {
                    stillEmpty.Add(FieldLabels.ContainsKey(field) ? FieldLabels[field] : field);
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
                string field = kvp.Key;
                Control ctrl = kvp.Value;
                string value = "";

                if (ctrl is TextBox tb)
                    value = tb.Text.Trim();
                else if (ctrl is ComboBox cb)
                    value = ComboValue(cb);
                else if (ctrl is DateTimePicker dtp)
                    value = dtp.Value.ToString("MM/dd/yyyy");

                PropertyValidator.SetProperty(_doc, field, value);
            }

            PropertiesSaved = true;
            this.Close();
        }
    }
}