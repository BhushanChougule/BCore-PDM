using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;

namespace PDMLite
{
    public class TaskPaneHost
    {
        private TaskpaneView _taskPaneView;
        private TaskPaneControl _taskPaneControl;

        // ── Create and register the Task Pane in SOLIDWORKS ──────────
        public void Register(ISldWorks swApp)
        {
            try
            {
                // Create the WinForms control
                _taskPaneControl = new TaskPaneControl();

                // Add it to SOLIDWORKS Task Pane
                // The icon path is optional — we pass empty string for default icon
                _taskPaneView = swApp.CreateTaskpaneView2(CreateIcon(), "BCore PDM");

                // Display our control inside the Task Pane
                _taskPaneView.DisplayWindowFromHandle(
                    _taskPaneControl.Handle.ToInt32());

                // Hook active document change to refresh the panel
                ((SldWorks)swApp).ActiveDocChangeNotify += OnActiveDocChange;

                // Refresh immediately on load for any already-open file
                ModelDoc2 openDoc = swApp.ActiveDoc as ModelDoc2;
                if (openDoc != null) _taskPaneControl.Refresh(openDoc);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Task Pane failed to load:\n" + ex.Message,
                    "BCore PDM", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }

        // ── Remove Task Pane when add-in unloads ─────────────────────
        public void Unregister(ISldWorks swApp)
        {
            try
            {
                ((SldWorks)swApp).ActiveDocChangeNotify -= OnActiveDocChange;
                _taskPaneView?.DeleteView();
            }
            catch { }
        }

        // ── Refresh panel every time user switches active document ────
        private int OnActiveDocChange()
        {
            try
            {
                ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                _taskPaneControl?.Refresh(doc);
            }
            catch { }
            return 0;
        }

        // ── Public method so SwAddin can trigger refresh manually ─────
        public void RefreshPanel()
        {
            try
            {
                ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                _taskPaneControl?.Refresh(doc);
            }
            catch { }
        }

        // ── Deferred refresh ──────────────────────────────────────────
        // Used from DestroyNotify. At the moment a document is closing,
        // SwApp.ActiveDoc still points to the closing doc and GetDocuments()
        // still lists it, so a synchronous refresh reads stale state. Posting
        // the refresh to the UI thread runs it AFTER SOLIDWORKS finishes the
        // close, by which point ActiveDoc is null (no files) or the correct
        // remaining document. This naturally handles the spurious-destroy case
        // too: if the doc is still open, ActiveDoc returns it.
        public void RefreshPanelDeferred()
        {
            try
            {
                if (_taskPaneControl == null || !_taskPaneControl.IsHandleCreated)
                    return;
                _taskPaneControl.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        ModelDoc2 doc = PDMLiteAddin.SwApp?.ActiveDoc as ModelDoc2;
                        _taskPaneControl.Refresh(doc);
                    }
                    catch { }
                }));
            }
            catch { }
        }
        private string CreateIcon()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "BCorePDM_icon.bmp");

                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(40, 40))
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    // Background — brand blue
                    g.Clear(System.Drawing.Color.FromArgb(44, 85, 128));

                    // White rounded rectangle feel
                    g.FillRectangle(
                        new System.Drawing.SolidBrush(
                            System.Drawing.Color.FromArgb(255, 255, 255)),
                        4, 4, 32, 32);

                    // Draw "BC" in brand color
                    using (System.Drawing.Font f = new System.Drawing.Font(
                        "Segoe UI", 11f, System.Drawing.FontStyle.Bold))
                    using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(
                        System.Drawing.Color.FromArgb(44, 85, 128)))
                    {
                        System.Drawing.StringFormat sf = new System.Drawing.StringFormat
                        {
                            Alignment = System.Drawing.StringAlignment.Center,
                            LineAlignment = System.Drawing.StringAlignment.Center
                        };
                        g.DrawString("BC", f, brush,
                            new System.Drawing.RectangleF(4, 4, 32, 32), sf);
                    }

                    bmp.Save(iconPath,
                        System.Drawing.Imaging.ImageFormat.Bmp);
                }

                return iconPath;
            }
            catch
            {
                return ""; // Falls back to default icon
            }
        }
    }
}