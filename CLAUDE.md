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

\- vault\\vault.xml        → XML database (System.Xml.Linq, no SQLite)

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

\- Addin\\                 → DLL and registration files



\## Master Users

\- bchougule (Master)

\- rkramarz (Master)

\- All others = Engineer automatically (read from vault.xml)



\## Required Custom Properties (Configuration-Specific)

PartNo, DrawingNo, Description, DrawnBy, DrawnDate, Material1, FinishType, Revision

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



\### DatabaseManager.cs

XML vault database at N:\\PDM-SolidWorks\\vault\\vault.xml

Classes: VaultFile, LockInfo, HistoryEntry, RevisionRequest

RevisionRequest.RequestType = "Unlock" | "Revision" | "Release"

Methods:

\- Initialize, UpsertFile, GetFileStatus, SetFileStatus

\- SetBrokenRefFlag, LockFile, UnlockFile, GetLockInfo

\- GetUserRole, AddUser

\- SearchFiles(term) → searches PartNumber + Description + FileName (Released files only); returns the canonical WIP path (RELEASED is never opened for editing); dedupes by filename as a safety net

\- FindPartNumberConflict(partNo, excludeFilePath) → returns filename of another file using same PartNo (case-insensitive, trimmed), or null. Excludes the file being saved so it never conflicts with itself.

\- GetFileHistory(filePath) → returns List<HistoryEntry> reversed (most recent first)

\- AddRevisionRequest, AddUnlockRequest, AddReleaseRequest → all call private AddRequest(type,...)

\- GetPendingRequests, GetRequestsByUser(user), ResolveRequest



\### VaultManager.cs

Core vault operations.

\- LockFile(path) → Master only, sets status=Locked

\- UnlockFile(path) → Master only, sets status=WIP, removes read-only

\- ReleaseFile(doc) → validates → exports → copies to RELEASED → sets read-only

\- StartNewRevision(doc) → removes read-only → archives → bumps rev → saves → sets WIP

\- RollbackRevision(doc) → shows RollbackDialog → archives current → restores selected

\- RequestRevision(doc), RequestUnlock(doc), RequestRelease(doc) → Engineer requests with note dialog

\- ApproveRequest(request) → Master approves, calls StartNewRevision

\- RejectRequest(request) → Master rejects, marks as Rejected in database

\- ViewMyRequests() → Engineer views their own requests (MessageBox)

\- OpenOrCreateDrawing(doc) → searches for matching .slddrw, prompts to create if not found

\- GetUnreleasedComponents(doc) → checks all assembly children are Released

\- GetDrawingNo(doc) → gets DrawingNo from referenced model

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

\- cRed(180,75,75), cMaroon(140,60,60)



Sections (top to bottom):

1\. Dark header banner "BCore PDM"

2\. Search (auto-search 600ms timer, ≥2 chars, Enter key via ProcessCmdKey)

3\. Active File card (filename, status, partNo, revision, lockedBy)

4\. Master Actions (Lock/Unlock/Release/New Revision/Rollback) — Masters only

5\. Engineer Actions (Request Unlock/Revision/Release, Update Drawings, My Requests) — Engineers only, same y-position as Master Actions via engY = y - S(5\*28)

6\. File History — Panel (\_historyPanel) with individual labels per entry, Height=S(300), y+=S(305)

7\. PENDING REQUESTS button (Masters only) — shows count, opens PendingRequestsForm popup

8\. Send Test Email button (all users) — calls EmailManager.SendTestEmail, shows success/error in MessageBox



File History uses PopulateHistoryPanel(List<HistoryEntry>) helper. Each entry: status label (StatusColor), date+user label, optional note label, 1px Panel divider. All labels have explicit Height.

No longer uses StringBuilder/AppendLine — individual labels prevent text overlap at small font sizes.



Search results: file cards with status color bar, part number, Open in SOLIDWORKS button.

Uses ActivateDoc3 if file already open, OpenDoc6 with correct type if not. Opens the canonical WIP copy (read-only when Released), never the RELEASED snapshot.



\### PendingRequestsForm.cs

DPI-aware Form (680×500 scaled). S(v)=v\*\_scale. Opened from PENDING REQUESTS button in task pane.

\- Three scrollable columns: Unlock | Revision | Release (categorised by RevisionRequest.RequestType)

\- Each column has a coloured header (orange/purple/green) and scrollable card list

\- Card: filename, requested-by, date, optional note, Approve + Reject buttons

\- Approve logic by type: Unlock→UnlockFile, Revision→ApproveRequest/StartNewRevision, Release→ReleaseFile

\- Legacy requests with no RequestType default to Revision column



\### TaskPaneHost.cs

\- Register/Unregister task pane

\- CreateTaskpaneView2(CreateIcon(), "BCore PDM")

\- CreateIcon() generates BC icon BMP in temp folder

\- Handles ActiveDocChangeNotify → calls RefreshPanel()



\### EmailManager.cs

Sends email notifications via SMTP (company uses Mailgun: smtp.mailgun.org:587, sender bcorepdm@mg.richardswilcox.com). Non-fatal — all sends wrapped in try/catch; failure never blocks workflow.

Config file: N:\\PDM-SolidWorks\\vault\\email.config (XML, created on first addin load if missing)

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

\- No COM auto-registration (no admin rights) — manual IT registration



\## Registration

\- GUID: {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}

\- Registry: HKLM\\SOFTWARE\\SolidWorks\\AddIns\\{GUID}

\- IT runs: N:\\PDM-SolidWorks\\Addin\\InstallPDMLite.bat as Administrator



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

validate properties → check broken refs → check assembly children released →

archive old exports → export STEP → copy to RELEASED → set read-only → update DB



\### Master Release (Drawing)

check referenced part is Released → sync drawing revision with part revision →

export PDF (all sheets) → copy to RELEASED → set read-only → update DB



\### Master New Revision

remove read-only → archive SW file to ARCHIVE\\{type}\\ → bump revision letter →

save → reset to WIP in DB



\### Rollback

scan ARCHIVE\\{type}\\ for matching files → show RollbackDialog →

archive current → restore selected → update RELEASED folder → 

cleanup exports → set read-only → update DB



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

\- Engineer Actions section: Request Unlock, Request Revision, Request Release, Update Drawings, My Requests

\- Master Pending Requests popup (PendingRequestsForm) with 3-column Unlock/Revision/Release view

\- File history rendered as individual labels (no text overlap), with StatusColor per entry

\- Email notifications (Mailgun SMTP) on request submit/approve/reject — config at N:\\PDM-SolidWorks\\vault\\email.config; "Send Test Email" button sends to the logged-in user to verify the pipeline

\- Duplicate part-number detection on save (warns with Yes/No override when another file already uses the same PartNo) — format validation deemed unfeasible due to 3 divisions with inconsistent numbering



\## Remaining Features (in priority order)

1\. BOM Export to Excel on assembly release

4\. Multi-user conflict detection (warn when two engineers open same WIP)

5\. Watermark on released PDFs

6\. Vault Dashboard (full screen file status view)

7\. Bulk Release

8\. Audit Report (export history to Excel)

9\. Engineer PC rollout



\## Development Workflow

\- Always create a new branch and PR for each change

\- Always update CLAUDE.md in the same PR when project structure or behaviour changes



\## Target Users

Richards-Wilcox engineering team

\- 2 Masters: bchougule, rkramarz

\- \~5-10 engineers on 1080p (100% scaling) and 4K (250% scaling) machines

