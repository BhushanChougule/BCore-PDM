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

\- WIP\\                   → work in progress files

\- RELEASED\\              → released files (OS read-only protected)

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

\- Hooks: FileSaveNotify, FileSaveAsNotify, FileSavePostNotify, DestroyNotify

\- OnFileSaveNotify: check lock → check released → validate properties → auto-weight → broken refs

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

\- Revision uses ComboBox (A through M)



\### DatabaseManager.cs

XML vault database at N:\\PDM-SolidWorks\\vault\\vault.xml

Classes: VaultFile, LockInfo, HistoryEntry, RevisionRequest

RevisionRequest.RequestType = "Unlock" | "Revision" | "Release"

Methods:

\- Initialize, UpsertFile, GetFileStatus, SetFileStatus

\- SetBrokenRefFlag, LockFile, UnlockFile, GetLockInfo

\- GetUserRole, AddUser

\- SearchFiles(term) → searches PartNumber + Description + FileName (Released files only)

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



File History uses PopulateHistoryPanel(List<HistoryEntry>) helper. Each entry: status label (StatusColor), date+user label, optional note label, 1px Panel divider. All labels have explicit Height.

No longer uses StringBuilder/AppendLine — individual labels prevent text overlap at small font sizes.



Search results: file cards with status color bar, part number, Open in SOLIDWORKS button.

Uses ActivateDoc3 if file already open, OpenDoc6 with correct type if not.



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



\### RollbackDialog.cs

DPI-aware Form. S(v)=v\*\_scale.

\- Shows archived revisions from ARCHIVE\\{type}\\ folder

\- Sorted most recent first

\- Restore button per revision

\- ExtractRevision() parses "FileName REV X.ext"



\## Key Technical Decisions

\- SQLite abandoned → XML (System.Xml.Linq) due to native DLL conflicts

\- Event names: FileSaveNotify + FileSaveAsNotify (NOT FileSavePreNotify)

\- FileSavePostNotify parameters: (int saveType, string FileName) - ORDER matters

\- DestroyNotify() has no parameters

\- Configuration properties via doc.GetActiveConfiguration() as Configuration

\- Drawings use configName="" for properties (no configurations)

\- DPI scaling: \_scale = g.DpiX / 96f, all sizes use S(v) = (int)(v \* \_scale)

\- Fonts: new Font("Segoe UI", Xf \* \_scale) — NOT S() for font sizes

\- OS read-only set on release, removed on unlock/new revision

\- No COM auto-registration (no admin rights) — manual IT registration



\## Registration

\- GUID: {A1B2C3D4-E5F6-7890-ABCD-EF1234567890}

\- Registry: HKLM\\SOFTWARE\\SolidWorks\\AddIns\\{GUID}

\- IT runs: N:\\PDM-SolidWorks\\Addin\\InstallPDMLite.bat as Administrator



\## Workflows



\### Engineer Save

FileSaveAsNotify/FileSaveNotify → check lock → check released → 

validate properties (show PropertyForm if missing) → auto-weight → 

check refs → allow save → post-save updates DB



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

Master clicks PENDING REQUESTS button → PendingRequestsForm opens →

Approve (by type: Unlock→UnlockFile, Revision→StartNewRevision, Release→ReleaseFile) or Reject



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



\## Remaining Features (in priority order)

1\. Email notifications (SMTP) when requests submitted/approved/rejected

2\. Part/Drawing number validation (enforce numbering format)

3\. BOM Export to Excel on assembly release

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

