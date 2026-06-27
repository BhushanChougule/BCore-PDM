using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PDMLite
{
    // ── Engineering Change Bulletin — create / edit dialog ───────────────────
    //
    // DPI-aware (S(v)=v*_scale), house-styled (brand title bar, flat coloured
    // buttons, fonts disposed in Dispose(bool)), matching ReasonForChangeForm /
    // SupersededByPickerForm. Lets a Master compose or edit an ECB: Number (read-
    // only once assigned), Title, categorised Reason (EcbManager.ReasonCodes),
    // Description, State, and an editable affected-items grid (File / Part No /
    // From Rev / To Rev). New items can be added via a vault search picker
    // (SupersededByPickerForm — the same search engine), removed via the Remove
    // button, and the From/To revisions are editable in-grid.
    //
    // Two modes: NEW (ctor with an optional seed Ecb, e.g. from the post-release
    // hook) and EDIT (ctor given an existing Ecb). On OK the dialog persists via
    // EcbManager.CreateEcb / UpdateEcb and exposes the saved Ecb via Result.
    internal class EcbForm : Form
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
        private readonly Color cMaroon    = Color.FromArgb(140, 60, 60);

        private const string ReasonSentinel = "-- Select --";

        private readonly Ecb _ecb;          // the working copy (null-state new)
        private readonly bool _isNew;

        private readonly TextBox  _title;
        private readonly ComboBox _reason;
        private readonly TextBox  _reasonDetail;
        private readonly TextBox  _desc;
        private readonly ComboBox _state;
        private readonly DataGridView _grid;
        private readonly Font _fTitle, _fLabel, _fInput, _fHint, _fBtn, _fCell;

        // The persisted ECB after OK (null if cancelled or save failed).
        public Ecb Result { get; private set; }

        public EcbForm(Ecb ecb)
        {
            _isNew = ecb == null || string.IsNullOrEmpty(ecb.Id);
            _ecb = ecb ?? new Ecb();
            if (_ecb.Items == null) _ecb.Items = new List<EcbAffectedItem>();

            using (var g = CreateGraphics())
                _scale = g.DpiX / 96f;

            _fTitle = new Font("Segoe UI", 6f * _scale, FontStyle.Bold);
            _fLabel = new Font("Segoe UI", 3.7f * _scale, FontStyle.Bold);
            _fInput = new Font("Segoe UI", 3.7f * _scale);
            _fHint  = new Font("Segoe UI", 3.1f * _scale);
            _fBtn   = new Font("Segoe UI", 3.6f * _scale, FontStyle.Bold);
            _fCell  = new Font("Segoe UI", 3.4f * _scale);

            string titleText = _isNew ? "New Engineering Change Bulletin"
                                      : "ECB " + (_ecb.Number ?? "");
            Text            = "BCore PDM — " + (_isNew ? "New ECB" : "Edit ECB");
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = cBg;
            ClientSize      = new Size(S(560), S(560));

            int cW = ClientSize.Width;

            // ── Brand title bar ───────────────────────────────────────
            Panel titleBar = new Panel
            {
                BackColor = cBrandDark, Location = new Point(0, 0),
                Width = cW, Height = S(32)
            };
            titleBar.Controls.Add(new Label
            {
                Text = titleText, Font = _fTitle, ForeColor = Color.White,
                Location = new Point(0, 0), AutoSize = false,
                Width = cW, Height = S(32),
                TextAlign = ContentAlignment.MiddleCenter
            });
            Controls.Add(titleBar);

            int x = S(16);
            int y = S(42);
            int fieldW = cW - S(32);

            // ── Title ─────────────────────────────────────────────────
            Controls.Add(Lbl("Title:", x, y, fieldW));
            y += S(18);
            _title = new TextBox
            {
                Font = _fInput, Location = new Point(x, y),
                Width = fieldW, Height = S(22), Text = _ecb.Title ?? ""
            };
            Controls.Add(_title);
            y += S(30);

            // ── Reason (categorised) + optional detail ────────────────
            Controls.Add(Lbl("Reason:", x, y + S(4), S(70)));
            _reason = new ComboBox
            {
                Font = _fInput, Location = new Point(x + S(74), y),
                Width = fieldW - S(74), DropDownStyle = ComboBoxStyle.DropDownList
            };
            _reason.Items.Add(ReasonSentinel);
            foreach (var c in EcbManager.ReasonCodes) _reason.Items.Add(c);
            // Pre-select the stored reason's leading code if it matches.
            _reason.SelectedIndex = 0;
            PreselectReason(_ecb.Reason);
            Controls.Add(_reason);
            y += S(30);

            _reasonDetail = new TextBox
            {
                Font = _fInput, Location = new Point(x, y),
                Width = fieldW, Height = S(22),
                Text = ReasonDetailOf(_ecb.Reason)
            };
            Controls.Add(_reasonDetail);
            Controls.Add(new Label
            {
                Text = "Optional reason detail.", Font = _fHint,
                ForeColor = cTextGray, Location = new Point(x, y + S(23)),
                AutoSize = false, Width = fieldW, Height = S(14)
            });
            y += S(40);

            // ── Description ───────────────────────────────────────────
            Controls.Add(Lbl("Description:", x, y, fieldW));
            y += S(18);
            _desc = new TextBox
            {
                Font = _fInput, Location = new Point(x, y),
                Width = fieldW, Height = S(48), Multiline = true,
                ScrollBars = ScrollBars.Vertical, Text = _ecb.Description ?? ""
            };
            Controls.Add(_desc);
            y += S(56);

            // ── State ─────────────────────────────────────────────────
            Controls.Add(Lbl("State:", x, y + S(4), S(70)));
            _state = new ComboBox
            {
                Font = _fInput, Location = new Point(x + S(74), y),
                Width = S(180), DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var st in EcbManager.States) _state.Items.Add(st);
            string curState = string.IsNullOrEmpty(_ecb.State) ? "Draft" : _ecb.State;
            int si = _state.Items.IndexOf(curState);
            _state.SelectedIndex = si >= 0 ? si : 0;
            Controls.Add(_state);
            y += S(32);

            // ── Affected items grid ───────────────────────────────────
            Controls.Add(Lbl("Affected items:", x, y, fieldW));
            y += S(18);
            _grid = new DataGridView
            {
                Location = new Point(x, y), Width = fieldW, Height = S(150),
                Font = _fCell, AllowUserToAddRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = cBrandDark;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            // Size the header + rows from the SCALED font (the header inherits the
            // grid's _fCell), not the WinForms default — so it never clips at DPI.
            _grid.ColumnHeadersHeight = _fCell.Height + S(10);
            _grid.RowTemplate.Height = _fCell.Height + S(8);

            var colFile = new DataGridViewTextBoxColumn
            { HeaderText = "File", ReadOnly = true, Width = S(180) };
            var colPn = new DataGridViewTextBoxColumn
            { HeaderText = "Part No", ReadOnly = true, Width = S(140) };
            var colFrom = new DataGridViewTextBoxColumn
            { HeaderText = "From Rev", Width = S(80) };
            var colTo = new DataGridViewTextBoxColumn
            { HeaderText = "To Rev", Width = S(80) };
            _grid.Columns.Add(colFile);
            _grid.Columns.Add(colPn);
            _grid.Columns.Add(colFrom);
            _grid.Columns.Add(colTo);
            _grid.CellEndEdit += Grid_CellEndEdit;
            Controls.Add(_grid);
            ReloadGrid();
            y += S(156);

            // ── Add / Remove item buttons ─────────────────────────────
            Button btnAdd = MakeBtn("Add Item…", cBrand, x, y, S(110));
            btnAdd.Click += (s, e) => AddItem();
            Controls.Add(btnAdd);

            Button btnRemove = MakeBtn("Remove Item", cMaroon,
                x + S(118), y, S(110));
            btnRemove.Click += (s, e) => RemoveItem();
            Controls.Add(btnRemove);

            // ── OK / Cancel ───────────────────────────────────────────
            int btnH = S(26);
            int btnW = S(110);
            int btnY = ClientSize.Height - S(8) - btnH;

            Button btnCancel = MakeBtn("Cancel", cDark,
                cW - S(8) - btnW, btnY, btnW);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            Button btnOk = MakeBtn(_isNew ? "Create ECB" : "Save", cGreen,
                cW - S(8) - btnW * 2 - S(6), btnY, btnW);
            btnOk.Click += (s, e) => Commit();
            Controls.Add(btnOk);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private Label Lbl(string text, int x, int y, int w) => new Label
        {
            Text = text, Font = _fLabel, ForeColor = cTextDark,
            Location = new Point(x, y), AutoSize = false,
            Width = w, Height = S(16)
        };

        private Button MakeBtn(string text, Color back, int x, int y, int w)
        {
            var b = new Button
            {
                Text = text, Font = _fBtn, Location = new Point(x, y),
                Width = w, Height = S(26), BackColor = back,
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // Pick the stored reason's leading code if it is one of our codes.
        private void PreselectReason(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return;
            foreach (var c in EcbManager.ReasonCodes)
            {
                if (stored.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                {
                    int i = _reason.Items.IndexOf(c);
                    if (i >= 0) _reason.SelectedIndex = i;
                    return;
                }
            }
        }

        // The detail portion of a stored "Code — detail" reason (the part after
        // the em-dash separator), or the whole string when no recognised code.
        private string ReasonDetailOf(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            foreach (var c in EcbManager.ReasonCodes)
            {
                if (stored.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = stored.Substring(c.Length).TrimStart();
                    if (rest.StartsWith("—")) rest = rest.Substring(1).Trim();
                    else if (rest.StartsWith("-")) rest = rest.Substring(1).Trim();
                    return rest;
                }
            }
            return stored; // unrecognised code (e.g. an "Other" free-form reason)
        }

        // Compose "Code — detail" (or the code alone / the detail alone).
        private string ComposeReason()
        {
            string code = _reason.SelectedIndex > 0
                ? (string)_reason.SelectedItem : "";
            string detail = (_reasonDetail.Text ?? "").Trim();
            if (string.IsNullOrEmpty(code)) return detail;          // free-form
            return detail.Length > 0 ? code + " — " + detail : code;
        }

        private void ReloadGrid()
        {
            _grid.Rows.Clear();
            foreach (var it in _ecb.Items)
            {
                int r = _grid.Rows.Add(
                    System.IO.Path.GetFileName(it.FilePath ?? ""),
                    it.PartNo ?? "", it.FromRev ?? "", it.ToRev ?? "");
                _grid.Rows[r].Tag = it;   // map row → item for edit/remove
            }
        }

        private void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
                var it = _grid.Rows[e.RowIndex].Tag as EcbAffectedItem;
                if (it == null) return;
                if (e.ColumnIndex == 2) // From Rev
                    it.FromRev = (_grid.Rows[e.RowIndex].Cells[2].Value
                        ?? "").ToString().Trim();
                else if (e.ColumnIndex == 3) // To Rev
                    it.ToRev = (_grid.Rows[e.RowIndex].Cells[3].Value
                        ?? "").ToString().Trim();
            }
            catch { }
        }

        private void AddItem()
        {
            try
            {
                using (var picker = new SupersededByPickerForm(null))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK ||
                        picker.Selected == null) return;
                    var f = picker.Selected;
                    // Skip a duplicate (same file already on the ECB).
                    if (_ecb.Items.Any(i => string.Equals(i.FilePath, f.FilePath,
                            StringComparison.OrdinalIgnoreCase)))
                        return;
                    _ecb.Items.Add(new EcbAffectedItem
                    {
                        FilePath = f.FilePath ?? "",
                        PartNo = f.PartNumber ?? "",
                        FromRev = f.Revision ?? "",
                        ToRev = ""
                    });
                    ReloadGrid();
                }
            }
            catch { }
        }

        private void RemoveItem()
        {
            try
            {
                if (_grid.CurrentRow == null) return;
                var it = _grid.CurrentRow.Tag as EcbAffectedItem;
                if (it == null) return;
                _ecb.Items.Remove(it);
                ReloadGrid();
            }
            catch { }
        }

        private void Commit()
        {
            string title = (_title.Text ?? "").Trim();
            if (title.Length == 0)
            {
                MessageBox.Show("Please enter a title for this ECB.",
                    "BCore PDM — ECB", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _ecb.Title = title;
            _ecb.Reason = ComposeReason();
            _ecb.Description = (_desc.Text ?? "").Trim();
            _ecb.State = _state.SelectedItem as string ?? "Draft";

            try
            {
                Result = _isNew ? EcbManager.CreateEcb(_ecb)
                                : (EcbManager.UpdateEcb(_ecb) ? _ecb : null);
            }
            catch { Result = null; }

            if (Result == null)
            {
                MessageBox.Show("Could not save the ECB (vault unavailable).",
                    "BCore PDM — ECB", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle?.Dispose(); _fLabel?.Dispose(); _fInput?.Dispose();
                _fHint?.Dispose();  _fBtn?.Dispose();   _fCell?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
