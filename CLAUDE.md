\# BCore PDM — SOLIDWORKS Add-in



\## Project Overview

A fully custom SOLIDWORKS PDM replacement system built in C# .NET Framework 4.8.

Named BCore PDM (BC = bchougule initials). Replaces expensive SOLIDWORKS PDM Professional.



\## Solution Location

D:\\06 SOLIDWORKS\_Automation\\08\_Documentation\\PDMLite\_CL\\PDMLite\\PDMLite\\

\- Project file: PDMLite.slnx

\- Output DLL: bin\\Debug\\PDMLite.dll (dev machine only); deployment uses bin\\Release via DeployPDMLite.bat (see Registration)

\- Namespace: PDMLite

\- Main class: PDMLiteAddin (implements ISwAddin)



\## Network Vault Structure

N:\\PDM-SolidWorks\\

\- VAULT\\vault.xml        → XML database (System.Xml.Linq, no SQLite)

\- VAULT\\vault.xml.bak    → one-generation backup, refreshed by every successful atomic Save (File.Replace's backup argument); LoadOrCreate auto-restores from it when vault.xml is missing or corrupt

\- VAULT\\audit.csv        → append-only audit trail (AuditLogger), separate from vault.xml so the DB stays lean; opens in Excel

\- WIP\\                   → work in progress files — THE single canonical home for every vault file. Engineers always save here. The DB tracks the WIP path.

  Division subfolders (auto-created by Initialize on first addin load):

  \- WIP\\A - Aurora Shelving\\

  \- WIP\\B - Aurora Mobile\\

  \- WIP\\E - Cabinets\\

  \- WIP\\G - Hardware\\

  \- WIP\\L - Library Shelving\\

  \- WIP\\M - Conveyor\\

  \- WIP\\O - Oil tank\\

  \- WIP\\X - Rotary\\

\- RELEASED\\              → read-only published snapshots (OS read-only). Write-once OUTPUT only — never opened for editing. To change a released file, Unlock or New Revision (both act on the WIP copy).

\- ARCHIVE\\PARTS\\         → archived .sldprt files

\- ARCHIVE\\ASSEMBLIES\\    → archived .sldasm files

\- ARCHIVE\\DRAWINGS\\      → archived .slddrw files

\- ARCHIVE\\PDF\\           → archived PDF exports

\- ARCHIVE\\STEP\\          → archived STEP exports

\- ARCHIVE\\BOM\\           → archived assembly BOM CSVs

\- EXPORTS\\PDF\\           → current released PDFs

\- EXPORTS\\STEP\\          → current released STEP files

\- EXPORTS\\BOM\\           → current released assembly BOM CSVs (top-level, one per assembly release)

\- SCRAP\\                 → files retired via Remove from Vault (WIP copy, RELEASED snapshot, exports MOVED here, timestamped). Separate from ARCHIVE (old revisions). Recoverable until a Master bulk-purges it. Auto-created by Initialize.

\- ADDIN\\                 → DLL and registration files



\## Master Users

\- bchougule (Master)

\- rkramarz (Master)

\- All others = Engineer automatically (read from vault.xml)



\## Required Custom Properties (Configuration-Specific)

PartNo, DrawingNo, Description, DrawnBy, DrawnDate, Material1, FinishType, Revision, PartType

\- PartType = Manufactured | Purchased (defaults to Manufactured). Drives the assembly drawing-release gate: a Manufactured part with no drawing warns (override); a Purchased part with no drawing is skipped. Applies to parts AND assemblies.

\- PartWeight = auto-filled from mass properties

\- CheckedBy = auto-filled on release (first 2 chars uppercase: BC, RK)

\- CheckedDate = auto-filled on release (MM/dd/yyyy format)

\- DrawnDate format: MM/dd/yyyy

\- Drawings inherit properties from referenced Part (no validation on drawings)



\## File Naming Conventions

\- Part STEP: {PartNoNoDots}-R{Rev}.step  e.g. TEST02-RA.step

\- Drawing PDF: {DrawingNo} REV {Rev}.pdf  e.g. TEST-02 REV A.pdf

\- Archive files: {FileName} REV {Rev}.ext  e.g. TestFile2 REV A.sldprt



\## Code Files



\### SwAddin.cs

Main entry point, ISwAddin implementation.

\- Hooks: FileSaveNotify, FileSaveAsNotify2, FileSavePostNotify, DestroyNotify, ActiveConfigChangePostNotify (parts/assemblies)

\- Per-doc hook attachment must NOT rely on ActiveDocChangeNotify alone: that event does NOT fire when the FIRST document is created/opened into an empty session (no previous active doc), so a brand-new doc could miss hooking entirely and its first save bypassed EVERY rule (no validation, no PropertyForm, no DB record — found in PR-A testing). ConnectToSW therefore also hooks FileNewNotify2 and FileOpenPostNotify → HookAllOpenDocs (TryHookDoc is idempotent, overlap harmless; FileOpenPostNotify also runs UpdateActivePresence for the same empty-session gap). Rule 2.6 and the overwrite block run for ROOTED targets only — a non-rooted name can only be the pre-dialog notify carrying the doc TITLE ("Part1"), a name the user never chose, and blocking on it would trap the doc before the Save As dialog opens (the post-save quarantine owns the first save); Rule 2.5 (outside-WIP warning) likewise only runs when the target path is rooted/known, and both rules share DatabaseManager.WipRoot with a "\\"-suffixed prefix test so WIP_OLD-style sibling folders never count as canonical WIP. A rooted target with a NON-SW extension is a format EXPORT (manual Save As → STEP/PDF, and the release flow's own exports fire the same notify in some SW builds) — ValidateSave returns 0 immediately for those instead of popping outside-WIP warnings mid-export. NEW: Save As ONTO another tracked file's exact path is HARD-BLOCKED ("TARGET IS A VAULT FILE") when the notify carries the target — BUT in this shop's SW build the pre-save notify fires BEFORE the Save As dialog resolves (no target available for ANY Save As, found in PR-A testing), so the POST-SAVE layer owns Save As outcomes: an untracked landing with a name rival → quarantine; a TRACKED landing whose save CAME FROM a different identity (OnSavePost's sourceId = the handler's pre-save key) → loud "VAULT FILE OVERWRITTEN" warning + "VaultFileOverwritten" audit row (the disk write already happened — Released targets are protected by OS read-only, this catches WIP targets), then the upsert proceeds because the record must reflect disk. Save As COPY skips the post-save pipeline entirely (DocEventHandler.OnPost checks saveType == swFileSaveAsCopy) — the doc itself was never written, so upserting stamped the ORIGINAL record's ModifiedBy/Date for a save that never touched it. RE-KEYING: a never-saved doc is hooked under "NEW:{title}" and a Save As re-binds a live doc to a new path while its key holds the old one; HookAllOpenDocs→RekeyNewDocHandlers migrates EVERY entry whose doc path no longer matches its key (also called at the end of OnSavePost so an immediate re-save after Save As carries the fresh identity) — without it the next rescan attached a SECOND handler to the same doc and every save ran validation twice (two PropertyForms / two block dialogs) and upserted twice. OnFileNew also hooks the doc object it is handed directly (GetDocuments may not list it yet at notify time).

\- ActiveConfigChangePostNotify (parts + assemblies only — drawings have no configs): fires when the user switches the active configuration. Calls OnActiveConfigChanged() → TaskPaneHost.RefreshPanel() so the Active File card re-reads the now-active config's PartNo/Revision/Description. ActiveDocChangeNotify does NOT fire on a config switch (only a document switch), so this is the only signal that the active config changed. Hooked/detached per-doc alongside the save hooks in DocEventHandler

\- OnFileSaveNotify: suppress-flag check → check lock → check released (blocks EVERYONE incl. Masters) → warn if outside WIP folder (Yes/No override) → duplicate FILE NAME hard block (Rule 2.6, all doc types incl. drawings, no override — FindFileNameConflict) → validate properties → auto-weight → duplicate part number → broken refs

\- ValidateSave judges the file being WRITTEN: FileSaveAsNotify2 is a PRE-save notify whose fileName argument carries the Save As TARGET path while doc.GetPathName() still returns the OLD path — so the rooted event fileName wins over the doc path (a bare/non-rooted name falls back to the doc path; a plain re-save passes the same path anyway). Previously every rule checked the OLD path, letting Save As bypass the lock/released/outside-WIP checks and the Rule 2.6 hard block entirely (and the post-save create-purge then wiped the same-named rival's history)

\- SuppressSaveValidation (static bool): VaultManager sets it around its own internal Save3 calls (Release, New Revision) so those programmatic saves bypass the released-file lock

\- OnFileSavePost: POST-SAVE QUARANTINE first (Rule 2.6's last line of defence — the FIRST save of a brand-new doc fires the pre-save notify BEFORE the Save As dialog resolves a target, so ValidateSave has no name to check and that one save cannot be blocked; found in PR-A testing). If the just-saved file is UNTRACKED and its name collides with a tracked vault file: NO record is created (a second same-named record corrupts every name-keyed lookup), a warning dialog tells the user to rename via Save As and delete the duplicate, and "DuplicateNameDetected" is audit-logged. The tracked ORIGINAL is exempt (it keeps saving normally even while a quarantined twin exists). Every later save of the quarantined file is blocked pre-save (its path is known from then on). The task pane is refreshed AFTER the upsert (or after the quarantine dialog) — refreshing before it showed every legitimate first save as "Untracked". Otherwise: upsert file to database, update description

\- CurrentUser = System.Environment.UserName

\- SwApp = static ISldWorks reference

\- Multi-user conflict detection: OnActiveDocChange (and once at ConnectToSW for an already-open file) calls UpdateActivePresence() — presence follows the ACTIVE doc, NOT the per-file hook, so a part loaded only as an assembly/drawing component never registers. _presenceChecked (HashSet) ensures the warning shows ONCE per open, not on every window switch. For a WIP active file: warns if GetOtherOpenSessions finds another user has it open ("FILE ALREADY OPEN — last save wins"), then RegisterOpenSession. Released files skipped (read-only, no save conflict). OnDocDestroyed clears the in-memory check + ClearOpenSession. ConnectToSW/DisconnectFromSW call ClearMachineSessions(MachineName) to wipe crash leftovers.



\### PropertyValidator.cs

\- Validate(doc) → ValidationResult with IsValid + EmptyFields list

\- GetProperty(doc, propName) → handles drawings (configName="") vs parts (configName=activeConfig)

\- SetProperty(doc, propName, value) → handles drawings safely with try/catch

\- AutoFillWeight(doc) → reads mass properties, sets PartWeight (parts/assemblies only)

\- FixDateFormats(doc) → converts yyyy-MM-dd to MM/dd/yyyy on save



\### PropertyForm.cs

WinForms dialog for missing properties. DPI-AWARE (house convention): _scale = g.DpiX / 96f (read once in the constructor via CreateGraphics), every size via S(v) = (int)(v * _scale), every font as pt × _scale, and AutoScaleMode.None so that explicit scaling is the ONLY scaling (no WinForms autoscale on top) — the dialog looks the same proportionally on a 1080p 100% machine and a 4K 250% machine. Single-line labels (header, subtitle, per-field section headers, row labels) use AutoSize / TextAlign so bold text can never clip at the bottom or right at any DPI; the subtitle wraps via MaximumSize. Fonts are stored as fields and disposed in Dispose(bool) (a Font assigned to a control is not owned by it). Enter=Save, Esc=Cancel (AcceptButton/CancelButton). Layout is laid out with a running y-cursor (label.Bottom + gap) rather than fixed offsets so it never overlaps regardless of wrap/scale.

TWO MODES via two constructors: (1) ACTIVE-CONFIG mode — PropertyForm(doc, List<string> emptyFields), one row per missing field, values written to the active configuration (save-time Rules 3/3.5); (2) MULTI-CONFIG mode — PropertyForm(doc, Dictionary<configName, List<missingFields>>), used by the RELEASE GATE: ONE dialog showing every configuration's missing fields, grouped BY FIELD with one row per config under each bold field header (rows labelled with the config name, indented), so the Master fills e.g. Material for every config in one pass with NO active-config switching; each row's value is written to ITS OWN config via SetProperty(doc, field, value, configName) and values may differ per config. Inputs are registered in a single _rows list (InputRow = Control + ConfigName + Field; ConfigName null = active config). Rows live in a SCROLLABLE panel capped to the screen working area (buttons stay fixed below), so any number of configs × fields fits.

\- Baseline sizes (× _scale via S()) calibrated to the HOUSE UNIT SYSTEM (cf. ConfigRevisionPickerForm ~440-wide) so the dialog is the SAME physical size as the other BCore dialogs at any DPI — at 4K/250% (_scale=2.5) the form is ~1200px, not ~3000px: formWidthBase=480, labelWidth=126, inputWidth=314, inputHeight=24, rowHeight=32, leftMargin=16, inputLeft=150

\- Fonts (× _scale, calibrated to house dialogs' 3.3–6f range): header 5.5f bold, subtitle 3.4f, section 4.1f bold, label/input 3.7f, button 3.9f bold

\- DateTimePicker format: MM/dd/yyyy

\- CharacterCasing=Upper on TextBoxes

\- Material1 and FinishType use ComboBox dropdowns. The property NAME is Material1 (linked to the drawing template) but the display label is "Material". Material1 dropdown includes "BOM" (material called out in the BOM/table, not on the part). All dropdown VALUES are ALL CAPS (everything on the drawing is uppercase). FinishType options: NONE, PAINTED, ZINC PLATE, BLACK ZINC, HOT DIPPED GALV., FNC, SEE TABLE, BLACK OXIDE, PASSIVATE. The "-- Select --" sentinel keeps its mixed case (it's a UI placeholder, not a value, and ComboValue compares it exactly)

\- DrawnBy (TextBox, editable) auto-defaults to the current user's initials via UserInitials() — first two letters of the Windows username, uppercased (bchougule → BC, rkramarz → RK), same rule as CheckedBy. Only pre-filled when empty; the engineer can overwrite it

\- Revision uses ComboBox (full revision sequence A through Z, skipping I,O,Q,S,X)

\- PartType uses ComboBox (Manufactured | Purchased) — NO "-- Select --" sentinel, so index 0 (Manufactured) is the valid default

\- ComboValue(cb) helper: dropdowns with a "-- Select --" first item treat index 0 as empty; sentinel-less dropdowns (PartType) return the selected item directly



\### DatabaseManager.cs

XML vault database at N:\\PDM-SolidWorks\\VAULT\\vault.xml

Concurrency (cross-machine): every DB critical section is `lock (_lock) using (AcquireProcessLock())` — the in-process `_lock` serialises threads in ONE SOLIDWORKS instance; AcquireProcessLock adds CROSS-MACHINE mutual exclusion via an exclusive lock file (N:\\PDM-SolidWorks\\VAULT\\vault.lock opened FileShare.None — SMB honours this across PCs; a named Mutex would NOT, it's machine-local). Without it the read-modify-write of vault.xml was last-writer-wins (A loads, B loads, A saves, B saves → A's changes lost) across 10+ machines. The lock is held for the FULL load→save so writes never interleave; reentrant per-thread (depth counter, ThreadStatic) since a process can't re-open its own FileShare.None handle and DB methods may nest. Retries 100×300ms (30s budget — hold times grow with the vault, and a save blocked for seconds is cheaper than a silently lost write) ONLY on genuine sharing violations (HResult 32/33); any other error (path unreachable/access denied = network down) breaks immediately so a down network fails fast instead of spinning. If the lock can't be acquired it proceeds WITHOUT it (degrades to the old last-writer-wins, never worse) rather than blocking the user — non-fatal philosophy — but degraded mode is OBSERVABLE and CONTAINED: it is audit-logged as "LockDegraded" (user="system", throttled to one entry per event type per 5 min per process via LogDbEvent), and while degraded (LockDegraded flag, ThreadStatic alongside the depth counter) all JANITORIAL writes are suppressed — stale-session purge (GetOtherOpenSessions), orphan auto-purge (SearchFiles) and presence bookkeeping (RegisterOpenSession/ClearOpenSession/ClearMachineSessions) skip their Save, because a mere READ must never write its stale whole-vault snapshot back over other machines' committed changes; user-initiated mutations (UpsertFile, SetFileStatus, …) still proceed. Save(doc) is ATOMIC: serialise to a PER-PROCESS temp (vault.xml.{machine}.{pid}.tmp) then File.Replace with vault.xml.bak as the backup argument — every successful save leaves the PREVIOUS vault.xml as a one-generation recovery file (a rename, costs nothing). The replace is retried 5×200ms (a degraded-mode reader on another machine can briefly hold vault.xml open); if File.Replace is genuinely unavailable the fallback refreshes .bak then delete+moves the fully-written temp into place (audit-logged "VaultSaveFallback"; the old direct-overwrite doc.Save remains only as the absolute last resort) so a crash mid-write can't leave a truncated vault.xml; the per-process temp name means even in degraded mode (lock unavailable) two machines never share one .tmp and interleave into a corrupt file. LoadOrCreate RECOVERS from the backup: a missing vault.xml on an established vault (crash/accidental delete) or a corrupt vault.xml (XmlException) is restored from vault.xml.bak (audit-logged "VaultRestoredFromBackup") instead of silently bootstrapping an EMPTY database or failing every DB call; only a genuinely fresh vault (NO backup either) creates the empty template — if a backup EXISTS but cannot be restored (corrupt .bak / transient IO), LoadOrCreate THROWS rather than bootstrapping: an empty vault would hand every caller a blank DB and the next Save would overwrite the .bak, destroying the last copy of an established vault. Before a restore, a corrupt-but-present vault.xml is preserved ONCE as vault.xml.corrupt.{stamp} (it holds exactly one save more than the backup — hand-repairable) and then DELETED: leaving it in place poisoned the next Save, whose File.Replace banked the corrupt file into vault.xml.bak — destroying the good backup just parsed (degraded mode skips the restore write-back but user mutations still save); with the poison gone, Save takes the File.Move path and the backup survives, and repeated degraded reads write nothing. Restore/bootstrap WRITES are skipped in degraded mode (a READ path must never write without the cross-machine lock — same rule as the janitorial writes); the parsed in-memory doc serves the call either way. Each DB call is quick (load/modify/save XML); the lock is NEVER held across long SOLIDWORKS operations (release/export call DB methods individually between SW work).

Classes: VaultFile, LockInfo, HistoryEntry, RevisionRequest

RevisionRequest.RequestType = "Unlock" | "Revision" | "Release"

Methods:

\- Initialize, UpsertFile (audit-logs Create vs Save; matches the existing record by FilePath CASE-INSENSITIVELY — a casing difference like n:\\ vs N:\\, which SOLIDWORKS can produce, used to miss the record, create a duplicate, and the wasCreate purge then wiped the file's whole history. CREATE-TIME RIVAL GUARD: on create, when another tracked WIP file already uses the same file name (FindNameRival — the shared private core of FindFileNameConflict and the quarantine, so all three can never disagree) NO record is created at all and the history purge is skipped: Rule 2.6's pre-save check and the post-save insert run in separate lock acquisitions, so two machines first-saving the same name within seconds can both get here; a second record corrupts every name-keyed lookup and PurgeHistoryFor (FILENAME-matched) would wipe the LIVING rival's timeline. Audit-logged "DuplicateNameDetected" — the note says to DELETE THE DUPLICATE FILE IN EXPLORER, never "Remove from Vault" (record removal matches by filename and would take the ORIGINAL's record/history too); rewrites the per-config <Configurations> block from the live doc on every save — but ONLY when the incoming list is non-empty: every real part/assembly has ≥1 config, so an empty list means GetConfigNames failed transiently and the existing block is PRESERVED rather than wiped, so a transient enumeration failure can't collapse a multi-config file to a single phantom config), GetFileStatus, SetFileStatus

\- SetBrokenRefFlag, LockFile, UnlockFile, GetLockInfo

\- RemoveFileRecord(filePath) → removes vault.xml record(s) for a file (matches FilePath then FileName for dupes/RELEASED-copy entries) AND purges that file's RevisionHistory entries via PurgeHistoryFor (so a new file of the same name never inherits the removed file's timeline); DB record ONLY, never touches files on disk; returns count of File records removed

\- PurgeHistoryFor(doc, filePath, fileName) → private helper; removes all RevisionHistory <Entry> nodes matching the file by exact FilePath or by filename. Also called by UpsertFile on CREATE (wasCreate) so a brand-new file always starts with a clean history, even if a same-named file was removed before the purge fix shipped

\- Initialize() → creates WIP division subfolders + SCRAP folder on first addin load

\- GetUserRole, AddUser

\- SearchFiles(term) / SearchFiles(term, out truncated) → searches PartNumber + Description + FileName (all statuses); returns canonical WIP path; dedupes by filename; capped at MaxSearchResults=50 (truncated=true when more matched, so UI can prompt to refine — prevents rendering thousands of cards at 50k scale); AUTO-PURGES orphaned records whose file is missing on disk, but ONLY when the WIP root is reachable (network-down guard so a transient outage never deletes records) AND the cross-machine lock was actually acquired (a degraded-mode search is a READ and must never write a stale snapshot back); auto-purges are audit-logged as "AutoPurgeOrphan"

\- FindPartNumberConflict(partNo, excludeFilePath) → returns filename of another file using same PartNo (case-insensitive, trimmed), or null. Excludes the file being saved so it never conflicts with itself.

\- FindFileNameConflict(fileName, excludeFilePath) → returns the FilePath of ANOTHER tracked vault file already using this file name (case-insensitive), or null. ValidateSave HARD-BLOCKS on a hit (Rule 2.6) — the vault keys on the file name everywhere (RELEASED/ARCHIVE/SCRAP are flat folders, search/dashboard dedupe by name, drawing↔model linkage is by basename, RemoveFileRecord/PurgeHistoryFor match by name), so a second same-named file in another division would overwrite the first one's released snapshot/archives and delete its history on first save. Only canonical records under WIP count as rivals: a same-name record OUTSIDE WIP is a legacy RELEASED-copy entry of the same file, and a WIP record whose file is gone from disk is an orphan awaiting purge — neither blocks a save.

\- GetFileHistory(filePath) → returns List<HistoryEntry> reversed (most recent first). Name-fallback matching (so RELEASED-folder copies and legacy records share history with the WIP original) is GATED on FindNameRival: when a LIVING rival WIP file owns the name, the query path is a same-named duplicate and gets exact-path entries only — but an orphaned/moved record (file gone from disk) is not a rival, so the graceful fallback for Explorer-moved files survives. GetFileStatusByName has the same gate for the same reason.

\- GetFileRecord(filePath) → returns the full VaultFile for an exact path (PartNumber, Revision, Status, etc.), or null if untracked. Used by the Pending Requests cards to show a request's PN + Revision (which live on the File record, not the RevisionRequest).

\- GetModelForDrawing(drawingFilePath) → returns the part/assembly VaultFile sharing the same base filename as the given drawing (PartNo/Description/Status live on the model, not the drawing); null if not found. Used by the merged search card to populate model details when a search matched the drawing.

\- GetDrawingPathForModel(modelFilePath) → returns the WIP path of the .slddrw sharing the same base filename as the given part/assembly (the drawing that documents it); null if none tracked. Used by the merged search card to wire "Open Drawing" when a search matched only the model.

\- GetReleasableFiles(filter, out truncated) → returns WIP (releasable) files for the Bulk Release picker, optionally filtered by PartNumber/Description/FileName; excludes Released/Locked; deduped by filename; capped at MaxSearchResults; file must exist on disk (orphans skipped, NOT purged — read-only picker)

\- GetAllFiles() → returns EVERY tracked file (all statuses) for the Vault Dashboard, deduped by filename (drops legacy double-records + RELEASED-folder copies). READ-ONLY snapshot: unlike SearchFiles it does NOT auto-purge orphans and does NOT hit the disk per file (so opening the dashboard never mutates the DB and stays fast at scale). NOT capped (the dashboard shows the whole vault). Populates FilePath, FileName, PartNumber, Description, Revision, Status, ModifiedBy, ModifiedDate (parsed round-trip), LockedBy, HasBrokenRefs, ReferencedModel + ReferencedConfigs (so the dashboard can map a drawing to its model and the config it documents without a re-query), and ReleasedDate + ReleasedBy (timestamp + ChangedBy of the most recent "Released" RevisionHistory entry; DateTime.MinValue / "" if never released — distinct from ModifiedDate/ModifiedBy). DRAWINGS carry no PartNumber/Description of their own (those props live on the model), so GetAllFiles fills them in-memory from the model — preferring the explicit ReferencedModel link, else a base-filename match (Widget.slddrw → Widget.sldprt/.sldasm) — with no extra DB round-trip. VaultFile.LockedBy and VaultFile.ReleasedDate are populated ONLY by this method (other queries leave them default — read lock state via GetLockInfo)

\- AddRevisionRequest, AddUnlockRequest, AddReleaseRequest → all call private AddRequest(type,...)

\- GetPendingRequests, GetRequestsByUser(user), ResolveRequest

\- Open sessions (multi-user conflict detection) — <OpenSessions> section in vault.xml, distinct from the hard Master Lock (soft presence). OpenSession class = FilePath, User, Machine, OpenedDate.

  \- RegisterOpenSession(path, user, machine) → records/refreshes that user@machine has the file open

  \- GetOtherOpenSessions(path, currentUser) → sessions held by OTHER users (excludes currentUser, who may have it open in several windows); skips + purges entries older than StaleSessionHours (24h) in the same pass

  \- ClearOpenSession(path, user, machine) → removes one session on file close

  \- ClearMachineSessions(machine) → removes ALL sessions for a PC; called on addin load AND unload so a crashed SOLIDWORKS never leaves a session that falsely warns others



\### VaultManager.cs

Core vault operations.

\- LockFile(path) → Master only, sets status=Locked. NOTE: no longer wired to any task-pane button (the Lock File button was replaced by the context-aware Open Drawing / Open Part-Assembly button); kept as an available method

\- UnlockFile(path) → Master only, sets status=WIP, removes read-only

\- ReleaseFile(doc, suppressPrompts=false) → validates → (assembly) parts Released + drawing-release gate → exports → copies to RELEASED → sets read-only. FAILURE-ATOMIC: every fallible step is verified and a failure ABORTS the release BEFORE the DB write, so the DB can never say Released while disk says otherwise — (1) the pre-export Save3 return is checked (a refused save would publish a snapshot missing the just-auto-filled CheckedBy/CheckedDate/PartWeight) — Save3's bool alone FALSE-NEGATIVES (it can return false when the save landed or nothing needed saving, which aborted good releases in testing), so on false the dirty flag is consulted as ground truth: !GetSaveFlag() (no unsaved changes) = save effectively succeeded; still-dirty = genuine failure → abort. The same Save3-then-GetSaveFlag verification is shared via the TrySaveVerified(doc) helper, used by ReleaseFile, StartNewRevision and StartDrawingRevisionWith — every internal Save3 call must go through it; (2) every PRIMARY export (PDF for drawings, STEP per config for models) is verified via ExportManager's bool returns and any failure aborts with a dialog listing the failed exports (the previous exports were already archived and are recoverable from ARCHIVE; flat-pattern DXF and BOM stay best-effort); (3) a failed copy to RELEASED aborts (previously it warned and FELL THROUGH to mark the file Released with a stale/no snapshot), re-clearing read-only so the file stays an editable WIP. Every abort is audit-logged as "ReleaseFailed" with the reason. Releasing a Drawing whose model is still WIP offers ONE prompt to release both; on Yes the model is released via ReleaseFile(model, suppressPrompts:true) so the pair needs only a single confirm + single combined success. suppressPrompts skips the confirm + success dialogs only (blocker/validation dialogs still show). The release-success popup uses ShowAutoCloseInfo (same look as MessageBox) which AUTO-DISMISSES after 4s if the user doesn't click OK — so an unattended release doesn't leave a dialog blocking the file close. A WinForms Timer is used (its WM_TIMER is pumped by the modal MessageBox loop); on tick it FindWindow(s) the dialog by caption and PostMessage(WM_CLOSE) — for an OK-only box that resolves exactly like clicking OK

\- StartNewRevision(doc, suppressPrompts=false) → removes read-only → archives → bumps rev → saves → sets WIP → auto-starts associated drawing revision → warns about parent assemblies. suppressPrompts (bulk approve) skips the confirm + final summary dialogs only; blocker/failure dialogs still show. The revision-bump Save3 return is CHECKED: on failure the in-memory Revision property is reverted, read-only restored, and the operation ABORTS before any DB write (audit-logged "NewRevisionFailed") — previously a refused save left disk at the OLD rev while the DB went WIP at the new rev and the drawing was bumped anyway

\- OpenByPath(filePath) → opens a vault file choosing doc type from extension (returns already-open doc if SOLIDWORKS has it; null on failure). Used by the batch helpers

\- BulkRelease(filePaths) → releases a batch of WIP files in one pass, ordered parts→assemblies→drawings (so gates pass); each: OpenByPath + ReleaseFile(suppressPrompts:true) + check status; returns a BatchResult (Succeeded/Skipped names). Blocker/validation dialogs still show so the Master sees why anything is skipped

\- BulkApprove(requests) → approves a batch of pending requests, each by type: Unlock→UnlockFile, Revision→StartNewRevision, Release→ReleaseFile (releases ordered parts→assemblies→drawings); only resolves a request when its action actually succeeded (a blocked release stays pending); returns a BatchResult

\- BatchResult (top-level class in VaultManager.cs) = Succeeded + Skipped name lists + BuildSummary(heading) for one summary dialog

\- RollbackRevision(doc) → shows RollbackDialog → archives current → restores selected → offers matching drawing rollback (if archive exists) → warns about parent assemblies

\- FindDrawingPath(modelPath) → finds {basename}.slddrw in the model folder or any WIP division; null if none (drawing filename MUST match the model basename)

\- GetParentAssemblies(filePath) → scans tracked .sldasm files via GetDocumentDependencies2 (reads refs without opening); returns filenames of assemblies that reference the file. Best-effort — depends on stored ref paths matching vault path format

\- StartDrawingRevisionWith(modelPath, currentRev, nextRev, user) → archives the Released drawing at the old rev (matched pair), returns it to WIP, opens it silently to bump the Revision property to nextRev and save, then closes it. StartNewRevision reopens it if the user had it open. Drawing rev LETTER syncs to model immediately at New Revision time. A failed open or a refused Save3 is surfaced as a WARNING in the returned summary line (open the drawing and set the rev manually) instead of silently reporting success with a stale rev on disk.

\- EvaluateAssemblyDrawings(doc, out blockers, out warnings) → per component: Toolbox skipped; drawing exists+not Released → blocker; no drawing + Manufactured → warning; no drawing + Purchased → skipped. Dedupes repeated instances

\- IsToolboxComponent(comp) → heuristic: path contains "\\Toolbox\\". Secondary net; PartType=Purchased is the authoritative mechanism

\- GetComponentPartType(comp) → reads PartType from the loaded component model ("" if unreadable). Reads the property from the config the ASSEMBLY references (comp.ReferencedConfiguration), NOT the component's active in-memory config — PartType is config-specific, so a shared child that is Manufactured in one config and Purchased in another is classified by the config actually used in the assembly; falls back to the active-config read when the referenced config is unknown

\- RemoveFromVault(doc) → Master only; retires the active file — MOVES its WIP copy, RELEASED snapshot and exports (STEP + PDF + BOM) to SCRAP (timestamped) and deletes the vault record; BLOCKED while Released (Unlock/New Revision first); MULTI-CONFIG AWARE: exports are scrapped for EVERY configuration's PartNo + DrawingNo (not just the active config's — exports are named per config, so the active numbers alone left other configs' stale "current" deliverables in EXPORTS), and ALL associated drawings are scrapped — the shared {basename}.slddrw (FindDrawingPath) plus every config-specific drawing (GetDrawingsForConfig per config), deduped by filename preferring the canonical WIP path; drawings are ALWAYS scrapped automatically (even if Released — a drawing without its model is blank and useless); confirmation dialog lists all drawings AND warns when parent assemblies reference the file (GetParentAssemblies, best-effort — warn not block, same pattern as New Revision/Rollback; removing a referenced component breaks those assemblies on next open); audit-logged per file. Orphans are NOT handled here (can't open a deleted file) — SearchFiles auto-purges them instead

\- MoveToScrap(filePath) → moves one file to SCRAP with a yyyyMMdd_HHmmss suffix (clears read-only first; no-op if missing). ScrapExports(partNo, drawingNo) → moves matching STEP (by dotless partNo) + PDF (by drawingNo) + BOM CSV (by raw partNo) exports to SCRAP, each via anchored glob + ExportNameFilter so retiring TEST02 can never scrap TEST021's deliverables

\- RequestRevision(doc), RequestUnlock(doc), RequestRelease(doc) → Engineer requests with note dialog

\- ApproveRequest(request) → Master approves, calls StartNewRevision

\- RejectRequest(request) → Master rejects; prompts for a rejection REASON (ShowNoteDialog) that is emailed to the engineer (not their own note echoed back); Cancel aborts; marks as Rejected in database

\- ViewMyRequests() → Engineer views their own requests (MessageBox)

\- OpenOrCreateDrawing(doc) → searches for matching .slddrw (model folder + every WIP division); opens it if found, else creates a new drawing FROM the model immediately (no prompt) — opens the default drawing template and calls DrawingDoc.InsertModelInPredefinedView to populate its predefined views (the "Make Drawing from Part/Assembly" equivalent); falls back to Create3rdAngleViews2 if the template has no predefined views, so the sheet is never blank. The part/assembly side of the context-aware Open button. Multi-config: searches for config-specific drawing ({configName}.slddrw) FIRST, then falls back to the shared drawing ({modelBasename}.slddrw); if NEITHER exists, prompts ONCE via DrawingScopeDialog (Common drawing for ALL configs → {modelBasename}.slddrw, or This configuration only → {configName}.slddrw, or Cancel) — the chosen file name on disk carries the decision so the prompt never repeats; the new drawing is auto-saved to the model's folder via SuppressSaveValidation; config name is sanitised for Windows filename safety before use. Single-config always creates the shared {modelBasename}.slddrw with no prompt. Unsaved in-memory drawings are matched to the active config via GetDrawingReferencedConfigs.

\- OpenReferencedModel(doc) → from a drawing, opens (or activates if already open) the part/assembly it references; warns if the referenced model can't be found. The drawing side of the context-aware Open button

\- GetUnreleasedComponentsByPath(asmPath) → checks all assembly children are Released by reading the dependency tree from disk via GetDocumentDependencies2 (independent of load mode — a lightweight assembly, or one loaded only as a drawing reference, still reports its WIP children). Skips Toolbox; dedupes; non-Released tracked components block. Used by both the assembly-release gate and the assembly-drawing-release gate

\- GetDrawingNo(doc) → gets DrawingNo from referenced model

\- GetDrawingPartNo(doc) → gets PartNo from the model a drawing references, reading the SPECIFIC config the drawing documents (via GetDrawingPrimaryConfig) NOT the model's active config (which may be any config after a release/export loop switched it); falls back to the active-config read, then the drawing's own PartNo, then ""; used by the task-pane Active File card. GetDrawingNo and the drawing-release revision sync use the same config-specific read.

\- GetDrawingPrimaryConfig(doc) → returns the ReferencedConfiguration of the drawing's first model view (the config the drawing documents), or "" if unreadable (treat as "use active config"). Underpins GetDrawingPartNo/GetDrawingNo and the drawing-release rev sync so a config-specific drawing always reports its OWN config's PartNo/DrawingNo/Revision.

\- GetDrawingReferencedModel(doc) → gets path of model referenced by drawing

\- SetReadOnly(path, bool) → sets/removes OS-level FileAttributes.ReadOnly

\- ArchiveOldExports(archiveId, isDrawing, bomIdentifier=null) → moves old STEP (non-drawings), PDF (all), and BOM CSV (non-drawings, by raw PartNo) to archive before release. All three go through MoveMatching as INDEPENDENT operations — a failure archiving one type can never skip the others. Globs are ANCHORED to the export naming conventions ({id}-R*.step, {id} REV *.pdf, {id}-R*_BOM.csv) and paired with an ExportNameFilter regex — a bare "{id}*.step" prefix glob silently archived OTHER parts' current exports whenever one part number started with another (releasing TEST02 swept TEST021's deliverables)

\- CleanupExportsOnRollback(partNoClean, drawingNo, rawPartNo=null) → moves all exports (STEP/PDF/BOM) to archive on rollback; same anchored-glob + ExportNameFilter matching as ArchiveOldExports

\- ExportNameFilter(identifier, sep, suffix) → private helper; builds the exact-name regex "^{id}{sep}[A-Za-z0-9]+{suffix}$" (case-insensitive) paired with every export glob. Even an anchored glob is still a prefix match ("{id}-R*.step" matches part TEST02-R1's "TEST02-R1-RA.step"); pinning the revision token to letters/digits means exactly "{identifier}{sep}{rev}{suffix}" survives and nothing else. Also post-filters RollbackRevision's ARCHIVE scan ("{name} REV *{ext}") so a file named "Bracket" never lists "Bracket REV PLATE"'s archives

\- MoveMatching(srcDir, destDir, pattern, exactName=null) → private helper; moves every file in srcDir matching pattern (post-filtered by the optional exactName regex — see ExportNameFilter) to destDir. Each file moved in its OWN try/catch (clears read-only on a stale archive copy + on the source first), so one locked/failed file never blocks the rest; GetFiles wrapped in try; no-op if srcDir missing/empty. Used by ArchiveOldExports and CleanupExportsOnRollback



\### ExportManager.cs

\- ExportAll(doc, exportRoot, stamp) → routes to correct export by file type (single-config path). Returns BOOL: true when the PRIMARY export (PDF for drawings, STEP for parts/assemblies) succeeded — SaveAs return + errors==0 + file-on-disk all verified. ReleaseFile honours it and aborts on failure (previously the return was discarded and a failed export still produced a "Released" file with no current export). Flat-pattern DXF stays best-effort and never fails a release

\- ExportStepOnly(doc, exportRoot, stamp) → exports STEP only; active config at call time = exported geometry. Called once per config in the multi-config release loop. Returns BOOL (same success contract as ExportAll); ReleaseFile collects per-config failures and aborts listing them all

\- ExportFlatPatternOnly(doc, exportRoot, stamp) → exports flat-pattern DXF only; called once for the original active config after the multi-config STEP loop

\- ExportDrawingPdf(doc, outPath) → drawing to PDF (all sheets); called by ExportAll which immediately follows with StampWatermark. SUPPRESSES SOLIDWORKS' own "View PDF after publishing" auto-open via ExportPdfData.ViewPdfAfterSaving=false (that setting would launch the viewer on the UN-watermarked file DURING SaveAs, before we can stamp). Preserves all-sheets export by SetSheets(swExportData_ExportAllSheets, GetSheetNames()). Falls back to null export data (original behaviour) if the export data can't be obtained, wrapped in try/catch so the export never fails over this.

\- OpenPdfExternally(pdfPath) → opens the finished (stamped) PDF in the user's default viewer via Process.Start. Called by ReleaseFile AFTER StampWatermark, for an interactive single drawing release only (skipped when suppressPrompts — bulk/chained — so batches don't spawn viewers). Replaces SW's suppressed auto-open so the user still sees the just-released drawing, now watermarked. Non-fatal.

\- StampWatermark(pdfPath) → THIN WRAPPER (references no PdfSharp types) that calls StampWatermarkCore in a try/catch: if PdfSharp.dll is missing the assembly-load failure is thrown when the JIT enters the core and is caught here (a single method referencing PdfSharp types could NOT catch its OWN load failure — it would propagate and break the release). Non-fatal: any PdfSharp error leaves the PDF un-stamped and the release continues.

\- StampWatermarkCore(pdfPath) → stamps a diagonal, very transparent "RELEASED" watermark on every page. READS THE PDF INTO MEMORY (File.ReadAllBytes → MemoryStream) and stamps there so PdfSharp holds NO file handle on the path; SOLIDWORKS keeps the freshly-exported PDF open briefly (shared read), so the in-place WRITE-BACK retries 5×300ms on IOException before a final throw (the read succeeds but an exclusive write is blocked until SW releases it — this was the "no watermark until reopen" bug). Text is aligned to the sheet's bottom-left→top-right diagonal: angle = −atan(height/width) (≈ −33° for a 17×11 sheet; NEGATIVE ascends in PdfSharp's y-down space). Font is Arial Bold auto-sized so the text spans ~48% of the page diagonal (scales to any sheet size A→E). Colour is gray (120,120,120) at alpha 11/255 ≈ 4.5% (PdfSharp emits alpha as a PDF ExtGState, so true transparency works). PdfSharp 1.50.5147 (the net20 build, which runs fine on .NET 4.8) is VENDORED directly into the project as PDMLite\\PdfSharp.dll and referenced by relative HintPath — NOT a NuGet restore (matches the existing System.Data.SQLite.dll pattern; no restore step on the build machine). It is marked Content/CopyToOutputDirectory so it lands in bin\\Debug next to PDMLite.dll. Deploy PdfSharp.dll alongside PDMLite.dll in N:\\PDM-SolidWorks\\ADDIN\\.

\- ExportBom(asmDoc, exportRoot, partNo, rev) → assemblies ONLY; writes EXPORTS\\BOM\\{partNo}-R{rev}\_BOM.csv using the RAW PartNo (dots/dashes preserved, no dot-stripping). TOP-LEVEL BOM: components enumerated via activeConfig.GetRootComponent3(true).GetChildren() — GetRootComponent3(true) RESOLVES lightweight components (so their models load and properties read back non-empty) and GetChildren() gives top-level children only; AssemblyDoc.GetComponents(true) is a FALLBACK (it returned only the first component when others were lightweight). Each unique component (path + ReferencedConfiguration) is ONE row with a Qty count (repeated instances increment Qty); Purchased/Toolbox hardware IS listed (a BOM needs it). Suppression is tested with comp.GetSuppression2() == swComponentSuppressed, NOT comp.IsSuppressed() — IsSuppressed() wrongly reports LIGHTWEIGHT components as suppressed, which was dropping lightweight-loaded components from the BOM; lightweight/resolved/fully-resolved all count as PRESENT. A lightweight component's properties are read via GetReadableModel (GetModelDoc2 → already-open doc → read-only OpenDoc6 fallback, closed only if opened here). Columns: Item,PartNo,Description,Revision,Material,PartType,Qty — read via ReadProp which tries the referenced config → document level (configName "") → active config, so a Material1 stored in any scope is found (PartNo falls back to filename if unreadable). RFC-4180 CSV escaping (Csv helper, mirrors AuditLogger). Wrapped in try/catch — a BOM failure NEVER blocks the release. Called from ReleaseFile after the STEP export, gated on docType==swDocASSEMBLY.

\- Part/Assembly → STEP export to EXPORTS\\STEP\\



\### TaskPaneControl.cs

DPI-aware UserControl. Scale: g.DpiX/96f. S(v) = v\*\_scale.

Imports: requires using System.Linq (for history.Take(5)).

Rebuild hygiene (audit C4): the search results and history panels are rebuilt constantly (every search keystroke / doc / config switch) and MUST be torn down via the private ClearAndDispose helper — Controls.Clear() alone re-parents the removed controls to the hidden WinForms parking window where they keep their USER/GDI handles forever, marching SOLIDWORKS toward the 10,000-handle ceiling over a multi-day session. Card/history fonts are SHARED FIELDS (_fBold38/_fBold35/_fBold34/_fBold31/_fReg35/_fReg33/_fItalic33) created once in the ctor and disposed in the Dispose(bool) override together with _searchTimer (a Font assigned to a control is NOT owned by it — the old per-card new Font() leaked a handle per label per rebuild); the vertical status-bar font is one of them (was created on every Paint). One-time constructor fonts (header/section/label, assigned to permanent controls) are intentionally left as locals — bounded, not a marching leak. PendingRequestsForm is opened via using (was never disposed); the About dialog's fonts are disposed on its FormClosed.

Color palette:

\- cBrand(65,120,175), cBrandDark(44,85,128), cGreen(60,140,95)

\- cOrange(185,115,55), cPurple(105,100,165), cDark(75,80,90)

\- cRed(180,75,75), cMaroon(140,60,60), cSwRed(190,55,50) — muted SOLIDWORKS red



Sections (top to bottom):

1\. Dark header banner "BCore PDM"

2\. Search (auto-search 600ms timer, ≥2 chars, Enter key via ProcessCmdKey)

3\. Active File card (filename, status, partNo, revision, lockedBy). Status is HONEST about untracked files: a saved file with NO vault record shows "Untracked" (grey) — or "DUPLICATE NAME" (cSwRed) when a tracked file already owns that name (the post-save quarantine case) — instead of borrowing the same-named original's status; an unsaved brand-new doc still shows WIP. Multi-config: the filename shows a "(N configs)" suffix when the part/assembly has more than one configuration; partNo + revision reflect the ACTIVE config (config name = Part No), refreshed live on every config switch via the ActiveConfigChangePostNotify hook. configCount comes from PropertyValidator.GetConfigNames(doc)

4\. Master Actions (Open Drawing/Unlock/Release/New Revision/Rollback) — Masters only

5\. Engineer Actions (Request Unlock/Revision/Release, Open Drawing, My Requests) — Engineers only, same y-position as Master Actions via engY = y - S(5\*28)

Open Drawing button (both roles, cBrand) is CONTEXT-AWARE: DoAction("openlinked") opens the matching .slddrw when a part/assembly is active (creating one if none exists), or opens the referenced part/assembly when a drawing is active. Its label flips between "Open Drawing" and "Open Part/Assembly" in Refresh() based on the active doc type, kept in sync across both role variants by SetOpenLinkedLabel(). Replaced the Master Lock File button and the Engineer Update Drawings button.

Open Drawing button label when a drawing is active: VaultManager.GetDrawingOpenLabel(doc) checks the referenced model's extension — shows "Open Part" for .sldprt, "Open Assembly" for .sldasm, falls back to "Open Part/Assembly" if the reference can't be resolved.

6\. File History — Panel (\_historyPanel) with individual labels per entry, Height=S(300), y+=S(305)

7\. Pending Requests button (Masters only) — custom-painted (TextRenderer): "Pending Requests" CENTERED + count badge "(N)" right-aligned, drawn only when N>0 (no number when zero). \_pendingCount stored in Refresh(), button Invalidate()d to repaint. Opens PendingRequestsForm popup

7b. Vault Dashboard button (ALL users, cBrand) — OpenDashboard() runs a SINGLE-WINDOW VIEW-SWITCH LOOP between the Vault Dashboard and the Audit Report: it ShowDialog()s VaultDashboardForm; if the form sets SwitchToAudit (its "Audit Report »" button) the loop disposes it and opens AuditReportForm instead; if THAT sets SwitchToDashboard (its "« Vault Dashboard" button) the loop reopens the dashboard — so only ONE window is ever up (no modal stacking). The loop ends when a form is closed normally (Close/Esc) or — for the dashboard — with a file to open: VaultManager.OpenByPath(form.FileToOpen) opens that file (deferred until after the modal closes, mirroring OpenRequestsPopup); if form.FileToOpenConfig is set (Open Model on a config-specific drawing) it then ModelDoc2.ShowConfiguration2() to land on that config. Engineers get it too — both views are read-only and only open files (OpenByPath respects every vault rule), so no risk; its y-advance is unconditional while Pending Requests above it stays Master-only (the Pending Requests button AND its divider are both gated behind isMaster — otherwise an engineer saw an orphan divider line with no button above the dashboard). There is NO separate Audit Report task-pane button — the Audit Report is reached by switching from the dashboard, keeping the pane uncluttered.

8\. Send Test Email button (all users) — calls EmailManager.SendTestEmail, shows success/error in MessageBox

9\. Remove from Vault button (Masters only, cSwRed — muted SOLIDWORKS red) — DoAction("remove") → VaultManager.RemoveFromVault on the active file (moves to SCRAP + deletes record; blocked if Released)

Search results are capped at 50; when SearchFiles reports truncated=true a "Showing first N — refine your search" hint is rendered below the cards. SECOND cap at the CARD level (MaxCards=50 in RunSearch): SearchFiles caps at 50 FILES, but a multi-config part expands to one card PER configuration, so the card list is trimmed to MaxCards and truncated is forced true (the same hint shows) — prevents hundreds of cards freezing the panel even when only a few files matched.



File History uses PopulateHistoryPanel(List<HistoryEntry>) helper. Each entry: status label (StatusColor), date+user label, optional note label, 1px Panel divider. All labels have explicit Height.

No longer uses StringBuilder/AppendLine — individual labels prevent text overlap at small font sizes.



Search results: PER-CONFIG cards — ONE card per matching configuration (config name = Part No by convention, so a part with 10 configs can yield up to 10 cards, each carrying that config's own PartNo, Description and Revision — never the active config's). RunSearch calls BuildConfigCards(results, term): for each model file it calls AddModelConfigCards, which expands the file's Configurations list and emits a card for every config whose PartNo/Description contains the term (or every config when the file matched by filename, or it is single-config; never drops a matched file — falls back to all configs if none text-match). A drawing result maps back to its model via GetModelForDrawing and is SKIPPED if that model also matched (it is expanded under the model instead); a true orphan drawing (no model) gets a drawing-only card. Each config's drawing comes from DatabaseManager.GetDrawingsForConfig(modelPath, configName) (config-specific OR a shared config-table drawing; its basename fallback matches the raw config name OR its filename-sanitised form via SanitizeFileName, so a config-specific drawing whose PartNo contains a filename-illegal char — saved on disk with '_' substitutions — still resolves, keeping its rev in sync during New Revision). Each card shows: thick left status bar with the status text painted VERTICALLY (rotated -90°, custom Panel.Paint), file name (no extension), "PartNo   REV x" line, description, and TWO buttons side by side — "Open PRT"/"Open ASM" (cBrand; disabled+greyed when no model record, e.g. orphan drawing) and "Open DRW" (cBrandDark); abbreviated so labels don't clip at the narrow task-pane width. SearchGroup is a private nested class in TaskPaneControl, now per-config (carries ConfigName, Revision, TotalConfigs).

Open PRT/ASM → OpenFileConfig(modelPath, configName): opens (or activates) the model then switches it to the card's configuration via ModelDoc2.ShowConfiguration2. Open DRW → OpenDrawingResult(modelPath, drawingPath, configName): opens the drawing if it exists, else opens the model on the right config and calls VaultManager.OpenOrCreateDrawing to make one (same as the task-pane Open Drawing button).

Uses ActivateDoc3 if file already open, OpenDoc6 with correct type if not. Opens the canonical WIP copy (read-only when Released), never the RELEASED snapshot.



\### PendingRequestsForm.cs

DPI-aware Form (680×560 scaled). S(v)=v\*\_scale. Opened from PENDING REQUESTS button in task pane (via using — ShowDialog does not dispose). Doubles as the Master batch-action hub (keeps the task pane uncluttered). Rebuild hygiene (audit C4): PopulateSection tears its column down via the private ClearAndDispose helper (Controls.Clear alone parks the removed cards forever) and card fonts are fields (\_fCardBold/\_fCardSub/\_fCardPn/\_fCardBtn) created once and disposed in the Dispose(bool) override (NOT FormClosed — it never fires for a form disposed without being shown) — PopulateSection runs 3× per LoadRequests, after every approve/reject.

\- Three scrollable columns: Unlock | Revision | Release (categorised by RevisionRequest.RequestType)

\- Each column has a coloured header (orange/purple/green) and scrollable card list

\- Column headers are title-case ("Unlock Requests" etc.) with a live count; the internal type code stays UPPERCASE for routing/colour logic (display name mapped separately)

\- Card: selection checkbox (top-right, Tag=request), filename, PN + Revision line (from DatabaseManager.GetFileRecord; drawings fall back to GetModelForDrawing since props live on the model), requested-by, date, optional note, Approve + Reject buttons. Card layout is laid out with a running y-cursor so heights adapt to the optional PN/Rev and Note lines

\- Single Approve (ApproveSingle): confirms (Yes/No), then routes through VaultManager.BulkApprove(new[]{req}) — the SAME success-gated engine the batch buttons use, so it auto-opens the file (OpenByPath) and ONLY resolves the request when the action actually succeeded. A blocked release/revision stays pending instead of vanishing. (Previously it resolved the request BEFORE acting and required the file to be open — both fixed.) Single confirm also covers Unlock (which had no confirm before)

\- Single Reject (VaultManager.RejectRequest): prompts the Master for a rejection REASON (ShowNoteDialog) — that reason is emailed to the engineer (NOT their own original note echoed back); Cancel aborts

\- Legacy requests with no RequestType default to Revision column

\- "All" column checkbox and the card checkboxes are two-way synced (a \_syncing guard prevents recursion): ticking "All" checks every card; unticking any one card clears "All"

\- Batch actions:

  \- Per-column "All" select-all checkbox in each header + per-column "Approve Selected" button → VaultManager.BulkApprove(checked requests of that column). Label is "Approve Selected" (type-aware via BulkApprove), NOT "Release" — the Unlock/Revision columns approve their own action

  \- Green "Approve All Pending" (left half of bottom row) → confirms counts, then BulkApprove(all pending)

  \- Blue "Bulk Release - WIP" (right half of bottom row, equal width + S(5) gap) → opens BulkReleaseForm (releases any WIP file, not request-based). Bottom buttons anchored from client bottom (no dead space)

  \- Every batch action ends with ONE summary dialog (BatchResult.BuildSummary) then LoadRequests() refresh



\### BulkReleaseForm.cs

DPI-aware Form (ClientSize 560×600 scaled — sizes the CLIENT area so bottom buttons never clip), Master-only. Opened from the blue "Bulk Release - WIP" button in PendingRequestsForm.

\- Lists WIP (releasable) files from DatabaseManager.GetReleasableFiles(filter, out truncated) — full-width DYNAMIC search box (no Filter button): 600ms debounce timer, fires at ≥2 chars, clears back to all files immediately when emptied (mirrors the task-pane search). Placeholder via Win32 EM_SETCUEBANNER (PlaceholderText doesn't exist on .NET 4.8). Timer is stopped+disposed in the Dispose(bool) override

\- Rebuild hygiene (audit C4): LoadFiles reruns on every debounce tick and tears the list down via the private ClearAndDispose helper (Controls.Clear alone parks the removed cards forever); card fonts are fields (\_fCardBold/\_fCardSub/\_fCardSubBold/\_fCardTag/\_fCardMeta) created once and disposed in the Dispose(bool) override alongside the timer (NOT FormClosed — it never fires for a form disposed without being shown)

\- "Select all" checkbox two-way synced with the card checkboxes (\_syncing guard): unticking one card clears "Select all"

\- Scrollable cards: checkbox + filename (no ext) + right-aligned type tag (SLDPRT=blue / SLDASM=orange / SLDDRW=purple) + a labelled "**PN:** value    **DESC:** value" line (labels bold via AddInlinePair, which measures text width so the DESC pair butts up after the PN value; only the last value fills remaining width with ellipsis) + "Modified by X · date" metadata. A drawing inherits PN/Description from its model (GetModelForDrawing); a genuine orphan with no PN shows an amber "(no part number)" hint. count label with "(first 50)" when truncated

\- "Release Selected" → confirms, then VaultManager.BulkRelease(checked paths) → one summary dialog → reloads (released files drop out, no longer WIP)

\- Distinct from request-based approve: these files need not have any pending request



\### VaultDashboardForm.cs

DPI-aware Form (S(v)=v\*\_scale, fonts = pt\*\_scale), ALL users (read-only). Opened from the "Vault Dashboard" task-pane button. Full-screen whole-vault status view. RESIZABLE (FormBorderStyle.Sizable, MaximizeBox) — ClientSize S(1120)×S(680), MinimumSize S(720)×S(460).

\- The ONE place in the app that uses a real DataGridView (every other list is hand-drawn Panels). Columns (in order, index): File Name(0), Part No(1), Description(2), Status(3), Rev(4), Modified By(5), Modified Date(6), Released By(7), Released Date(8), WIP Days(9). Read-only, dark header, alternating row colour, Status cell coloured by StatusColor (Released=green, Locked=maroon, WIP=orange) in a shared bold font; broken-ref rows show the File cell in red with a tooltip; a Locked Status cell tooltips "Locked by {LockedBy}". Drawings show their model's PartNumber/Description (filled by GetAllFiles). Released By = ChangedBy of the latest "Released" history entry (the releaser, not the locker). WIP Days (ColWipDays=9, right-aligned, appended last so indices 0–8 stay fixed) = WipDays(f): days since last modified for WIP files (blank for non-WIP, -1 internally — as the smallest value blanks sort to the TOP ascending / BOTTOM descending, so sorting the column descending surfaces the stalest real WIP files first); KeySelector sorts it numerically, OrderFilterValues orders its filter list numerically.

\- KEYBOARD NAV (ProcessCmdKey, works regardless of focus): PageDown/PageUp = next/prev page, Ctrl+Home/Ctrl+End = first/last page, Ctrl+F = focus+select the search box, Enter = open the selected grid row (or, in the search box, apply the search now), Esc = close. OpenSelectedRow maps \_grid.CurrentCell.RowIndex through PageStart like every other consumer.

\- VIRTUALMODE for scale (built for a 50–100k-file vault). The grid holds NO DataGridViewRow objects; it renders straight from the \_view list (filtered+sorted) and pulls data for ONLY the visible cells via three providers: CellValueNeeded (the cell value — real DateTime for date cols so the column Format applies, DBNull for blanks), CellFormatting (Status colour+bold, broken-ref File-name red — applied per visible cell, NOT baked into row objects), CellToolTipTextNeeded (broken-ref tooltip; returns early for headers so their HeaderCell.ToolTipText survives). All three providers map the grid row index to the backing list via PageStart + e.RowIndex (see pagination below). ApplyFilter just rebuilds \_view, resets to page 1 and calls ShowGridPage — O(visible), instant even on every keystroke at 100k rows. Double-click + CSV export read straight from \_view; double-click maps through PageStart, CSV uses CellText per column over the WHOLE filtered \_view (all pages, no materialised rows to read).

\- PAGINATED 20 rows per page (PageSize = VisibleRows = 20, the "20 row rule"). \_view holds the full filtered+sorted list; the grid's RowCount is just the current page's slice (≤ PageSize) and CellValueNeeded/CellFormatting/CellToolTipTextNeeded/double-click index \_view[PageStart + e.RowIndex] where PageStart = \_page \* PageSize. PageCount = ceil(\_view.Count / PageSize) (min 1). ShowGridPage clamps \_page, clears CurrentCell, sets RowCount to the page slice, repaints, rebuilds the pager AND refreshes the summary (UpdateSummary folded in so the "Page X of Y" indicator can never desync — callers no longer call it separately); GoToPage(page) is the click handler (it clamps the target and early-returns when it equals the current page, so a dead-end PgUp/PgDn at the first/last page skips a wasteful pager rebuild). Any new filter/sort/search/Clear resets to page 1 (ApplyFilter sets \_page=0). Because a page never exceeds VisibleRows the grid never needs a vertical scrollbar. The PAGER lives in the bottom panel (rebuilt by BuildPager on every page move + filter + bottom-panel resize): First · ‹ · numbered page buttons with ellipsis (PageTokens: ≤7 pages shows all, else 1 … window-of-3-around-current … last) · › · Last. The current page is a boxed white button (cBorder); the others are plain cBrand links; First/Last/arrows grey out and go inert at the ends. Pager buttons are sized to their text (TextRenderer.MeasureText, \_pagerFont) and centred in the bottom panel left of a reserved Close-button zone (S(140)). \_pagerControls tracks them for disposal/rebuild; \_pagerFont disposed AND nulled on FormClosed (BuildPager early-returns when \_pagerFont==null, so a \_bottomPanel.Resize firing during teardown can't MeasureText on a disposed font).

\- Column widths are CONTENT-FIT but measured from a BOUNDED SAMPLE: AutoSizeColumnsMode=None, then AutoSizeColumns() measures each column's header (\_cellBold) + up to WidthSampleRows (400) data rows via TextRenderer.MeasureText, ×1.20 + GlyphZone + padding, clamped S(56)..S(540). O(sample) not O(rows) — GetPreferredWidth(AllCells) would measure 100k×9 cells (and doesn't work in VirtualMode). Columns are user-resizable, so the rare extra-wide value past the sample can be widened by hand.

\- The "Modified Date" and "Released Date" columns are TYPED DateTime columns (ValueType=DateTime, DefaultCellStyle.Format="MM/dd/yyyy HH:mm") so they sort CHRONOLOGICALLY (a string date would sort alphabetically); a missing date is DBNull (empty cell, sorts earliest). CSV export uses cell.FormattedValue so dates export as displayed.

\- EXCEL-STYLE per-column filtering. Every column SortMode=Programmatic; the header is CUSTOM-PAINTED in CellPainting (solid cBrandDark fill, white header text with EndEllipsis, a filter arrow at the far right, a faint right divider, and — on the sort column — a small ▲/▼ glyph left of the arrow). The arrow is tinted cFunnel (gold) when that column has an active filter, so active filters are visible at a glance. ColumnHeaderMouseClick (left button) splits the header click: a click in the right GlyphZone (S(20)) opens that column's filter popup, a click anywhere else toggles the sort (ToggleSort: same column flips Asc↔Desc, else new column Asc). Sorting is done IN DATA (KeySelector — date columns sort by the real DateTime, all others by lower-cased display text) and re-applied by ApplyFilter so it survives a re-filter; \_grid.Invalidate() repaints the glyphs. DEFAULT sort = Modified Date DESCENDING (DefaultSortColumn=6) so the freshest work is on top; "Clear Filters" resets to this default (not to no-sort).

\- The filter list NARROWS like Excel: ShowColumnFilter shows only the values present in rows that pass every OTHER active column filter + the global search (KeysPassingOtherFilters), ordered by OrderFilterValues (date columns chronologically, others alphabetically). Values this column currently allows but that are hidden by another filter (hiddenAllowed) are PRESERVED — folded back into the committed set so removing the other filter later never silently drops them. "No filter" = the committed allowed set covers ALL of the column's distinct keys (allKeys); otherwise \_colFilters[col] = that set.

\- DATE columns are GROUPED to DAY granularity for filtering (FilterKey): the popup lists distinct days ("MM/dd/yyyy", InvariantCulture, chronological; blank → "(Blanks)") instead of every minute, and ticking a day keeps every time on that day. All OTHER columns filter on their display text. FilterKey (not CellText) is what \_colFilters stores and what ApplyFilter matches; CellText stays the grid's display/sort/global-search source. Date display in the grid is still minute-precision ("MM/dd/yyyy HH:mm").

\- The filter popup is ColumnFilterDialog (nested class) — a searchable CheckedListBox of the (already narrowed) values it's given (blank → "(Blanks)"), with its own search box, Select All / Clear, and OK / Cancel. The RENDERED list is CAPPED at DisplayCap (2000) items so a high-cardinality column (e.g. 100k file names) never builds a 100k-item control; a count label shows "N of M shown — type to narrow" when capped. \_state (value→checked, OrdinalIgnoreCase) covers EVERY value (not just rendered ones) and persists across the in-popup search, so Commit and check state stay correct beyond the cap; ItemCheck is detached during RebuildList to avoid feedback. Select All / Clear (SetMatched) flip every value MATCHED by the in-popup search — NOT just the rendered ≤DisplayCap subset — via the shared MatchesTerm predicate (also used by RebuildList, so the matched set and the list can't drift): iterating \_visibleRaw flipped only the first 2000 entries on a capped column, so "Clear, then tick one" left the other ~98k checked and committed a filter that quietly allowed almost everything (audit C5; identical fix in AuditReportForm's private copy). OK while a search term is active commits the CHECKED MATCHES ONLY (Excel's "filter to search results", via Commit() filtering on MatchesTerm): values outside the search keep whatever checked state they had from before the term was typed, and folding them in meant "search, tick one, OK" still showed every unmatched file — the count label reads "N matches — OK filters to ticked" while a term is active so the semantics are visible ("No matches" when the term matches nothing). OK is DISABLED (greyed, UpdateOkEnabled — wired through RebuildList + ItemCheck) while zero matches are ticked: committing would write an EMPTY filter and blank the whole grid (a typo'd term + Enter — OK is the AcceptButton — or Clear + OK), so it can't be clicked until at least one match is ticked, like Excel. Commit() returns SelectedValues = the checked (and matched, when a term is active) subset (never null); the caller (ShowColumnFilter) folds in hiddenAllowed and decides "no filter". Anchored under the clicked header arrow, clamped to the screen working area. Reuses VaultDashboardForm.SetCueBanner.

\- Active filters live in \_colFilters (column index → allowed FilterKey HashSet, OrdinalIgnoreCase); a column absent = unfiltered. ApplyFilter keeps a row only if it passes EVERY \_colFilters entry (FilterKey(f,col) ∈ set) AND the global search term.

\- Control row (search box, Refresh, Export CSV, "Clear Filters") is height-matched to the textbox's font-derived PreferredHeight so the row lines up. "Clear Filters" resets \_colFilters + sort + the search box. The summary strip sits below with the panel tightened to it (no dead space). The header label reads "BCore VAULT DASHBOARD". The title, control row and summary are CENTER-aligned horizontally via LayoutTopControls (called on load + every top-panel resize).

\- Form is SIZED TO FIT THE COLUMNS (FitFormSize, called once per LoadData): client width = sum of content-fit column widths + chrome, capped at 80% of the screen working area (MaxScreenFraction); min S(800) so the top row stays visible. Height is CONSTANT = ColumnHeadersHeight + 20×RowTemplate.Height + panels (VisibleRows=20) — a short final page just leaves blank space below; also capped at 80% of screen height. Pages cap at VisibleRows rows so normally NO vertical scrollbar appears (no width reserved for one); BUT if the 80%-height cap bites (small screen / high DPI) the grid would scroll, so FitFormSize detects that clamp (needsVScroll) and then DOES reserve the scrollbar width so the last column never clips under it. Column widths + form size are computed ONCE per load (from the full dataset, unfiltered) so the form does NOT resize while the user types in the search box.

\- Data from DatabaseManager.GetAllFiles() (read-only snapshot, all statuses, no orphan purge). Fetched once into \_all; the global search box (300ms debounce timer, matches PartNo/Description/FileName) + the per-column Excel-style filters all filter \_all IN MEMORY via ApplyFilter (no re-query).

\- SUMMARY STRIP = CLICKABLE QUICK FILTERS, not a static label. A FlowLayoutPanel of count "links" (cBrand, hand cursor, tooltip) centred as one unit by LayoutTopControls: Total (whole vault — click = Clear Filters), WIP / Released / Locked (click = toggle the Status column filter to that one value via ToggleStatusFilter → \_colFilters[3]; clicking the active one again clears it), Broken Refs (click = toggle \_brokenRefsOnly), then a plain grey "(Showing from–to of N · Page X of Y · as of HH:mm)" label — the "as of" stamp is \_loadedAt (the snapshot time, set in LoadData) so a Master can see how stale the view is (the grid is a point-in-time snapshot; Refresh re-reads). The ACTIVE quick-filter is underlined (\_summaryFontActive). Status quick-filters share \_colFilters[3] with the Status header dropdown, so the header funnel glyph lights up too; IsStatusFilter detects "filtered to exactly this value"; clicking a quick-link sets the Status filter to exactly that value (collapsing any multi-value dropdown selection — intended: "show only X"). \_brokenRefsOnly is a SEPARATE non-column filter (HasBrokenRefs is a flag, not a column) honoured by both ApplyFilter and KeysPassingOtherFilters; Clear Filters resets it. The whole-vault counts (\_cntWip/\_cntRel/\_cntLck/\_cntBrk) are CACHED once per load by ComputeVaultCounts (invariant under filtering) instead of re-scanning \_all on every keystroke/page. A faint discoverability hint footer (\_lblHint) sits below the counts ("Double-click or right-click a row to open · Drag a column edge to resize · Click a count to filter · PgUp/PgDn to page"), also centred by LayoutTopControls. (There is intentionally NO background auto-refresh — many client machines polling a 100k-file vault.xml would add network load, and a reload would disrupt active filtering; the manual Refresh button + the "as of" stamp cover staleness.)

\- Double-click a row → OpenDeferred(FilePath): sets FileToOpen + DialogResult.OK + closes; the caller (TaskPaneControl.OpenDashboard) then VaultManager.OpenByPath(FileToOpen) AFTER the modal closes (opens the canonical WIP copy, read-only when Released). Deferred-open mirrors OpenRequestsPopup.

\- ROW RIGHT-CLICK MENU (ContextMenuStrip, Grid_MouseDown on MouseButtons.Right): Open · Open Drawing/Open Model · Copy File Path · Open Containing Folder. HitTest finds the row, selects it, and the linked item flips Drawing↔Model by the row's extension. The linked path is resolved IN-MEMORY from \_all (FindLinkedPath, NO per-click DB/disk hit, honouring GetAllFiles' no-disk-per-file design): drawing→model uses the drawing's ReferencedModel link FIRST (so a config-specific {configName}.slddrw — whose basename differs from the model, e.g. DEMO.05.slddrw documenting "FILE 1.sldprt" config DEMO.05 — still resolves), then the shared {basename} convention; model→drawing prefers the shared {basename}.slddrw, else any drawing whose ReferencedModel points back. The item is disabled only when none exists. Open / Open linked go through OpenDeferred (same deferred-open as double-click); Copy File Path → Clipboard.SetText (swallows clipboard-busy); Open Containing Folder → explorer.exe /select,"path" (falls back to the directory, then a message). The menu is styled with a custom flat MenuRenderer + MenuColors (white drop-down, brand-blue hover, no image gutter, house font) instead of the dull grey OS menu. \_rowMenu disposed on FormClosed.

\- OPEN MODEL ON THE DRAWING'S CONFIG: "Open Model" on a config-specific drawing also lands the model on the configuration that drawing documents. DrawingConfigToOpen derives it from the drawing's ReferencedConfigs when that names a SINGLE config, else from a config-specific {configName}.slddrw filename (basename ≠ the model's); a shared/all-config drawing yields null (open at active config). It is carried out of the modal via FileToOpenConfig (alongside FileToOpen); TaskPaneControl.OpenDashboard, after VaultManager.OpenByPath, calls ModelDoc2.ShowConfiguration2(FileToOpenConfig) — best-effort, so a stale/illegal config name simply no-ops.

\- "Export CSV" → SaveFileDialog → dumps the WHOLE filtered \_view (all pages, RFC-4180 Csv helper, headers + CellText per column). "Refresh" → re-runs GetAllFiles. "Close" → closes. Disposed on FormClosed: search debounce Timer, \_cellBold, \_pagerFont, \_summaryFont, \_summaryFontActive, \_summaryTip, \_rowMenu. Placeholder via Win32 EM_SETCUEBANNER (no PlaceholderText on .NET 4.8).



\### AuditReportForm.cs

DPI-aware Form (S(v)=v\*\_scale, fonts=pt\*\_scale), ALL users (read-only). NOT opened from its own task-pane button — reached by SWITCHING from the Vault Dashboard (the dashboard's "Audit Report »" button) and switches back via its own "« Vault Dashboard" button (a top-row button next to Clear Filters). The switch is a single-window view swap orchestrated by TaskPaneControl.OpenDashboard's loop: each form exposes a bool (VaultDashboardForm.SwitchToAudit / AuditReportForm.SwitchToDashboard) that it sets before closing; the loop reopens the other form, so only one window is ever up (no modal stacking). The COMPANION to the Vault Dashboard: the dashboard shows the CURRENT state of every file; this shows the HISTORY of events over time, read from N:\\PDM-SolidWorks\\VAULT\\audit.csv. Built on the SAME proven plumbing as VaultDashboardForm (VirtualMode DataGridView, PAGINATED 20 rows/page with the First·‹·numbered·›·Last pager, EXCEL-STYLE per-column filtering, global search, clickable-count summary strip, keyboard nav). Self-contained (own palette, S(), SetCueBanner, and a private copy of ColumnFilterDialog — the house "one form, one file" convention). The log is the source of truth; the report NEVER writes it (only reads + exports the filtered view to CSV).

\- Columns (index): Timestamp(0, typed DateTime "MM/dd/yyyy HH:mm:ss" so it sorts chronologically), User(1), Action(2), File Name(3), Part No(4), Rev(5), Note(6). DEFAULT sort = Timestamp DESC (freshest event on top). Action cell is COLOUR-CODED by category via ActionColor (Release/ApproveRequest=green, NewRevision=cBrand, Rollback=orange, RemoveFromVault/RejectRequest=red, Lock/Unlock=maroon, Request\*=purple, Create/Save/AutoPurgeOrphan/else=grey) in a shared bold font.

\- ReadAuditLog() reads the whole audit.csv ONCE (FileStream FileShare.ReadWrite so an in-progress AuditLogger append or the file open in Excel never blocks the read; missing file = empty report) and ParseCsv() turns it into List<AuditEntry>. ParseCsv is a proper RFC-4180 parser (handles quoted fields, escaped "" quotes, and commas/newlines INSIDE quotes — the Note field can contain them), mirroring AuditLogger.Csv's escaping. The header row (first record, field 0 == "Timestamp") is skipped, as is any all-empty record (a stray blank line parses to a single empty field — every real event carries a Timestamp, so an all-empty row can only be a blank line, never a phantom report row). ParseTimestamp parses AuditLogger's "yyyy-MM-dd HH:mm:ss" (then a lenient fallback, else DateTime.MinValue → blank cell). Reading it all is fine — a report can afford it; rendering stays cheap because the grid is VirtualMode + paginated.

\- FilterKey groups the Timestamp column to DAY granularity ("MM/dd/yyyy") so the column filter lists distinct days (date-range filtering) not every second; KeySelector sorts Timestamp by the real DateTime, others by lower-cased text; OrderFilterValues orders the Timestamp filter list chronologically. Global search matches User/Action/FileName/PartNo/Note. ApplyFilter resets to page 1; ShowGridPage folds in UpdateSummary; GoToPage early-returns on a dead-end page move; \_pagerFont nulled after dispose (same hardening as the dashboard).

\- Summary strip = clickable count quick-filters: Total (click = Clear Filters), Releases / Revisions / Removals (click = toggle the Action column filter to "Release"/"NewRevision"/"RemoveFromVault" via ToggleActionFilter → \_colFilters[ColAction]; the active one is underlined and its header funnel lights up), then a plain "(Showing from–to of N · Page X of Y · as of HH:mm)" label. Counts (\_cntRelease/\_cntRevision/\_cntRemoval) cached once per load by ComputeCounts. NO row-open / context menu (it's a log, not a file browser — opening files is the dashboard's job). Export CSV dumps the whole filtered \_view; Refresh re-reads audit.csv. Disposed on FormClosed: search debounce Timer, \_cellBold, \_pagerFont(+null), \_summaryFont, \_summaryFontActive, \_summaryTip.



\### TaskPaneHost.cs

\- Register/Unregister task pane

\- CreateTaskpaneView2(CreateIcon(), "BCore PDM")

\- CreateIcon() generates BC icon BMP in temp folder

\- Handles ActiveDocChangeNotify → calls RefreshPanel()

\- Unregister DISPOSES the TaskPaneControl after DeleteView (and nulls it so the doc-change handlers no-op) — DeleteView alone destroys only the native pane, orphaning the managed control tree + fonts + armed search timer on every add-in unload/reload; without this the control's Dispose(bool) never ran



\### EmailManager.cs

Sends email notifications via SMTP (company uses Mailgun: smtp.mailgun.org:587, sender bcorepdm@mg.richardswilcox.com). Non-fatal — all sends wrapped in try/catch; failure never blocks workflow.

Config file: N:\\PDM-SolidWorks\\VAULT\\email.config (XML, created on first addin load if missing)

\- Enabled = true/false toggle

\- SmtpServer, SmtpPort (587), SenderEmail, SenderPassword (SMTP password), EmailDomain

\- Email addresses derived as {username}@{EmailDomain}

Methods:

\- EnsureConfigTemplate() → creates template email.config if not present (called in ConnectToSW)

\- NotifyRequestSubmitted(type, fileName, partNo, rev, requester, note) → emails both Masters

\- NotifyRequestApproved(type, fileName, requestedBy) → emails the engineer

\- NotifyRequestRejected(type, fileName, requestedBy, note) → emails the engineer

\- SendTestEmail(out success) → diagnostic; sends to SenderEmail itself, returns human-readable result string (surfaces real SMTP error instead of failing silently). Wired to "Send Test Email" button in task pane.

Trigger points:

\- VaultManager.RequestRevision/Unlock/Release → NotifyRequestSubmitted

\- VaultManager.ApproveRequest → NotifyRequestApproved

\- VaultManager.RejectRequest → NotifyRequestRejected

\- PendingRequestsForm Unlock/Release direct approve paths → NotifyRequestApproved



\### AuditLogger.cs

Append-only CSV audit trail at N:\\PDM-SolidWorks\\VAULT\\audit.csv (separate from vault.xml so the DB stays lean; opens directly in Excel).

\- Log(action, user, fileName, partNo="", revision="", note="") → appends one row. Columns: Timestamp,User,Action,FileName,PartNo,Revision,Note

\- Append-only (never rewrites the whole file) → stays fast at any size. Cross-process safe: opens with an exclusive write handle (FileShare.Read) and retries 5×100ms on a sharing violation (two machines writing at once). Non-fatal — every failure swallowed so logging never blocks a workflow. CSV fields RFC-4180 escaped.

\- Actions logged: Create, Save (UpsertFile), Lock, Unlock, Release, NewRevision, Rollback, RemoveFromVault, AutoPurgeOrphan (user="system"), RequestRevision/Unlock/Release, ApproveRequest, RejectRequest, ReleaseFailed / NewRevisionFailed (aborted operations, with reason), DuplicateNameDetected (a first save / Save As landed under a taken name — file NOT tracked), VaultFileOverwritten (a Save As replaced a tracked WIP file's geometry on disk — record updated to match disk, loud warning shown), LockDegraded / VaultSaveFallback / VaultRestoredFromBackup (DB-layer health events, user="system", throttled to one per event type per 5 min per process)



\### RollbackDialog.cs

DPI-aware Form. S(v)=v\*\_scale.

\- Shows archived revisions from ARCHIVE\\{type}\\ folder

\- Sorted most recent first

\- Restore button per revision

\- ExtractRevision() parses "FileName REV X.ext"



\### ConfigRevisionPickerForm.cs

DPI-aware Form. S(v)=v\*\_scale. Opened by VaultManager.StartNewRevision for multi-config parts/assemblies.

Styled to house convention (brand title bar, 3.7–6f ×\_scale fonts, flat coloured buttons, white bordered CheckedListBox) matching DrawingScopeDialog / PendingRequestsForm.

\- Brand title bar (cBrandDark), body/list font 3.7f × \_scale, title font 6f × \_scale Bold

\- CheckedListBox: white background, BorderStyle.FixedSingle, IntegralHeight=false (no clipping at any DPI)

\- All configs pre-checked; Master unchecks configs whose drawing did NOT change

\- Each item shows: "ConfigName   (REV A  →  REV B)"

\- Buttons: All (cBrand), None (cDark), OK (cGreen), Cancel (cDark) — all flat, white text

\- Helpers: SetAll(bool) + MakeButton(...) to keep layout code concise

\- Public API: ConfigRevisionPickerForm(List<string> configNames, List<string> currentRevs, List<string> nextRevs) + SelectedConfigs (null if cancelled)



\### DrawingScopeDialog.cs

DPI-aware Form. S(v)=v\*\_scale. Opened ONCE when a multi-config part/assembly gets its FIRST drawing via OpenOrCreateDrawing.

Lets the user decide whether the new drawing is shared by all configurations ({modelBasename}.slddrw) or specific to the active configuration ({configName}.slddrw). After this choice the filename on disk carries the decision so the prompt never repeats.

Styled to house convention: brand title bar (cBrandDark), body 3.7f × \_scale, option labels 3.9f Bold, hints 3.1f, buttons 3.6f Bold.

\- RadioButton: "Common drawing (one for ALL configurations)" — default checked

\- RadioButton: "This configuration only ("{activeConfig}")"

\- Hint labels beneath each option explain the trade-off

\- Buttons: OK (cGreen), Cancel (cDark) — flat, white text, anchored to client bottom

\- Result enum: Scope { Cancel, Common, PerConfig }; accessed via Result property



\## Key Technical Decisions

\- SQLite abandoned → XML (System.Xml.Linq) due to native DLL conflicts

\- Event names: FileSaveNotify (re-saves) + FileSaveAsNotify2 (first save / Save As). MUST use FileSaveAsNotify2 NOT the legacy FileSaveAsNotify — only Notify2 honours the return value to abort a new-file save. NOT FileSavePreNotify.

\- FileSavePostNotify parameters: (int saveType, string FileName) - ORDER matters

\- DestroyNotify() has no parameters

\- Configuration properties via doc.GetActiveConfiguration() as Configuration

\- Drawings use configName="" for properties (no configurations)

\- DPI scaling: \_scale = g.DpiX / 96f, all sizes use S(v) = (int)(v \* \_scale)

\- Fonts: new Font("Segoe UI", Xf \* \_scale) — NOT S() for font sizes

\- OS read-only set on release, removed on unlock/new revision

\- Released files are locked for EVERYONE (incl. Masters) — must use New Revision or Unlock to edit; enforced in ValidateSave, bypassed for internal saves via SuppressSaveValidation

\- Release copies the SW file to RELEASED via delete-then-copy (overwriting a read-only file on the network share fails and left stale copies); copy failures are surfaced, not swallowed

\- Release CLOSES the released file afterwards (does NOT reopen it read-only) — a released file is pure output, so reopening it only wastes load time/memory and makes the user wait. It opens read-only on demand next time it's actually needed. Applies to single AND bulk release. If a part/assembly's drawing is open it holds a reference and blocks CloseDoc(model), so Release closes the drawing first; for an interactive single release it then reopens that drawing ONLY if it is still WIP (the user's working file) — a Released drawing is never reopened, and in bulk/chained releases (suppressPrompts) nothing is reopened. CHAINED RELEASE (drawing triggers model release): the model's own ReleaseFile attempts CloseDoc(model) but SOLIDWORKS refuses while the drawing holds a reference; after the drawing release closes the drawing, the drawing's close block explicitly CloseDoc(referencedModel) so both files close. referencedModel is hoisted to function scope (set inside the isDrawing gate, accessible in the close block). New Revision still closes+reopens (the file must stay open and writable to keep editing)

\- No COM auto-registration (no admin rights) — manual IT registration



\## Registration

\- GUID: {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}

\- Registry: HKLM\\SOFTWARE\\SolidWorks\\AddIns\\{GUID}

\- DEPLOY (build machine): DeployPDMLite.bat (repo root) copies the RELEASE build (PDMLite\\bin\\Release\\PDMLite.dll + PdfSharp.dll) plus the registration files to N:\\PDM-SolidWorks\\ADDIN\\. Aborts if the Release output is missing (Debug builds are never deployed) or the copy fails (PDMLite.dll is locked while any machine runs SOLIDWORKS with the add-in loaded — deploy after hours). Already-registered PCs pick the new DLL up on next SOLIDWORKS start; no re-registration needed while AssemblyVersion stays 1.0.0.0.

\- INSTALL (each engineer PC): IT runs N:\\PDM-SolidWorks\\ADDIN\\InstallPDMLite.bat as Administrator. Registers the DEPLOYED N:\\PDM-SolidWorks\\ADDIN\\PDMLite.dll (never a dev-machine bin\\Debug path — audit C6: the old installer pointed RegAsm at D:\\...\\bin\\Debug, which exists only on the dev PC, and wrote the SOLIDWORKS keys even when RegAsm failed, leaving a broken add-in entry). FAILURE-ORDERED: checks admin (net session), DLL exists, RegAsm exists, then RegAsm /codebase, and only writes the HKLM SOLIDWORKS keys after RegAsm SUCCEEDS (errorlevel checked) so a failed install never leaves a half-registered add-in. Warns (non-fatal) if PdfSharp.dll is missing (PDFs would release un-watermarked). Elevated sessions often lack the per-user N: mapping, so the ADDIN folder can be overridden as arg 1 with a UNC path: InstallPDMLite.bat "\\\\server\\PDM-SolidWorks\\ADDIN".

\- RegisterPDMLite.reg = manual machine-wide fallback (admin) when RegAsm is unavailable; RegisterPDMLite\_Old.reg = per-user HKCU variant (NO admin). Both CodeBase entries point at N:\\PDM-SolidWorks\\ADDIN\\PDMLite.dll. INVARIANT: their hardcoded "Version=1.0.0.0" must match AssemblyVersion in Properties\\AssemblyInfo.cs (pinned 1.0.0.0, no wildcard) — bump the version and the .reg files must be updated (or just rerun InstallPDMLite.bat, which derives metadata from the DLL).

\- LINE-ENDING INVARIANT: all .bat/.reg files are committed with literal CRLF and .gitattributes pins them `-text` (no git eol conversion) — cmd.exe's goto/label scanning misbehaves in LF-only batch files and regedit expects CRLF imports, so a checkout with any core.autocrlf setting (or a GitHub zip download) must still yield CRLF. The installer's reg add calls pin /reg:64 so the SOLIDWORKS keys land in the 64-bit registry view even if run from a 32-bit cmd.



\## Workflows



\### Canonical File Location (WIP-only model)

There is ONE home for every vault file: N:\\PDM-SolidWorks\\WIP. Engineers save there from the start.

\- The DB tracks the WIP path as the canonical FilePath. There is exactly one vault.xml entry per file.

\- RELEASED holds read-only published snapshots — pure OUTPUT, never opened for editing.

\- Release copies WIP → RELEASED and freezes BOTH (read-only). The DB status on the WIP path becomes Released.

\- Unlock / New Revision remove read-only from the WIP copy so editing resumes on WIP. RELEASED stays untouched until the next Release overwrites the snapshot.

\- Search returns only Released files and opens the canonical WIP copy (so task-pane actions operate on the managed file, not a frozen snapshot). The WIP copy is OS read-only while Released, so SOLIDWORKS opens it read-only for viewing.

\- ValidateSave warns (Yes/No override) if a file is saved outside WIP — keeps the single-location model intact.



\### Engineer Save

FileSaveAsNotify2/FileSaveNotify → check lock → check released → 

warn if outside WIP folder → duplicate FILE NAME hard block (vault-wide, all doc types, no override) → validate properties (show PropertyForm if missing) → 

auto-weight → (multi-config) unique PartNo per config (block) + combined per-config health warn (name==PartNo + completeness) → duplicate part number → check refs → allow save → post-save updates DB



\### Master Release (Part/Assembly)

validate ALL configs (ValidateAllConfigs) → offer ONE multi-config PropertyForm covering EVERY config's missing fields (grouped by field, one row per config — no per-config activate-and-retry loop) → check broken refs →

(assembly) check child parts Released → (assembly) drawing-release gate →

confirm dialog: single-config shows PartNo/Rev; multi-config lists all configs with PartNo + Rev →

auto-fill CheckedBy, CheckedDate, PartWeight on EVERY configuration →

archive old exports + export files:

\- Single-config: ArchiveOldExports(partNoClean) + ExportAll (STEP + DXF)

\- Multi-config: archive per-config → ExportStepOnly per config (switch config before each) → restore active config → ExportFlatPatternOnly once (sheet metal DXF for original active config)

→ copy to RELEASED → set read-only → update DB

Success dialog lists all configs (PN + Rev) for multi-config files.



\### Master Release (Drawing)

check referenced part is Released → if NOT Released: ONE Yes/No prompt to release BOTH

files now (lists model + drawing) → Yes: open model if needed → ReleaseFile(model,

suppressPrompts:true) (all validations still apply, but its own confirm + success

dialogs are suppressed) → if model still not Released after that, abort drawing release;

re-fetch drawing doc (model release closes/reopens the drawing) → (if referenced model is

an assembly) check all assembly components are Released → sync drawing revision with part

revision → export PDF (all sheets) → copy to RELEASED → set read-only → update DB → ONE

combined success dialog ("Both files Released Successfully")

\- Chained release = exactly one confirmation + one success popup for the model+drawing

pair (no per-file confirm/success). ReleaseFile(doc, suppressPrompts=false) — when true,

skips its confirm + success dialogs (used for the chained model release); validation and

blocker dialogs still show.



\### Master New Revision

\- Single-config: confirm dialog (current REV → new REV) → remove read-only → archive SW file to ARCHIVE\\{type}\\ → bump revision letter → save → reset to WIP in DB → StartDrawingRevisionWith (basename drawing) → warn about parent assemblies

\- Multi-config: ConfigRevisionPickerForm (checklist, all configs pre-checked, current→next per config) → Master picks which configs to bump → archive SW file (file-level, at active config's currentRev) → reopen writable → loop: SetProperty(Revision, nextRevForConfig, cfgName) for each selected config → save → reset to WIP → drawing revision: collect unique drawings (FindDrawingPath for shared + GetDrawingsForConfig per selected config) → StartDrawingRevisionWith for each unique drawing → warn parent assemblies

\- suppressPrompts (bulk approve): skips picker, bumps ALL configs automatically

\- Archive naming: file-level (one SW archive per revision bump, regardless of config count). Named "{basename} REV {activeConfigRev}.ext". COLLISION GUARD via shared helper CollisionSafeArchivePath(dir, baseName, rev, ext, multiCfg) — used by BOTH New Revision AND Rollback's archive-current steps (model + drawing): for a multi-config file a partial bump can leave the active config's rev unchanged, and a rollback can hit a rev a New Revision already archived, so two different file snapshots would map to the same name and ArchiveCopy (delete-then-copy) would silently overwrite the earlier one. When the target already exists, a yyyyMMdd_HHmmss stamp is inserted into the basename BEFORE " REV " ("{basename}_{stamp} REV {rev}.ext") so both snapshots survive AND RollbackDialog.ExtractRevision (parses the token after " REV ") still reads the rev letter. Single-config keeps the old overwrite behaviour (re-archiving the same rev is harmless)

\- StartDrawingRevisionWith(modelPath, currentRev, nextRev, user, explicitDrwPath=null) — optional explicit drawing path lets multi-config path pass a pre-resolved drawing (skips FindDrawingPath)



\### Drawing / Assembly linkage (Option B — automatic)

\- Drawing filename conventions: single-config uses {modelBasename}.slddrw (shared); multi-config ALSO supports {configName}.slddrw (config-specific, takes priority over shared). Both patterns can coexist — a shared drawing covers all configs; a config-specific drawing covers only that config. PDF export name still comes from the DrawingNo property.

\- Part rev drives the drawing rev (part is the master). Drawing letter syncs to the model immediately at New Revision time (StartDrawingRevisionWith opens the drawing, sets Revision = nextRev, saves, closes). The sync at drawing-release time is still a no-op confirmation.

\- New Revision on a part/assembly auto-starts its drawing revision and warns which assemblies use it.

\- Assembly release is gated: every component that HAS a drawing must have it Released first. Manufactured-with-no-drawing warns (override); Purchased/Toolbox with no drawing skipped.

\- Enforced sequence: part drawings → parts → assembly → assembly drawing.

\- Assemblies are NOT auto-revised when a child part revs — they reference parts by path and pick up changes on open; the warning lets the Master decide when to re-release.



\### Rollback

scan ARCHIVE\\{type}\\ for matching files → show RollbackDialog →

archive current → CLOSE the doc (an open drawing first — it holds a reference; SOLIDWORKS holds the open file's handle, so restoring over the ACTIVE file always failed with a sharing violation; restore retries 5×300ms while SW releases the handle; doc properties are captured BEFORE the close) → restore selected → update RELEASED folder → cleanup exports →

set read-only → update DB → if a matching drawing archive exists at the target rev,

offer to roll the drawing back too (archives current drawing, restores, updates RELEASED) →

warn about parent assemblies

\- Multi-config rollback: archives are FILE-level snapshots, so rollback reverts the WHOLE file (every config) to the archived revision — configs independently bumped to a newer rev since that archive lose those changes. Rollback is NOT config-aware; a multi-config file shows a Yes/No warning (config count + "ALL configurations revert together") that the Master must acknowledge before proceeding.



\### Engineer Requests (Unlock / Revision / Release)

Engineer clicks action button (Request Unlock / Request Revision / Request Release) →

show note dialog → log request to vault.xml RevisionRequests with RequestType →

email both Masters (bchougule, rkramarz) via EmailManager.NotifyRequestSubmitted →

Master clicks PENDING REQUESTS button → PendingRequestsForm opens →

Approve (by type: Unlock→UnlockFile, Revision→StartNewRevision, Release→ReleaseFile) or Reject →

email engineer via EmailManager.NotifyRequestApproved / NotifyRequestRejected



\## Revision Sequence

A,B,C,D,E,F,G,H,J,K,L,M,N,P,R,T,U,V,W,Y,Z (skips I,O,Q,S,X)

GetNextRevision() in VaultManager.cs handles this



\## Completed Features

\- Property enforcement on save (blocks save until all fields filled)

\- Configuration-specific properties

\- Auto PartWeight, auto-capitalize (CharacterCasing.Upper)

\- Broken reference detection

\- File locking + OS read-only protection

\- Lock/Unlock/Release/New Revision/Rollback buttons

\- Modern Task Pane with BC icon, DPI-aware

\- File search by part number/description (auto-search 600ms)

\- Open from search (handles part/assembly/drawing types, activates if already open)

\- STEP/PDF export with correct naming conventions

\- Revision archiving with subfolder structure

\- Export archive/cleanup on release and rollback

\- File History with timeline (last 5 entries, most recent first)

\- Engineer lock/release notifications on file open

\- Drawing release checks (referenced part must be Released first)

\- Drawing revision syncs with part revision on release

\- Assembly release check (all child parts Released before assembly releases)

\- Auto CheckedBy/CheckedDate on release

\- Date format MM/dd/yyyy throughout

\- CheckedBy = first 2 chars uppercase (BC, RK)

\- Archive old exports before new release

\- Two Masters (bchougule, rkramarz)

\- BCore PDM branding with BC icon

\- Engineer Actions section: Request Unlock, Request Revision, Request Release, Open Drawing (context-aware), My Requests

\- Context-aware Open button (both roles): one button that opens the matching drawing from a part/assembly (creates one if none exists, no prompt) or opens the referenced part/assembly from a drawing; label flips between "Open Drawing" and "Open Part/Assembly". Replaced the Master Lock File button and the Engineer Update Drawings button

\- Master Pending Requests popup (PendingRequestsForm) with 3-column Unlock/Revision/Release view

\- File history rendered as individual labels (no text overlap), with StatusColor per entry

\- Email notifications (Mailgun SMTP) on request submit/approve/reject — config at N:\\PDM-SolidWorks\\VAULT\\email.config; "Send Test Email" button sends to the logged-in user to verify the pipeline

\- Duplicate part-number detection on save (warns with Yes/No override when another file already uses the same PartNo) — format validation deemed unfeasible due to 3 divisions with inconsistent numbering

\- Search results capped at 50 with a "refine your search" hint (prevents UI freeze rendering thousands of cards at 50k-file scale)

\- Orphaned records auto-purged by search when the file is gone on disk (network-down guarded so a transient outage never deletes records; audit-logged)

\- Remove from Vault (Master, active file, blocked if Released) — MOVES the file + RELEASED snapshot + STEP + PDF exports to SCRAP and deletes the record; matching drawing always scrapped automatically (no prompt, Released drawing is not exempt — blank without its model)

\- SCRAP folder for retired files (separate from ARCHIVE; recoverable until bulk-purged)

\- Audit trail (AuditLogger → VAULT\\audit.csv, append-only CSV, Excel-friendly): logs create/save/lock/unlock/release/new-rev/rollback/remove/requests/approve/reject/auto-purge

\- PartType property (Manufactured | Purchased, default Manufactured) on parts and assemblies

\- Drawing/Assembly revision linkage: New Revision auto-starts the matching drawing revision and warns about parent assemblies; Rollback offers to roll the drawing back too

\- Assembly drawing-release gate: component drawings must be Released before the assembly (Manufactured-with-no-drawing warns + override; Purchased/Toolbox skipped)

\- Multi-user conflict detection: opening a WIP file already open by another engineer warns "FILE ALREADY OPEN — last save wins" (soft presence in vault.xml <OpenSessions>, distinct from the Master Lock). Presence follows the active doc (assembly/drawing components don't register), warns once per open, cleared on close; per-machine sessions wiped on addin load/unload + 24h staleness backstop so a crash never leaves a false warning

\- Bulk operations (all in PendingRequestsForm, so the task pane stays uncluttered): per-column checkboxes + "Approve Selected" (type-aware via VaultManager.BulkApprove), green "Approve All Pending", and blue "Bulk Release…" → BulkReleaseForm (pick any WIP files, release in one pass). Batch releases ordered parts→assemblies→drawings; one summary dialog reports done/skipped; blocked files stay pending

\- Multi-config Release: ValidateAllConfigs before release; CheckedBy/CheckedDate/PartWeight set on every config; one STEP per config exported (ExportStepOnly per config, config switched before each); flat DXF once for original active config (ExportFlatPatternOnly); confirm + success dialogs list all configs with PN + Rev. ReleaseFile best-effort ACTIVATES the doc first (ActivateDoc3) when it isn't the active document — bulk/chained releases hand it background docs, where ShowConfiguration2 and mass-property reads are unreliable and the verified switches would false-abort healthy files. Both config-switch loops (auto-fill + STEP export) are wrapped in try/finally so an export failure can't leave the model on the wrong config, and BOTH CHECK ShowConfiguration2's return: a refused switch (rebuild error) would stamp the previous config's PartWeight and export the previous config's GEOMETRY under this config's part number as a verified success — so the auto-fill loop collects failed configs and aborts the release before any archive/export (audit-logged "ReleaseFailed", config switch failed), and the STEP loop treats a failed switch exactly like a failed export (failedExports abort). STEP export has a collision guard: two configs sharing PartNo+Rev would overwrite each other's .step, so the duplicate is skipped and reported in one warning dialog. The drawing-release revision sync reads the rev from the drawing's documented config (GetDrawingPrimaryConfig), not the model's active config.

\- Multi-config New Revision: ConfigRevisionPickerForm checklist (all configs pre-checked, current→next per config); Master selects which configs to bump; selected configs get their own next Revision via SetProperty(doc, "Revision", nextRev, cfgName); drawings collected via FindDrawingPath (shared) + GetDrawingsForConfig per selected config. Each drawing is bumped to the CORRECT revision: a config-specific drawing gets ITS config's next rev (from cfgNextRevs), while the shared {modelBasename}.slddrw keeps the file-level active-config rev (first-assignment-wins map drawing→rev). suppressPrompts bumps all configs with no picker; archive naming remains file-level

\- File-level status in multi-config: status (WIP/Released/Locked) is tracked per-FILE, not per-config — a multi-config part is one .sldprt, and OS read-only is file-level. Releasing one config (or its config-specific drawing, which chains to release the model) freezes ALL configurations read-only, and the release gate (ValidateAllConfigs) requires EVERY config's properties complete. This is intentional (all-or-nothing release); the multi-config confirm dialog and the chained drawing-release prompt both warn that all N configs freeze together. To edit any config again, Unlock or New Revision the whole file. Rule 3.5 on save: on the first save of a brand-new multi-config file the active config (just filled via Rule 3) is treated as established so it isn't re-prompted with a PropertyForm.

\- Intra-file duplicate PartNo detection on save (Rule 3.5 in ValidateSave): when a file has multiple configs and two or more share the same PartNo (e.g. after creating a new config from an existing one), the "new" configs (not yet tracked in vault.xml) are identified, the UI switches to each in turn, shows a warning + PropertyForm pre-populated with PartNo/DrawingNo/Description, then blocks the save if duplicates remain. Uses DatabaseManager.GetConfigsForFile to distinguish new vs established configs.

\- Per-config health warning on save (Rule 3.6 in ValidateSave, multi-config ONLY, non-blocking): ONE combined dialog (so a freshly-added config never triggers a chain of pop-ups) that scans every config once and lists each affected config on a single line with ALL its issues, then a single Yes/No "Save anyway?" override. Two checks combined: (a) config name ≠ its PartNo — the convention config name == PartNo underpins per-config drawings (GetDrawingsForConfig), search and revision tracking; configs with no PartNo yet are skipped; caught EARLY because renaming a config after an assembly references it breaks those references; (b) any NON-active config missing required properties (via ValidateAllConfigs) — Rule 3 already hard-blocks the ACTIVE config, the others are otherwise only validated at the release gate, so an incomplete config could go unnoticed until release blocks. Single-config files are exempt from both. Rule 3.5's duplicate-PartNo block stays SEPARATE — it's an interactive PropertyForm fix flow that hard-blocks the save, not a passive warning, and it short-circuits (return 1) before Rule 3.6 runs.

\- Per-config drawing support in OpenOrCreateDrawing: config-specific drawing ({configName}.slddrw) is searched for first; shared drawing ({modelBasename}.slddrw) is the fallback. When neither exists for a multi-config part, DrawingScopeDialog prompts ONCE — Common drawing (one for all configs, {modelBasename}.slddrw) vs This configuration only ({configName}.slddrw) vs Cancel — so the common-vs-per-config decision is an explicit user choice at creation time, not a guess. After that the file name on disk carries the decision and the prompt never repeats. Both patterns coexist — switching active config and clicking "Open Drawing" opens (or creates) the right drawing for that config. Single-config parts skip the prompt (always shared).

\- BOM Export (CSV) on assembly release: every assembly release auto-generates a TOP-LEVEL BOM at EXPORTS\\BOM\\{partNoClean}-R{rev}\_BOM.csv (ExportManager.ExportBom). One row per unique top-level component (path + referenced config) with a Qty count; Purchased/Toolbox hardware included; columns Item,PartNo,Description,Revision,Material,PartType,Qty read config-specifically. Non-fatal (a BOM failure never blocks release). Old BOMs archived to ARCHIVE\\BOM\\ on re-release and rollback (via the shared MoveMatching helper). CSV chosen over .xlsx to avoid a NuGet/native dependency on the engineer PCs (matches audit.csv); reflects the assembly's active config at release time.

\- Watermark on released PDFs: ExportManager.StampWatermark post-processes every exported drawing PDF after SOLIDWORKS writes it. Diagonal, very transparent "RELEASED" text (gray, alpha 11/255 ≈ 4.5%), Arial Bold auto-sized to ~48% of the page diagonal, aligned to the sheet's bottom-left→top-right diagonal (angle = −atan(h/w), ascending). The PDF is read into memory and stamped there (no file handle held) then written back with a 5×300ms retry, because SOLIDWORKS holds the export open briefly (caused a "no watermark until reopen" bug). SW's own "View PDF after publishing" auto-open is suppressed (ExportPdfData.ViewPdfAfterSaving=false) and OpenPdfExternally re-opens the STAMPED PDF for an interactive single release. Implemented with PdfSharp 1.50.5147 (MIT, pure managed, net20 build runs on .NET 4.8). VENDORED as PDMLite\\PdfSharp.dll (committed to the repo, no NuGet restore — matches System.Data.SQLite.dll); copied to output and must be deployed to N:\\PDM-SolidWorks\\ADDIN\\ alongside PDMLite.dll. Non-fatal (a missing DLL or any PdfSharp error leaves the PDF un-stamped, never blocks release).

\- Release-success popup auto-dismiss: the "File Released Successfully" dialog (single + chained model+drawing) uses VaultManager.ShowAutoCloseInfo — same MessageBox look but auto-closes after 4s if the user doesn't click OK (so an unattended release doesn't leave a dialog blocking the file close). A WinForms Timer (pumped by the modal message loop) FindWindow(s) the dialog by caption and PostMessage(WM_CLOSE); for an OK-only box that resolves like clicking OK. Failure/blocker dialogs stay modal as before.

\- Vault Dashboard (ALL users, read-only): full-screen, resizable VaultDashboardForm — a sortable/filterable DataGridView of EVERY tracked file (DatabaseManager.GetAllFiles, all statuses, read-only snapshot, no orphan purge). Engineers get it too (self-service status visibility; it only opens files via OpenByPath, which respects every vault rule — no Master actions). Columns File Name/PartNo/Description/Status/Rev/Modified By/Modified Date/Released By/Released Date/WIP Days (days-since-modified staleness for WIP files); Status colour-coded, broken-ref rows flagged red, a Locked cell tooltips who holds it; drawings show their model's PartNo/Description. VirtualMode + PAGINATED 20 rows/page (PageSize=VisibleRows=20) with a bottom pager (First · ‹ · numbered pages with ellipsis · › · Last; current page boxed) and KEYBOARD NAV (PgUp/PgDn pages, Ctrl+Home/End first/last, Ctrl+F search, Enter opens the selected row, Esc closes). Content-fit column widths (widest value + 20%); date columns sort chronologically (typed DateTime). EXCEL-STYLE per-column filtering: every column header has a dropdown arrow (gold/funnel-tinted when active) that opens a searchable checkbox list (Select All / Clear, OK / Cancel) — so any user can isolate any value (e.g. one engineer in Modified By or Released By); clicking the header text sorts. The list NARROWS to the values present given the other active filters (like Excel, preserving allowed values hidden by another filter), and DATE columns group to DAY granularity (distinct days, chronological) instead of every minute. A global search box (PartNo/Description/Name) + a "Clear Filters" button complement it; all filtering is in memory (a new filter/sort/search jumps back to page 1). Summary strip doubles as CLICKABLE QUICK FILTERS (Total=clear, WIP/Released/Locked=Status filter, Broken Refs=broken-only; active one underlined) + page range. Double-click OR a row RIGHT-CLICK MENU (Open · Open Drawing/Model · Copy File Path · Open Containing Folder; linked file resolved in-memory, no DB hit) opens the file (canonical WIP copy via OpenByPath). Export CSV dumps the whole filtered view (all pages). Opened from a task-pane button shown to all users.

\- ConfigRevisionPickerForm restyled to house convention (PR39): brand title bar, 3.7–6f ×\_scale fonts, flat coloured buttons (All=cBrand/fSmall, None=cDark/fSmall, OK=cGreen/fBtn, Cancel=cDark/fBtn), white bordered CheckedListBox with IntegralHeight=false so text never clips at any DPI. List height is dynamic (fills space between body label and button row). Fixes text and button label truncation visible at high DPI on the multi-config New Revision picker.

\- Audit Report (ALL users, read-only): AuditReportForm — a sortable/filterable/paginated view of the WHOLE audit trail (audit.csv), companion to the Vault Dashboard (dashboard = current file state; audit report = event history over time). Reuses the dashboard's VirtualMode + 20-rows/page pager, Excel-style per-column filtering (Timestamp grouped to DAY for date-range filtering), global search, keyboard nav and CSV export. Columns Timestamp/User/Action/File Name/Part No/Rev/Note; default sort Timestamp DESC; Action colour-coded by category. Reads audit.csv with a proper RFC-4180 parser (handles quoted fields + embedded commas/newlines); the log is never written. Summary strip = clickable Total / Releases / Revisions / Removals quick filters + page range. "Export history to Excel" delivered as a CSV (opens directly in Excel — same no-NuGet/native-dep convention as audit.csv and the BOM export). Has NO separate task-pane button: reached by a single-window SWITCH from the Vault Dashboard (dashboard "Audit Report »" ↔ audit "« Vault Dashboard"), orchestrated by OpenDashboard's view-switch loop, so only one window is ever open and the task pane stays uncluttered.

\- Exact-match export archiving + vault-wide filename uniqueness (audit C3 + H1): (a) every export glob (ArchiveOldExports, CleanupExportsOnRollback, ScrapExports, RollbackRevision's ARCHIVE scan) is anchored to the export naming convention AND post-filtered by ExportNameFilter's exact-name regex — a bare "{id}*.step" prefix glob silently archived/scrapped OTHER parts' current exports whenever one part number started with another (releasing TEST02 swept TEST021's deliverables); (b) save-time HARD BLOCK on a duplicate file name (ValidateSave Rule 2.6 → DatabaseManager.FindFileNameConflict, all doc types, no override) — RELEASED/ARCHIVE/SCRAP are flat folders keyed on file name and the DB dedupes/purges by it, so a second same-named file in another division would overwrite the first's released snapshot/archives and delete its history on first save (legacy RELEASED-copy records and on-disk-missing orphans don't count as rivals, so established files never false-block); (c) UpsertFile matches FilePath case-insensitively — a casing difference (n:\\ vs N:\\) used to create a duplicate record and wipe the file's history via the wasCreate purge; (d) ScrapExports now also scraps the BOM CSV (was leaked in EXPORTS forever when an assembly was retired); (e) RemoveFromVault is multi-config aware — exports scrapped per config's PartNo/DrawingNo and ALL drawings (shared + config-specific) scrapped, not just the active config's deliverables and the shared drawing.

\- GDI/USER handle leak fix (audit C4): Controls.Clear() never disposes the removed controls — WinForms re-parents them to a hidden parking window where they keep their handles until the process dies, and the rebuilt panels rebuild constantly (task-pane search on every keystroke, history on every doc/config switch, request columns after every approve/reject, bulk-release list on every debounce tick), marching SOLIDWORKS toward the 10,000-handle ceiling over a multi-day session. Every rebuilt container now goes through a ClearAndDispose helper (dispose children, then clear — private copy in TaskPaneControl, PendingRequestsForm and BulkReleaseForm per the one-form-one-file convention; the dashboard/audit pagers already disposed correctly). Per-card fonts hoisted to once-created fields and disposed on teardown, since a Font assigned to a control is NOT owned by it (Dispose(bool) overrides everywhere — TaskPaneControl also disposes \_searchTimer and the status-bar StringFormat; FormClosed was rejected because it never fires for a form disposed without being shown). Previously-undisposed dialogs fixed: PendingRequestsForm opened via using, RollbackDialog via using, ShowNoteDialog's form+fonts disposed in a finally around ShowDialog, About-dialog fonts disposed on FormClosed. One-time constructor fonts on permanent controls intentionally left (bounded, not a march).

\- Filter-popup Select All / Clear beyond the render cap (audit C5): ColumnFilterDialog's Select All / Clear used to iterate \_visibleRaw — the RENDERED list, capped at DisplayCap (2000) — so on a high-cardinality column (File Name at vault scale, exactly what the cap exists for) "Clear, then tick one" flipped only the first 2000 entries, left the rest checked, and committed a filter that quietly allowed almost everything. Now SetMatched flips every value MATCHED by the in-popup search via the shared MatchesTerm predicate (also used by RebuildList so the two loops can't drift). Found while testing the same dialog: OK with a search term active committed the stale checked state of every UNMATCHED value too ("search 'as', tick one file, OK" still showed all the non-matching files — they were never shown but stayed checked from before the term was typed); Commit now keeps the CHECKED MATCHES ONLY when a term is active (Excel's "filter to search results"), and the count label says so ("N matches — OK filters to ticked"). Fixed identically in BOTH private copies (VaultDashboardForm + AuditReportForm, one-form-one-file convention).

\- Installer/registration fixed for rollout (audit C6): InstallPDMLite.bat used to point RegAsm at the dev machine's D:\\...\\bin\\Debug\\PDMLite.dll (exists ONLY there — on any engineer PC RegAsm failed) and wrote the SOLIDWORKS HKLM keys regardless, leaving a broken add-in entry; both .reg files hardcoded the same dev path as CodeBase. Now everything registers the DEPLOYED N:\\PDM-SolidWorks\\ADDIN\\PDMLite.dll: the installer is failure-ordered (admin check → DLL exists → RegAsm exists → RegAsm /codebase verified → ONLY THEN the SW keys), warns if PdfSharp.dll is missing, and takes an optional UNC ADDIN-folder argument (elevated sessions often lack the per-user N: mapping). New DeployPDMLite.bat (build machine) copies the RELEASE build + PdfSharp.dll + registration files to ADDIN, refusing to deploy when the Release output is missing (Debug builds never ship) and explaining the locked-DLL-while-SW-runs case. The .reg files (machine-wide admin fallback + per-user HKCU no-admin variant) document the Version=1.0.0.0 pin against AssemblyInfo.cs. Full flow: see \## Registration.



\## Remaining Features (in priority order)

9\. Engineer PC rollout

10\. Licensing / super-access / installer — DEFERRED until development is declared done. Full design pinned in LICENSING.md (offline signed license file with expiry + owner password hash, Owner role above Master via license not vault.xml, graduated expiry: governance features off but files still open/save, ConfuserEx obfuscation, single Inno Setup installer, commercialization addendum incl. the IP-ownership gate). During development only two habits apply: keep conventions centralized when touched anyway, and build nothing that fights that design.



\## Development Workflow

\- Always create a new branch and PR for each change

\- Always update CLAUDE.md in the same PR when project structure or behaviour changes



\## Target Users

Richards-Wilcox engineering team

\- 2 Masters: bchougule, rkramarz

\- \~5-10 engineers on 1080p (100% scaling) and 4K (250% scaling) machines

