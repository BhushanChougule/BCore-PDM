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
        // The live add-in instance, so the task pane can drive add-in helpers
        // (e.g. WarnObsoleteOnOpen after it opens a file from a search card,
        // where SOLIDWORKS' own open/activate notifications don't reliably fire).
        internal static PDMLiteAddin Instance { get; private set; }

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
                Instance = this;   // so the task pane can reach add-in helpers
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
                WarnObsoleteOnOpen();
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
            Instance = null;
            return true;
        }

        // On every active-doc change, re-scan ALL open documents.
        // This catches new files and any document that lost its handler
        // (e.g. due to a spurious DestroyNotify from the Custom Properties tab).
        private int OnActiveDocChange() { HookAllOpenDocs(); UpdateActivePresence(); WarnObsoleteOnOpen(); return 0; }

        // File→New / File→Open into an EMPTY session never fires
        // ActiveDocChangeNotify — these cover that gap (see ConnectToSW).
        // OnFileNew hooks the doc object it is HANDED first: if the new doc
        // is not yet enumerated by GetDocuments at notify time, the rescan
        // alone would miss it — the exact bypass these hooks exist to close.
        private int OnFileNew(object newDoc, int docType, string templateName)
        { TryHookDoc(newDoc as ModelDoc2); HookAllOpenDocs(); return 0; }

        private int OnFileOpenPost(string fileName)
        { HookAllOpenDocs(); UpdateActivePresence(); WarnObsoleteOnOpen(); return 0; }

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

        // A never-saved doc is hooked under "NEW:{title}", and a Save As
        // re-binds the live doc to a NEW path while its dictionary key keeps
        // the OLD one. Either way, without re-keying the next rescan would
        // see the current path absent from _docHandlers and attach a SECOND
        // handler to the same doc — every save then ran validation twice
        // (two PropertyForms / two block dialogs) and upserted twice.
        // Re-key EVERY handler whose doc path no longer matches its key.
        private void RekeyNewDocHandlers()
        {
            try
            {
                List<string> staleKeys = null;
                foreach (var kv in _docHandlers)
                {
                    string path = kv.Value.DocPath;
                    if (string.IsNullOrEmpty(path)) continue; // still unsaved
                    if (!kv.Key.Equals(path, StringComparison.OrdinalIgnoreCase))
                        (staleKeys ?? (staleKeys = new List<string>()))
                            .Add(kv.Key);
                }
                if (staleKeys == null) return;

                foreach (var k in staleKeys)
                {
                    var h = _docHandlers[k];
                    string path = h.DocPath;
                    if (string.IsNullOrEmpty(path)) continue;

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
                        "Locked on : " + lockInfo.LockedDate.ToString("MM/dd/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "\n\n" +
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

        // Tracks assemblies already WARNED about obsolete components this open.
        // Distinct from _presenceChecked: a path is added here ONLY once we
        // actually warn — so if the FIRST activation's dependency read came back
        // empty (deps not resolved yet, or the child was obsoleted after this
        // assembly was already open), a later activation re-checks and still
        // warns, instead of being permanently guarded out. Cleared on close.
        private readonly HashSet<string> _obsoleteWarned =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Design-time guard: opening/activating an assembly that still contains
        // OBSOLETE (superseded) components warns ONCE per open (the release gate
        // blocks it later, but flag it early so an obsolete part doesn't quietly
        // spread into new work). Non-blocking. Re-evaluates every activation
        // until it finds obsolete children (then guards to avoid nagging) — see
        // _obsoleteWarned. Independent of UpdateActivePresence so the presence
        // flow's Released-skip can't suppress it.
        internal void WarnObsoleteOnOpen()
        {
            try
            {
                var doc = SwApp?.ActiveDoc as ModelDoc2;
                if (doc == null) return;
                if (doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

                string path = doc.GetPathName();
                if (string.IsNullOrEmpty(path)) return;

                var obs = VaultManager.GetObsoleteComponentsByPath(path);
                if (obs.Count == 0) return;          // nothing (or not resolvable yet)
                if (!_obsoleteWarned.Add(path)) return; // already warned this open

                SwApp.SendMsgToUser2(
                    "⚠  OBSOLETE COMPONENTS — BCore PDM\n\n" +
                    "This assembly contains components that are OBSOLETE " +
                    "(superseded):\n\n  • " + string.Join("\n  • ", obs) + "\n\n" +
                    "Replace them with their current versions — an obsolete " +
                    "component will block this assembly's release.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch { }
        }

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
                        o.OpenedDate.ToString("MM/dd/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ")"));
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
            _obsoleteWarned.Remove(id);   // re-warn about obsolete children on reopen
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

                // Rule 2b: Obsolete file — superseded, kept for reference but not
                // editable. It is already read-only on disk; this is the belt-and-
                // suspenders DB-status gate (same as Released) so a save is blocked
                // even if the read-only attribute was cleared out of band.
                if (status == "Obsolete")
                {
                    string repl = "";
                    try { repl = DatabaseManager.GetSupersededBy(filePath); } catch { }
                    Block("This file is OBSOLETE (superseded).\n\n" +
                          (string.IsNullOrEmpty(repl)
                              ? "" : "Superseded by: " + repl + "\n\n") +
                          "Obsolete files are kept for reference but cannot be " +
                          "edited.\n\nA Master can Reinstate it (Vault Dashboard " +
                          "→ right-click the row → Reinstate) to return it to " +
                          "WIP for editing.\n\nSave blocked.");
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
                                    "Description and starting Revision for this " +
                                    "configuration.",
                                    (int)swMessageBoxIcon_e.swMbWarning,
                                    (int)swMessageBoxBtn_e.swMbOk);

                                using (var form = new PropertyForm(
                                    doc,
                                    new List<string>
                                        { "PartNo", "DrawingNo", "Description",
                                          "Revision" },
                                    askDrawingScope: true))
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
                        // Name-mismatched configs that can be AUTO-RENAMED to
                        // their PartNo (one click in ConfigHealthDialog), and
                        // the ones excluded with a reason.
                        // {oldCfg, newCfg, drwOldPath|null, drwNewPath|null}
                        var renameable = new List<string[]>();
                        var renamePreview = new List<string>();
                        var renameSkipped = new List<string>();
                        foreach (string c in allCfgsVS)
                        {
                            var issues = new List<string>();

                            // (a) name == PartNo (skip configs with no PartNo yet)
                            string cpn = PropertyValidator.GetProperty(
                                doc, "PartNo", c);
                            if (!string.IsNullOrWhiteSpace(cpn) &&
                                !string.Equals(c.Trim(), cpn.Trim(),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                issues.Add("name should match Part No " + cpn);

                                string target = cpn.Trim();
                                string drwPath = ConfigDrawingPath(filePath, c);
                                string drwTarget = null, drwSkip = null;
                                if (drwPath != null)
                                {
                                    // The config's drawing must be renamed
                                    // WITH it (file + record + the views'
                                    // config references) — verify it can be.
                                    drwTarget = System.IO.Path.Combine(
                                        System.IO.Path.GetDirectoryName(drwPath),
                                        SafeCfgFileName(target) + ".slddrw");
                                    var drwRec =
                                        DatabaseManager.GetFileRecord(drwPath);
                                    if (System.IO.File.Exists(drwTarget))
                                        drwSkip = "a drawing named \"" +
                                            System.IO.Path.GetFileName(drwTarget) +
                                            "\" already exists";
                                    else if (drwRec == null ||
                                        string.IsNullOrEmpty(drwRec.ReferencedModel))
                                        drwSkip = "its drawing \"" +
                                            System.IO.Path.GetFileName(drwPath) +
                                            "\" can't be verified as THIS " +
                                            "part's (no tracked model link)";
                                    else if (!string.Equals(drwRec.ReferencedModel,
                                        filePath, StringComparison.OrdinalIgnoreCase))
                                        drwSkip = "its drawing \"" +
                                            System.IO.Path.GetFileName(drwPath) +
                                            "\" documents a DIFFERENT part";
                                    else if (drwRec.Status == "Released" ||
                                             drwRec.Status == "Locked")
                                        drwSkip = "its drawing is " +
                                            drwRec.Status +
                                            " (Unlock it first)";
                                }
                                if (allCfgsVS.Any(o => string.Equals(o.Trim(),
                                        target,
                                        StringComparison.OrdinalIgnoreCase)))
                                    renameSkipped.Add("  \"" + c +
                                        "\" — a config named \"" + target +
                                        "\" already exists");
                                else if (drwSkip != null)
                                    renameSkipped.Add("  \"" + c + "\" — " +
                                        drwSkip);
                                else
                                {
                                    renameable.Add(new[]
                                        { c, target, drwPath, drwTarget });
                                    renamePreview.Add("  " + c + "  →  " +
                                        target + (drwPath == null ? "" :
                                        "   (+ drawing " +
                                        System.IO.Path.GetFileName(drwPath) +
                                        "  →  " +
                                        System.IO.Path.GetFileName(drwTarget) +
                                        ")"));
                                }
                            }

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
                            // DESIGN-TABLE parts never get the rename action:
                            // the table owns the config names, and an API
                            // rename would desynchronise it (rename in the
                            // table instead). Detected via the FEATURE TREE:
                            // an inserted Excel design table is a real
                            // "DesignTable" feature, while the auto-generated
                            // Configuration Table (present on ordinary multi-
                            // config parts) is UI-only — GetDesignTable()
                            // false-positived on it and withheld the rename
                            // button from manually-configured parts (found in
                            // PR-52 testing).
                            bool designTable = false;
                            try
                            {
                                var ft = doc.FirstFeature() as Feature;
                                while (ft != null)
                                {
                                    if (ft.GetTypeName2() == "DesignTable")
                                    {
                                        designTable = true;
                                        break;
                                    }
                                    ft = ft.GetNextFeature() as Feature;
                                }
                            }
                            catch { }
                            int parentCount = 0;
                            if (!designTable && renameable.Count > 0)
                            {
                                try
                                {
                                    parentCount = VaultManager
                                        .GetParentAssemblies(filePath).Count;
                                }
                                catch { }
                            }

                            using (var dlg = new ConfigHealthDialog(
                                issueLines, renamePreview, renameSkipped,
                                designTable, parentCount))
                            {
                                dlg.ShowDialog();
                                if (dlg.Result ==
                                    ConfigHealthDialog.Choice.Cancel)
                                    return 1;
                                if (dlg.Result ==
                                    ConfigHealthDialog.Choice.Rename)
                                {
                                    var failed = new List<string>();
                                    var performed = new List<string[]>();
                                    foreach (var m in renameable)
                                    {
                                        // ORDER: drawing file + record first
                                        // (a failure here skips the config
                                        // rename, never leaving a renamed
                                        // config with a stale-named drawing),
                                        // then the config, then the drawing's
                                        // VIEW references (which point at the
                                        // config BY NAME and must follow it).
                                        bool drawingRenamed = false;
                                        if (m[2] != null)
                                        {
                                            if (!RenameConfigDrawing(m[2], m[3]))
                                            {
                                                failed.Add(m[0] +
                                                    " (its drawing could not be " +
                                                    "renamed)");
                                                continue;
                                            }
                                            drawingRenamed = true;
                                        }
                                        try
                                        {
                                            var cfgObj =
                                                doc.GetConfigurationByName(m[0])
                                                as SolidWorks.Interop.sldworks
                                                    .Configuration;
                                            if (cfgObj != null)
                                                cfgObj.Name = m[1];
                                            if (doc.GetConfigurationByName(m[1])
                                                    == null)
                                            {
                                                // Config rename did NOT take — UNDO
                                                // the drawing rename so the pair never
                                                // diverges (drawing at the new name
                                                // while the config keeps the old one).
                                                if (drawingRenamed)
                                                    RenameConfigDrawing(m[3], m[2]);
                                                failed.Add(m[0]);
                                                continue;
                                            }
                                        }
                                        catch
                                        {
                                            if (drawingRenamed)
                                                RenameConfigDrawing(m[3], m[2]);
                                            failed.Add(m[0]);
                                            continue;
                                        }
                                        // Repoint the drawing's views to the new
                                        // config name, save-VERIFIED (TrySaveVerified
                                        // per the house rule — a bare Save3 could
                                        // silently leave the views on the old name).
                                        bool repointed = m[2] == null ||
                                            RepointDrawingViews(m[3], m[0], m[1]);
                                        performed.Add(
                                            new[] { m[0], m[1] });
                                        if (!repointed)
                                            failed.Add(m[0] +
                                                " (config + drawing renamed, but its " +
                                                "drawing's views could not be saved — " +
                                                "open it and set the views to " +
                                                m[1] + ")");
                                    }
                                    if (failed.Count > 0)
                                        SwApp.SendMsgToUser2(
                                            "These configurations could NOT " +
                                            "be renamed — rename them " +
                                            "manually in the Configuration" +
                                            "Manager:\n  • " +
                                            string.Join("\n  • ", failed),
                                            (int)swMessageBoxIcon_e.swMbWarning,
                                            (int)swMessageBoxBtn_e.swMbOk);
                                    // Parent assemblies reference these
                                    // configs BY NAME — offer the repair
                                    // AFTER the save completes (deferred via
                                    // the task pane's BeginInvoke; opening
                                    // big assemblies inside the save event
                                    // would freeze it).
                                    if (performed.Count > 0)
                                    {
                                        string fpCap = filePath;
                                        var perfCap = performed;
                                        _taskPane?.RunDeferred(() =>
                                            VaultManager
                                                .RepairParentConfigRefs(
                                                    fpCap, perfCap));
                                    }
                                    // Fall through: THIS save persists the
                                    // new names, and the post-save upsert
                                    // refreshes the per-config DB block.
                                }
                            }
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

                    // Rule 5 moved OUT of this block — it must run for
                    // drawings too (see below).
                }

                // Rule 5: broken references — runs for ALL doc types (parts,
                // assemblies AND drawings). A DRAWING whose referenced model is
                // missing — or a part with a missing external/derived parent —
                // must be flagged too. ReferenceChecker handles every type, but
                // this gate previously sat INSIDE the isPart||isAsm block, so a
                // drawing's broken model was never caught at save and its
                // dashboard flag stayed clear (found in PR-J testing).
                var broken = ReferenceChecker.GetBrokenReferences(doc);
                if (broken.Count > 0)
                {
                    int brokenChoice = SwApp.SendMsgToUser2(
                        "BROKEN REFERENCES:\n\n• " +
                        string.Join("\n• ", broken) + "\n\n" +
                        "Cannot release with broken refs.\nSave as WIP anyway?",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbYesNo);
                    if (brokenChoice == (int)swMessageBoxResult_e.swMbHitNo) return 1;
                    DatabaseManager.SetBrokenRefFlag(filePath, true);
                }
                else
                {
                    DatabaseManager.SetBrokenRefFlag(filePath, false);
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

        // sourceId = the per-doc handler's key at save time: the doc's path
        // when it was hooked ("NEW:{title}" for a never-saved doc). After a
        // Save As it still holds the PRE-SAVE identity, which is how the
        // overwrite detection below knows this save CAME FROM somewhere else.
        internal int OnSavePost(ModelDoc2 doc, string fileName,
            string sourceId = null)
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
                        RekeyNewDocHandlers();     // Save As moved the doc's path
                        return 0;
                    }
                }

                // OVERWRITE DETECTION: in this SOLIDWORKS' event order the
                // pre-save notify carries NO target, so a Save As that lands
                // ON a tracked vault file's path cannot be blocked — the disk
                // write has already replaced that file's geometry, and the
                // silent upsert below would re-bind its record and whole
                // history to a different part with only an innocent "Save"
                // row. Can't undo the write; make it LOUD instead. (Released
                // targets are already protected by OS read-only; this catches
                // WIP targets.) sourceId still holds the pre-save identity:
                // a different path, or "NEW:{title}" for a brand-new doc.
                if (!string.IsNullOrEmpty(currentStatus) &&
                    !string.IsNullOrEmpty(sourceId) &&
                    !sourceId.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    string from = sourceId.StartsWith("NEW:", StringComparison.Ordinal)
                        ? "a new unsaved document"
                        : sourceId;
                    AuditLogger.Log("VaultFileOverwritten", CurrentUser,
                        System.IO.Path.GetFileName(filePath),
                        PropertyValidator.GetProperty(doc, "PartNo"), "",
                        "Save As from " + from + " replaced this tracked " +
                        "file's geometry on disk");
                    SwApp.SendMsgToUser2(
                        "VAULT FILE OVERWRITTEN — BCore PDM\n\n" +
                        "This Save As just REPLACED the tracked vault file:\n" +
                        filePath + "\n\n" +
                        "Its previous geometry is gone from WIP (check " +
                        "RELEASED/ARCHIVE for recoverable copies) and its " +
                        "record and history now describe the NEW content.\n\n" +
                        "If this was unintentional, tell a Master IMMEDIATELY " +
                        "— the event is in the audit log as " +
                        "'VaultFileOverwritten'.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    // fall through — the record must reflect what is on disk
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
                // Re-key THIS doc's handler right away if the save moved its
                // path (Save As) — waiting for the next doc-switch rescan
                // would repeat the overwrite warning on an immediate re-save
                // and double-hook the doc.
                RekeyNewDocHandlers();

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

        // Config name sanitised for filename use. Single-sources the rule through
        // DatabaseManager.SanitizeFileName (was a 3rd copy of the same logic that
        // could drift). null → "" so callers can null-check the result.
        private static string SafeCfgFileName(string cfgName) =>
            DatabaseManager.SanitizeFileName(cfgName ?? "");

        // Path of the config-specific drawing named after this config (model's
        // folder or any WIP division), or null. Rule 3.6's auto-rename either
        // renames it ALONG WITH the config (file + record + view references)
        // or skips the config with a reason.
        private static string ConfigDrawingPath(string modelPath, string cfgName)
        {
            try
            {
                string safe = SafeCfgFileName(cfgName);
                if (string.IsNullOrEmpty(safe)) return null;
                string name = safe + ".slddrw";

                string md = System.IO.Path.GetDirectoryName(modelPath);
                if (!string.IsNullOrEmpty(md))
                {
                    string full = System.IO.Path.Combine(md, name);
                    if (System.IO.File.Exists(full)) return full;
                }
                foreach (string div in DatabaseManager.WipDivisions)
                {
                    string full = System.IO.Path.Combine(
                        DatabaseManager.WipRoot, div, name);
                    if (System.IO.File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        // Rename a config-specific drawing FILE on disk (closing it first if
        // open) and its vault record. Returns false on any failure — the
        // caller then skips the config rename so the pair never diverges.
        private static bool RenameConfigDrawing(string oldPath, string newPath)
        {
            try
            {
                try
                {
                    if (SwApp.GetOpenDocumentByName(oldPath) != null)
                        SwApp.CloseDoc(oldPath);
                }
                catch { }

                // SOLIDWORKS may release the handle a beat after CloseDoc.
                for (int attempt = 0; ; attempt++)
                {
                    try { System.IO.File.Move(oldPath, newPath); break; }
                    catch (System.IO.IOException)
                    {
                        if (attempt == 4) return false;
                        System.Threading.Thread.Sleep(200);
                    }
                }

                DatabaseManager.RenameFileRecord(oldPath, newPath,
                    CurrentUser);
                // Deliberately NOT reopened here — RepointDrawingViews opens
                // it at the new path after the config rename.
                return true;
            }
            catch { return false; }
        }

        // After the CONFIG was renamed, the drawing's views still reference
        // the OLD config name — open the renamed drawing silently, repoint
        // every model view, save, close. Best-effort: a failure is reported
        // by the caller's summary only via the audit trail of the next save.
        private static bool RepointDrawingViews(string drwPath,
            string oldCfg, string newCfg)
        {
            try
            {
                int e2 = 0, w2 = 0;
                ModelDoc2 drw = SwApp.OpenDoc6(drwPath,
                    (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "", ref e2, ref w2) as ModelDoc2;
                if (drw == null)
                    drw = SwApp.GetOpenDocumentByName(drwPath) as ModelDoc2;
                if (drw == null) return false;

                var draw = drw as DrawingDoc;
                if (draw != null)
                {
                    var sheet = draw.GetFirstView()
                        as SolidWorks.Interop.sldworks.View;
                    var v = sheet != null
                        ? sheet.GetNextView()
                            as SolidWorks.Interop.sldworks.View
                        : null;
                    while (v != null)
                    {
                        try
                        {
                            if (string.Equals(v.ReferencedConfiguration,
                                    oldCfg, StringComparison.OrdinalIgnoreCase))
                                v.ReferencedConfiguration = newCfg;
                        }
                        catch { }
                        v = v.GetNextView()
                            as SolidWorks.Interop.sldworks.View;
                    }
                }

                // TrySaveVerified manages SuppressSaveValidation itself and
                // consults the dirty flag, so a refused save is reported (not
                // silently lost as a bare Save3's false-negative would be).
                bool saved = VaultManager.TrySaveVerified(drw);
                try { SwApp.CloseDoc(drwPath); } catch { }
                return saved;
            }
            catch { return false; }
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
                    ? 0 : _addin.OnSavePost(_doc, fn, _id);
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
