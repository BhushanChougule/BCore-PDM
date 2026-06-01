using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PDMLite
{
    [Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
    [ComVisible(true)]
    public class PDMLiteAddin : ISwAddin
    {
        public static ISldWorks SwApp { get; private set; }
        public static string CurrentUser => System.Environment.UserName;

        private int _addinId;
        private TaskPaneHost _taskPane;
        private readonly HashSet<string> _hookedDocs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool ConnectToSW(object thisSW, int cookie)
        {
            try
            {
                SwApp = (ISldWorks)thisSW;
                _addinId = cookie;
                DatabaseManager.Initialize();
                _taskPane = new TaskPaneHost();
                _taskPane.Register(SwApp);
                ((SldWorks)SwApp).ActiveDocChangeNotify += OnActiveDocChange;
                HookCurrentDoc();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"BCore PDM failed to load:\n\n{ex.Message}",
                    "BCore PDM Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            _taskPane?.Unregister(SwApp);
            ((SldWorks)SwApp).ActiveDocChangeNotify -= OnActiveDocChange;
            _hookedDocs.Clear();
            SwApp = null;
            return true;
        }

        private int OnActiveDocChange() { HookCurrentDoc(); return 0; }

        private void HookCurrentDoc()
        {
            ModelDoc2 doc = SwApp?.ActiveDoc as ModelDoc2;
            if (doc == null) return;

            string path = doc.GetPathName();
            string identifier = string.IsNullOrEmpty(path) ? "NEW:" + doc.GetTitle() : path;
            if (string.IsNullOrEmpty(identifier) || _hookedDocs.Contains(identifier)) return;

            int t = doc.GetType();

            // Capture doc and identifier in closures so that event handlers
            // never rely on SwApp.ActiveDoc. When the Custom Properties task
            // pane has focus, ActiveDoc returns null — using it inside a save
            // handler causes a fail-open (save allowed without validation).
            // Capturing the specific document avoids this entirely.
            ModelDoc2 capturedDoc = doc;
            string capturedId    = identifier;

            // NOTE: For a document that has never been saved, SOLIDWORKS fires
            // FileSaveAsNotify2 (NOT the legacy FileSaveAsNotify). Returning a
            // non-zero value from FileSaveAsNotify2 actually aborts the save —
            // the legacy FileSaveAsNotify ignores the return value.
            if (t == (int)swDocumentTypes_e.swDocPART)
            {
                DPartDocEvents_Event d = (DPartDocEvents_Event)doc;
                d.FileSaveNotify     += fn       => ValidateSave(capturedDoc, fn);
                d.FileSaveAsNotify2  += fn       => ValidateSave(capturedDoc, fn);
                d.FileSavePostNotify += (st, fn) => OnSavePost(capturedDoc, fn);
                d.DestroyNotify      += ()       => { _hookedDocs.Remove(capturedId); return 0; };
            }
            else if (t == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                DAssemblyDocEvents_Event d = (DAssemblyDocEvents_Event)doc;
                d.FileSaveNotify     += fn       => ValidateSave(capturedDoc, fn);
                d.FileSaveAsNotify2  += fn       => ValidateSave(capturedDoc, fn);
                d.FileSavePostNotify += (st, fn) => OnSavePost(capturedDoc, fn);
                d.DestroyNotify      += ()       => { _hookedDocs.Remove(capturedId); return 0; };
            }
            else if (t == (int)swDocumentTypes_e.swDocDRAWING)
            {
                DDrawingDocEvents_Event d = (DDrawingDocEvents_Event)doc;
                d.FileSaveNotify     += fn       => ValidateSave(capturedDoc, fn);
                d.FileSaveAsNotify2  += fn       => ValidateSave(capturedDoc, fn);
                d.FileSavePostNotify += (st, fn) => OnSavePost(capturedDoc, fn);
                d.DestroyNotify      += ()       => { _hookedDocs.Remove(capturedId); return 0; };
            }

            // ── Show status notification when file opens ──────────────────
            string fileStatus = DatabaseManager.GetFileStatus(path);
            var lockInfo = DatabaseManager.GetLockInfo(path);
            string userRole = DatabaseManager.GetUserRole(CurrentUser);

            if (lockInfo.IsLocked &&
                !lockInfo.LockedBy.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase))
            {
                SwApp.SendMsgToUser2(
                    "🔒  FILE LOCKED — BCore PDM\n\n" +
                    "Locked by : " + lockInfo.LockedBy + "\n" +
                    "Locked on : " + lockInfo.LockedDate.ToString("dd/MM/yyyy HH:mm") + "\n\n" +
                    "You can view and reference this file\nbut cannot save any changes.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            else if (fileStatus == "Released" && userRole != "Master")
            {
                SwApp.SendMsgToUser2(
                    "✅  FILE RELEASED — BCore PDM\n\n" +
                    "This file has been officially released.\n\n" +
                    "You can view and use it in assemblies\nbut cannot save changes to it.\n\n" +
                    "Contact " + lockInfo.LockedBy + " to start a new revision.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }

            _hookedDocs.Add(identifier);
        }

        // ── Fires BEFORE the file is saved (both regular save and first-time save) ──
        // Return 1 = CANCEL the save
        // Return 0 = ALLOW the save
        private int ValidateSave(ModelDoc2 doc, string fileName)
        {
            // doc is the specific document captured at hook time — never null here
            // unless SOLIDWORKS recycled the COM object, which we treat as fail-closed.
            if (doc == null)
            {
                Block("Save blocked: could not identify the document.\nPlease try again.");
                return 1;
            }

            string filePath = doc.GetPathName();
            if (string.IsNullOrEmpty(filePath)) filePath = fileName;
            string userRole = DatabaseManager.GetUserRole(CurrentUser);

            // Rule 1: Is file locked by someone else?
            var lockInfo = DatabaseManager.GetLockInfo(filePath);
            if (lockInfo.IsLocked &&
                !lockInfo.LockedBy.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase))
            {
                Block("File is locked by: " + lockInfo.LockedBy + "\n" +
                      "Locked: " + lockInfo.LockedDate.ToString("yyyy-MM-dd HH:mm") + "\n\n" +
                      "Contact your Master user to unlock it.");
                return 1;
            }

            // Rule 2: Released files are read-only
            string status = DatabaseManager.GetFileStatus(filePath);
            if (status == "Released" && userRole != "Master")
            {
                Block("This file is Released and locked.\n" +
                      "Only the Master user can modify released files.\n" +
                      "Create a new revision instead.");
                return 1;
            }

            int docType = doc.GetType();
            bool isPart = docType == (int)swDocumentTypes_e.swDocPART;
            bool isAsm  = docType == (int)swDocumentTypes_e.swDocASSEMBLY;

            if (isPart || isAsm)
            {
                // Rule 3: All required properties must be filled.
                // Fail-closed: if validation itself errors, block the save.
                ValidationResult validation;
                try
                {
                    validation = PropertyValidator.Validate(doc);
                }
                catch (Exception ex)
                {
                    Block("Could not verify required properties:\n\n" +
                          ex.Message + "\n\n" +
                          "Save blocked to protect the vault. Try again.");
                    return 1;
                }

                if (!validation.IsValid)
                {
                    var form = new PropertyForm(doc, validation.EmptyFields);
                    form.ShowDialog();

                    if (!form.PropertiesSaved)
                    {
                        Block("Required properties incomplete:\n\n" +
                              "• " + string.Join("\n• ", validation.EmptyFields) + "\n\n" +
                              "Fill all fields and try again.");
                        return 1;
                    }

                    // Re-validate after form closes — confirm fields were actually saved.
                    var recheck = PropertyValidator.Validate(doc);
                    if (!recheck.IsValid)
                    {
                        Block("Required properties still incomplete:\n\n" +
                              "• " + string.Join("\n• ", recheck.EmptyFields) + "\n\n" +
                              "Fill all fields and try again.");
                        return 1;
                    }
                }

                // Auto-convert date formats if in old yyyy-MM-dd format
                PropertyValidator.FixDateFormats(doc);

                // Rule 4: Auto-fill PartWeight from mass properties
                PropertyValidator.AutoFillWeight(doc);

                // Rule 5: Check for broken references
                var broken = ReferenceChecker.GetBrokenReferences(doc);
                if (broken.Count > 0)
                {
                    int choice = SwApp.SendMsgToUser2(
                        "BROKEN REFERENCES:\n\n• " +
                        string.Join("\n• ", broken) + "\n\n" +
                        "Cannot release with broken refs.\nSave as WIP anyway?",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbYesNo);

                    if (choice == (int)swMessageBoxResult_e.swMbHitNo) return 1;

                    DatabaseManager.SetBrokenRefFlag(filePath, true);
                }
                else
                {
                    DatabaseManager.SetBrokenRefFlag(filePath, false);
                }
            }

            return 0;
        }

        // ── Fires AFTER the file is saved — update database ──────────────────
        private int OnSavePost(ModelDoc2 doc, string fileName)
        {
            if (doc == null) return 0;

            string filePath = doc.GetPathName();
            if (string.IsNullOrEmpty(filePath)) filePath = fileName;
            string currentStatus = DatabaseManager.GetFileStatus(filePath);

            _taskPane?.RefreshPanel();
            DatabaseManager.UpsertFile(new VaultFile
            {
                FilePath   = filePath,
                FileName   = System.IO.Path.GetFileName(filePath),
                PartNumber = PropertyValidator.GetProperty(doc, "PartNo"),
                Description = PropertyValidator.GetProperty(doc, "Description"),
                Status     = string.IsNullOrEmpty(currentStatus) ? "WIP" : currentStatus,
                ModifiedBy = CurrentUser,
                ModifiedDate = DateTime.Now
            });

            return 0;
        }

        private void Block(string reason) =>
            SwApp.SendMsgToUser2(
                "SAVE BLOCKED — BCore PDM\n\n" + reason,
                (int)swMessageBoxIcon_e.swMbStop,
                (int)swMessageBoxBtn_e.swMbOk);
    }
}
