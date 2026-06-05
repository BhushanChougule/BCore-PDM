using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PDMLite
{
    public static class VaultManager
    {
        private const string WipFolder = @"N:\PDM-SolidWorks\WIP";
        private const string RelFolder = @"N:\PDM-SolidWorks\RELEASED";
        private const string ObsFolder = @"N:\PDM-SolidWorks\ARCHIVE";
        private const string ExportRoot = @"N:\PDM-SolidWorks\EXPORTS";

        // ── LOCK ─────────────────────────────────────────────────────────
        public static void LockFile(string filePath)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            // Check file has been saved to disk
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show(
                    "Please save the file before releasing it.",
                    "BCore PDM — Release Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            DatabaseManager.LockFile(filePath, user);
            DatabaseManager.SetFileStatus(filePath, "Locked", user,
                "File locked by Master");

            MessageBox.Show(
                "File locked successfully.\nOther users will open this file as read-only.",
                "BCore PDM — Locked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // ── UNLOCK ────────────────────────────────────────────────────────
        public static void UnlockFile(string filePath)
        {
            if (!IsMaster(PDMLiteAddin.CurrentUser)) { NotMaster(); return; }

            // Remove OS-level read-only protection
            SetReadOnly(filePath, false);

            DatabaseManager.UnlockFile(filePath);
            DatabaseManager.SetFileStatus(filePath, "WIP",
                PDMLiteAddin.CurrentUser, "Unlocked by Master");

            // Check if the file is open before showing the message so we can
            // close-and-reopen it after the user clicks OK.
            ModelDoc2 openDoc = PDMLiteAddin.SwApp
                ?.GetOpenDocumentByName(filePath) as ModelDoc2;
            int reopenType = openDoc != null ? openDoc.GetType() : -1;

            MessageBox.Show(
                "File unlocked and returned to WIP.\nEngineers can now edit it.",
                "BCore PDM — Unlocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            // Close and reopen so SOLIDWORKS discards its cached read-only state.
            if (reopenType >= 0)
            {
                try
                {
                    PDMLiteAddin.SwApp.CloseDoc(filePath);
                    int errs = 0, warnings = 0;
                    PDMLiteAddin.SwApp.OpenDoc6(filePath, reopenType,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref errs, ref warnings);
                }
                catch { }
            }
        }

        // ── RELEASE ───────────────────────────────────────────────────────
        public static void ReleaseFile(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            int docType = doc.GetType();
            bool isDrawing = docType == (int)swDocumentTypes_e.swDocDRAWING;

            if (!IsMaster(user)) { NotMaster(); return; }

            // Check file has been saved to disk
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show(
                    "Please save the file before releasing it.",
                    "BCore PDM — Release Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            // ── Drawing: check referenced part is Released first ──────────
            if (isDrawing)
            {
                string referencedModel = GetDrawingReferencedModel(doc);
                if (!string.IsNullOrEmpty(referencedModel))
                {
                    string modelStatus = DatabaseManager.GetFileStatus(referencedModel);
                    if (modelStatus != "Released")
                    {
                        MessageBox.Show(
                            "Cannot release Drawing — the referenced Part or Assembly " +
                            "is not yet Released.\n\n" +
                            "Referenced file : " + Path.GetFileName(referencedModel) + "\n" +
                            "Current status  : " +
                            (string.IsNullOrEmpty(modelStatus) ? "WIP" : modelStatus) + "\n\n" +
                            "Release the Part or Assembly first, then release the Drawing.",
                            "BCore PDM — Release Blocked",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Stop);
                        return;
                    }
                }
            }

            // ── Assembly: check all child parts are Released first ────────
            if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var unreleased = GetUnreleasedComponents(doc);
                if (unreleased.Count > 0)
                {
                    MessageBox.Show(
                        "Cannot release Assembly — the following " +
                        "parts are not yet Released:\n\n• " +
                        string.Join("\n• ", unreleased) + "\n\n" +
                        "Release all parts first then release the Assembly.",
                        "BCore PDM — Release Blocked",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Stop);
                    return;
                }

                // ── Drawing-release gate ──────────────────────────────────
                // Every component with a drawing must have it Released first.
                // Manufactured parts with no drawing warn (override); Purchased
                // and Toolbox parts with no drawing are skipped.
                List<string> drwBlockers, drwWarnings;
                EvaluateAssemblyDrawings(doc, out drwBlockers, out drwWarnings);

                if (drwBlockers.Count > 0)
                {
                    MessageBox.Show(
                        "Cannot release Assembly — these component drawings " +
                        "are not yet Released:\n\n• " +
                        string.Join("\n• ", drwBlockers) + "\n\n" +
                        "Release the part drawings first, then release the Assembly.",
                        "BCore PDM — Release Blocked",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Stop);
                    return;
                }

                if (drwWarnings.Count > 0)
                {
                    var choice = MessageBox.Show(
                        "These Manufactured components have NO drawing:\n\n• " +
                        string.Join("\n• ", drwWarnings) + "\n\n" +
                        "They may be missing a drawing. Release the Assembly anyway?",
                        "BCore PDM — Missing Drawings",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (choice != DialogResult.Yes) return;
                }
            }

            // ── Validate properties (Parts and Assemblies only) ───────────
            if (!isDrawing)
            {
                var validation = PropertyValidator.Validate(doc);
                if (!validation.IsValid)
                {
                    // Show PropertyForm so the Master can fill missing fields
                    // without closing this dialog and doing a manual save first.
                    using (var form = new PropertyForm(doc, validation.EmptyFields))
                    {
                        form.ShowDialog();
                        if (!form.PropertiesSaved)
                        {
                            MessageBox.Show(
                                "Release blocked — required properties incomplete:\n\n• " +
                                string.Join("\n• ", validation.EmptyFields),
                                "BCore PDM — Release Blocked",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Stop);
                            return;
                        }
                    }
                    var recheck = PropertyValidator.Validate(doc);
                    if (!recheck.IsValid)
                    {
                        MessageBox.Show(
                            "Release blocked — required properties still incomplete:\n\n• " +
                            string.Join("\n• ", recheck.EmptyFields),
                            "BCore PDM — Release Blocked",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Stop);
                        return;
                    }
                }
            }

            // ── Check broken references ───────────────────────────────────
            var brokenRefs = ReferenceChecker.GetBrokenReferences(doc);
            if (brokenRefs.Count > 0)
            {
                MessageBox.Show(
                    "Cannot release — broken references found:\n\n• " +
                    string.Join("\n• ", brokenRefs),
                    "BCore PDM — Release Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            // ── Get identifiers and revision ──────────────────────────────
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");
            string rev = PropertyValidator.GetProperty(doc, "Revision");
            string stamp = "";

            if (isDrawing)
            {
                // Sync drawing revision with referenced part revision
                string referencedPath = GetDrawingReferencedModel(doc);
                if (!string.IsNullOrEmpty(referencedPath))
                {
                    ModelDoc2 refModel = PDMLiteAddin.SwApp
                        .GetOpenDocumentByName(referencedPath) as ModelDoc2;

                    if (refModel != null)
                    {
                        string partRev = PropertyValidator.GetProperty(
                            refModel, "Revision");
                        if (!string.IsNullOrEmpty(partRev))
                        {
                            rev = partRev;
                            // Update drawing revision to match part
                            PropertyValidator.SetProperty(doc, "Revision", rev);
                        }
                    }
                }

                // Drawing PDF stamp: "DrawingNo REV B"  e.g. TEST-02 REV B
                string drawingNo = GetDrawingNo(doc);

                // Fallback to drawing filename if DrawingNo is empty
                if (string.IsNullOrEmpty(drawingNo))
                    drawingNo = Path.GetFileNameWithoutExtension(filePath);

                stamp = $"{drawingNo} REV {rev}";
                partNo = drawingNo;
            }
            else
            {
                // Part/Assembly STEP stamp: "PartNo-RA"  e.g. TEST02-RA
                // Remove dots from part number for clean filename
                string partNoClean = partNo.Replace(".", "");
                stamp = $"{partNoClean}-R{rev}";
            }

            // ── Confirm ───────────────────────────────────────────────────
            string fileTypeLabel = isDrawing ? "Drawing No" : "Part No";
            var confirm = MessageBox.Show(
                "Release this file?\n\n" +
                fileTypeLabel + "  : " + partNo + "\n" +
                "Revision      : REV " + rev + "\n" +
                "File          : " + Path.GetFileName(filePath) + "\n\n" +
                "This will:\n" +
                (isDrawing
                    ? "  • Export PDF\n"
                    : "  • Auto-fill Part Weight\n  • Export STEP file\n") +
                "  • Lock file as Released\n" +
                "  • Log the revision",
                "BCore PDM — Confirm Release",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            // ── Auto-fill fields ──────────────────────────────────────────
            PropertyValidator.SetProperty(doc, "CheckedBy",
                user.Length >= 2 ? user.Substring(0, 2).ToUpper() : user.ToUpper());
            PropertyValidator.SetProperty(doc, "CheckedDate",
                DateTime.Now.ToString("MM/dd/yyyy"));

            if (!isDrawing)
                PropertyValidator.AutoFillWeight(doc);

            // This programmatic save must bypass the released-file save lock.
            PDMLiteAddin.SuppressSaveValidation = true;
            try { doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, 0, 0); }
            finally { PDMLiteAddin.SuppressSaveValidation = false; }

            // ── Archive old exports before creating new ones ──────────────
            // Parts use cleaned part number (dots removed) for STEP naming
            string archiveId = isDrawing ? partNo : partNo.Replace(".", "");
            ArchiveOldExports(archiveId, isDrawing);

            // ── Export files ──────────────────────────────────────────────
            ExportManager.ExportAll(doc, ExportRoot, stamp);

            // ── Copy to RELEASED folder ───────────────────────────────────
            Directory.CreateDirectory(RelFolder);
            string releasedCopy = Path.Combine(RelFolder,
                Path.GetFileName(filePath));

            // Safety net: if the file being released somehow already lives in
            // the RELEASED folder, it is already in place (and open in
            // SOLIDWORKS), so we must NOT delete/copy it onto itself; doing so
            // throws "being used by another process". Just re-apply read-only.
            // In the normal flow the file lives in WIP, so this stays false.
            bool fileIsReleasedCopy = string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(releasedCopy),
                StringComparison.OrdinalIgnoreCase);

            // Delete any existing released copy first. Overwriting a read-only
            // file on the network share fails, which previously left a STALE
            // copy in RELEASED while the exports updated. Delete-then-copy is
            // reliable, and any genuine failure is now surfaced, not swallowed.
            try
            {
                if (!fileIsReleasedCopy)
                {
                    if (File.Exists(releasedCopy))
                    {
                        SetReadOnly(releasedCopy, false);
                        File.Delete(releasedCopy);
                    }
                    File.Copy(filePath, releasedCopy, overwrite: true);
                }

                // ── Set OS-level read-only protection ─────────────────────
                SetReadOnly(filePath, true);
                if (!fileIsReleasedCopy)
                    SetReadOnly(releasedCopy, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "WARNING: the released copy could not be updated in:\n" +
                    RelFolder + "\n\n" + ex.Message + "\n\n" +
                    "The exports were updated, but the SOLIDWORKS file in the " +
                    "RELEASED folder is STALE. Check folder/file permissions.",
                    "BCore PDM — Released Copy Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // ── Update database ───────────────────────────────────────────
            // Only track the source-path entry. SearchFiles() redirects callers
            // to the RELEASED folder at open time, so no second entry is needed.
            // A second entry would cause duplicate search results and false
            // part-number conflict warnings.
            DatabaseManager.LockFile(filePath, user);
            DatabaseManager.SetFileStatus(filePath, "Released", user,
                "Released REV " + rev);

            MessageBox.Show(
                "File Released Successfully!\n\n" +
                fileTypeLabel + " : " + partNo + "  REV " + rev + "\n" +
                "Exports saved to:\n" + ExportRoot,
                "BCore PDM — Released",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            // Close and reopen the WIP copy so SOLIDWORKS immediately adopts
            // the OS read-only flag. Without this, SW keeps its cached writable
            // state until the user manually closes and reopens the file.
            try
            {
                PDMLiteAddin.SwApp.CloseDoc(filePath);
                int errs = 0, warnings = 0;
                PDMLiteAddin.SwApp.OpenDoc6(filePath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "", ref errs, ref warnings);
            }
            catch { }
        }

        // ── NEW REVISION ──────────────────────────────────────────────────
        public static void StartNewRevision(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            // Check file has been saved to disk
            if (string.IsNullOrEmpty(doc.GetPathName()))
            {
                MessageBox.Show(
                    "Please save the file before releasing it.",
                    "BCore PDM — Release Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            string filePath = doc.GetPathName();
            string currentRev = PropertyValidator.GetProperty(doc, "Revision");
            string nextRev = GetNextRevision(currentRev);
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");

            var confirm = MessageBox.Show(
                "Start a new revision?\n\n" +
                "Current : REV " + currentRev + "\n" +
                "New     : REV " + nextRev + "\n\n" +
                "The current released file will be archived.\n" +
                "A new WIP revision will begin.",
                "BCore PDM — New Revision",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            int reopenType = doc.GetType();

            // Archive the current released SW file (still on disk at the old rev)
            // to the correct subfolder BEFORE we reopen and bump the revision.
            string ext = Path.GetExtension(filePath).ToLower();
            string swSubFolder = ext == ".sldprt" ? "PARTS"
                               : ext == ".sldasm" ? "ASSEMBLIES"
                               : ext == ".slddrw" ? "DRAWINGS"
                               : "PARTS";
            string swArchive = Path.Combine(ObsFolder, swSubFolder);
            Directory.CreateDirectory(swArchive);
            string archiveName =
                Path.GetFileNameWithoutExtension(filePath) +
                " REV " + currentRev +
                Path.GetExtension(filePath);
            ArchiveCopy(filePath, Path.Combine(swArchive, archiveName));

            // If the drawing is open, close it BEFORE we close/reopen the part.
            // When a drawing is open and references the part, OpenDoc6 on the part
            // may load it as a lightweight reference of the drawing (read-only
            // context) rather than a standalone writable document, causing Save3
            // to silently fail and leaving the revision letter unchanged on disk.
            // We reopen it below once the model is saved and returned to WIP.
            bool drwWasOpen = false;
            string drwPreClose = ext != ".slddrw" ? FindDrawingPath(filePath) : null;
            if (drwPreClose != null)
            {
                ModelDoc2 openDrwCheck = PDMLiteAddin.SwApp
                    ?.GetOpenDocumentByName(drwPreClose) as ModelDoc2;
                if (openDrwCheck != null)
                {
                    drwWasOpen = true;
                    try { PDMLiteAddin.SwApp.CloseDoc(drwPreClose); } catch { }
                }
            }

            // CRITICAL: the file was opened read-only (it was Released), so
            // SOLIDWORKS keeps it in read-only mode internally and silently
            // refuses to Save3 — the revision bump would update memory but never
            // reach disk. We must remove the OS read-only flag and reopen the
            // document as WRITABLE *before* bumping the revision and saving.
            SetReadOnly(filePath, false);
            PDMLiteAddin.SwApp.CloseDoc(filePath);
            int oErr = 0, oWarn = 0;
            ModelDoc2 fresh = PDMLiteAddin.SwApp.OpenDoc6(filePath, reopenType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref oErr, ref oWarn) as ModelDoc2;

            // OpenDoc6 can return null or the wrong doc on some SW versions even
            // when the file opened successfully (e.g. another doc becomes ActiveDoc
            // during the reopen). Always verify by path.
            if (fresh == null || !string.Equals(
                    fresh.GetPathName(), filePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                fresh = PDMLiteAddin.SwApp.GetOpenDocumentByName(filePath) as ModelDoc2;
            }

            if (fresh == null)
            {
                MessageBox.Show(
                    "Could not reopen the file to start the new revision:\n" +
                    filePath + "\n\nPlease open it manually and try again.",
                    "BCore PDM — New Revision Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Bump revision on the now-writable document and save. This save
            // happens while the file is still "Released" in the DB, so it must
            // bypass the released-file lock.
            PropertyValidator.SetProperty(fresh, "Revision", nextRev);
            PDMLiteAddin.SuppressSaveValidation = true;
            try { fresh.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, 0, 0); }
            finally { PDMLiteAddin.SuppressSaveValidation = false; }

            // Reset to WIP — source path only (no separate entry for RELEASED copy)
            DatabaseManager.UnlockFile(filePath);
            DatabaseManager.SetFileStatus(filePath, "WIP", user,
                "New revision started: REV " + nextRev);

            // ── Auto-start the associated drawing revision (Option B) ─────
            // Done AFTER the model is writable/bumped so the drawing reopens
            // against the new model state. Skipped if THIS file is a drawing
            // (it was already handled as the primary file above).
            string drwSummary = ext == ".slddrw"
                ? "n/a (this file is a drawing)"
                : StartDrawingRevisionWith(filePath, currentRev, nextRev, user);

            // Reopen the drawing if we pre-closed it above. By this point
            // the drawing's read-only flag is cleared and the DB is WIP, so
            // it reopens as a writable document referencing the updated part.
            if (drwWasOpen && drwPreClose != null)
            {
                try
                {
                    int eDrw = 0, wDrw = 0;
                    PDMLiteAddin.SwApp.OpenDoc6(drwPreClose,
                        (int)swDocumentTypes_e.swDocDRAWING,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref eDrw, ref wDrw);
                }
                catch { }
            }

            // ── Warn about parent assemblies that use this file ───────────
            List<string> parents = GetParentAssemblies(filePath);

            // ── Build summary message ─────────────────────────────────────
            string msg = "Revision bumped to REV " + nextRev +
                ".\nFile is now back in WIP and ready to edit.\n";

            msg += "\nDrawing: " + (drwSummary ?? "none found (no matching .slddrw)");

            if (parents.Count > 0)
                msg += "\n\nUsed in these assemblies — review and re-release " +
                       "when ready:\n  • " + string.Join("\n  • ", parents);

            MessageBox.Show(msg, "BCore PDM",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── HELPERS ───────────────────────────────────────────────────────
        private static bool IsMaster(string user) =>
            DatabaseManager.GetUserRole(user) == "Master";

        private static void NotMaster() =>
            MessageBox.Show(
                "This action requires Master user privileges.\n" +
                "Contact your administrator.",
                "BCore PDM — Access Denied",
                MessageBoxButtons.OK,
                MessageBoxIcon.Stop);

        private static string GetNextRevision(string current)
        {
            string[] revs = {
                "A","B","C","D","E","F","G","H","J",
                "K","L","M","N","P","R","T","U","V","W","Y","Z"
            };
            int idx = Array.IndexOf(revs, current.ToUpper());
            return idx >= 0 && idx < revs.Length - 1
                ? revs[idx + 1] : current + "1";
        }

        // ════════════════════════════════════════════════════════════════
        //  Drawing / Assembly linkage helpers
        // ════════════════════════════════════════════════════════════════

        // Locate the drawing for a part/assembly by the convention that the
        // drawing shares the model's base filename (e.g. TEST 1.sldprt →
        // TEST 1.slddrw). Searches the model's own folder first, then every
        // WIP division. Returns null if no matching drawing exists.
        private static string FindDrawingPath(string modelPath)
        {
            try
            {
                if (string.IsNullOrEmpty(modelPath)) return null;
                string name = Path.GetFileNameWithoutExtension(modelPath);
                string dir = Path.GetDirectoryName(modelPath);

                var searchDirs = new List<string>();
                if (!string.IsNullOrEmpty(dir)) searchDirs.Add(dir);
                foreach (string div in DatabaseManager.WipDivisions)
                    searchDirs.Add(Path.Combine(WipFolder, div));

                foreach (string sd in searchDirs)
                {
                    string candidate = Path.Combine(sd, name + ".slddrw");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        // Returns filenames of tracked assemblies that reference the given file
        // (directly or via a sub-assembly). Best-effort: relies on the
        // reference paths stored inside each assembly matching the vault path
        // format. GetDocumentDependencies2 reads the file from disk WITHOUT
        // opening it in the UI.
        private static List<string> GetParentAssemblies(string filePath)
        {
            var parents = new List<string>();
            try
            {
                string target;
                try { target = Path.GetFullPath(filePath); }
                catch { target = filePath; }

                foreach (string asmPath in
                         DatabaseManager.GetTrackedFilePathsByExtension(".sldasm"))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(asmPath), target,
                                StringComparison.OrdinalIgnoreCase))
                            continue; // skip self
                    }
                    catch { }
                    if (!File.Exists(asmPath)) continue;

                    // Returns a string array alternating: name, path, name, path…
                    object depsObj = PDMLiteAddin.SwApp.GetDocumentDependencies2(
                        asmPath, true, true, false);
                    string[] deps = depsObj as string[];
                    if (deps == null) continue;

                    for (int i = 1; i < deps.Length; i += 2)
                    {
                        if (string.IsNullOrEmpty(deps[i])) continue;
                        string depPath;
                        try { depPath = Path.GetFullPath(deps[i]); }
                        catch { depPath = deps[i]; }
                        if (string.Equals(depPath, target,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            parents.Add(Path.GetFileName(asmPath));
                            break;
                        }
                    }
                }
            }
            catch { }
            return parents;
        }

        // When a part/assembly starts a new revision, its drawing (same
        // basename) must follow (Option B — automatic). The drawing's revision
        // LETTER syncs to the model at drawing-release time, so here we only
        // archive the released drawing at the OLD rev and return it to WIP for
        // editing. Returns a human-readable summary line, or null if there is
        // no drawing at all.
        private static string StartDrawingRevisionWith(
            string modelPath, string currentRev, string nextRev, string user)
        {
            string drwPath = FindDrawingPath(modelPath);
            if (drwPath == null) return null; // no drawing — nothing to do

            string drwName = Path.GetFileName(drwPath);
            string drwStatus = DatabaseManager.GetFileStatusByName(drwPath);
            string result;

            try
            {
                if (drwStatus == "Released")
                {
                    // Archive the released drawing at the OLD rev as a matched pair
                    // with the model archive.
                    string drwArchive = Path.Combine(ObsFolder, "DRAWINGS");
                    Directory.CreateDirectory(drwArchive);
                    string archiveName =
                        Path.GetFileNameWithoutExtension(drwPath) +
                        " REV " + currentRev + Path.GetExtension(drwPath);
                    ArchiveCopy(drwPath, Path.Combine(drwArchive, archiveName));

                    // Return the WIP drawing to an editable state.
                    SetReadOnly(drwPath, false);
                    DatabaseManager.UnlockFile(drwPath);
                    DatabaseManager.SetFileStatus(drwPath, "WIP", user,
                        "New revision started with model (auto)");
                    result = drwName + " — returned to WIP for editing";
                }
                else
                {
                    result = drwName + " — already editable (" +
                             (string.IsNullOrEmpty(drwStatus) ? "WIP" : drwStatus) + ")";
                }

                // Bump the drawing's Revision property to match the model's new
                // revision immediately. The drawing is closed here (pre-closed in
                // StartNewRevision to prevent the part reopening as a lightweight
                // reference), so we open it silently, set the property, save, and
                // close. If the user had it open, StartNewRevision reopens it after.
                try
                {
                    int e2 = 0, w2 = 0;
                    ModelDoc2 drwDoc = PDMLiteAddin.SwApp.OpenDoc6(drwPath,
                        (int)swDocumentTypes_e.swDocDRAWING,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref e2, ref w2) as ModelDoc2;
                    if (drwDoc == null)
                        drwDoc = PDMLiteAddin.SwApp
                            .GetOpenDocumentByName(drwPath) as ModelDoc2;
                    if (drwDoc != null)
                    {
                        PropertyValidator.SetProperty(drwDoc, "Revision", nextRev);
                        PDMLiteAddin.SuppressSaveValidation = true;
                        try { drwDoc.Save3(
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent, 0, 0); }
                        finally { PDMLiteAddin.SuppressSaveValidation = false; }
                        PDMLiteAddin.SwApp.CloseDoc(drwPath);
                    }
                }
                catch { }

                return result;
            }
            catch (Exception ex)
            {
                return drwName + " — could not auto-revise (" + ex.Message + ")";
            }
        }

        // Heuristic SOLIDWORKS-Toolbox detection: standard Toolbox parts live
        // under a "\Toolbox\" folder. Version-independent and safe. The primary
        // classification mechanism is the PartType property; this is a secondary
        // net for parts pulled from the SW Toolbox library.
        private static bool IsToolboxComponent(Component2 comp)
        {
            try
            {
                string p = comp.GetPathName();
                return !string.IsNullOrEmpty(p) &&
                    p.IndexOf(@"\Toolbox\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // Reads the PartType property (Manufactured/Purchased) from a loaded
        // component. Returns "" if it cannot be read (lightweight/suppressed).
        private static string GetComponentPartType(Component2 comp)
        {
            try
            {
                ModelDoc2 cd = comp.GetModelDoc2() as ModelDoc2;
                if (cd == null) return "";
                return PropertyValidator.GetProperty(cd, "PartType");
            }
            catch { return ""; }
        }

        // Evaluates every component of an assembly against the drawing-release
        // gate. Populates:
        //   blockers — components whose drawing exists but is NOT Released
        //   warnings — Manufactured components with NO drawing (override allowed)
        // Toolbox and Purchased-with-no-drawing components are skipped silently.
        private static void EvaluateAssemblyDrawings(
            ModelDoc2 doc, out List<string> blockers, out List<string> warnings)
        {
            blockers = new List<string>();
            warnings = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                AssemblyDoc asm = (AssemblyDoc)doc;
                object[] components = (object[])asm.GetComponents(false);
                if (components == null) return;

                foreach (object obj in components)
                {
                    Component2 comp = obj as Component2;
                    if (comp == null) continue;
                    if (comp.IsSuppressed()) continue;

                    string path = comp.GetPathName();
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!seen.Add(path)) continue;      // dedupe repeated instances
                    if (IsToolboxComponent(comp)) continue;

                    string compName = Path.GetFileName(path);
                    string drwPath = FindDrawingPath(path);

                    if (drwPath != null)
                    {
                        // Drawing exists → must be Released (Purchased or not).
                        string st = DatabaseManager.GetFileStatusByName(drwPath);
                        if (st != "Released")
                            blockers.Add(Path.GetFileName(drwPath) + " — " +
                                (string.IsNullOrEmpty(st) ? "WIP" : st));
                    }
                    else
                    {
                        // No drawing → only warn for Manufactured parts.
                        string pType = GetComponentPartType(comp);
                        if (!string.Equals(pType, "Purchased",
                                StringComparison.OrdinalIgnoreCase))
                            warnings.Add(compName);
                    }
                }
            }
            catch { }
        }

        // ── Move old exports to archive before releasing new revision ─
        private static void ArchiveOldExports(string fileIdentifier,
                                               bool isDrawing)
        {
            try
            {
                string pdfExport = Path.Combine(ExportRoot, "PDF");
                string stepExport = Path.Combine(ExportRoot, "STEP");
                string pdfArchive = Path.Combine(ObsFolder, "PDF");
                string stepArchive = Path.Combine(ObsFolder, "STEP");

                Directory.CreateDirectory(pdfArchive);
                Directory.CreateDirectory(stepArchive);

                // Move old PDFs matching this identifier
                if (Directory.Exists(pdfExport))
                {
                    foreach (string file in Directory.GetFiles(
                        pdfExport, fileIdentifier + "*.pdf"))
                    {
                        string dest = Path.Combine(pdfArchive,
                            Path.GetFileName(file));
                        if (File.Exists(dest))
                            File.Delete(dest);
                        File.Move(file, dest);
                    }
                }

                // Move old STEP files matching this identifier
                if (!isDrawing && Directory.Exists(stepExport))
                {
                    foreach (string file in Directory.GetFiles(
                        stepExport, fileIdentifier + "*.step"))
                    {
                        string dest = Path.Combine(stepArchive,
                            Path.GetFileName(file));
                        if (File.Exists(dest))
                            File.Delete(dest);
                        File.Move(file, dest);
                    }
                }
            }
            catch { }
        }
        // ── ROLLBACK REVISION ─────────────────────────────────────────────
        public static void RollbackRevision(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath).ToLower();

            if (!IsMaster(user)) { NotMaster(); return; }

            // Check file has been saved to disk
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show(
                    "Please save the file before releasing it.",
                    "BCore PDM — Release Blocked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                return;
            }

            // Find correct archive subfolder
            string subFolder = ext == ".sldprt" ? "PARTS"
                             : ext == ".sldasm" ? "ASSEMBLIES"
                             : ext == ".slddrw" ? "DRAWINGS"
                             : "PARTS";

            string archivePath = Path.Combine(ObsFolder, subFolder);

            if (!Directory.Exists(archivePath))
            {
                MessageBox.Show(
                    "No archived versions found for this file.",
                    "BCore PDM — Rollback",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Find all archived revisions matching this filename
            string[] archivedFiles = Directory.GetFiles(
                archivePath, fileName + " REV *" + ext);

            if (archivedFiles.Length == 0)
            {
                MessageBox.Show(
                    "No archived revisions found for:\n" + fileName + "\n\n" +
                    "Archive folder checked:\n" + archivePath,
                    "BCore PDM — Rollback",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Get current revision
            string currentRev = PropertyValidator.GetProperty(doc, "Revision");

            // Show rollback dialog
            var dialog = new RollbackDialog(archivedFiles, currentRev);
            if (dialog.ShowDialog() != DialogResult.OK) return;

            string selectedFile = dialog.SelectedFile;
            string targetRev = dialog.SelectedRevision;

            // Final confirmation
            var confirm = MessageBox.Show(
                "Confirm Rollback?\n\n" +
                "Restore  :  " + targetRev + "\n" +
                "Archive  :  Current REV " + currentRev + "\n\n" +
                "Current file will be archived.\n" +
                "You will need to reopen the file after rollback.",
                "BCore PDM — Confirm Rollback",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            try
            {
                // ── Step 1: Remove read-only from current files ───────────
                SetReadOnly(filePath, false);
                string releasedCopy = Path.Combine(RelFolder,
                    Path.GetFileName(filePath));
                if (File.Exists(releasedCopy))
                    SetReadOnly(releasedCopy, false);

                // ── Step 2: Archive current version ───────────────────────
                string currentArchiveName = fileName +
                    " REV " + currentRev + ext;
                string currentArchivePath = Path.Combine(
                    archivePath, currentArchiveName);
                ArchiveCopy(filePath, currentArchivePath);

                // ── Step 3: Remove read-only from archived file ───────────
                SetReadOnly(selectedFile, false);

                // ── Step 4: Restore selected revision to active location ──
                File.Copy(selectedFile, filePath, overwrite: true);

                // ── Step 5: Restore to RELEASED folder ───────────────────
                if (File.Exists(releasedCopy))
                    File.Copy(selectedFile, releasedCopy, overwrite: true);

                // ── Step 6: Set read-only on restored files ───────────────
                SetReadOnly(filePath, true);
                SetReadOnly(releasedCopy, true);

                // ── Step 7: Archive old exports ───────────────────────────────
                // Use actual PartNo (not filename) to match STEP/PDF naming
                string partNo = PropertyValidator.GetProperty(doc, "PartNo");
                string partNoClean = partNo.Replace(".", "");
                string drawingNo = PropertyValidator.GetProperty(doc, "DrawingNo");
                CleanupExportsOnRollback(partNoClean, drawingNo);

                // ── Step 8: Update database ───────────────────────────────
                DatabaseManager.LockFile(filePath, user);
                DatabaseManager.SetFileStatus(filePath, "Released", user,
                    "Rolled back to " + targetRev);

                // ── Step 9: Offer to roll back the matching drawing ───────
                // Drawings are archived as a matched pair with the model, so a
                // drawing archive at the same revision should exist if one was
                // ever released. Restoring it keeps part + drawing consistent.
                // Skipped when THIS file is itself a drawing (already restored).
                string drwSummary = ext == ".slddrw"
                    ? "n/a (this file is a drawing)"
                    : "no matching drawing archive found";
                string targetLetter = targetRev
                    .Replace("REV", "").Replace("rev", "").Trim();
                string drwArchiveDir = Path.Combine(ObsFolder, "DRAWINGS");
                string drwArchiveFile = Path.Combine(drwArchiveDir,
                    fileName + " REV " + targetLetter + ".slddrw");

                if (ext != ".slddrw" && File.Exists(drwArchiveFile))
                {
                    var drwChoice = MessageBox.Show(
                        "A matching drawing archive was found:\n" +
                        Path.GetFileName(drwArchiveFile) + "\n\n" +
                        "Roll the drawing back to " + targetRev + " as well?",
                        "BCore PDM — Roll Back Drawing",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                    if (drwChoice == DialogResult.Yes)
                    {
                        try
                        {
                            string drwTarget = FindDrawingPath(filePath) ??
                                Path.Combine(Path.GetDirectoryName(filePath),
                                    fileName + ".slddrw");

                            // Archive the current drawing before overwriting it.
                            if (File.Exists(drwTarget))
                            {
                                SetReadOnly(drwTarget, false);
                                ArchiveCopy(drwTarget, Path.Combine(drwArchiveDir,
                                    fileName + " REV " + currentRev + ".slddrw"));
                            }

                            // Restore the archived drawing to the WIP path.
                            SetReadOnly(drwArchiveFile, false);
                            File.Copy(drwArchiveFile, drwTarget, overwrite: true);

                            // Mirror into the RELEASED folder snapshot.
                            string drwReleased = Path.Combine(RelFolder,
                                fileName + ".slddrw");
                            if (File.Exists(drwReleased))
                            {
                                SetReadOnly(drwReleased, false);
                                File.Copy(drwArchiveFile, drwReleased, overwrite: true);
                                SetReadOnly(drwReleased, true);
                            }

                            SetReadOnly(drwTarget, true);
                            DatabaseManager.LockFile(drwTarget, user);
                            DatabaseManager.SetFileStatus(drwTarget, "Released",
                                user, "Drawing rolled back to " + targetRev);

                            drwSummary = Path.GetFileName(drwTarget) +
                                " → " + targetRev;
                        }
                        catch (Exception dex)
                        {
                            drwSummary = "drawing rollback failed: " + dex.Message;
                        }
                    }
                    else
                    {
                        drwSummary = "drawing left unchanged (you declined)";
                    }
                }

                // ── Step 10: Warn about parent assemblies ─────────────────
                List<string> parents = GetParentAssemblies(filePath);

                string rbMsg =
                    "Rollback Successful!\n\n" +
                    "Restored : " + targetRev + "\n" +
                    "Archived : REV " + currentRev + "\n\n" +
                    "Drawing: " + drwSummary + "\n\n" +
                    "Please close and reopen the file\nto see the restored version.";

                if (parents.Count > 0)
                    rbMsg += "\n\nUsed in these assemblies — review when ready:" +
                             "\n  • " + string.Join("\n  • ", parents);

                MessageBox.Show(rbMsg,
                    "BCore PDM — Rollback Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Rollback failed:\n\n" + ex.Message,
                    "BCore PDM — Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        // ── Get list of unreleased components in an assembly ──────────
        private static List<string> GetUnreleasedComponents(ModelDoc2 doc)
        {
            var unreleased = new List<string>();
            try
            {
                AssemblyDoc asm = (AssemblyDoc)doc;
                object[] components = (object[])asm.GetComponents(false);

                if (components == null) return unreleased;

                foreach (object obj in components)
                {
                    Component2 comp = (Component2)obj;
                    if (comp == null) continue;

                    string path = comp.GetPathName();
                    if (string.IsNullOrEmpty(path)) continue;

                    // Skip suppressed or lightweight components
                    if (comp.IsSuppressed()) continue;

                    string status = DatabaseManager.GetFileStatus(path);
                    string fileName = Path.GetFileName(path);

                    if (status != "Released")
                    {
                        string statusLabel = string.IsNullOrEmpty(status)
                            ? "WIP" : status;
                        unreleased.Add(fileName + "  —  " + statusLabel);
                    }
                }
            }
            catch { }
            return unreleased;
        }
        // ── REQUEST REVISION — Engineer requests a new revision ───────
        public static void RequestRevision(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            string fileName = Path.GetFileName(filePath);
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");
            string rev = PropertyValidator.GetProperty(doc, "Revision");

            string note = ShowNoteDialog("Request Revision",
                "Describe the changes needed (optional):");
            if (note == null) return;

            DatabaseManager.AddRevisionRequest(filePath, user, note);
            EmailManager.NotifyRequestSubmitted("Revision", fileName, partNo, rev, user, note);
            MessageBox.Show(
                "Revision request submitted!\n\nFile    : " + fileName +
                "\nPart No : " + partNo + "\nRev     : REV " + rev +
                "\n\nThe Master will be notified.",
                "BCore PDM — Request Submitted",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── REQUEST UNLOCK — Engineer requests master to unlock a file ──
        public static void RequestUnlock(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            string fileName = Path.GetFileName(filePath);
            string note = ShowNoteDialog("Request Unlock",
                "Reason for unlock request (optional):");
            if (note == null) return;

            DatabaseManager.AddUnlockRequest(filePath, user, note);
            EmailManager.NotifyRequestSubmitted("Unlock", fileName, "", "", user, note);
            MessageBox.Show(
                "Unlock request submitted!\n\nFile : " + fileName +
                "\n\nThe Master will be notified.",
                "BCore PDM — Request Submitted",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── REQUEST RELEASE — Engineer requests master to release a file ─
        public static void RequestRelease(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            string fileName = Path.GetFileName(filePath);
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");
            string rev = PropertyValidator.GetProperty(doc, "Revision");
            string note = ShowNoteDialog("Request Release",
                "Notes for master (optional):");
            if (note == null) return;

            DatabaseManager.AddReleaseRequest(filePath, user, note);
            EmailManager.NotifyRequestSubmitted("Release", fileName, partNo, rev, user, note);
            MessageBox.Show(
                "Release request submitted!\n\nFile    : " + fileName +
                "\nPart No : " + partNo + "\nRev     : REV " + rev +
                "\n\nThe Master will be notified.",
                "BCore PDM — Request Submitted",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── OPEN OR CREATE DRAWING for current part/assembly ──────────
        public static void OpenOrCreateDrawing(ModelDoc2 doc)
        {
            string filePath = doc.GetPathName();
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show("Please save the file first.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dir = Path.GetDirectoryName(filePath);
            string name = Path.GetFileNameWithoutExtension(filePath);

            // Build search list: same folder as the part first (fastest, most
            // common case), then each WIP division subfolder as a fallback in
            // case the drawing was saved in a different division than the part.
            var searchDirs = new System.Collections.Generic.List<string> { dir };
            foreach (string div in DatabaseManager.WipDivisions)
                searchDirs.Add(Path.Combine(WipFolder, div));

            foreach (string searchDir in searchDirs)
            {
                string candidate = Path.Combine(searchDir, name + ".slddrw");
                if (File.Exists(candidate))
                {
                    int errs = 0, warnings = 0;
                    PDMLiteAddin.SwApp.OpenDoc6(candidate,
                        (int)swDocumentTypes_e.swDocDRAWING,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref errs, ref warnings);
                    return;
                }
            }

            // No drawing found — prompt user
            var result = MessageBox.Show(
                "No drawing found for:\n" + name +
                "\n\nWould you like to create a new drawing in SOLIDWORKS?",
                "BCore PDM — Open Drawing",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
                PDMLiteAddin.SwApp.NewDocument(
                    PDMLiteAddin.SwApp.GetUserPreferenceStringValue(
                        (int)swUserPreferenceStringValue_e
                            .swDefaultTemplateDrawing),
                    0, 0, 0);
        }

        // ── VIEW MY REQUESTS — Engineer sees their submitted requests ──
        public static void ViewMyRequests()
        {
            string user = PDMLiteAddin.CurrentUser;
            var requests = DatabaseManager.GetRequestsByUser(user);

            if (requests.Count == 0)
            {
                MessageBox.Show("You have no submitted requests.",
                    "BCore PDM — My Requests",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Your Submitted Requests");
            sb.AppendLine(new string('─', 40));
            foreach (var req in requests)
            {
                sb.AppendLine($"● [{req.RequestType}]  {req.FileName}");
                if (DateTime.TryParse(req.RequestDate, out DateTime dt))
                    sb.AppendLine($"  Submitted: {dt:dd/MM/yy HH:mm}");
                sb.AppendLine($"  Status: {req.Status}");
                if (!string.IsNullOrEmpty(req.Note))
                    sb.AppendLine($"  Note: {req.Note}");
                sb.AppendLine();
            }

            MessageBox.Show(sb.ToString().TrimEnd(),
                "BCore PDM — My Requests",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── Shared note input dialog ──────────────────────────────────
        // Returns null if user cancelled, otherwise the note text (may be empty)
        private static string ShowNoteDialog(string title, string prompt)
        {
            string note = null;
            float scale = 1f;
            using (var tmpG = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                scale = tmpG.DpiX / 96f;

            int SC(float v) => (int)(v * scale);
            int dlgW = SC(280);
            int padX = SC(12);
            int innerW = dlgW - padX * 2;

            Font fDlgLabel = new Font("Segoe UI", 4f * scale);
            Font fDlgBtn = new Font("Segoe UI", 4.5f * scale, FontStyle.Bold);

            Form noteForm = new Form
            {
                Text = "BCore PDM — " + title,
                Width = dlgW,
                Height = SC(185),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Color.FromArgb(245, 247, 250)
            };

            Panel dlgHeader = new Panel
            {
                BackColor = Color.FromArgb(44, 85, 128),
                Location = new Point(0, 0),
                Width = dlgW, Height = SC(26)
            };
            dlgHeader.Controls.Add(new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 4.5f * scale, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(0, 0),
                AutoSize = false, Width = dlgW, Height = SC(26),
                TextAlign = ContentAlignment.MiddleCenter
            });
            noteForm.Controls.Add(dlgHeader);

            noteForm.Controls.Add(new Label
            {
                Text = prompt,
                Font = fDlgLabel,
                ForeColor = Color.FromArgb(60, 60, 60),
                Location = new Point(padX, SC(34)),
                AutoSize = false, Width = innerW, Height = SC(18)
            });

            TextBox noteTb = new TextBox
            {
                Font = fDlgLabel,
                Location = new Point(padX, SC(54)),
                Width = innerW, Height = SC(45),
                Multiline = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White
            };
            noteForm.Controls.Add(noteTb);

            Button noteOk = new Button
            {
                Text = "Submit",
                Font = fDlgBtn,
                Location = new Point(padX, SC(108)),
                Width = SC(128), Height = SC(28),
                BackColor = Color.FromArgb(44, 85, 128),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            noteOk.FlatAppearance.BorderSize = 0;
            noteOk.Click += (s, e) =>
            {
                note = noteTb.Text.Trim();
                noteForm.DialogResult = DialogResult.OK;
                noteForm.Close();
            };
            noteForm.Controls.Add(noteOk);

            Button noteCancel = new Button
            {
                Text = "Cancel",
                Font = fDlgBtn,
                Location = new Point(SC(148), SC(108)),
                Width = SC(80), Height = SC(28),
                BackColor = Color.FromArgb(220, 220, 220),
                ForeColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat
            };
            noteCancel.FlatAppearance.BorderSize = 0;
            noteCancel.Click += (s, e) =>
            {
                noteForm.DialogResult = DialogResult.Cancel;
                noteForm.Close();
            };
            noteForm.Controls.Add(noteCancel);

            return noteForm.ShowDialog() == DialogResult.OK ? note : null;
        }

        // ── APPROVE REQUEST — Master approves and starts new revision ─
        public static void ApproveRequest(RevisionRequest request)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            ModelDoc2 doc = PDMLiteAddin.SwApp
                .GetOpenDocumentByName(request.FilePath) as ModelDoc2;

            if (doc == null)
            {
                MessageBox.Show(
                    "Please open the file first:\n" + request.FileName +
                    "\n\nThen approve the request.",
                    "BCore PDM — Open File First",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            DatabaseManager.ResolveRequest(request.Id, "Approved");
            EmailManager.NotifyRequestApproved(
                string.IsNullOrEmpty(request.RequestType) ? "Revision" : request.RequestType,
                request.FileName, request.RequestedBy);
            StartNewRevision(doc);
        }

        // ── REJECT REQUEST — Master rejects the request ───────────────
        public static void RejectRequest(RevisionRequest request)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            var confirm = MessageBox.Show(
                "Reject revision request?\n\n" +
                "File      : " + request.FileName + "\n" +
                "Requested : " + request.RequestedBy + "\n" +
                "Note      : " + request.Note,
                "BCore PDM — Reject Request",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            DatabaseManager.ResolveRequest(request.Id, "Rejected");
            EmailManager.NotifyRequestRejected(
                string.IsNullOrEmpty(request.RequestType) ? "Revision" : request.RequestType,
                request.FileName, request.RequestedBy, request.Note);

            MessageBox.Show(
                "Request rejected and removed from pending list.",
                "BCore PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        // ── Get DrawingNo from the referenced Part ────────────────────────
        private static string GetDrawingNo(ModelDoc2 drawingDoc)
        {
            try
            {
                string referencedPath = GetDrawingReferencedModel(drawingDoc);
                if (!string.IsNullOrEmpty(referencedPath))
                {
                    ModelDoc2 refModel = PDMLiteAddin.SwApp
                        .GetOpenDocumentByName(referencedPath) as ModelDoc2;

                    if (refModel != null)
                    {
                        string drawingNo = PropertyValidator.GetProperty(
                            refModel, "DrawingNo");
                        if (!string.IsNullOrEmpty(drawingNo))
                            return drawingNo;
                    }
                }
            }
            catch { }

            // Fallback: use drawing filename
            return Path.GetFileNameWithoutExtension(drawingDoc.GetPathName());
        }

        // ── Get referenced model path from drawing ────────────────────────
        private static string GetDrawingReferencedModel(ModelDoc2 doc)
        {
            try
            {
                DrawingDoc drw = (DrawingDoc)doc;
                SolidWorks.Interop.sldworks.View sheet =
                    (SolidWorks.Interop.sldworks.View)drw.GetFirstView();
                if (sheet == null) return "";

                SolidWorks.Interop.sldworks.View drawingView =
                    (SolidWorks.Interop.sldworks.View)sheet.GetNextView();
                if (drawingView == null) return "";

                ModelDoc2 refDoc = (ModelDoc2)drawingView.ReferencedDocument;
                return refDoc?.GetPathName() ?? "";
            }
            catch { return ""; }
        }
        // ── Move ALL exports for this part to archive on rollback ─────
        private static void CleanupExportsOnRollback(string partNoClean,
                                                      string drawingNo)
        {
            try
            {
                string pdfExport = Path.Combine(ExportRoot, "PDF");
                string stepExport = Path.Combine(ExportRoot, "STEP");
                string pdfArchive = Path.Combine(ObsFolder, "PDF");
                string stepArchive = Path.Combine(ObsFolder, "STEP");

                Directory.CreateDirectory(pdfArchive);
                Directory.CreateDirectory(stepArchive);

                // Move all STEP files matching part number
                if (Directory.Exists(stepExport))
                {
                    foreach (string file in Directory.GetFiles(
                        stepExport, partNoClean + "*.step"))
                    {
                        string dest = Path.Combine(stepArchive,
                            Path.GetFileName(file));
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(file, dest);
                    }
                }

                // Move all PDFs matching drawing number
                if (!string.IsNullOrEmpty(drawingNo) &&
                    Directory.Exists(pdfExport))
                {
                    foreach (string file in Directory.GetFiles(
                        pdfExport, drawingNo + "*.pdf"))
                    {
                        string dest = Path.Combine(pdfArchive,
                            Path.GetFileName(file));
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(file, dest);
                    }
                }
            }
            catch { }
        }
        // ── Set or remove OS-level read-only on a file ────────────────
        private static void SetReadOnly(string filePath, bool readOnly)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                FileAttributes attrs = File.GetAttributes(filePath);

                if (readOnly)
                    File.SetAttributes(filePath,
                        attrs | FileAttributes.ReadOnly);
                else
                    File.SetAttributes(filePath,
                        attrs & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        // ── Copy a file into an archive/destination, overwriting safely ──
        // File.Copy(overwrite:true) throws UnauthorizedAccessException
        // ("Access to the path is denied") when the destination already
        // exists AND is read-only. Archived and released copies are read-only
        // (File.Copy preserves the attribute), so re-archiving the same
        // revision — common during testing or after a rollback — hits this.
        // Clear the flag and delete the stale file first, then copy.
        private static void ArchiveCopy(string source, string dest)
        {
            if (File.Exists(dest))
            {
                SetReadOnly(dest, false);
                File.Delete(dest);
            }
            File.Copy(source, dest);
        }
    }
}