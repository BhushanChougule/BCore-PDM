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

        // Keeps a strong reference to each document's event handler for the
        // lifetime of the document. This is REQUIRED — without it the .NET
        // garbage collector reclaims the COM event sink and document events
        // (FileSaveNotify etc.) silently stop firing. Interacting with UI such
        // as the Custom Properties task pane triggers a GC, which is why saves
        // stopped being validated after touching that tab.
        private readonly Dictionary<string, DocEventHandler> _docHandlers =
            new Dictionary<string, DocEventHandler>(StringComparer.OrdinalIgnoreCase);

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
            foreach (var h in _docHandlers.Values) h.Detach();
            _docHandlers.Clear();
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
            if (string.IsNullOrEmpty(identifier) || _docHandlers.ContainsKey(identifier)) return;

            // Attach and STORE the handler so it is not garbage collected.
            var handler = new DocEventHandler(this, doc, identifier);
            if (!handler.Attach()) return;     // unsupported doc type
            _docHandlers[identifier] = handler;

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
        }

        internal void RemoveHandler(string identifier) => _docHandlers.Remove(identifier);

        // ── Fires BEFORE the file is saved (regular save AND first-time save) ──
        // Return 1 = CANCEL the save ; Return 0 = ALLOW the save
        internal int ValidateSave(ModelDoc2 doc, string fileName)
        {
            // Fail-closed: never allow a save we cannot fully validate.
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
        internal int OnSavePost(ModelDoc2 doc, string fileName)
        {
            if (doc == null) return 0;

            string filePath = doc.GetPathName();
            if (string.IsNullOrEmpty(filePath)) filePath = fileName;
            string currentStatus = DatabaseManager.GetFileStatus(filePath);

            _taskPane?.RefreshPanel();
            DatabaseManager.UpsertFile(new VaultFile
            {
                FilePath    = filePath,
                FileName    = System.IO.Path.GetFileName(filePath),
                PartNumber  = PropertyValidator.GetProperty(doc, "PartNo"),
                Description = PropertyValidator.GetProperty(doc, "Description"),
                Status      = string.IsNullOrEmpty(currentStatus) ? "WIP" : currentStatus,
                ModifiedBy  = CurrentUser,
                ModifiedDate = DateTime.Now
            });

            return 0;
        }

        private void Block(string reason) =>
            SwApp.SendMsgToUser2(
                "SAVE BLOCKED — BCore PDM\n\n" + reason,
                (int)swMessageBoxIcon_e.swMbStop,
                (int)swMessageBoxBtn_e.swMbOk);

        // ─────────────────────────────────────────────────────────────────────
        // Per-document event handler. Held alive by _docHandlers so the COM
        // event sink is never garbage collected (which would stop events firing).
        // ─────────────────────────────────────────────────────────────────────
        private class DocEventHandler
        {
            private readonly PDMLiteAddin _addin;
            private readonly ModelDoc2 _doc;
            private readonly string _id;
            private readonly int _type;

            private DPartDocEvents_Event _part;
            private DAssemblyDocEvents_Event _asm;
            private DDrawingDocEvents_Event _drw;

            public DocEventHandler(PDMLiteAddin addin, ModelDoc2 doc, string id)
            {
                _addin = addin;
                _doc   = doc;
                _id    = id;
                _type  = doc.GetType();
            }

            public bool Attach()
            {
                // NOTE: never-saved docs fire FileSaveAsNotify2 (NOT the legacy
                // FileSaveAsNotify, whose return value is ignored). FileSaveNotify
                // covers re-saves of already-saved docs.
                if (_type == (int)swDocumentTypes_e.swDocPART)
                {
                    _part = (DPartDocEvents_Event)_doc;
                    _part.FileSaveNotify     += OnSave;
                    _part.FileSaveAsNotify2  += OnSave;
                    _part.FileSavePostNotify += OnSavePost;
                    _part.DestroyNotify      += OnDestroy;
                    return true;
                }
                if (_type == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    _asm = (DAssemblyDocEvents_Event)_doc;
                    _asm.FileSaveNotify     += OnSave;
                    _asm.FileSaveAsNotify2  += OnSave;
                    _asm.FileSavePostNotify += OnSavePost;
                    _asm.DestroyNotify      += OnDestroy;
                    return true;
                }
                if (_type == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    _drw = (DDrawingDocEvents_Event)_doc;
                    _drw.FileSaveNotify     += OnSave;
                    _drw.FileSaveAsNotify2  += OnSave;
                    _drw.FileSavePostNotify += OnSavePost;
                    _drw.DestroyNotify      += OnDestroy;
                    return true;
                }
                return false;
            }

            public void Detach()
            {
                if (_part != null)
                {
                    _part.FileSaveNotify     -= OnSave;
                    _part.FileSaveAsNotify2  -= OnSave;
                    _part.FileSavePostNotify -= OnSavePost;
                    _part.DestroyNotify      -= OnDestroy;
                    _part = null;
                }
                if (_asm != null)
                {
                    _asm.FileSaveNotify     -= OnSave;
                    _asm.FileSaveAsNotify2  -= OnSave;
                    _asm.FileSavePostNotify -= OnSavePost;
                    _asm.DestroyNotify      -= OnDestroy;
                    _asm = null;
                }
                if (_drw != null)
                {
                    _drw.FileSaveNotify     -= OnSave;
                    _drw.FileSaveAsNotify2  -= OnSave;
                    _drw.FileSavePostNotify -= OnSavePost;
                    _drw.DestroyNotify      -= OnDestroy;
                    _drw = null;
                }
            }

            private int OnSave(string fileName) => _addin.ValidateSave(_doc, fileName);
            private int OnSavePost(int saveType, string fileName) => _addin.OnSavePost(_doc, fileName);

            private int OnDestroy()
            {
                Detach();
                _addin.RemoveHandler(_id);
                return 0;
            }
        }
    }
}
