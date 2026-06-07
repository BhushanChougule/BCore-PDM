\# BCore PDM — SOLIDWORKS Add-in



\## Project Overview

A fully custom SOLIDWORKS PDM replacement system built in C# .NET Framework 4.8.

Named BCore PDM (BC = bchougule initials). Replaces expensive SOLIDWORKS PDM Professional.



\## Solution Location

D:\\06 SOLIDWORKS\_Automation\\08\_Documentation\\PDMLite\_CL\\PDMLite\\PDMLite\\

\- Project file: PDMLite.slnx

\- Output DLL: bin\\Debug\\PDMLite.dll

\- Namespace: PDMLite

\- Main class: PDMLiteAddin (implements ISwAddin)



\## Network Vault Structure

N:\\PDM-SolidWorks\\

\- VAULT\\vault.xml        → XML database (System.Xml.Linq, no SQLite)

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

\- EXPORTS\\PDF\\           → current released PDFs

\- EXPORTS\\STEP\\          → current released STEP files

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

\- Hooks: FileSaveNotify, FileSaveAsNotify2, FileSavePostNotify, DestroyNotify

\- OnFileSaveNotify: suppress-flag check → check lock → check released (blocks EVERYONE incl. Masters) → warn if outside WIP folder (Yes/No override) → validate properties → auto-weight → duplicate part number → broken refs

\- SuppressSaveValidation (static bool): VaultManager sets it around its own internal Save3 calls (Release, New Revision) so those programmatic saves bypass the released-file lock

\- OnFileSavePost: upsert file to database, update description

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

WinForms dialog for missing properties. Fixed sizes (not DPI-scaled, form is shown at system scale).

\- formWidth=1200, labelWidth=380, inputWidth=480, inputHeight=46

\- rowHeight=62, inputLeft=410, startY=210

\- DateTimePicker format: MM/dd/yyyy

\- CharacterCasing=Upper on TextBoxes

\- Material1 and FinishType use ComboBox dropdowns

\- Revision uses ComboBox (full revision sequence A through Z, skipping I,O,Q,S,X)

\- PartType uses ComboBox (Manufactured | Purchased) — NO "-- Select --" sentinel, so index 0 (Manufactured) is the valid default

\- ComboValue(cb) helper: dropdowns with a "-- Select --" first item treat index 0 as empty; sentinel-less dropdowns (PartType) return the selected item directly



\### DatabaseManager.cs

XML vault database at N:\\PDM-SolidWorks\\VAULT\\vault.xml

Classes: VaultFile, LockInfo, HistoryEntry, RevisionRequest

RevisionRequest.RequestType = "Unlock" | "Revision" | "Release"

Methods:

\- Initialize, UpsertFile (audit-logs Create vs Save), GetFileStatus, SetFileStatus

\- SetBrokenRefFlag, LockFile, UnlockFile, GetLockInfo

\- RemoveFileRecord(filePath) → removes vault.xml record(s) for a file (matches FilePath then FileName for dupes/RELEASED-copy entries) AND purges that file's RevisionHistory entries via PurgeHistoryFor (so a new file of the same name never inherits the removed file's timeline); DB record ONLY, never touches files on disk; returns count of File records removed

\- PurgeHistoryFor(doc, filePath, fileName) → private helper; removes all RevisionHistory <Entry> nodes matching the file by exact FilePath or by filename. Also called by UpsertFile on CREATE (wasCreate) so a brand-new file always starts with a clean history, even if a same-named file was removed before the purge fix shipped

\- Initialize() → creates WIP division subfolders + SCRAP folder on first addin load

\- GetUserRole, AddUser

\- SearchFiles(term) / SearchFiles(term, out truncated) → searches PartNumber + Description + FileName (all statuses); returns canonical WIP path; dedupes by filename; capped at MaxSearchResults=50 (truncated=true when more matched, so UI can prompt to refine — prevents rendering thousands of cards at 50k scale); AUTO-PURGES orphaned records whose file is missing on disk, but ONLY when the WIP root is reachable (network-down guard so a transient outage never deletes records); auto-purges are audit-logged as "AutoPurgeOrphan"

\- FindPartNumberConflict(partNo, excludeFilePath) → returns filename of another file using same PartNo (case-insensitive, trimmed), or null. Excludes the file being saved so it never conflicts with itself.

\- GetFileHistory(filePath) → returns List<HistoryEntry> reversed (most recent first)

\- GetModelForDrawing(drawingFilePath) → returns the part/assembly VaultFile sharing the same base filename as the given drawing (PartNo/Description/Status live on the model, not the drawing); null if not found. Used by the merged search card to populate model details when a search matched the drawing.

\- GetDrawingPathForModel(modelFilePath) → returns the WIP path of the .slddrw sharing the same base filename as the given part/assembly (the drawing that documents it); null if none tracked. Used by the merged search card to wire "Open Drawing" when a search matched only the model.

\- GetReleasableFiles(filter, out truncated) → returns WIP (releasable) files for the Bulk Release picker, optionally filtered by PartNumber/Description/FileName; excludes Released/Locked; deduped by filename; capped at MaxSearchResults; file must exist on disk (orphans skipped, NOT purged — read-only picker)

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

\- ReleaseFile(doc, suppressPrompts=false) → validates → (assembly) parts Released + drawing-release gate → exports → copies to RELEASED → sets read-only. Releasing a Drawing whose model is still WIP offers ONE prompt to release both; on Yes the model is released via ReleaseFile(model, suppressPrompts:true) so the pair needs only a single confirm + single combined success. suppressPrompts skips the confirm + success dialogs only (blocker/validation dialogs still show)

\- StartNewRevision(doc, suppressPrompts=false) → removes read-only → archives → bumps rev → saves → sets WIP → auto-starts associated drawing revision → warns about parent assemblies. suppressPrompts (bulk approve) skips the confirm + final summary dialogs only; blocker/failure dialogs still show

\- OpenByPath(filePath) → opens a vault file choosing doc type from extension (returns already-open doc if SOLIDWORKS has it; null on failure). Used by the batch helpers

\- BulkRelease(filePaths) → releases a batch of WIP files in one pass, ordered parts→assemblies→drawings (so gates pass); each: OpenByPath + ReleaseFile(suppressPrompts:true) + check status; returns a BatchResult (Succeeded/Skipped names). Blocker/validation dialogs still show so the Master sees why anything is skipped

\- BulkApprove(requests) → approves a batch of pending requests, each by type: Unlock→UnlockFile, Revision→StartNewRevision, Release→ReleaseFile (releases ordered parts→assemblies→drawings); only resolves a request when its action actually succeeded (a blocked release stays pending); returns a BatchResult

\- BatchResult (top-level class in VaultManager.cs) = Succeeded + Skipped name lists + BuildSummary(heading) for one summary dialog

\- RollbackRevision(doc) → shows RollbackDialog → archives current → restores selected → offers matching drawing rollback (if archive exists) → warns about parent assemblies

\- FindDrawingPath(modelPath) → finds {basename}.slddrw in the model folder or any WIP division; null if none (drawing filename MUST match the model basename)

\- GetParentAssemblies(filePath) → scans tracked .sldasm files via GetDocumentDependencies2 (reads refs without opening); returns filenames of assemblies that reference the file. Best-effort — depends on stored ref paths matching vault path format

\- StartDrawingRevisionWith(modelPath, currentRev, nextRev, user) → archives the Released drawing at the old rev (matched pair), returns it to WIP, opens it silently to bump the Revision property to nextRev and save, then closes it. StartNewRevision reopens it if the user had it open. Drawing rev LETTER syncs to model immediately at New Revision time.

\- EvaluateAssemblyDrawings(doc, out blockers, out warnings) → per component: Toolbox skipped; drawing exists+not Released → blocker; no drawing + Manufactured → warning; no drawing + Purchased → skipped. Dedupes repeated instances

\- IsToolboxComponent(comp) → heuristic: path contains "\\Toolbox\\". Secondary net; PartType=Purchased is the authoritative mechanism

\- GetComponentPartType(comp) → reads PartType from the loaded component model ("" if unreadable)

\- RemoveFromVault(doc) → Master only; retires the active file — MOVES its WIP copy, RELEASED snapshot and exports (STEP + PDF) to SCRAP (timestamped) and deletes the vault record; BLOCKED while Released (Unlock/New Revision first); matching drawing is ALWAYS scrapped automatically (even if Released — a drawing without its model is blank and useless); confirmation dialog; audit-logged. Orphans are NOT handled here (can't open a deleted file) — SearchFiles auto-purges them instead

\- MoveToScrap(filePath) → moves one file to SCRAP with a yyyyMMdd_HHmmss suffix (clears read-only first; no-op if missing). ScrapExports(partNo, drawingNo) → moves matching STEP (by dotless partNo) + PDF (by drawingNo) exports to SCRAP

\- RequestRevision(doc), RequestUnlock(doc), RequestRelease(doc) → Engineer requests with note dialog

\- ApproveRequest(request) → Master approves, calls StartNewRevision

\- RejectRequest(request) → Master rejects, marks as Rejected in database

\- ViewMyRequests() → Engineer views their own requests (MessageBox)

\- OpenOrCreateDrawing(doc) → searches for matching .slddrw (model folder + every WIP division); opens it if found, else creates a new drawing FROM the model immediately (no prompt) — opens the default drawing template and calls DrawingDoc.InsertModelInPredefinedView to populate its predefined views (the "Make Drawing from Part/Assembly" equivalent); falls back to Create3rdAngleViews2 if the template has no predefined views, so the sheet is never blank. The part/assembly side of the context-aware Open button

\- OpenReferencedModel(doc) → from a drawing, opens (or activates if already open) the part/assembly it references; warns if the referenced model can't be found. The drawing side of the context-aware Open button

\- GetUnreleasedComponentsByPath(asmPath) → checks all assembly children are Released by reading the dependency tree from disk via GetDocumentDependencies2 (independent of load mode — a lightweight assembly, or one loaded only as a drawing reference, still reports its WIP children). Skips Toolbox; dedupes; non-Released tracked components block. Used by both the assembly-release gate and the assembly-drawing-release gate

\- GetDrawingNo(doc) → gets DrawingNo from referenced model

\- GetDrawingPartNo(doc) → gets PartNo from the model a drawing references (falls back to the drawing's own PartNo, then ""); used by the task-pane Active File card so a drawing shows its part/assembly's number instead of literal "Drawing"

\- GetDrawingReferencedModel(doc) → gets path of model referenced by drawing

\- SetReadOnly(path, bool) → sets/removes OS-level FileAttributes.ReadOnly

\- ArchiveOldExports(archiveId, isDrawing) → moves old STEP/PDF to archive before release

\- CleanupExportsOnRollback(partNoClean, drawingNo) → moves all exports to archive on rollback



\### ExportManager.cs

\- ExportAll(doc, exportRoot, stamp) → routes to correct export by file type

\- ExportDrawingPdf(doc, outPath) → drawing to PDF (all sheets)

\- Part/Assembly → STEP export to EXPORTS\\STEP\\



\### TaskPaneControl.cs

DPI-aware UserControl. Scale: g.DpiX/96f. S(v) = v\*\_scale.

Imports: requires using System.Linq (for history.Take(5)).

Color palette:

\- cBrand(65,120,175), cBrandDark(44,85,128), cGreen(60,140,95)

\- cOrange(185,115,55), cPurple(105,100,165), cDark(75,80,90)

\- cRed(180,75,75), cMaroon(140,60,60), cSwRed(190,55,50) — muted SOLIDWORKS red



Sections (top to bottom):

1\. Dark header banner "BCore PDM"

2\. Search (auto-search 600ms timer, ≥2 chars, Enter key via ProcessCmdKey)

3\. Active File card (filename, status, partNo, revision, lockedBy)

4\. Master Actions (Open Drawing/Unlock/Release/New Revision/Rollback) — Masters only

5\. Engineer Actions (Request Unlock/Revision/Release, Open Drawing, My Requests) — Engineers only, same y-position as Master Actions via engY = y - S(5\*28)

Open Drawing button (both roles, cBrand) is CONTEXT-AWARE: DoAction("openlinked") opens the matching .slddrw when a part/assembly is active (creating one if none exists), or opens the referenced part/assembly when a drawing is active. Its label flips between "Open Drawing" and "Open Part/Assembly" in Refresh() based on the active doc type, kept in sync across both role variants by SetOpenLinkedLabel(). Replaced the Master Lock File button and the Engineer Update Drawings button.

Open Drawing button label when a drawing is active: VaultManager.GetDrawingOpenLabel(doc) checks the referenced model's extension — shows "Open Part" for .sldprt, "Open Assembly" for .sldasm, falls back to "Open Part/Assembly" if the reference can't be resolved.

6\. File History — Panel (\_historyPanel) with individual labels per entry, Height=S(300), y+=S(305)

7\. PENDING REQUESTS button (Masters only) — shows count, opens PendingRequestsForm popup

8\. Send Test Email button (all users) — calls EmailManager.SendTestEmail, shows success/error in MessageBox

9\. Remove from Vault button (Masters only, cSwRed — muted SOLIDWORKS red) — DoAction("remove") → VaultManager.RemoveFromVault on the active file (moves to SCRAP + deletes record; blocked if Released)

Search results are capped at 50; when SearchFiles reports truncated=true a "Showing first N — refine your search" hint is rendered below the cards.



File History uses PopulateHistoryPanel(List<HistoryEntry>) helper. Each entry: status label (StatusColor), date+user label, optional note label, 1px Panel divider. All labels have explicit Height.

No longer uses StringBuilder/AppendLine — individual labels prevent text overlap at small font sizes.



Search results: MERGED cards — a part/assembly and its drawing collapse into ONE card keyed by shared base filename (the model is primary; it owns PartNo + Description). RunSearch groups the flat SearchFiles list by basename, then fills in the counterpart that did NOT match the term (GetModelForDrawing / GetDrawingPathForModel) so both buttons can be offered. Each card shows: thick left status bar with the status text painted VERTICALLY (rotated -90°, custom Panel.Paint), file name (no extension), part number, description, and TWO buttons side by side — "Open PRT"/"Open ASM" (cBrand; disabled+greyed when no model record, e.g. orphan drawing) and "Open DRW" (cBrandDark); abbreviated so labels don't clip at the narrow task-pane width. SearchGroup is a private nested class in TaskPaneControl.

Open PRT/ASM → OpenFile(modelPath). Open DRW → OpenDrawingResult(modelPath, drawingPath): opens the drawing if it exists, else opens the model and calls VaultManager.OpenOrCreateDrawing to make one (same as the task-pane Open Drawing button).

Uses ActivateDoc3 if file already open, OpenDoc6 with correct type if not. Opens the canonical WIP copy (read-only when Released), never the RELEASED snapshot.



\### PendingRequestsForm.cs

DPI-aware Form (680×560 scaled). S(v)=v\*\_scale. Opened from PENDING REQUESTS button in task pane. Doubles as the Master batch-action hub (keeps the task pane uncluttered).

\- Three scrollable columns: Unlock | Revision | Release (categorised by RevisionRequest.RequestType)

\- Each column has a coloured header (orange/purple/green) and scrollable card list

\- Card: selection checkbox (top-right, Tag=request), filename, requested-by, date, optional note, Approve + Reject buttons

\- Single Approve logic by type (ApproveSingle): Unlock→UnlockFile, Revision→ApproveRequest/StartNewRevision, Release→ReleaseFile

\- Legacy requests with no RequestType default to Revision column

\- Batch actions:

  \- Per-column "All" select-all checkbox in each header + per-column "Approve Selected" button → VaultManager.BulkApprove(checked requests of that column). Label is "Approve Selected" (type-aware via BulkApprove), NOT "Release" — the Unlock/Revision columns approve their own action

  \- Green "Approve All Pending" (spans cols 1-2) → confirms counts, then BulkApprove(all pending)

  \- Blue "Bulk Release…" (col 3) → opens BulkReleaseForm (releases any WIP file, not request-based)

  \- Every batch action ends with ONE summary dialog (BatchResult.BuildSummary) then LoadRequests() refresh



\### BulkReleaseForm.cs

DPI-aware Form (560×560 scaled), Master-only. Opened from the blue "Bulk Release…" button in PendingRequestsForm.

\- Lists WIP (releasable) files from DatabaseManager.GetReleasableFiles(filter, out truncated) — filter box (Enter or Filter button), "Select all" checkbox, scrollable cards (checkbox + filename + PN/description), count label with "(first 50)" when truncated

\- "Release Selected" → confirms, then VaultManager.BulkRelease(checked paths) → one summary dialog → reloads (released files drop out, no longer WIP)

\- Distinct from request-based approve: these files need not have any pending request



\### TaskPaneHost.cs

\- Register/Unregister task pane

\- CreateTaskpaneView2(CreateIcon(), "BCore PDM")

\- CreateIcon() generates BC icon BMP in temp folder

\- Handles ActiveDocChangeNotify → calls RefreshPanel()



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

\- Actions logged: Create, Save (UpsertFile), Lock, Unlock, Release, NewRevision, Rollback, RemoveFromVault, AutoPurgeOrphan (user="system"), RequestRevision/Unlock/Release, ApproveRequest, RejectRequest



\### RollbackDialog.cs

DPI-aware Form. S(v)=v\*\_scale.

\- Shows archived revisions from ARCHIVE\\{type}\\ folder

\- Sorted most recent first

\- Restore button per revision

\- ExtractRevision() parses "FileName REV X.ext"



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

\- Release closes and reopens the WIP file so SOLIDWORKS adopts the OS read-only flag immediately. If the model's drawing is open, the open drawing holds a reference to the model and CloseDoc(model) is refused — so Release pre-closes the drawing first, reopens the model read-only, then reopens the drawing (same pattern as New Revision)

\- No COM auto-registration (no admin rights) — manual IT registration



\## Registration

\- GUID: {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}

\- Registry: HKLM\\SOFTWARE\\SolidWorks\\AddIns\\{GUID}

\- IT runs: N:\\PDM-SolidWorks\\ADDIN\\InstallPDMLite.bat as Administrator



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

warn if outside WIP folder → validate properties (show PropertyForm if missing) → 

auto-weight → duplicate part number → check refs → allow save → post-save updates DB



\### Master Release (Part/Assembly)

validate properties → check broken refs → (assembly) check child parts Released →

(assembly) drawing-release gate: each component with a drawing must have it Released

(blocks); Manufactured components with no drawing warn (override); Purchased/Toolbox

skipped → archive old exports → export STEP → copy to RELEASED → set read-only → update DB



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

remove read-only → archive SW file to ARCHIVE\\{type}\\ → bump revision letter →

save → reset to WIP in DB → auto-start associated drawing revision (archive drawing as

matched pair at old rev, return to WIP — drawing rev letter syncs at drawing release) →

warn about parent assemblies that reference this file



\### Drawing / Assembly linkage (Option B — automatic)

\- Drawing filename MUST match the part/assembly basename ({name}.slddrw). PDF export name still comes from the DrawingNo property.

\- Part rev drives the drawing rev (part is the master). Drawing letter syncs to the model immediately at New Revision time (StartDrawingRevisionWith opens the drawing, sets Revision = nextRev, saves, closes). The sync at drawing-release time is still a no-op confirmation.

\- New Revision on a part/assembly auto-starts its drawing revision and warns which assemblies use it.

\- Assembly release is gated: every component that HAS a drawing must have it Released first. Manufactured-with-no-drawing warns (override); Purchased/Toolbox with no drawing skipped.

\- Enforced sequence: part drawings → parts → assembly → assembly drawing.

\- Assemblies are NOT auto-revised when a child part revs — they reference parts by path and pick up changes on open; the warning lets the Master decide when to re-release.



\### Rollback

scan ARCHIVE\\{type}\\ for matching files → show RollbackDialog →

archive current → restore selected → update RELEASED folder → cleanup exports →

set read-only → update DB → if a matching drawing archive exists at the target rev,

offer to roll the drawing back too (archives current drawing, restores, updates RELEASED) →

warn about parent assemblies



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



\## Remaining Features (in priority order)

1\. BOM Export to Excel on assembly release

5\. Watermark on released PDFs

6\. Vault Dashboard (full screen file status view)

8\. Audit Report (export history to Excel)

9\. Engineer PC rollout



\## Development Workflow

\- Always create a new branch and PR for each change

\- Always update CLAUDE.md in the same PR when project structure or behaviour changes



\## Target Users

Richards-Wilcox engineering team

\- 2 Masters: bchougule, rkramarz

\- \~5-10 engineers on 1080p (100% scaling) and 4K (250% scaling) machines

