using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;
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
                // Clear any open-session entries this PC left behind after a
                // crash/forced-close so they never falsely warn other engineers.
                DatabaseManager.ClearMachineSessions(System.Environment.MachineName);
                EmailManager.EnsureConfigTemplate();
                _taskPane = new TaskPaneHost();
                _taskPane.Register(SwApp);
                ((SldWorks)SwApp).ActiveDocChangeNotify += OnActiveDocChange;
                // ActiveDocChangeNotify does NOT fire when the FIRST document
                // is created/opened into an empty session (no previous active
                // doc to change from), so a brand-new doc could miss hooking
                // entirely — its first save then bypassed EVERY save rule (no
                // validation, no PropertyForm, no DB record). Hook on the
                // new/open events too; TryHookDoc is idempotent so the
                // overlap with ActiveDocChangeNotify is harmless.
                ((SldWorks)SwApp).FileNewNotify2 += OnFileNew;
                ((SldWorks)SwApp).FileOpenPostNotify += OnFileOpenPost;
                HookAllOpenDocs();
                UpdateActivePresence();
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
            try { DatabaseManager.ClearMachineSessions(System.Environment.MachineName); } catch { }
            ((SldWorks)SwApp).ActiveDocChangeNotify -= OnActiveDocChange;
            ((SldWorks)SwApp).FileNewNotify2 -= OnFileNew;
            ((SldWorks)SwApp).FileOpenPostNotify -= OnFileOpenPost;
            foreach (var h in _docHandlers.Values)
                try { h.Detach(); } catch { }
            _docHandlers.Clear();
            SwApp = null;
            return true;
        }

        // On every active-doc change, re-scan ALL open documents.
        // This catches new files and any document that lost its handler
        // (e.g. due to a spurious DestroyNotify from the Custom Properties tab).
        private int OnActiveDocChange() { HookAllOpenDocs(); UpdateActivePresence(); return 0; }

        // File→New / File→Open into an EMPTY session never fires
        // ActiveDocChangeNotify — these cover that gap (see ConnectToSW).
        // OnFileNew hooks the doc object it is HANDED first: if the new doc
        // is not yet enumerated by GetDocuments at notify time, the rescan
        // alone would miss it — the exact bypass these hooks exist to close.
        private int OnFileNew(object newDoc, int docType, string templateName)
        { TryHookDoc(newDoc as ModelDoc2); HookAllOpenDocs(); return 0; }

        private int OnFileOpenPost(string fileName)
        { HookAllOpenDocs(); UpdateActivePresence(); return 0; }

        internal void HookAllOpenDocs()
        {
            try
            {
                RekeyNewDocHandlers();
                object[] docs = (object[])SwApp?.GetDocuments();
                if (docs == null) return;
                foreach (object d in docs)
                    TryHookDoc(d as ModelDoc2);
            }
            catch { }
        }

        // A never-saved doc is hooked under "NEW:{title}". After its first
        // save the doc has a real path; without re-keying, the next rescan
        // would see that path absent from _docHandlers and attach a SECOND
        // handler to the same doc — every save then ran validation twice
        // (two PropertyForms / two block dialogs) and upserted twice.
        private void RekeyNewDocHandlers()
        {
            try
            {
                List<string> newKeys = null;
                foreach (var k in _docHandlers.Keys)
                    if (k.StartsWith("NEW:", StringComparison.Ordinal))
                        (newKeys ?? (newKeys = new List<string>())).Add(k);
                if (newKeys == null) return;

                foreach (var k in newKeys)
                {
                    var h = _docHandlers[k];
                    string path = h.DocPath;
                    if (string.IsNullOrEmpty(path)) continue; // still unsaved

                    _docHandlers.Remove(k);
                    if (_docHandlers.ContainsKey(path))
                    {
                        // Already double-hooked (legacy state) — drop this one.
                        try { h.Detach(); } catch { }
                    }
                    else
                    {
                        h.SetId(path);
                        _docHandlers[path] = h;
                    }
                }
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

        // ── Multi-user conflict detection ─────────────────────────────────────
        // Presence follows the ACTIVE document, not the one-time hook: a part
        // pulled in as an assembly/drawing component is loaded but not being
        // edited, so only the doc the engineer actually brings to the front
        // registers. _presenceChecked tracks paths already evaluated this open
        // so the warning shows once per open (not on every window switch).
        private readonly HashSet<string> _presenceChecked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void UpdateActivePresence()
        {
            try
            {
                var doc = SwApp?.ActiveDoc as ModelDoc2;
                if (doc == null) return;

                string path = doc.GetPathName();
                if (string.IsNullOrEmpty(path)) return;        // unsaved — not tracked
                if (!_presenceChecked.Add(path)) return;        // already handled this open

                // Released files are read-only for editing, so two people can
                // never produce a save conflict on them — skip presence there.
                if (DatabaseManager.GetFileStatus(path) == "Released") return;

                var others = DatabaseManager.GetOtherOpenSessions(path, CurrentUser);
                if (others.Count > 0)
                {
                    string who = string.Join("\n", others.ConvertAll(o =>
                        "  • " + o.User + "   (since " +
                        o.OpenedDate.ToString("dd/MM/yyyy HH:mm") + ")"));
                    SwApp.SendMsgToUser2(
                        "⚠  FILE ALREADY OPEN — BCore PDM\n\n" +
                        "This file is currently open by:\n" + who + "\n\n" +
                        "If you both save, the last save wins and the other\n" +
                        "person's changes will be lost. Coordinate before editing.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                }

                DatabaseManager.RegisterOpenSession(path, CurrentUser,
                    System.Environment.MachineName);
            }
            catch { }
        }

        // Called from DocEventHandler when the user switches the active
        // configuration of a part/assembly. Config name = Part No by convention,
        // so each config carries its own PartNo / Description / Revision; the
        // Active File card must re-read them when the active config changes.
        // (ActiveDocChangeNotify does NOT fire on a config switch — only on a
        // document switch — so this is the only signal for it.)
        internal void OnActiveConfigChanged()
        {
            try { _taskPane?.RefreshPanel(); } catch { }
        }

        // Called from DocEventHandler.OnDestroy. Removes the stale handler, then
        // re-scans open docs and refreshes the task pane — but DEFERRED.
        internal void OnDocDestroyed(string id)
        {
            _docHandlers.Remove(id);

            // Release our open-session presence so we no longer warn others.
            // id is the file path for saved docs ("NEW:..." for unsaved ones,
            // which were never registered — a harmless no-op). Also forget the
            // in-memory check so reopening the file warns/registers afresh.
            _presenceChecked.Remove(id);
            try { DatabaseManager.ClearOpenSession(id, CurrentUser,
                System.Environment.MachineName); } catch { }

            // BOTH the re-hook and the refresh must run AFTER the close settles,
            // not synchronously here. At DestroyNotify time the doc is still
            // mid-close: GetDocuments() and ActiveDoc still report it. A
            // synchronous HookAllOpenDocs() would therefore re-bind the doc's
            // path id to the DYING doc object. When a vault operation
            // (Release / New Revision / Rollback / Unlock) immediately reopens
            // the file via OpenDoc6, TryHookDoc sees the id already present and
            // SKIPS hooking the fresh doc — leaving it with no DestroyNotify
            // handler, so closing it later never clears the task pane.
            //
            // Deferring runs this after CloseDoc (and any immediate OpenDoc6)
            // completes: the dead doc is gone, the fresh doc gets hooked, and
            // ActiveDoc is correct (null when nothing remains → panel clears).
            if (_taskPane != null)
            {
                _taskPane.RunDeferred(() =>
                {
                    HookAllOpenDocs();
                    _taskPane.RefreshPanel();
                });
            }
            else
            {
                HookAllOpenDocs();
            }
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

                // FileSaveAsNotify2 is a PRE-save notify: fileName carries the
                // TARGET path while doc.GetPathName() still returns the OLD
                // path ("" for a never-saved doc). Every rule below must judge
                // the file being WRITTEN — validating the old path let Save As
                // bypass the lock/released/outside-WIP checks AND the Rule 2.6
                // duplicate-name hard block (whose own dialog tells the user
                // to use Save As!), after which the post-save create-purge
                // wiped the rival file's history. Prefer the event's target
                // whenever it is a full path; a plain re-save passes the doc's
                // own path anyway, and a bare name falls back to the doc path.
                string docPath = doc.GetPathName();
                bool fileNameRooted = false;
                try
                {
                    fileNameRooted = !string.IsNullOrEmpty(fileName)
                        && System.IO.Path.IsPathRooted(fileName);
                }
                catch { } // invalid path chars — treat as not rooted
                string filePath = fileNameRooted ? fileName : docPath;
                if (string.IsNullOrEmpty(filePath)) filePath = fileName;

                // A rooted target with a NON-SolidWorks extension is a format
                // EXPORT (File → Save As → STEP/IGES/PDF…, and
                // ModelDocExtension.SaveAs fires this same notify in some SW
                // builds — including the release flow's own exports). Exports
                // are never vault saves: judging them here popped "FILE
                // OUTSIDE THE VAULT" on every manual STEP export and could
                // interject dialogs mid-release.
                if (fileNameRooted)
                {
                    string tExt = "";
                    try
                    {
                        tExt = System.IO.Path.GetExtension(fileName)
                            .ToLowerInvariant();
                    }
                    catch { }
                    if (tExt.Length > 0 && tExt != ".sldprt" &&
                        tExt != ".sldasm" && tExt != ".slddrw")
                        return 0;
                }

                // Rooted event target, or the doc's own (always rooted) path.
                // When NEITHER is known (first save of a brand-new doc — the
                // notify fires before the Save As dialog resolves a target)
                // the location/name rules cannot run; the post-save
                // quarantine in OnSavePost owns that case.
                bool targetPathKnown = fileNameRooted
                    || !string.IsNullOrEmpty(docPath);

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
                // Single shared WIP-root constant (DatabaseManager owns it) so
                // Rule 2.5/2.6 and the DB-side rival checks can never disagree
                // about what is "inside the vault". The "\\" suffix stops
                // prefix-siblings (N:\PDM-SolidWorks\WIP_OLD\…) counting as
                // canonical WIP — the prefix-match class audit C3 fixed.
                string WipRootPath = DatabaseManager.WipRoot;
                if (targetPathKnown)
                {
                    bool underWip;
                    try
                    {
                        underWip = System.IO.Path.GetFullPath(filePath)
                            .StartsWith(
                                System.IO.Path.GetFullPath(WipRootPath) + "\\",
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

                // Rule 2.6: vault-wide file-name uniqueness — HARD BLOCK, all
                // doc types (drawing basenames drive the drawing↔model link).
                // RELEASED/ARCHIVE/SCRAP are flat folders keyed on file name
                // and the DB dedupes/purges by it, so a second "Bracket.sldprt"
                // anywhere would overwrite the first one's released snapshot
                // and archives and delete its revision history on first save.
                // No override — unlike a duplicate PartNo this corrupts data.
                // ROOTED targets only: a non-rooted name here can only be the
                // pre-dialog notify of a brand-new doc carrying the doc TITLE
                // ("Part1") — a name the user never chose; blocking on it
                // would trap the doc before the Save As dialog could even
                // open. The post-save quarantine in OnSavePost owns that case.
                if (targetPathKnown && !string.IsNullOrEmpty(filePath))
                {
                    string conflictName = System.IO.Path.GetFileName(filePath);

                    // Save As ONTO another tracked file's exact path slips
                    // past the name check via self-exclusion (the exclude
                    // param IS the target). Overwriting a different vault
                    // file's geometry while its record and history live on is
                    // the same corruption Rule 2.6 exists to stop.
                    if (!filePath.Equals(docPath ?? "",
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(
                            DatabaseManager.GetFileStatus(filePath)))
                    {
                        Block("TARGET IS A VAULT FILE:\n\n" +
                              "This save would OVERWRITE the tracked vault " +
                              "file:\n" + filePath + "\n\n" +
                              "Saving over another vault file destroys its " +
                              "geometry while its record and history live " +
                              "on.\n\nSave under a different name " +
                              "(File → Save As).");
                        return 1;
                    }

                    string dupPath = DatabaseManager.FindFileNameConflict(
                        conflictName, filePath);
                    if (dupPath != null)
                    {
                        Block("DUPLICATE FILE NAME:\n\n" +
                              "'" + conflictName +
                              "' already exists in the vault at:\n" +
                              dupPath + "\n\n" +
                              "File names must be unique across ALL divisions — " +
                              "the vault keys released snapshots, archives and " +
                              "drawing links on the file name.\n\n" +
                              "Save this file under a different name " +
                              "(File → Save As).");
                        return 1;
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

                    // Rule 3.5: every configuration in the file must have a
                    // unique Part Number. SOLIDWORKS copies all properties when
                    // a new config is created, so duplicate PartNos are the
                    // normal result of "right-click → Add Configuration". Detect
                    // and force the user to enter unique values before saving.
                    var allCfgsVS = PropertyValidator.GetConfigNames(doc);
                    if (allCfgsVS.Count > 1)
                    {
                        // Map PartNo → [configs that share it]
                        var pnMap = new Dictionary<string, List<string>>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (string c in allCfgsVS)
                        {
                            string pn = PropertyValidator.GetProperty(
                                doc, "PartNo", c);
                            if (!pnMap.ContainsKey(pn))
                                pnMap[pn] = new List<string>();
                            pnMap[pn].Add(c);
                        }

                        // Groups of configs that share a PartNo (size > 1 = dup)
                        var dupGroups = pnMap.Values
                            .Where(g => g.Count > 1).ToList();

                        if (dupGroups.Count > 0)
                        {
                            string activeCfgVS = (doc.GetActiveConfiguration()
                                as SolidWorks.Interop.sldworks.Configuration)
                                ?.Name ?? "";

                            // Configs already tracked in vault are "established".
                            // Configs NOT in vault are newly added (the copy).
                            var vaultCfgNames = DatabaseManager
                                .GetConfigsForFile(filePath)
                                .Select(c => c.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            // On the FIRST save the file isn't in vault yet, so
                            // every config looks "new" and the user would be
                            // prompted for the active config they just filled in
                            // (Rule 3). Treat the active config as established so
                            // only the genuine duplicates get a form.
                            if (vaultCfgNames.Count == 0 &&
                                !string.IsNullOrEmpty(activeCfgVS))
                                vaultCfgNames.Add(activeCfgVS);

                            // Collect configs to fix: prefer new (not-in-vault)
                            // ones; fall back to non-active ones when all are
                            // established (user manually duplicated a PartNo).
                            var toFix = new List<string>();
                            foreach (var grp in dupGroups)
                            {
                                var newOnes = grp
                                    .Where(c => !vaultCfgNames.Contains(c))
                                    .ToList();
                                if (newOnes.Count > 0)
                                    toFix.AddRange(newOnes);
                                else
                                    toFix.AddRange(grp.Where(c =>
                                        !string.Equals(c, activeCfgVS,
                                            StringComparison.OrdinalIgnoreCase)));
                            }
                            toFix = toFix
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            foreach (string dupCfg in toFix)
                            {
                                string dupPn = PropertyValidator.GetProperty(
                                    doc, "PartNo", dupCfg);
                                doc.ShowConfiguration2(dupCfg);
                                SwApp.SendMsgToUser2(
                                    "DUPLICATE PART NUMBER — UNIQUE VALUES NEEDED:\n\n" +
                                    "Configuration : \"" + dupCfg + "\"\n" +
                                    "Part No       : " + dupPn +
                                    "  (same as another configuration)\n\n" +
                                    "SOLIDWORKS copies all properties when a new " +
                                    "configuration is created.\n" +
                                    "Please enter a unique Part No, Drawing No, " +
                                    "and Description for this configuration.",
                                    (int)swMessageBoxIcon_e.swMbWarning,
                                    (int)swMessageBoxBtn_e.swMbOk);

                                using (var form = new PropertyForm(
                                    doc,
                                    new List<string>
                                        { "PartNo", "DrawingNo", "Description" }))
                                {
                                    form.ShowDialog();
                                }
                            }

                            // Restore original active config
                            if (toFix.Count > 0 &&
                                !string.IsNullOrEmpty(activeCfgVS))
                                doc.ShowConfiguration2(activeCfgVS);

                            // Re-check: block if any duplicates remain
                            var recheckMap = new Dictionary<string, List<string>>(
                                StringComparer.OrdinalIgnoreCase);
                            foreach (string c in allCfgsVS)
                            {
                                string pn = PropertyValidator.GetProperty(
                                    doc, "PartNo", c);
                                if (!recheckMap.ContainsKey(pn))
                                    recheckMap[pn] = new List<string>();
                                recheckMap[pn].Add(c);
                            }
                            var stillDups = recheckMap.Values
                                .Where(g => g.Count > 1)
                                .SelectMany(g => g)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (stillDups.Count > 0)
                            {
                                Block(
                                    "Save blocked — duplicate Part Numbers across " +
                                    "configurations:\n\n• " +
                                    string.Join("\n• ", stillDups) + "\n\n" +
                                    "Each configuration must have a unique Part No. " +
                                    "Switch to the affected configuration and change " +
                                    "its Part No before saving.");
                                return 1;
                            }
                        }
                    }

                    // Rule 3.6: per-configuration health warning (multi-config
                    // only, non-blocking). Combines two checks into ONE dialog so
                    // a freshly-added config never triggers a chain of pop-ups —
                    // each affected config gets a single line listing ALL its
                    // issues, then one Yes/No "Save anyway?" override:
                    //   (a) config name must match its PartNo — the convention
                    //       config name == PartNo underpins per-config drawings
                    //       (GetDrawingsForConfig), search and revision tracking.
                    //       Caught BEFORE the config is referenced by an assembly,
                    //       where renaming it would break those references.
                    //   (b) every NON-active config must have its required
                    //       properties. Rule 3 already hard-blocks the ACTIVE
                    //       config; the others are otherwise only validated at the
                    //       release gate, so an incomplete config could go
                    //       unnoticed until release blocks.
                    // Single-config files are exempt from both checks. (Rule 3.5's
                    // duplicate-PartNo block stays separate — it's an interactive
                    // fix flow that hard-blocks, not a passive warning.)
                    if (allCfgsVS.Count > 1)
                    {
                        string activeCfg36 = (doc.GetActiveConfiguration()
                            as SolidWorks.Interop.sldworks.Configuration)
                            ?.Name ?? "";
                        var cfgGaps = PropertyValidator.ValidateAllConfigs(doc);

                        var issueLines = new List<string>();
                        foreach (string c in allCfgsVS)
                        {
                            var issues = new List<string>();

                            // (a) name == PartNo (skip configs with no PartNo yet)
                            string cpn = PropertyValidator.GetProperty(
                                doc, "PartNo", c);
                            if (!string.IsNullOrWhiteSpace(cpn) &&
                                !string.Equals(c.Trim(), cpn.Trim(),
                                    StringComparison.OrdinalIgnoreCase))
                                issues.Add("name should match Part No " + cpn);

                            // (b) completeness — non-active configs only
                            // (Rule 3 already covers the active config)
                            if (!string.Equals(c, activeCfg36,
                                    StringComparison.OrdinalIgnoreCase)
                                && cfgGaps.ContainsKey(c))
                                issues.Add("missing: " +
                                    string.Join(", ", cfgGaps[c]));

                            if (issues.Count > 0)
                                issueLines.Add("  \"" + c + "\" — " +
                                    string.Join("; ", issues));
                        }

                        if (issueLines.Count > 0)
                        {
                            int choice = SwApp.SendMsgToUser2(
                                "SOME CONFIGURATIONS NEED ATTENTION:\n\n" +
                                string.Join("\n", issueLines) + "\n\n" +
                                "Config names should match their Part No (per-config " +
                                "drawings, search and revision tracking rely on it), " +
                                "and every config needs all required properties " +
                                "before the release gate will pass.\n\n" +
                                "Fix them now — renaming a config after an assembly " +
                                "references it breaks those references.\n\n" +
                                "Save anyway?",
                                (int)swMessageBoxIcon_e.swMbWarning,
                                (int)swMessageBoxBtn_e.swMbYesNo);
                            if (choice == (int)swMessageBoxResult_e.swMbHitNo)
                                return 1;
                        }
                    }

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
                // NOTE: the task pane is refreshed AFTER the upsert below (or
                // after the quarantine dialog) — refreshing here showed every
                // legitimate first save as "Untracked" because no record
                // existed yet, and nothing re-rendered until the next event.

                // POST-SAVE QUARANTINE (Rule 2.6's last line of defence): on
                // the FIRST save of a brand-new document SOLIDWORKS fires the
                // pre-save notify BEFORE the Save As dialog has resolved a
                // target — ValidateSave had no name to check, so that one
                // save physically cannot be blocked (verified in PR-A
                // testing: PropertyForm ran, Rule 2.6 never saw a name). The
                // name IS knowable now. If this just-saved file is UNTRACKED
                // and its name collides with a tracked vault file, do NOT
                // create a record — a second same-named record corrupts
                // every name-keyed lookup (status, history, search,
                // RELEASED/ARCHIVE) — and tell the user to rename. Every
                // further save of this doc IS blocked pre-save (its path is
                // known from now on). The tracked original is exempt
                // (currentStatus non-empty), so it keeps saving normally
                // even while a quarantined twin exists on disk.
                if (string.IsNullOrEmpty(currentStatus) &&
                    !string.IsNullOrEmpty(filePath))
                {
                    string dupPath = DatabaseManager.FindFileNameConflict(
                        System.IO.Path.GetFileName(filePath), filePath);
                    if (dupPath != null)
                    {
                        AuditLogger.Log("DuplicateNameDetected", CurrentUser,
                            System.IO.Path.GetFileName(filePath),
                            PropertyValidator.GetProperty(doc, "PartNo"), "",
                            "first save under a taken name — saved to disk " +
                            "but NOT tracked; rename or delete it. Original: "
                            + dupPath);
                        SwApp.SendMsgToUser2(
                            "DUPLICATE FILE NAME — BCore PDM\n\n" +
                            "'" + System.IO.Path.GetFileName(filePath) +
                            "' already exists in the vault at:\n" +
                            dupPath + "\n\n" +
                            "The first save of a new file happens before the " +
                            "chosen name can be checked, so this file is on " +
                            "disk but is NOT TRACKED by the vault — and every " +
                            "further save of it will be blocked.\n\n" +
                            "Save it under a different name now (File → Save " +
                            "As), then delete the duplicate:\n" + filePath,
                            (int)swMessageBoxIcon_e.swMbWarning,
                            (int)swMessageBoxBtn_e.swMbOk);
                        _taskPane?.RefreshPanel(); // card shows DUPLICATE
                        return 0;
                    }
                }

                int docType    = doc.GetType();
                bool isDrawing = docType == (int)SolidWorks.Interop.swconst
                                    .swDocumentTypes_e.swDocDRAWING;

                var vf = new VaultFile
                {
                    FilePath     = filePath,
                    FileName     = System.IO.Path.GetFileName(filePath),
                    PartNumber   = PropertyValidator.GetProperty(doc, "PartNo"),
                    Description  = PropertyValidator.GetProperty(doc, "Description"),
                    Revision     = PropertyValidator.GetProperty(doc, "Revision"),
                    Status       = string.IsNullOrEmpty(currentStatus) ? "WIP" : currentStatus,
                    ModifiedBy   = CurrentUser,
                    ModifiedDate = DateTime.Now
                };

                if (isDrawing)
                {
                    // Drawings: record which model file and which of its
                    // configurations this drawing documents so the DB can answer
                    // "which drawings cover config WGT-005?" without relying on
                    // fragile filename conventions.
                    vf.ReferencedModel   = VaultManager.GetDrawingReferencedModel(doc);
                    vf.ReferencedConfigs = VaultManager.GetDrawingReferencedConfigs(doc);
                }
                else
                {
                    // Parts / Assemblies: capture every configuration's identity
                    // so the vault can find the file by any of its part numbers
                    // and detect duplicate-PN conflicts across all configs.
                    var cfgNames = PropertyValidator.GetConfigNames(doc);
                    var configs  = new System.Collections.Generic.List<ConfigEntry>();
                    foreach (string cfgName in cfgNames)
                    {
                        configs.Add(new ConfigEntry
                        {
                            Name        = cfgName,
                            PartNo      = PropertyValidator.GetProperty(doc, "PartNo",      cfgName),
                            Description = PropertyValidator.GetProperty(doc, "Description", cfgName),
                            DrawingNo   = PropertyValidator.GetProperty(doc, "DrawingNo",   cfgName),
                            Revision    = PropertyValidator.GetProperty(doc, "Revision",    cfgName)
                        });
                    }
                    vf.Configurations = configs;
                }

                DatabaseManager.UpsertFile(vf);
                _taskPane?.RefreshPanel(); // record exists now — card is accurate

                // Warn (do not block) when non-active configs have incomplete
                // properties so the engineer knows to switch configs and save.
                if (!isDrawing)
                {
                    string activeConfig = "";
                    try
                    {
                        var cfg = doc.GetActiveConfiguration()
                            as SolidWorks.Interop.sldworks.Configuration;
                        activeConfig = cfg?.Name ?? "";
                    }
                    catch { }

                    var allIssues = PropertyValidator.ValidateAllConfigs(doc);
                    allIssues.Remove(activeConfig); // already validated / prompted above
                    if (allIssues.Count > 0)
                    {
                        string summary = string.Join("\n", allIssues.Select(kv =>
                            "  • " + kv.Key + ": " + string.Join(", ", kv.Value)));
                        SwApp.SendMsgToUser2(
                            "OTHER CONFIGURATIONS HAVE INCOMPLETE PROPERTIES:\n\n" +
                            summary + "\n\n" +
                            "Switch to each configuration and save to complete " +
                            "their required fields before releasing.",
                            (int)SolidWorks.Interop.swconst.swMessageBoxIcon_e.swMbWarning,
                            (int)SolidWorks.Interop.swconst.swMessageBoxBtn_e.swMbOk);
                    }
                }
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
            private string                _id; // re-keyed after first save
            private readonly int          _type;

            // Current on-disk path of the wrapped doc ("" while unsaved).
            // Used by RekeyNewDocHandlers to migrate "NEW:" ids to real paths.
            public string DocPath
            {
                get { try { return _doc?.GetPathName() ?? ""; } catch { return ""; } }
            }

            public void SetId(string id) { _id = id; }

            // Part delegates
            private DPartDocEvents_FileSaveNotifyEventHandler    _partSave;
            private DPartDocEvents_FileSaveAsNotify2EventHandler _partSaveAs;
            private DPartDocEvents_FileSavePostNotifyEventHandler _partPost;
            private DPartDocEvents_DestroyNotifyEventHandler     _partDestroy;
            private DPartDocEvents_ActiveConfigChangePostNotifyEventHandler _partCfgChange;

            // Assembly delegates
            private DAssemblyDocEvents_FileSaveNotifyEventHandler    _asmSave;
            private DAssemblyDocEvents_FileSaveAsNotify2EventHandler _asmSaveAs;
            private DAssemblyDocEvents_FileSavePostNotifyEventHandler _asmPost;
            private DAssemblyDocEvents_DestroyNotifyEventHandler     _asmDestroy;
            private DAssemblyDocEvents_ActiveConfigChangePostNotifyEventHandler _asmCfgChange;

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
                    _partCfgChange = new DPartDocEvents_ActiveConfigChangePostNotifyEventHandler(OnConfigChange);
                    d.FileSaveNotify    += _partSave;
                    d.FileSaveAsNotify2 += _partSaveAs;
                    d.FileSavePostNotify += _partPost;
                    d.DestroyNotify     += _partDestroy;
                    d.ActiveConfigChangePostNotify += _partCfgChange;
                    return true;
                }
                if (_type == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    var d = (DAssemblyDocEvents_Event)_doc;
                    _asmSave    = new DAssemblyDocEvents_FileSaveNotifyEventHandler(OnSave);
                    _asmSaveAs  = new DAssemblyDocEvents_FileSaveAsNotify2EventHandler(OnSave);
                    _asmPost    = new DAssemblyDocEvents_FileSavePostNotifyEventHandler(OnPost);
                    _asmDestroy = new DAssemblyDocEvents_DestroyNotifyEventHandler(OnDestroy);
                    _asmCfgChange = new DAssemblyDocEvents_ActiveConfigChangePostNotifyEventHandler(OnConfigChange);
                    d.FileSaveNotify    += _asmSave;
                    d.FileSaveAsNotify2 += _asmSaveAs;
                    d.FileSavePostNotify += _asmPost;
                    d.DestroyNotify     += _asmDestroy;
                    d.ActiveConfigChangePostNotify += _asmCfgChange;
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
                        if (_partCfgChange != null)
                            d.ActiveConfigChangePostNotify -= _partCfgChange;
                    }
                    else if (_type == (int)swDocumentTypes_e.swDocASSEMBLY && _asmSave != null)
                    {
                        var d = (DAssemblyDocEvents_Event)_doc;
                        d.FileSaveNotify    -= _asmSave;
                        d.FileSaveAsNotify2 -= _asmSaveAs;
                        d.FileSavePostNotify -= _asmPost;
                        d.DestroyNotify     -= _asmDestroy;
                        if (_asmCfgChange != null)
                            d.ActiveConfigChangePostNotify -= _asmCfgChange;
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
                _partCfgChange = null; _asmCfgChange = null;
            }

            private int OnSave(string fileName) => _addin.ValidateSave(_doc, fileName);

            // Save As COPY writes a side file while the doc itself stays at
            // its old path, unwritten — upserting would stamp the ORIGINAL
            // record's ModifiedBy/Date and audit a "Save" that never touched
            // it. Skip the post-save pipeline for copies (the pre-save rules
            // already judged the copy's target).
            private int OnPost(int t, string fn) =>
                t == (int)swFileSaveTypes_e.swFileSaveAsCopy
                    ? 0 : _addin.OnSavePost(_doc, fn);
            private int OnConfigChange() { _addin.OnActiveConfigChanged(); return 0; }

            private int OnDestroy()
            {
                Detach();
                _addin.OnDocDestroyed(_id);
                return 0;
            }
        }
    }
}
