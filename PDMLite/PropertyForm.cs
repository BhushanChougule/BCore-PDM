using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    // DPI-aware (house convention): _scale = g.DpiX / 96f, every size via
    // S(v) = (int)(v * _scale), every font as pt * _scale. AutoScaleMode.None
    // so this explicit scaling is the ONLY scaling — the form then looks the
    // same proportionally on a 1080p 100% machine and a 4K 250% machine.
    // Single-line labels use AutoSize so the bold text can never clip at the
    // bottom/right regardless of DPI; the subtitle wraps via MaximumSize.
    public class PropertyForm : Form
    {
        public bool PropertiesSaved { get; private set; } = false;

        private readonly float _scale = 1f;
        private int S(float v) => (int)(v * _scale);

        private ModelDoc2 _doc;
        private List<string> _fieldsToFill;                     // active-config mode
        private Dictionary<string, List<string>> _configIssues; // multi-config mode (null otherwise)

        // One entry per input row: the control plus the (config, field) it
        // writes to. ConfigName == null targets the active configuration.
        private sealed class InputRow
        {
            public Control Control;
            public string ConfigName;
            public string Field;
        }
        private readonly List<InputRow> _rows = new List<InputRow>();

        // Fonts are fields so they can be disposed with the form (assigning a
        // Font to a control does not transfer ownership — they would otherwise
        // leak a handle per dialog, and this dialog pops on every blocked save).
        private Font _headerFont, _subFont, _sectionFont, _labelFont, _inputFont, _buttonFont;

        // ── Baseline sizes (× _scale via S()) ─────────────────────────────
        // Calibrated to the house unit system (cf. ConfigRevisionPickerForm:
        // ~440-wide dialog, small base fonts) so this dialog renders at the
        // SAME physical size as the other BCore dialogs at any DPI — at
        // 4K/250% (_scale = 2.5) the form is ~1200px, not ~3000px.
        private const int FormWidthBase = 480;
        private const int LabelWidth = 126;
        private const int InputWidth = 314;
        private const int InputHeight = 24;
        private const int RowHeight = 32;
        private const int LeftMargin = 16;
        private const int InputLeft = 150;
        private const int StartYBase = 100;

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
            { "PartType",    "Part Type"      },
            // Config-specific drawing-scope choice (Rule 3.5 new-config form
            // only — see the askDrawingScope ctor parameter). Drives the Open
            // Drawing button: COMMON opens the shared {basename}.slddrw,
            // SEPARATE opens/creates {configName}.slddrw.
            { "DrawingScope", "Drawing"       }
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
            }},
            // Like PartType: NO "-- Select --" sentinel — index 0 (COMMON
            // DRAWING, the shop convention) is a valid default, so the field
            // never blocks a save.
            { "DrawingScope", new[] {
                "COMMON DRAWING",
                "SEPARATE DRAWING"
            }}
        };

        // Active-config mode (save-time Rules 3/3.5): one row per missing
        // field; values are written to the ACTIVE configuration.
        // askDrawingScope (Rule 3.5 new-config flow only): appends a
        // "Drawing" dropdown (COMMON DRAWING / SEPARATE DRAWING) so the
        // common-vs-per-config drawing decision is made the moment the new
        // configuration gets its identity — stored as the config-specific
        // DrawingScope property, which the Open Drawing button honours.
        public PropertyForm(ModelDoc2 doc, List<string> emptyFields,
            bool askDrawingScope = false)
        {
            _doc = doc;
            _fieldsToFill = emptyFields;
            if (askDrawingScope &&
                !emptyFields.Contains("DrawingScope"))
            {
                _fieldsToFill = new List<string>(emptyFields);
                _fieldsToFill.Add("DrawingScope");
            }
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;
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
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;
            BuildUI();
        }

        private void BuildUI()
        {
            bool multiCfg = _configIssues != null;

            // Base font sizes (× _scale) calibrated to the house dialogs
            // (cf. ConfigRevisionPickerForm: title 6f, body 3.7f) so the text
            // is the same physical size as the rest of the app at any DPI.
            _headerFont  = new Font("Segoe UI", 5.5f * _scale, FontStyle.Bold);
            _subFont     = new Font("Segoe UI", 3.4f * _scale);
            _sectionFont = new Font("Segoe UI", 4.1f * _scale, FontStyle.Bold);
            _labelFont   = new Font("Segoe UI", 3.7f * _scale);
            _inputFont   = new Font("Segoe UI", 3.7f * _scale);
            _buttonFont  = new Font("Segoe UI", 3.9f * _scale, FontStyle.Bold);

            this.Text = "BCore PDM — Complete Required Properties";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            // None = our S()/_scale is the ONLY scaling (no WinForms autoscale
            // on top of it), so the layout is identical on every monitor.
            this.AutoScaleMode = AutoScaleMode.None;
            this.BackColor = Color.FromArgb(245, 247, 250);
            this.ClientSize = new Size(S(FormWidthBase), S(StartYBase));
            this.AutoScroll = false;

            int contentWidth = S(FormWidthBase) - S(LeftMargin) * 2;

            // ── Header (AutoSize — never clips) ───────────────────────────
            Label headerLbl = new Label
            {
                Text = "Required properties are missing",
                Font = _headerFont,
                ForeColor = Color.FromArgb(180, 50, 50),
                AutoSize = true,
                Location = new Point(S(LeftMargin), S(16))
            };
            this.Controls.Add(headerLbl);
            int y = headerLbl.Bottom + S(6);

            // ── Subtitle (wraps via MaximumSize, grows vertically) ────────
            Label subLbl = new Label
            {
                Text = multiCfg
                    ? "Each configuration missing a field has its own row below."
                    : "File cannot be saved until all fields are filled.",
                Font = _subFont,
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoSize = true,
                MaximumSize = new Size(contentWidth, 0),
                Location = new Point(S(LeftMargin), y)
            };
            this.Controls.Add(subLbl);
            y = subLbl.Bottom + S(6);

            // ── Divider (below the subtitle) ──────────────────────────────
            Panel divider = new Panel
            {
                BackColor = Color.FromArgb(200, 210, 220),
                Height = Math.Max(1, S(1)),
                Width = contentWidth,
                Location = new Point(S(LeftMargin), y)
            };
            this.Controls.Add(divider);
            y = divider.Bottom + S(8);

            // ── Rows live in a scrollable panel so any number of configs ×
            //    fields fits on screen (buttons stay fixed below the panel) ──
            int panelTop = y;
            Panel rowsPanel = new Panel
            {
                Location = new Point(0, panelTop),
                Width = S(FormWidthBase),
                AutoScroll = true,
                BackColor = this.BackColor
            };

            int ry = 0;
            if (!multiCfg)
            {
                // One row per empty field, active configuration.
                foreach (string field in _fieldsToFill)
                {
                    if (!FieldLabels.ContainsKey(field)) continue;
                    ry = AddFieldRow(rowsPanel, field,
                        FieldLabels[field] + " *", null, ry);
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
                        Font = _sectionFont,
                        AutoSize = true,           // bold header never clips
                        Location = new Point(S(LeftMargin), ry + S(6)),
                        ForeColor = Color.FromArgb(44, 85, 128)
                    };
                    rowsPanel.Controls.Add(fieldHdr);
                    ry = fieldHdr.Bottom + S(4);

                    foreach (string cfg in needy)
                        ry = AddFieldRow(rowsPanel, field,
                            "      " + cfg, cfg, ry);
                }
            }

            // Cap the rows area to the screen so long lists scroll instead of
            // pushing the buttons off-screen.
            int maxRows = Screen.PrimaryScreen.WorkingArea.Height
                          - panelTop - S(80);
            if (maxRows < S(RowHeight)) maxRows = S(RowHeight);
            rowsPanel.Height = Math.Min(ry + S(6), maxRows);

            // When the cap bites, AutoScroll shows a vertical scrollbar that
            // would sit over the inputs' right edge (the right margin is
            // thinner than a scrollbar at high DPI) — shrink the inputs by
            // whatever part of the scrollbar the margin doesn't absorb.
            if (ry + S(6) > maxRows)
            {
                int rightMargin = S(FormWidthBase) - S(InputLeft) - S(InputWidth);
                int overlap = SystemInformation.VerticalScrollBarWidth
                              - rightMargin + S(4);
                if (overlap > 0)
                    foreach (var row in _rows)
                        row.Control.Width -= overlap;
            }
            this.Controls.Add(rowsPanel);

            int btnH = S(26);
            int btnY = rowsPanel.Bottom + S(10);

            // ── Buttons ───────────────────────────────────────────────────
            Button btnSave = new Button
            {
                Text = "Save Properties",
                Font = _buttonFont,
                Width = S(150),
                Height = btnH,
                Location = new Point(S(InputLeft), btnY),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += OnSaveClick;
            this.Controls.Add(btnSave);

            Button btnCancel = new Button
            {
                Text = "Cancel",
                Font = _buttonFont,
                Width = S(84),
                Height = btnH,
                Location = new Point(S(InputLeft) + S(158), btnY),
                BackColor = Color.FromArgb(220, 220, 220),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.Click += (s, e) => { PropertiesSaved = false; this.Close(); };
            this.Controls.Add(btnCancel);

            // Enter = Save, Esc = Cancel (every other house dialog wires these).
            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;

            // ── Final form height: fit panel + buttons + bottom padding ───
            this.ClientSize = new Size(S(FormWidthBase), btnY + btnH + S(14));
        }

        // Adds one label+input row for `field` targeting `configName` (null =
        // active configuration). Returns the next row's y position.
        private int AddFieldRow(Control parent, string field, string labelText,
            string configName, int y)
        {
            // Vertically centre the label against the input box.
            Label fieldLbl = new Label
            {
                Text = labelText,
                Font = _labelFont,
                AutoSize = false,
                Width = S(LabelWidth),
                Height = S(InputHeight),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(S(LeftMargin), y),
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
                    Width = S(InputWidth),
                    Location = new Point(S(InputLeft), y),
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
            // DrawnDate is the only user-entered date (CheckedDate is
            // auto-filled at release and is not in FieldLabels, so no row
            // can ever be created for it).
            else if (field == "DrawnDate")
            {
                DateTimePicker dtp = new DateTimePicker
                {
                    Font = _inputFont,
                    Width = S(InputWidth),
                    Location = new Point(S(InputLeft), y),
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
                    Width = S(InputWidth),
                    Location = new Point(S(InputLeft), y),
                    Text = defaultText,
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    CharacterCasing = CharacterCasing.Upper
                };
                input = tb;
            }

            parent.Controls.Add(input);
            _rows.Add(new InputRow
            {
                Control = input,
                ConfigName = configName,
                Field = field
            });
            return y + S(RowHeight);
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

            foreach (var row in _rows)
            {
                string value = InputValue(row.Control);

                if (string.IsNullOrEmpty(value))
                {
                    string label = FieldLabels.ContainsKey(row.Field)
                        ? FieldLabels[row.Field] : row.Field;
                    if (row.ConfigName != null)
                        label += "  (config \"" + row.ConfigName + "\")";
                    stillEmpty.Add(label);
                    row.Control.BackColor = Color.FromArgb(255, 220, 220);
                }
                else
                {
                    row.Control.BackColor = Color.White;
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

            foreach (var row in _rows)
            {
                string value = InputValue(row.Control);

                if (row.ConfigName == null)
                    PropertyValidator.SetProperty(_doc, row.Field, value);
                else
                    PropertyValidator.SetProperty(
                        _doc, row.Field, value, row.ConfigName);
            }

            PropertiesSaved = true;
            this.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _headerFont?.Dispose();
                _subFont?.Dispose();
                _sectionFont?.Dispose();
                _labelFont?.Dispose();
                _inputFont?.Dispose();
                _buttonFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
