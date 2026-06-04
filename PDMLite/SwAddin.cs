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

        // Set by VaultManager around its own internal Save3 calls (release,
        // new revision) so those programmatic saves are not blocked by the
        // "Released files cannot be edited" rule in ValidateSave.
        internal static bool SuppressSaveValidation = false;

        private int _addinId;
        private TaskPaneHost _taskPane;

        // Strong references to per-document event handlers.
        // This dictionary MUST be kept alive for the lifetime of the add-in.
        // Without it, the .NET GC collects the event sinks and document events
        // (FileSaveNotify, FileSaveAsNotify2, etc.) silently stop firing.
        private readonly Dictionary<string, DocEventHandler> _docHandlers =
            new Dictionary<string, DocEventHandler>(StringComparer.OrdinalIgnoreCase);

        public bool ConnectToSW(object thisSW, int cookie)
        {
            try
            {
                SwApp = (ISldWorks)thisSW;
                _addinId = cookie;
                DatabaseManager.Initialize();
                EmailManager.EnsureConfigTemplate();
                _taskPane = new TaskPaneHost();
                _taskPane.Register(SwApp);
                ((SldWorks)SwApp).ActiveDocChangeNotify += OnActiveDocChange;
                HookAllOpenDocs();
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
            foreach (var h in _docHandlers.Values)
                try { h.Detach(); } catch { }
            _docHandlers.Clear();
            SwApp = null;
            return true;
        }

        // On every active-doc change, re-scan ALL open documents.
        // This catches new files and any document that lost its handler
        // (e.g. due to a spurious DestroyNotify from the Custom Properties tab).
        private int OnActiveDocChange() { HookAllOpenDocs(); return 0; }

        internal void HookAllOpenDocs()
        {
            try
            {
                object[] docs = (object[])SwApp?.GetDocuments();
                if (docs == null) return;
                foreach (object d in docs)
                    TryHookDoc(d as ModelDoc2);
            }
            catch { }
        }

        private void TryHookDoc(ModelDoc2 doc)
        {
            if (doc == null) return;
            try
            {
                string path = doc.GetPathName();
                string id = string.IsNullOrEmpty(path) ? "NEW:" + doc.GetTitle() : path;
                if (string.IsNullOrEmpty(id) || _docHandlers.ContainsKey(id)) return;

                var handler = new DocEventHandler(this, doc, id);
                if (!handler.Attach()) return;
                _docHandlers[id] = handler;

                // ── Status notification when a file is opened ─────────────
                string fileStatus = DatabaseManager.GetFileStatus(path);
                var lockInfo     = DatabaseManager.GetLockInfo(path);
                string userRole  = DatabaseManager.GetUserRole(CurrentUser);

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
            catch { }
        }

        // Called from DocEventHandler.OnDestroy. Removes stale handler then
        // immediately re-scans open docs so any still-open file gets re-hooked.
        internal void OnDocDestroyed(string id)
        {
            _docHandlers.Remove(id);
            // Re-hook: DestroyNotify sometimes fires spuriously (e.g. when the
            // Custom Properties task pane opens). Re-scanning immediately ensures
            // the document gets re-registered if it is still open.
            HookAllOpenDocs();
            // ActiveDocChangeNotify does not fire when the last document closes.
            // We cannot use RefreshPanel() here because SwApp.ActiveDoc still
            // points to the closing document at the time DestroyNotify fires.
            // Instead, check our own handler count: if zero, no docs are truly
            // open and we explicitly pass null; otherwise refresh normally.
            if (_docHandlers.Count == 0)
                _taskPane?.ClearPanel();
            else
                _taskPane?.RefreshPanel();
        }

        // ── Pre-save validation ───────────────────────────────────────────────
        // Return 1 = CANCEL the save ; Return 0 = ALLOW the save
        // The entire body is wrapped in try/catch. Any unhandled exception from
        // a COM event handler is silently swallowed by the COM layer, which then
        // uses the default return value (0 = allow). Explicitly returning 1 on
        // any exception keeps the save blocked instead of silently allowed.
        internal int ValidateSave(ModelDoc2 doc, string fileName)
        {
            try
            {
                // Internal saves performed by VaultManager (release / new
                // revision) bypass validation — they manage status themselves.
                if (SuppressSaveValidation) return 0;

                if (doc == null)
                {
                    Block("Save blocked: document reference is invalid. Try again.");
                    return 1;
                }

                string filePath = doc.GetPathName();
                if (string.IsNullOrEmpty(filePath)) filePath = fileName;
                string userRole = DatabaseManager.GetUserRole(CurrentUser);

                // Rule 1: locked by someone else?
                var lockInfo = DatabaseManager.GetLockInfo(filePath);
                if (lockInfo.IsLocked &&
                    !lockInfo.LockedBy.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase))
                {
                    Block("File is locked by: " + lockInfo.LockedBy + "\n" +
                          "Locked: " + lockInfo.LockedDate.ToString("yyyy-MM-dd HH:mm") + "\n\n" +
                          "Contact your Master user to unlock it.");
                    return 1;
                }

                // Rule 2: released file — locked for EVERYONE, including Masters.
                // A released file must not be edited in place; it has to be
                // explicitly returned to WIP first.
                string status = DatabaseManager.GetFileStatus(filePath);
                if (status == "Released")
                {
                    string howTo = userRole == "Master"
                        ? "To make changes, use the BCore PDM task pane:\n" +
                          "  • \"New Revision\" — bumps the revision and returns it to WIP, or\n" +
                          "  • \"Unlock File\" — returns it to WIP at the same revision."
                        : "To make changes, request a new revision or unlock\n" +
                          "from a Master via the BCore PDM task pane.";

                    Block("This file is RELEASED and locked.\n\n" +
                          "Released files cannot be edited directly.\n\n" +
                          howTo + "\n\nSave blocked.");
                    return 1;
                }

                // Rule 2.5: vault files must live under a WIP division subfolder.
                // Every file has one canonical home in N:\PDM-SolidWorks\WIP\<Division>.
                // Saving elsewhere (Desktop, local drive) breaks SOLIDWORKS references
                // and the released-snapshot model.
                // Warn but allow override so nobody is ever hard-trapped.
                const string WipRootPath = @"N:\PDM-SolidWorks\WIP";
                if (!string.IsNullOrEmpty(filePath))
                {
                    bool underWip;
                    try
                    {
                        underWip = System.IO.Path.GetFullPath(filePath)
                            .StartsWith(System.IO.Path.GetFullPath(WipRootPath),
                                        StringComparison.OrdinalIgnoreCase);
                    }
                    catch { underWip = true; } // never block on a path parse error

                    if (!underWip)
                    {
                        string divList = string.Join("\n",
                            System.Array.ConvertAll(DatabaseManager.WipDivisions,
                                d => "  " + WipRootPath + @"\" + d));
                        int choice = SwApp.SendMsgToUser2(
                            "FILE OUTSIDE THE VAULT:\n\n" +
                            "This file is being saved to:\n" + filePath + "\n\n" +
                            "All vault files must be saved under a division folder:\n" +
                            divList + "\n\n" +
                            "Saving outside the vault breaks references and the file " +
                            "will not be tracked correctly.\nSave here anyway?",
                            (int)swMessageBoxIcon_e.swMbWarning,
                            (int)swMessageBoxBtn_e.swMbYesNo);
                        if (choice == (int)swMessageBoxResult_e.swMbHitNo) return 1;
                    }
                }

                int docType = doc.GetType();
                bool isPart = docType == (int)swDocumentTypes_e.swDocPART;
                bool isAsm  = docType == (int)swDocumentTypes_e.swDocASSEMBLY;

                if (isPart || isAsm)
                {
                    // Rule 3: required properties
                    ValidationResult validation;
                    try   { validation = PropertyValidator.Validate(doc); }
                    catch (Exception ex)
                    {
                        Block("Could not verify required properties:\n\n" +
                              ex.Message + "\n\nSave blocked. Try again.");
                        return 1;
                    }

                    if (!validation.IsValid)
                    {
                        using (var form = new PropertyForm(doc, validation.EmptyFields))
                        {
                            form.ShowDialog();
                            if (!form.PropertiesSaved)
                            {
                                Block("Required properties incomplete:\n\n" +
                                      "• " + string.Join("\n• ", validation.EmptyFields) + "\n\n" +
                                      "Fill all fields and try again.");
                                return 1;
                            }
                        }
                        // Re-validate after form closes
                        var recheck = PropertyValidator.Validate(doc);
                        if (!recheck.IsValid)
                        {
                            Block("Required properties still incomplete:\n\n" +
                                  "• " + string.Join("\n• ", recheck.EmptyFields) + "\n\n" +
                                  "Fill all fields and try again.");
                            return 1;
                        }
                    }

                    PropertyValidator.FixDateFormats(doc);
                    PropertyValidator.AutoFillWeight(doc);

                    // Rule 4: duplicate part number (another file already uses it)
                    string partNo = PropertyValidator.GetProperty(doc, "PartNo");
                    string conflict = DatabaseManager.FindPartNumberConflict(partNo, filePath);
                    if (conflict != null)
                    {
                        int choice = SwApp.SendMsgToUser2(
                            "DUPLICATE PART NUMBER:\n\n" +
                            "Part No '" + partNo + "' is already used by:\n• " + conflict + "\n\n" +
                            "Two files should not share the same part number.\n" +
                            "Save anyway?",
                            (int)swMessageBoxIcon_e.swMbWarning,
                            (int)swMessageBoxBtn_e.swMbYesNo);
                        if (choice == (int)swMessageBoxResult_e.swMbHitNo) return 1;
                    }

                    // Rule 5: broken references
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
            catch (Exception ex)
            {
                // Fail-closed: block the save if anything unexpected goes wrong.
                Block("Unexpected error during save validation:\n\n" +
                      ex.Message + "\n\nSave blocked to protect the vault. Try again.");
                return 1;
            }
        }

        internal int OnSavePost(ModelDoc2 doc, string fileName)
        {
            try
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
            }
            catch { }
            return 0;
        }

        private void Block(string reason) =>
            SwApp.SendMsgToUser2(
                "SAVE BLOCKED — BCore PDM\n\n" + reason,
                (int)swMessageBoxIcon_e.swMbStop,
                (int)swMessageBoxBtn_e.swMbOk);

        // ─────────────────────────────────────────────────────────────────────
        // Per-document event handler.
        //
        // CRITICAL: Delegate instances must be stored as typed fields.
        // Writing `part.FileSaveNotify += OnSave` creates a temporary delegate
        // object that has no other strong .NET reference, so it can be GC'd at
        // any time. Storing them as fields on this class (which itself lives in
        // _docHandlers on the add-in) keeps them alive for the document's life.
        // ─────────────────────────────────────────────────────────────────────
        private class DocEventHandler
        {
            private readonly PDMLiteAddin _addin;
            private readonly ModelDoc2    _doc;
            private readonly string       _id;
            private readonly int          _type;

            // Part delegates
            private DPartDocEvents_FileSaveNotifyEventHandler    _partSave;
            private DPartDocEvents_FileSaveAsNotify2EventHandler _partSaveAs;
            private DPartDocEvents_FileSavePostNotifyEventHandler _partPost;
            private DPartDocEvents_DestroyNotifyEventHandler     _partDestroy;

            // Assembly delegates
            private DAssemblyDocEvents_FileSaveNotifyEventHandler    _asmSave;
            private DAssemblyDocEvents_FileSaveAsNotify2EventHandler _asmSaveAs;
            private DAssemblyDocEvents_FileSavePostNotifyEventHandler _asmPost;
            private DAssemblyDocEvents_DestroyNotifyEventHandler     _asmDestroy;

            // Drawing delegates
            private DDrawingDocEvents_FileSaveNotifyEventHandler    _drwSave;
            private DDrawingDocEvents_FileSaveAsNotify2EventHandler _drwSaveAs;
            private DDrawingDocEvents_FileSavePostNotifyEventHandler _drwPost;
            private DDrawingDocEvents_DestroyNotifyEventHandler     _drwDestroy;

            public DocEventHandler(PDMLiteAddin addin, ModelDoc2 doc, string id)
            {
                _addin = addin;
                _doc   = doc;
                _id    = id;
                _type  = doc.GetType();
            }

            public bool Attach()
            {
                // NOTE: FileSaveAsNotify2 is fired for never-saved docs (first
                // save / Save As). FileSaveNotify fires for re-saves. Only
                // FileSaveAsNotify2 honours the nonzero return to abort the save;
                // the legacy FileSaveAsNotify ignores it.
                if (_type == (int)swDocumentTypes_e.swDocPART)
                {
                    var d = (DPartDocEvents_Event)_doc;
                    _partSave    = new DPartDocEvents_FileSaveNotifyEventHandler(OnSave);
                    _partSaveAs  = new DPartDocEvents_FileSaveAsNotify2EventHandler(OnSave);
                    _partPost    = new DPartDocEvents_FileSavePostNotifyEventHandler(OnPost);
                    _partDestroy = new DPartDocEvents_DestroyNotifyEventHandler(OnDestroy);
                    d.FileSaveNotify    += _partSave;
                    d.FileSaveAsNotify2 += _partSaveAs;
                    d.FileSavePostNotify += _partPost;
                    d.DestroyNotify     += _partDestroy;
                    return true;
                }
                if (_type == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    var d = (DAssemblyDocEvents_Event)_doc;
                    _asmSave    = new DAssemblyDocEvents_FileSaveNotifyEventHandler(OnSave);
                    _asmSaveAs  = new DAssemblyDocEvents_FileSaveAsNotify2EventHandler(OnSave);
                    _asmPost    = new DAssemblyDocEvents_FileSavePostNotifyEventHandler(OnPost);
                    _asmDestroy = new DAssemblyDocEvents_DestroyNotifyEventHandler(OnDestroy);
                    d.FileSaveNotify    += _asmSave;
                    d.FileSaveAsNotify2 += _asmSaveAs;
                    d.FileSavePostNotify += _asmPost;
                    d.DestroyNotify     += _asmDestroy;
                    return true;
                }
                if (_type == (int)swDocumentTypes_e.swDocDRAWING)
                {
                    var d = (DDrawingDocEvents_Event)_doc;
                    _drwSave    = new DDrawingDocEvents_FileSaveNotifyEventHandler(OnSave);
                    _drwSaveAs  = new DDrawingDocEvents_FileSaveAsNotify2EventHandler(OnSave);
                    _drwPost    = new DDrawingDocEvents_FileSavePostNotifyEventHandler(OnPost);
                    _drwDestroy = new DDrawingDocEvents_DestroyNotifyEventHandler(OnDestroy);
                    d.FileSaveNotify    += _drwSave;
                    d.FileSaveAsNotify2 += _drwSaveAs;
                    d.FileSavePostNotify += _drwPost;
                    d.DestroyNotify     += _drwDestroy;
                    return true;
                }
                return false;
            }

            public void Detach()
            {
                try
                {
                    if (_type == (int)swDocumentTypes_e.swDocPART && _partSave != null)
                    {
                        var d = (DPartDocEvents_Event)_doc;
                        d.FileSaveNotify    -= _partSave;
                        d.FileSaveAsNotify2 -= _partSaveAs;
                        d.FileSavePostNotify -= _partPost;
                        d.DestroyNotify     -= _partDestroy;
                    }
                    else if (_type == (int)swDocumentTypes_e.swDocASSEMBLY && _asmSave != null)
                    {
                        var d = (DAssemblyDocEvents_Event)_doc;
                        d.FileSaveNotify    -= _asmSave;
                        d.FileSaveAsNotify2 -= _asmSaveAs;
                        d.FileSavePostNotify -= _asmPost;
                        d.DestroyNotify     -= _asmDestroy;
                    }
                    else if (_type == (int)swDocumentTypes_e.swDocDRAWING && _drwSave != null)
                    {
                        var d = (DDrawingDocEvents_Event)_doc;
                        d.FileSaveNotify    -= _drwSave;
                        d.FileSaveAsNotify2 -= _drwSaveAs;
                        d.FileSavePostNotify -= _drwPost;
                        d.DestroyNotify     -= _drwDestroy;
                    }
                }
                catch { }
                _partSave = null; _partSaveAs = null; _partPost = null; _partDestroy = null;
                _asmSave  = null; _asmSaveAs  = null; _asmPost  = null; _asmDestroy  = null;
                _drwSave  = null; _drwSaveAs  = null; _drwPost  = null; _drwDestroy  = null;
            }

            private int OnSave(string fileName) => _addin.ValidateSave(_doc, fileName);
            private int OnPost(int t, string fn) => _addin.OnSavePost(_doc, fn);

            private int OnDestroy()
            {
                Detach();
                _addin.OnDocDestroyed(_id);
                return 0;
            }
        }
    }
}
