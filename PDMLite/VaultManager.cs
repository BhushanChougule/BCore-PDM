using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PDMLite
{
    // Outcome of a batch operation (bulk release / bulk approve). Names of files
    // that succeeded vs. were skipped (with a short reason), for one summary
    // dialog at the end instead of a popup per file.
    public class BatchResult
    {
        public List<string> Succeeded = new List<string>();
        public List<string> Skipped   = new List<string>();
        public int Total => Succeeded.Count + Skipped.Count;

        public string BuildSummary(string heading)
        {
            var sb = new StringBuilder();
            sb.AppendLine(heading);
            sb.AppendLine();
            sb.AppendLine("Done  (" + Succeeded.Count + "):");
            sb.AppendLine(Succeeded.Count == 0
                ? "   (none)"
                : "   • " + string.Join("\n   • ", Succeeded));
            if (Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Skipped  (" + Skipped.Count + "):");
                sb.AppendLine("   • " + string.Join("\n   • ", Skipped));
            }
            return sb.ToString();
        }
    }

    public static class VaultManager
    {
        private const string WipFolder = @"N:\PDM-SolidWorks\WIP";
        private const string RelFolder = @"N:\PDM-SolidWorks\RELEASED";
        private const string ObsFolder = @"N:\PDM-SolidWorks\ARCHIVE";
        private const string ExportRoot = @"N:\PDM-SolidWorks\EXPORTS";
        private const string ScrapFolder = @"N:\PDM-SolidWorks\SCRAP";

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
            AuditLogger.Log("Lock", user, Path.GetFileName(filePath));

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
            AuditLogger.Log("Unlock", PDMLiteAddin.CurrentUser,
                Path.GetFileName(filePath));

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

        // ── REMOVE FROM VAULT ──────────────────────────────────────────────
        // Master-only. Retires a file: moves its on-disk artifacts (WIP copy,
        // RELEASED snapshot, exports) to the SCRAP folder and deletes the
        // vault.xml record. Files are MOVED, not deleted, so a mistaken removal
        // is recoverable from SCRAP until a Master bulk-purges it.
        //
        // Blocked while Released — a published file must be Unlocked / New-
        // Revisioned first. Orphans (file already deleted on disk) are NOT
        // handled here; they are auto-purged by SearchFiles when encountered,
        // because a deleted file can't be opened to click this button.
        public static void RemoveFromVault(ModelDoc2 doc)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            string filePath = doc?.GetPathName();
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show("Please open and save the file first.",
                    "BCore PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string fileName = Path.GetFileName(filePath);
            string status = DatabaseManager.GetFileStatus(filePath);

            if (string.IsNullOrEmpty(status))
            {
                MessageBox.Show(
                    fileName + " is not tracked in the vault — nothing to remove.",
                    "BCore PDM — Remove from Vault",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.Equals(status, "Released", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Cannot remove a Released file from the vault.\n\n" +
                    "File : " + fileName + "\n\n" +
                    "Unlock or start a New Revision first, then remove it.",
                    "BCore PDM — Remove Blocked",
                    MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return;
            }

            // Capture properties NOW — the doc is closed before files are moved.
            string ext = Path.GetExtension(filePath).ToLower();
            bool isDrawing = ext == ".slddrw";
            string partNo = PropertyValidator.GetProperty(doc, "PartNo");
            string rev = PropertyValidator.GetProperty(doc, "Revision");
            string drawingNo = PropertyValidator.GetProperty(doc, "DrawingNo");

            // Associated drawing (models only). Always scrapped together with
            // the model — a drawing without its referenced model is blank and
            // useless. Released drawings are NOT exempt: the Released status
            // becomes meaningless without the model they document.
            string drwPath = isDrawing ? null : FindDrawingPath(filePath);
            bool removeDrawing = drwPath != null;

            string confirmMsg =
                "Remove " + fileName + " from the vault?\n\n" +
                "Status : " + status + "\n\n" +
                "The file" +
                (removeDrawing
                    ? " and its drawing (" + Path.GetFileName(drwPath) + "),"
                    : "") +
                " their released copies and exports will be MOVED to:\n" +
                ScrapFolder + "\n\n" +
                "The vault record will be deleted. Files stay recoverable in " +
                "SCRAP until purged.";

            if (MessageBox.Show(confirmMsg, "BCore PDM — Remove from Vault",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes) return;

            // Close open documents before moving files on disk (Windows holds a
            // lock on an open file). Close the drawing first — it references the
            // model, so the model can't close while the drawing is open.
            if (drwPath != null)
            {
                var openDrw = PDMLiteAddin.SwApp
                    ?.GetOpenDocumentByName(drwPath) as ModelDoc2;
                if (openDrw != null)
                    try { PDMLiteAddin.SwApp.CloseDoc(drwPath); } catch { }
            }
            try { PDMLiteAddin.SwApp.CloseDoc(filePath); } catch { }

            // Move the primary file's artifacts to SCRAP.
            MoveToScrap(filePath);
            MoveToScrap(Path.Combine(RelFolder, fileName));
            if (isDrawing) ScrapExports(null, drawingNo);      // drawing → PDF only
            else ScrapExports(partNo, drawingNo);             // model   → STEP + PDF
            DatabaseManager.RemoveFileRecord(filePath);
            AuditLogger.Log("RemoveFromVault", user, fileName, partNo, rev,
                "moved to SCRAP");

            // Move the associated drawing's artifacts if the user opted in.
            if (removeDrawing && drwPath != null)
            {
                string drwName = Path.GetFileName(drwPath);
                MoveToScrap(drwPath);
                MoveToScrap(Path.Combine(RelFolder, drwName));
                ScrapExports(null, drawingNo);
                DatabaseManager.RemoveFileRecord(drwPath);
                AuditLogger.Log("RemoveFromVault", user, drwName, partNo, rev,
                    "drawing of " + fileName + ", moved to SCRAP");
            }

            MessageBox.Show(
                fileName + " removed from the vault." +
                (removeDrawing ? "\nAssociated drawing also removed." : "") +
                "\n\nFiles moved to SCRAP:\n" + ScrapFolder,
                "BCore PDM — Remove from Vault",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Move a single file into SCRAP, timestamped so repeated removals of
        // the same name never collide. Clears read-only first (RELEASED copies
        // are read-only). No-op if the file isn't there. Returns dest or null.
        private static string MoveToScrap(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return null;
                Directory.CreateDirectory(ScrapFolder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string baseName = Path.GetFileNameWithoutExtension(filePath)
                    + "_" + stamp;
                string ext = Path.GetExtension(filePath);

                // Guarantee a unique destination — the WIP copy and RELEASED
                // snapshot share a basename and hit the same 1-second stamp in
                // one removal; never overwrite, append a counter instead.
                string dest = Path.Combine(ScrapFolder, baseName + ext);
                int n = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(ScrapFolder, baseName + "_" + (n++) + ext);

                SetReadOnly(filePath, false);

                // SOLIDWORKS may not release the file handle the instant
                // CloseDoc returns; retry the move briefly so the file doesn't
                // get stranded in WIP with its record already gone.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try { File.Move(filePath, dest); return dest; }
                    catch (IOException)
                    {
                        if (attempt == 3) throw;
                        System.Threading.Thread.Sleep(200);
                    }
                }
                return dest;
            }
            catch { return null; }
        }

        // Move a file's current exports to SCRAP. STEP files are named by the
        // dotless part number; PDFs by the drawing number — match the same
        // prefixes the exporter uses.
        private static void ScrapExports(string partNo, string drawingNo)
        {
            try
            {
                string stepExport = Path.Combine(ExportRoot, "STEP");
                string pdfExport = Path.Combine(ExportRoot, "PDF");

                string partNoClean = (partNo ?? "").Replace(".", "");
                if (!string.IsNullOrEmpty(partNoClean) &&
                    Directory.Exists(stepExport))
                    foreach (string f in Directory.GetFiles(
                        stepExport, partNoClean + "*.step"))
                        MoveToScrap(f);

                if (!string.IsNullOrEmpty(drawingNo) &&
                    Directory.Exists(pdfExport))
                    foreach (string f in Directory.GetFiles(
                        pdfExport, drawingNo + "*.pdf"))
                        MoveToScrap(f);
            }
            catch { }
        }

        // ── RELEASE ───────────────────────────────────────────────────────
        // suppressPrompts skips the confirm + success dialogs (used when this
        // file is being released as part of a chained drawing+model release so
        // the Master only confirms once). Validation/blocker dialogs still show.
        public static void ReleaseFile(ModelDoc2 doc, bool suppressPrompts = false)
        {
            string user = PDMLiteAddin.CurrentUser;
            string filePath = doc.GetPathName();
            int docType = doc.GetType();
            bool isDrawing = docType == (int)swDocumentTypes_e.swDocDRAWING;

            // Set when a WIP referenced model was released alongside this drawing
            // (see the drawing gate below). Suppresses this drawing's own confirm
            // dialog and swaps the success message for a combined one.
            bool chainedRelease = false;
            string chainedModelName = null;
            // Hoisted to function scope so the close block can reference it.
            // Set inside the isDrawing gate below.
            string referencedModel = null;

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
                referencedModel = GetDrawingReferencedModel(doc);
                if (!string.IsNullOrEmpty(referencedModel))
                {
                    string modelStatus = DatabaseManager.GetFileStatus(referencedModel);
                    if (modelStatus != "Released")
                    {
                        string modelName = Path.GetFileName(referencedModel);
                        bool isRefAsm = Path.GetExtension(referencedModel)
                            .Equals(".sldasm", StringComparison.OrdinalIgnoreCase);
                        string statusDisplay =
                            string.IsNullOrEmpty(modelStatus) ? "WIP" : modelStatus;

                        // If the referenced model has multiple configs, releasing
                        // it freezes EVERY config (status is file-level) — warn so
                        // the user isn't surprised that sibling configs lock too.
                        int refCfgCount = DatabaseManager
                            .GetConfigsForFile(referencedModel).Count;
                        string multiCfgNote = refCfgCount > 1
                            ? "\n\nNOTE: the " + (isRefAsm ? "assembly" : "part") +
                              " has " + refCfgCount + " configurations — releasing " +
                              "it freezes ALL of them (read-only), and every config " +
                              "must have complete properties to pass validation."
                            : "";

                        var chain = MessageBox.Show(
                            "The referenced " + (isRefAsm ? "Assembly" : "Part") +
                            " is still " + statusDisplay + ".\n\n" +
                            "Release BOTH files now?\n\n" +
                            "  • " + modelName +
                                "  (" + (isRefAsm ? "Assembly" : "Part") + ")\n" +
                            "  • " + Path.GetFileName(filePath) + "  (Drawing)\n\n" +
                            "Each file's properties and references are still " +
                            "validated before release." + multiCfgNote,
                            "BCore PDM — Release Drawing + " +
                                (isRefAsm ? "Assembly" : "Part"),
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                        if (chain != DialogResult.Yes) return;

                        // Open the model if it isn't already.
                        ModelDoc2 refDoc = PDMLiteAddin.SwApp
                            .GetOpenDocumentByName(referencedModel) as ModelDoc2;
                        if (refDoc == null)
                        {
                            int openType = isRefAsm
                                ? (int)swDocumentTypes_e.swDocASSEMBLY
                                : (int)swDocumentTypes_e.swDocPART;
                            int oErrs = 0, oWarn = 0;
                            refDoc = PDMLiteAddin.SwApp.OpenDoc6(
                                referencedModel, openType,
                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                                "", ref oErrs, ref oWarn) as ModelDoc2;
                        }

                        if (refDoc == null)
                        {
                            MessageBox.Show(
                                "Could not open " + modelName + ".\n" +
                                "Open it manually and release it first.",
                                "BCore PDM — Release Blocked",
                                MessageBoxButtons.OK, MessageBoxIcon.Stop);
                            return;
                        }

                        // Release through the normal path — all validations apply,
                        // but suppress its confirm/success dialogs: the Master
                        // already confirmed both files in the prompt above.
                        ReleaseFile(refDoc, suppressPrompts: true);

                        // If the model release was blocked by a validation issue
                        // (missing properties, broken refs, unreleased children),
                        // abort the drawing release too — ReleaseFile already
                        // explained what blocked it.
                        if (DatabaseManager.GetFileStatus(referencedModel) != "Released")
                            return;

                        // Both files confirmed at once — skip this drawing's own
                        // confirm dialog and show a single combined success.
                        chainedRelease = true;
                        chainedModelName = modelName;

                        // The model release closed the drawing (it referenced the
                        // model, blocking CloseDoc(model)) and — being a suppressed
                        // release — did not reopen it. Re-fetch, reopening the
                        // drawing if needed, so the rest of this function has a
                        // live object to finish releasing.
                        doc = PDMLiteAddin.SwApp.GetOpenDocumentByName(filePath)
                            as ModelDoc2;
                        if (doc == null)
                        {
                            int rErrs = 0, rWarn = 0;
                            doc = PDMLiteAddin.SwApp.OpenDoc6(filePath,
                                (int)swDocumentTypes_e.swDocDRAWING,
                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                                "", ref rErrs, ref rWarn) as ModelDoc2;
                        }
                        if (doc == null)
                        {
                            MessageBox.Show(
                                "The " + (isRefAsm ? "Assembly" : "Part") +
                                " was released, but the Drawing could not be " +
                                "reopened.\n\nPlease try releasing the Drawing again.",
                                "BCore PDM — Reopen Failed",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }

                    // If the drawing's model is an ASSEMBLY, the assembly can be
                    // Released while one of its child parts was just unlocked back
                    // to WIP. Releasing the assembly drawing in that state would
                    // publish a PDF whose components are not all Released — so apply
                    // the same child-component gate the assembly itself uses. Read
                    // from disk: the assembly is loaded only as the drawing's
                    // reference (often lightweight) and may not even be reachable
                    // via GetOpenDocumentByName, so the live read is unreliable.
                    if (Path.GetExtension(referencedModel)
                            .Equals(".sldasm", StringComparison.OrdinalIgnoreCase))
                    {
                        var unreleasedChildren =
                            GetUnreleasedComponentsByPath(referencedModel);
                        if (unreleasedChildren.Count > 0)
                        {
                            MessageBox.Show(
                                "Cannot release Assembly Drawing — these " +
                                "components of the referenced assembly are not " +
                                "yet Released:\n\n• " +
                                string.Join("\n• ", unreleasedChildren) + "\n\n" +
                                "Release all components first, then release the " +
                                "Assembly Drawing.",
                                "BCore PDM — Release Blocked",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Stop);
                            return;
                        }
                    }
                }
            }

            // ── Assembly: check all child parts are Released first ────────
            if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                // Read from disk so a lightweight-loaded assembly still reports
                // its WIP children (the live GetComponents read misses them).
                var unreleased = GetUnreleasedComponentsByPath(filePath);
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

            // ── Validate properties — all configurations ─────────────────
            if (!isDrawing)
            {
                var cfgIssues = PropertyValidator.ValidateAllConfigs(doc);
                if (cfgIssues.Count > 0)
                {
                    // Offer PropertyForm for the active config first so the
                    // Master can fix its fields without a separate manual save.
                    string activeCfg = (doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration)?.Name ?? "";
                    if (cfgIssues.ContainsKey(activeCfg))
                    {
                        using (var form = new PropertyForm(
                            doc, cfgIssues[activeCfg]))
                        {
                            form.ShowDialog();
                            if (form.PropertiesSaved)
                                cfgIssues = PropertyValidator.ValidateAllConfigs(doc);
                        }
                    }

                    if (cfgIssues.Count > 0)
                    {
                        var sb2 = new StringBuilder(
                            "Release blocked — required properties incomplete:\n");
                        foreach (var kv in cfgIssues)
                        {
                            sb2.AppendLine();
                            sb2.AppendLine("Config \"" + kv.Key + "\":");
                            sb2.Append("  • " + string.Join("\n  • ", kv.Value));
                        }
                        MessageBox.Show(sb2.ToString(),
                            "BCore PDM — Release Blocked",
                            MessageBoxButtons.OK, MessageBoxIcon.Stop);
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
                        // Read the revision from the SPECIFIC config this drawing
                        // documents — not the model's active config (which may be
                        // any config after a release/export loop switched it).
                        // Mirrors GetDrawingPartNo / GetDrawingNo.
                        string drwCfg = GetDrawingPrimaryConfig(doc);
                        string partRev = !string.IsNullOrEmpty(drwCfg)
                            ? PropertyValidator.GetProperty(refModel, "Revision", drwCfg)
                            : PropertyValidator.GetProperty(refModel, "Revision");
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
            // Skipped when suppressPrompts (chained model release) or chained
            // (the Master already confirmed both files in the chain prompt).
            string fileTypeLabel = isDrawing ? "Drawing No" : "Part No";
            if (!suppressPrompts && !chainedRelease)
            {
                string confirmBody;
                if (!isDrawing)
                {
                    var relCfgsCfm = PropertyValidator.GetConfigNames(doc);
                    if (relCfgsCfm.Count > 1)
                    {
                        var cfgLines = string.Join("\n", relCfgsCfm.Select(c =>
                        {
                            string cp = PropertyValidator.GetProperty(doc, "PartNo", c);
                            string cr = PropertyValidator.GetProperty(doc, "Revision", c);
                            return "  • " + (string.IsNullOrEmpty(cp) ? c : cp)
                                + "   REV " + cr;
                        }));
                        confirmBody =
                            "File          : " + Path.GetFileName(filePath) + "\n\n" +
                            "Configurations:\n" + cfgLines + "\n\n" +
                            "This will:\n" +
                            "  • Auto-fill Part Weight per config\n" +
                            "  • Export STEP per config\n" +
                            "  • Lock file as Released\n" +
                            "  • Log the revision\n\n" +
                            "NOTE: status is file-level — releasing freezes ALL " +
                            (relCfgsCfm.Count) + " configurations (read-only). " +
                            "Use Unlock or New Revision to edit any of them again.";
                    }
                    else
                    {
                        confirmBody =
                            fileTypeLabel + "  : " + partNo + "\n" +
                            "Revision      : REV " + rev + "\n" +
                            "File          : " + Path.GetFileName(filePath) + "\n\n" +
                            "This will:\n" +
                            "  • Auto-fill Part Weight\n  • Export STEP file\n" +
                            "  • Lock file as Released\n" +
                            "  • Log the revision";
                    }
                }
                else
                {
                    confirmBody =
                        fileTypeLabel + "  : " + partNo + "\n" +
                        "Revision      : REV " + rev + "\n" +
                        "File          : " + Path.GetFileName(filePath) + "\n\n" +
                        "This will:\n" +
                        "  • Export PDF\n" +
                        "  • Lock file as Released\n" +
                        "  • Log the revision";
                }

                var confirm = MessageBox.Show(
                    "Release this file?\n\n" + confirmBody,
                    "BCore PDM — Confirm Release",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (confirm != DialogResult.Yes) return;
            }

            // ── Auto-fill fields (all configurations for parts/assemblies) ─
            string checkedByVal = user.Length >= 2
                ? user.Substring(0, 2).ToUpper() : user.ToUpper();
            string checkedDateVal = DateTime.Now.ToString("MM/dd/yyyy");

            if (!isDrawing)
            {
                var relCfgsAF = PropertyValidator.GetConfigNames(doc);
                if (relCfgsAF.Count > 1)
                {
                    // Multi-config: set on every config; mass is config-specific
                    // so switch to each one before reading mass properties.
                    // try/finally guarantees we restore the original config even
                    // if a mass-property read throws mid-loop.
                    string origCfgAF = (doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration)?.Name;
                    try
                    {
                        foreach (string c in relCfgsAF)
                        {
                            PropertyValidator.SetProperty(doc, "CheckedBy",
                                checkedByVal, c);
                            PropertyValidator.SetProperty(doc, "CheckedDate",
                                checkedDateVal, c);
                            doc.ShowConfiguration2(c);
                            PropertyValidator.AutoFillWeight(doc);
                        }
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(origCfgAF))
                            doc.ShowConfiguration2(origCfgAF);
                    }
                }
                else
                {
                    PropertyValidator.SetProperty(doc, "CheckedBy", checkedByVal);
                    PropertyValidator.SetProperty(doc, "CheckedDate", checkedDateVal);
                    PropertyValidator.AutoFillWeight(doc);
                }
            }
            else
            {
                PropertyValidator.SetProperty(doc, "CheckedBy", checkedByVal);
                PropertyValidator.SetProperty(doc, "CheckedDate", checkedDateVal);
            }

            // This programmatic save must bypass the released-file save lock.
            PDMLiteAddin.SuppressSaveValidation = true;
            try { doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, 0, 0); }
            finally { PDMLiteAddin.SuppressSaveValidation = false; }

            // ── Archive old exports + export files ────────────────────────
            if (isDrawing)
            {
                ArchiveOldExports(partNo, isDrawing: true);
                ExportManager.ExportAll(doc, ExportRoot, stamp);
            }
            else
            {
                var relCfgsEx = PropertyValidator.GetConfigNames(doc);
                if (relCfgsEx.Count <= 1)
                {
                    // Single-config: existing path
                    ArchiveOldExports(partNo.Replace(".", ""), isDrawing: false,
                        bomIdentifier: partNo);
                    ExportManager.ExportAll(doc, ExportRoot, stamp);
                }
                else
                {
                    // Multi-config: archive old + export STEP for every config
                    string origCfgEx = (doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration)?.Name;

                    // Archive old STEP files for all configs before exporting new ones.
                    // Pass raw PartNo as bomIdentifier — the BOM file uses the raw
                    // PartNo; only one BOM exists (active config), so only one of
                    // these MoveMatching calls will actually find a match.
                    foreach (string c in relCfgsEx)
                    {
                        string cp = PropertyValidator.GetProperty(doc, "PartNo", c);
                        if (!string.IsNullOrEmpty(cp))
                            ArchiveOldExports(cp.Replace(".", ""), isDrawing: false,
                                bomIdentifier: cp);
                    }

                    // Export one STEP per config (switch → export → next).
                    // try/finally restores the original config even if a SaveAs
                    // throws mid-loop. A HashSet guards against two configs that
                    // share PartNo+Rev silently overwriting each other's STEP.
                    var emittedStamps = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    var collidedStamps = new List<string>();
                    try
                    {
                        foreach (string c in relCfgsEx)
                        {
                            string cp = PropertyValidator.GetProperty(doc, "PartNo", c);
                            string cr = PropertyValidator.GetProperty(doc, "Revision", c);
                            if (string.IsNullOrEmpty(cp)) continue;
                            string cfgStamp = cp.Replace(".", "") + "-R" + cr;
                            if (!emittedStamps.Add(cfgStamp))
                            {
                                // Another config already exported this exact name —
                                // exporting again would overwrite it. Skip + report.
                                collidedStamps.Add(cfgStamp);
                                continue;
                            }
                            doc.ShowConfiguration2(c);
                            ExportManager.ExportStepOnly(doc, ExportRoot, cfgStamp);
                        }
                    }
                    finally
                    {
                        // Restore original config; export flat DXF once (sheet metal)
                        if (!string.IsNullOrEmpty(origCfgEx))
                            doc.ShowConfiguration2(origCfgEx);
                    }
                    ExportManager.ExportFlatPatternOnly(doc, ExportRoot, stamp);

                    if (collidedStamps.Count > 0)
                        MessageBox.Show(
                            "Warning: these STEP exports were SKIPPED because two " +
                            "or more configurations share the same Part No + " +
                            "Revision (they would overwrite each other):\n\n  • " +
                            string.Join("\n  • ", collidedStamps.Distinct()) +
                            "\n\nGive each configuration a unique Part No and " +
                            "release again to export all of them.",
                            "BCore PDM — Duplicate Export Skipped",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            // ── BOM CSV (assemblies only, top-level) ──────────────────────
            // Auto-generated on every assembly release alongside the STEP.
            // Non-fatal — a BOM failure never blocks the release.
            if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
                ExportManager.ExportBom(doc, ExportRoot, partNo, rev);

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
            AuditLogger.Log("Release", user, Path.GetFileName(filePath),
                partNo, rev);

            if (chainedRelease)
            {
                // One combined dialog for the model + drawing released together.
                MessageBox.Show(
                    "Both files Released Successfully!\n\n" +
                    "  • " + chainedModelName + "\n" +
                    "  • " + Path.GetFileName(filePath) + "  (Drawing)\n\n" +
                    "Revision : REV " + rev + "\n" +
                    "Exports saved to:\n" + ExportRoot,
                    "BCore PDM — Released",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else if (!suppressPrompts)
            {
                string successDetail;
                if (!isDrawing)
                {
                    var sucCfgs = PropertyValidator.GetConfigNames(doc);
                    if (sucCfgs.Count > 1)
                    {
                        var lines = string.Join("\n", sucCfgs.Select(c =>
                        {
                            string cp = PropertyValidator.GetProperty(doc, "PartNo", c);
                            string cr = PropertyValidator.GetProperty(doc, "Revision", c);
                            return "  • " + (string.IsNullOrEmpty(cp) ? c : cp)
                                + "   REV " + cr;
                        }));
                        successDetail = "Configurations:\n" + lines;
                    }
                    else
                    {
                        successDetail = fileTypeLabel + " : " + partNo + "  REV " + rev;
                    }
                }
                else
                {
                    successDetail = fileTypeLabel + " : " + partNo + "  REV " + rev;
                }
                MessageBox.Show(
                    "File Released Successfully!\n\n" + successDetail + "\n" +
                    "Exports saved to:\n" + ExportRoot,
                    "BCore PDM — Released",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            // A Released file is pure output — there is no point reopening it
            // read-only afterwards (it only wastes load time and memory and
            // makes the user wait). So we CLOSE the released file instead of
            // reopening it. It opens read-only on demand the next time it is
            // genuinely needed.
            try
            {
                // If this is a part/assembly with its drawing open, the open
                // drawing holds a reference to the model, so CloseDoc(model) is
                // refused. Close the drawing first so the model can close.
                // For an interactive single release we then reopen that drawing
                // — but only if it is still WIP (the user's working file). A
                // Released drawing is never reopened, and in bulk/chained
                // releases (suppressPrompts) nothing is reopened.
                bool reopenWipDrawing = false;
                string drwPath = !isDrawing ? FindDrawingPath(filePath) : null;
                if (drwPath != null)
                {
                    ModelDoc2 openDrwCheck = PDMLiteAddin.SwApp
                        ?.GetOpenDocumentByName(drwPath) as ModelDoc2;
                    if (openDrwCheck != null)
                    {
                        reopenWipDrawing = !suppressPrompts &&
                            DatabaseManager.GetFileStatus(drwPath) != "Released";
                        try { PDMLiteAddin.SwApp.CloseDoc(drwPath); } catch { }
                    }
                }

                PDMLiteAddin.SwApp.CloseDoc(filePath);

                // Chained release: close the referenced model now that the drawing
                // is closed. The model's own ReleaseFile (called with
                // suppressPrompts:true) attempted CloseDoc(model) earlier, but the
                // drawing was still open at that point and SOLIDWORKS refused to
                // close the model while a document was referencing it. Now that
                // the drawing is closed the reference is gone, so the close succeeds.
                if (chainedRelease && !string.IsNullOrEmpty(referencedModel))
                {
                    if (PDMLiteAddin.SwApp?.GetOpenDocumentByName(referencedModel) != null)
                        try { PDMLiteAddin.SwApp.CloseDoc(referencedModel); } catch { }
                }

                if (reopenWipDrawing && drwPath != null)
                {
                    int eDrw = 0, wDrw = 0;
                    PDMLiteAddin.SwApp.OpenDoc6(drwPath,
                        (int)swDocumentTypes_e.swDocDRAWING,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                        "", ref eDrw, ref wDrw);
                }
            }
            catch { }
        }

        // ── NEW REVISION ──────────────────────────────────────────────────
        // suppressPrompts (used by bulk approve): skips the confirm prompt and
        // the final summary dialog only. Blocker/failure dialogs still show.
        public static void StartNewRevision(ModelDoc2 doc, bool suppressPrompts = false)
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

            string filePath   = doc.GetPathName();
            int docTypeSNR    = doc.GetType();
            bool isDrawingSNR = docTypeSNR == (int)swDocumentTypes_e.swDocDRAWING;
            string currentRev = PropertyValidator.GetProperty(doc, "Revision");
            string nextRev    = GetNextRevision(currentRev);
            string partNo     = PropertyValidator.GetProperty(doc, "PartNo");

            // ── Multi-config: per-config revision picker ──────────────────
            var allCfgsSNR = !isDrawingSNR
                ? PropertyValidator.GetConfigNames(doc)
                : new List<string>();
            bool isMultiCfg = allCfgsSNR.Count > 1;

            // Per-config current/next revision maps
            var cfgCurrentRevs = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var cfgNextRevs = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            List<string> selectedCfgs = allCfgsSNR; // default = all configs

            if (isMultiCfg)
            {
                foreach (string c in allCfgsSNR)
                {
                    string r = PropertyValidator.GetProperty(doc, "Revision", c);
                    cfgCurrentRevs[c] = r;
                    cfgNextRevs[c]    = GetNextRevision(r);
                }

                if (!suppressPrompts)
                {
                    using (var picker = new ConfigRevisionPickerForm(
                        allCfgsSNR,
                        allCfgsSNR.Select(c => cfgCurrentRevs[c]).ToList(),
                        allCfgsSNR.Select(c => cfgNextRevs[c]).ToList()))
                    {
                        if (picker.ShowDialog() != DialogResult.OK) return;
                        selectedCfgs = picker.SelectedConfigs ?? new List<string>();
                        if (selectedCfgs.Count == 0)
                        {
                            MessageBox.Show(
                                "No configurations selected.",
                                "BCore PDM — New Revision",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            return;
                        }
                    }
                }
                // suppressPrompts (bulk approve): bump ALL configs, no picker
            }
            else
            {
                // Single-config: existing confirm dialog
                if (!suppressPrompts)
                {
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
                }
            }

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
            string archiveBase = Path.GetFileNameWithoutExtension(filePath);
            string archiveExt  = Path.GetExtension(filePath);
            // Collision-safe (see CollisionSafeArchivePath): a partial multi-
            // config bump can map two different snapshots to the same rev letter.
            string archiveDest = CollisionSafeArchivePath(
                swArchive, archiveBase, currentRev, archiveExt, isMultiCfg);
            ArchiveCopy(filePath, archiveDest);

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
            if (isMultiCfg)
            {
                foreach (string cfgN in selectedCfgs)
                {
                    string nr = cfgNextRevs.ContainsKey(cfgN)
                        ? cfgNextRevs[cfgN] : nextRev;
                    PropertyValidator.SetProperty(fresh, "Revision", nr, cfgN);
                }
            }
            else
            {
                PropertyValidator.SetProperty(fresh, "Revision", nextRev);
            }
            PDMLiteAddin.SuppressSaveValidation = true;
            try { fresh.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, 0, 0); }
            finally { PDMLiteAddin.SuppressSaveValidation = false; }

            // Reset to WIP — source path only (no separate entry for RELEASED copy)
            DatabaseManager.UnlockFile(filePath);
            DatabaseManager.SetFileStatus(filePath, "WIP", user,
                "New revision started: REV " + nextRev);
            AuditLogger.Log("NewRevision", user, Path.GetFileName(filePath),
                partNo, nextRev);

            // ── Auto-start the associated drawing revision (Option B) ─────
            // Done AFTER the model is writable/bumped so the drawing reopens
            // against the new model state. Skipped if THIS file is a drawing.
            string drwSummary;
            if (ext == ".slddrw")
            {
                drwSummary = "n/a (this file is a drawing)";
            }
            else if (isMultiCfg)
            {
                // Map each unique drawing path to the (current,next) revision it
                // should receive. A config-specific drawing gets ITS config's
                // revision (not the active config's); the shared basename drawing
                // covers all configs so it gets the file-level active-config rev.
                // First assignment wins, so the shared drawing keeps the file-
                // level rev even if GetDrawingsForConfig also returns it (covers-
                // all). This fixes config-specific drawings being stamped with the
                // wrong config's revision letter.
                var drwRevs = new Dictionary<string, string[]>(
                    StringComparer.OrdinalIgnoreCase);
                string baseDrw = FindDrawingPath(filePath);
                if (baseDrw != null)
                    drwRevs[baseDrw] = new[] { currentRev, nextRev };
                foreach (string cfgN in selectedCfgs)
                {
                    string cCur = cfgCurrentRevs.ContainsKey(cfgN)
                        ? cfgCurrentRevs[cfgN] : currentRev;
                    string cNext = cfgNextRevs.ContainsKey(cfgN)
                        ? cfgNextRevs[cfgN] : nextRev;
                    foreach (string d in
                        DatabaseManager.GetDrawingsForConfig(filePath, cfgN))
                        if (!string.IsNullOrEmpty(d) && !drwRevs.ContainsKey(d))
                            drwRevs[d] = new[] { cCur, cNext };
                }

                if (drwRevs.Count == 0)
                {
                    drwSummary = "none found";
                }
                else
                {
                    var summaries = new List<string>();
                    foreach (var kv in drwRevs)
                        summaries.Add(StartDrawingRevisionWith(
                            filePath, kv.Value[0], kv.Value[1], user, kv.Key));
                    drwSummary = string.Join("; ", summaries);
                }
            }
            else
            {
                drwSummary =
                    StartDrawingRevisionWith(filePath, currentRev, nextRev, user);
            }

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
            string msg;
            if (isMultiCfg && selectedCfgs.Count > 0)
            {
                var cfgLines = string.Join("\n", selectedCfgs.Select(c =>
                    "  • " + c + ":  REV " +
                    (cfgNextRevs.ContainsKey(c) ? cfgNextRevs[c] : nextRev)));
                msg = "Revisions bumped:\n" + cfgLines +
                      "\nFile is now back in WIP and ready to edit.\n";
            }
            else
            {
                msg = "Revision bumped to REV " + nextRev +
                      ".\nFile is now back in WIP and ready to edit.\n";
            }

            msg += "\nDrawing: " + (drwSummary ?? "none found (no matching .slddrw)");

            if (parents.Count > 0)
                msg += "\n\nUsed in these assemblies — review and re-release " +
                       "when ready:\n  • " + string.Join("\n  • ", parents);

            if (!suppressPrompts)
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
            string modelPath, string currentRev, string nextRev, string user,
            string explicitDrwPath = null)
        {
            string drwPath = explicitDrwPath ?? FindDrawingPath(modelPath);
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
                // PartType is a config-specific property, so read it from the
                // configuration the ASSEMBLY actually references — not the
                // component's active in-memory config, which may be any config
                // (a shared child can be Manufactured in one config, Purchased in
                // another). Falls back to the active-config read when the
                // referenced config is unknown.
                string refCfg = comp.ReferencedConfiguration;
                return !string.IsNullOrEmpty(refCfg)
                    ? PropertyValidator.GetProperty(cd, "PartType", refCfg)
                    : PropertyValidator.GetProperty(cd, "PartType");
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

        // Move every file in srcDir matching pattern into destDir, overwriting
        // a stale same-named archive copy. No-op if srcDir is missing.
        private static void MoveMatching(string srcDir, string destDir,
            string pattern)
        {
            if (!Directory.Exists(srcDir)) return;
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(srcDir, pattern))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
        }

        // ── Move old exports to archive before releasing new revision ─
        // bomIdentifier: raw PartNo for BOM glob (dots preserved). Defaults to
        // fileIdentifier when null (e.g. drawings, which have no BOM).
        private static void ArchiveOldExports(string fileIdentifier,
                                               bool isDrawing,
                                               string bomIdentifier = null)
        {
            try
            {
                string pdfExport = Path.Combine(ExportRoot, "PDF");
                string stepExport = Path.Combine(ExportRoot, "STEP");
                string bomExport = Path.Combine(ExportRoot, "BOM");
                string pdfArchive = Path.Combine(ObsFolder, "PDF");
                string stepArchive = Path.Combine(ObsFolder, "STEP");
                string bomArchive = Path.Combine(ObsFolder, "BOM");

                Directory.CreateDirectory(pdfArchive);
                Directory.CreateDirectory(stepArchive);

                // Move old BOM CSVs matching this assembly's raw PartNo (assemblies
                // only; parts/drawings never produce a BOM, so nothing matches them)
                if (!isDrawing)
                {
                    string bid = bomIdentifier ?? fileIdentifier;
                    MoveMatching(bomExport, bomArchive, bid + "*_BOM.csv");
                }

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

            // Multi-config warning: archives are file-level snapshots, so a
            // rollback restores the WHOLE file (every configuration) to the
            // archived state. Configs that were independently bumped to a higher
            // revision since that archive will lose those changes. Rollback is
            // not config-aware — make the Master acknowledge this before proceeding.
            bool rbMultiCfg = ext != ".slddrw"
                && PropertyValidator.GetConfigNames(doc).Count > 1;
            if (ext != ".slddrw")
            {
                var rbCfgs = PropertyValidator.GetConfigNames(doc);
                if (rbCfgs.Count > 1)
                {
                    var ack = MessageBox.Show(
                        "This file has " + rbCfgs.Count + " configurations.\n\n" +
                        "Rollback restores the ENTIRE file to the archived " +
                        "revision — ALL configurations revert together. Any " +
                        "configuration that was bumped to a newer revision since " +
                        "that archive will LOSE those changes.\n\n" +
                        "Rollback is not per-configuration. Continue?",
                        "BCore PDM — Multi-Config Rollback",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ack != DialogResult.Yes) return;
                }
            }

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
                // Collision-safe: a New Revision may already have archived this
                // file at the same rev letter; don't let the rollback overwrite
                // (destroy) that distinct snapshot for a multi-config file.
                string currentArchivePath = CollisionSafeArchivePath(
                    archivePath, fileName, currentRev, ext, rbMultiCfg);
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
                CleanupExportsOnRollback(partNoClean, drawingNo, partNo);

                // ── Step 8: Update database ───────────────────────────────
                DatabaseManager.LockFile(filePath, user);
                DatabaseManager.SetFileStatus(filePath, "Released", user,
                    "Rolled back to " + targetRev);
                AuditLogger.Log("Rollback", user, Path.GetFileName(filePath),
                    partNo, targetRev);

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

                            // Archive the current drawing before overwriting it
                            // (collision-safe, same rationale as the model above).
                            if (File.Exists(drwTarget))
                            {
                                SetReadOnly(drwTarget, false);
                                ArchiveCopy(drwTarget, CollisionSafeArchivePath(
                                    drwArchiveDir, fileName, currentRev,
                                    ".slddrw", rbMultiCfg));
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
        // Disk-based component release check. Reads the assembly's dependency
        // tree straight from the file via GetDocumentDependencies2 (no UI open
        // required), so it is independent of how the assembly is loaded — a
        // lightweight assembly, or one loaded only as a drawing's reference,
        // would cause the live GetComponents read above to miss WIP children.
        // Returns "<filename>  —  <status>" for every tracked component that is
        // not Released. Toolbox/standard hardware is skipped (not vault-managed).
        // Best-effort: relies on the reference paths stored in the assembly
        // matching the vault path format the DB tracks (same caveat as
        // GetParentAssemblies); an unmatched path reads as WIP and blocks.
        private static List<string> GetUnreleasedComponentsByPath(string asmPath)
        {
            var unreleased = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath))
                    return unreleased;

                string asmNorm;
                try { asmNorm = Path.GetFullPath(asmPath); }
                catch { asmNorm = asmPath; }

                // Alternating array: name, path, name, path…
                object depsObj = PDMLiteAddin.SwApp.GetDocumentDependencies2(
                    asmPath, true, true, false);
                string[] deps = depsObj as string[];
                if (deps == null) return unreleased;

                for (int i = 1; i < deps.Length; i += 2)
                {
                    string path = deps[i];
                    if (string.IsNullOrEmpty(path)) continue;

                    // Skip Toolbox / standard hardware — not vault-managed.
                    if (path.IndexOf("\\Toolbox\\",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    string norm;
                    try { norm = Path.GetFullPath(path); }
                    catch { norm = path; }

                    // Skip the assembly itself and any duplicate instances.
                    if (string.Equals(norm, asmNorm,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(norm)) continue;

                    string ext = Path.GetExtension(norm).ToLower();
                    if (ext != ".sldprt" && ext != ".sldasm") continue;

                    string status = DatabaseManager.GetFileStatus(norm);
                    if (status != "Released")
                    {
                        string statusLabel = string.IsNullOrEmpty(status)
                            ? "WIP" : status;
                        unreleased.Add(Path.GetFileName(norm) + "  —  " + statusLabel);
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
            AuditLogger.Log("RequestRevision", user, fileName, partNo, rev, note);
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
            AuditLogger.Log("RequestUnlock", user, fileName, "", "", note);
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
            AuditLogger.Log("RequestRelease", user, fileName, partNo, rev, note);
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

            string dir           = Path.GetDirectoryName(filePath);
            string modelBasename = Path.GetFileNameWithoutExtension(filePath);

            // Active configuration — relevant for multi-config parts/assemblies
            string activeConfig  = "";
            bool   isMultiConfig = false;
            int    docType       = doc.GetType();
            if (docType != (int)swDocumentTypes_e.swDocDRAWING)
            {
                try
                {
                    var cfg = doc.GetActiveConfiguration()
                        as SolidWorks.Interop.sldworks.Configuration;
                    activeConfig  = cfg?.Name ?? "";
                    isMultiConfig = PropertyValidator.GetConfigNames(doc).Count > 1;
                }
                catch { }
            }

            // Sanitise the config name for use as a filename (config = PartNo by
            // convention, but guard against any characters Windows disallows).
            var   invalidChars   = Path.GetInvalidFileNameChars();
            string safeCfgName   = string.IsNullOrEmpty(activeConfig) ? "" :
                new string(activeConfig.Select(
                    c => invalidChars.Contains(c) ? '_' : c).ToArray());

            // Build an ORDERED list of drawing names to search for:
            //   1. Config-specific: {configName}.slddrw  (multi-config only, first)
            //   2. Shared:          {modelBasename}.slddrw
            // The first one found on disk wins, giving config-specific drawings
            // priority over the shared config-table drawing.
            var candidateNames = new List<string>();
            if (isMultiConfig && !string.IsNullOrEmpty(safeCfgName))
                candidateNames.Add(safeCfgName + ".slddrw");
            candidateNames.Add(modelBasename + ".slddrw");

            // Folders to search: model's own folder first, then every WIP division
            var searchDirs = new List<string>();
            if (!string.IsNullOrEmpty(dir)) searchDirs.Add(dir);
            foreach (string div in DatabaseManager.WipDivisions)
                searchDirs.Add(Path.Combine(WipFolder, div));

            foreach (string candidateName in candidateNames)
            {
                foreach (string searchDir in searchDirs)
                {
                    string fullPath = Path.Combine(searchDir, candidateName);
                    if (!File.Exists(fullPath)) continue;

                    // Already open → bring to front without reopening
                    ModelDoc2 already = PDMLiteAddin.SwApp
                        .GetOpenDocumentByName(fullPath) as ModelDoc2;
                    if (already != null)
                    {
                        int ae = 0;
                        PDMLiteAddin.SwApp.ActivateDoc3(fullPath, false,
                            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                            ref ae);
                    }
                    else
                    {
                        int errs = 0, warnings = 0;
                        PDMLiteAddin.SwApp.OpenDoc6(fullPath,
                            (int)swDocumentTypes_e.swDocDRAWING,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                            "", ref errs, ref warnings);
                    }
                    return;
                }
            }

            // No drawing found on disk — check if an unsaved new drawing is
            // already open in memory for this model (prevents duplicate creation
            // when the user clicks "Open Drawing" multiple times before saving).
            object[] openDocs = PDMLiteAddin.SwApp.GetDocuments() as object[];
            if (openDocs != null)
            {
                foreach (object obj in openDocs)
                {
                    ModelDoc2 openDoc = obj as ModelDoc2;
                    if (openDoc == null) continue;
                    if (openDoc.GetType() !=
                        (int)swDocumentTypes_e.swDocDRAWING) continue;
                    if (!string.IsNullOrEmpty(openDoc.GetPathName())) continue;

                    string refModel = GetDrawingReferencedModel(openDoc);
                    if (!string.Equals(refModel, filePath,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    // For multi-config: the unsaved drawing must reference the
                    // same config — don't reuse a drawing created for a different
                    // config that happens to reference the same model.
                    if (isMultiConfig && !string.IsNullOrEmpty(activeConfig))
                    {
                        string refCfgs = GetDrawingReferencedConfigs(openDoc);
                        if (!string.IsNullOrEmpty(refCfgs))
                        {
                            bool matches = refCfgs.Split(',').Any(c =>
                                string.Equals(c.Trim(), activeConfig,
                                    StringComparison.OrdinalIgnoreCase));
                            if (!matches) continue;
                        }
                    }

                    int ae2 = 0;
                    PDMLiteAddin.SwApp.ActivateDoc3(openDoc.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref ae2);
                    return;
                }
            }

            // No drawing found — create one from the model immediately.
            // Single-config → always the shared {modelBasename}.slddrw.
            // Multi-config  → ask ONCE whether this should be a common drawing
            //               (shared by every config) or a config-specific one
            //               ({configName}.slddrw). The name chosen here is the
            //               only place the decision is made; after that the file
            //               on disk carries it and the prompt never repeats.
            bool createPerConfig = false;
            if (isMultiConfig && !string.IsNullOrEmpty(safeCfgName))
            {
                int cfgCount = PropertyValidator.GetConfigNames(doc).Count;
                using (var scopeDlg = new DrawingScopeDialog(cfgCount, activeConfig))
                {
                    if (scopeDlg.ShowDialog() != DialogResult.OK) return; // cancelled
                    createPerConfig =
                        scopeDlg.Result == DrawingScopeDialog.Scope.PerConfig;
                }
            }

            string drwTemplate = PDMLiteAddin.SwApp.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
            ModelDoc2 newDrw = PDMLiteAddin.SwApp
                .NewDocument(drwTemplate, 0, 0, 0) as ModelDoc2;

            DrawingDoc draw = newDrw as DrawingDoc;
            if (draw != null)
            {
                bool inserted = draw.InsertModelInPredefinedView(filePath);
                if (!inserted)
                    draw.Create3rdAngleViews2(filePath);

                // Pin every model view to the active configuration so that
                // config-switching on the model (e.g. during a release export
                // loop) does not cause this drawing's views to follow and show
                // the wrong geometry. Only needed for multi-config parts; for
                // single-config parts the config name is irrelevant but pinning
                // is harmless.
                if (!string.IsNullOrEmpty(activeConfig))
                {
                    try
                    {
                        // GetFirstView() returns the sheet (paper space), not a
                        // model view. Model views start at sheet.GetNextView().
                        SolidWorks.Interop.sldworks.View sheet =
                            (SolidWorks.Interop.sldworks.View)draw.GetFirstView();
                        SolidWorks.Interop.sldworks.View v = sheet != null
                            ? (SolidWorks.Interop.sldworks.View)sheet.GetNextView()
                            : null;
                        while (v != null)
                        {
                            v.ReferencedConfiguration = activeConfig;
                            v = (SolidWorks.Interop.sldworks.View)v.GetNextView();
                        }
                    }
                    catch { }
                }

                newDrw.ViewZoomtofit2();
            }

            // Auto-save the new drawing to the correct WIP path so it is
            // immediately findable (and tracked in vault.xml via OnSavePost).
            if (newDrw != null && !string.IsNullOrEmpty(dir))
            {
                string newDrwName = createPerConfig
                    ? safeCfgName + ".slddrw"
                    : modelBasename + ".slddrw";
                string newDrwPath = Path.Combine(dir, newDrwName);

                // Only auto-save if the target path does not already exist —
                // if it does, something else created it between our search and
                // now (race condition); the user can deal with it manually.
                if (!File.Exists(newDrwPath))
                {
                    try
                    {
                        int sErr = 0, sWarn = 0;
                        PDMLiteAddin.SuppressSaveValidation = true;
                        try
                        {
                            newDrw.Extension.SaveAs(
                                newDrwPath,
                                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                                null, ref sErr, ref sWarn);
                        }
                        finally { PDMLiteAddin.SuppressSaveValidation = false; }
                    }
                    catch { }
                }
            }
        }

        // ── Label for the context-aware Open button when a drawing is active ─
        // Returns "Open Part", "Open Assembly", or the generic fallback when the
        // referenced model path can't be resolved.
        public static string GetDrawingOpenLabel(ModelDoc2 drawingDoc)
        {
            string refPath = GetDrawingReferencedModel(drawingDoc);
            if (string.IsNullOrEmpty(refPath)) return "Open Part/Assembly";
            string ext = Path.GetExtension(refPath).ToLower();
            if (ext == ".sldasm") return "Open Assembly";
            if (ext == ".sldprt") return "Open Part";
            return "Open Part/Assembly";
        }

        // ── OPEN REFERENCED MODEL from a drawing ──────────────────────
        // The drawing → part/assembly counterpart of OpenOrCreateDrawing.
        // Opens (or activates, if already open) the part/assembly that the
        // active drawing documents. Used by the context-aware Open button.
        public static void OpenReferencedModel(ModelDoc2 drawingDoc)
        {
            string refPath = GetDrawingReferencedModel(drawingDoc);
            if (string.IsNullOrEmpty(refPath) || !File.Exists(refPath))
            {
                MessageBox.Show(
                    "Could not find the part or assembly this drawing " +
                    "references.\n\nIt may have been removed from the vault or " +
                    "moved outside the WIP folder.",
                    "BCore PDM — Open Part/Assembly",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Already open → just bring it to the front.
            ModelDoc2 already = PDMLiteAddin.SwApp
                .GetOpenDocumentByName(refPath) as ModelDoc2;
            if (already != null)
            {
                int e = 0;
                PDMLiteAddin.SwApp.ActivateDoc3(refPath, false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e);
                return;
            }

            string ext = Path.GetExtension(refPath).ToLower();
            int docType = ext == ".sldasm"
                ? (int)swDocumentTypes_e.swDocASSEMBLY
                : (int)swDocumentTypes_e.swDocPART;

            int errs = 0, warnings = 0;
            PDMLiteAddin.SwApp.OpenDoc6(refPath, docType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref errs, ref warnings);
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

        // ── Batch helpers (bulk release / bulk approve) ───────────────────

        // Open a vault file by path, choosing the doc type from its extension.
        // Returns the already-open doc if SOLIDWORKS has it; null on failure.
        public static ModelDoc2 OpenByPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            ModelDoc2 existing = PDMLiteAddin.SwApp
                ?.GetOpenDocumentByName(filePath) as ModelDoc2;
            if (existing != null) return existing;

            string ext = Path.GetExtension(filePath).ToLower();
            int type = ext == ".sldasm" ? (int)swDocumentTypes_e.swDocASSEMBLY
                     : ext == ".slddrw" ? (int)swDocumentTypes_e.swDocDRAWING
                     : (int)swDocumentTypes_e.swDocPART;
            int e = 0, w = 0;
            return PDMLiteAddin.SwApp.OpenDoc6(filePath, type,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref e, ref w) as ModelDoc2;
        }

        // Release ordering: parts before assemblies (assembly gate needs its
        // children Released) before drawings (drawing gate needs its model
        // Released). Keeps batch gates passing without manual ordering.
        private static int ReleaseOrderKey(string filePath)
        {
            string ext = Path.GetExtension(filePath ?? "").ToLower();
            if (ext == ".sldprt") return 0;
            if (ext == ".sldasm") return 1;
            return 2; // .slddrw and anything else last
        }

        // Release a batch of WIP files in one pass. Opens each, runs the normal
        // ReleaseFile with its confirm/success popups suppressed (blocker and
        // validation dialogs still show so the Master sees why anything is
        // skipped). Returns a per-file result for one summary at the end.
        public static BatchResult BulkRelease(IEnumerable<string> filePaths)
        {
            var result = new BatchResult();
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return result; }

            var ordered = (filePaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ReleaseOrderKey)
                .ToList();

            foreach (string path in ordered)
            {
                string name = Path.GetFileName(path);

                if (DatabaseManager.GetFileStatus(path) == "Released")
                {
                    result.Skipped.Add(name + " — already Released");
                    continue;
                }

                ModelDoc2 doc = OpenByPath(path);
                if (doc == null)
                {
                    result.Skipped.Add(name + " — could not open");
                    continue;
                }

                ReleaseFile(doc, suppressPrompts: true);

                if (DatabaseManager.GetFileStatus(path) == "Released")
                    result.Succeeded.Add(name);
                else
                    result.Skipped.Add(name + " — blocked (see message)");
            }

            return result;
        }

        // Approve a batch of pending requests, each by its own type: Unlock →
        // UnlockFile, Revision → StartNewRevision, Release → ReleaseFile. Done
        // silently with one summary at the end. A request is only resolved when
        // its action actually succeeded, so a blocked release stays pending for
        // the Master to retry after fixing it.
        public static BatchResult BulkApprove(IEnumerable<RevisionRequest> requests)
        {
            var result = new BatchResult();
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return result; }

            var list = (requests ?? Enumerable.Empty<RevisionRequest>()).ToList();

            // 1) Unlocks — pure DB/read-only ops, always succeed.
            foreach (var r in list.Where(r => r.RequestType == "Unlock"))
            {
                UnlockFile(r.FilePath);
                DatabaseManager.ResolveRequest(r.Id, "Approved");
                EmailManager.NotifyRequestApproved("Unlock", r.FileName, r.RequestedBy);
                AuditLogger.Log("ApproveRequest", user, r.FileName, "", "",
                    "bulk unlock (requested by " + r.RequestedBy + ")");
                result.Succeeded.Add(r.FileName + "  (unlock)");
            }

            // 2) Revisions.
            foreach (var r in list.Where(r => r.RequestType == "Revision" ||
                                              string.IsNullOrEmpty(r.RequestType)))
            {
                ModelDoc2 doc = OpenByPath(r.FilePath);
                if (doc == null)
                {
                    result.Skipped.Add(r.FileName + " — could not open (revision)");
                    continue;
                }
                StartNewRevision(doc, suppressPrompts: true);
                if (DatabaseManager.GetFileStatus(r.FilePath) == "WIP")
                {
                    DatabaseManager.ResolveRequest(r.Id, "Approved");
                    EmailManager.NotifyRequestApproved("Revision", r.FileName, r.RequestedBy);
                    AuditLogger.Log("ApproveRequest", user, r.FileName, "", "",
                        "bulk revision (requested by " + r.RequestedBy + ")");
                    result.Succeeded.Add(r.FileName + "  (revision)");
                }
                else
                {
                    result.Skipped.Add(r.FileName + " — revision did not complete");
                }
            }

            // 3) Releases — ordered parts→assemblies→drawings.
            foreach (var r in list.Where(r => r.RequestType == "Release")
                                  .OrderBy(r => ReleaseOrderKey(r.FilePath)))
            {
                ModelDoc2 doc = OpenByPath(r.FilePath);
                if (doc == null)
                {
                    result.Skipped.Add(r.FileName + " — could not open (release)");
                    continue;
                }
                ReleaseFile(doc, suppressPrompts: true);
                if (DatabaseManager.GetFileStatus(r.FilePath) == "Released")
                {
                    DatabaseManager.ResolveRequest(r.Id, "Approved");
                    EmailManager.NotifyRequestApproved("Release", r.FileName, r.RequestedBy);
                    result.Succeeded.Add(r.FileName + "  (release)");
                }
                else
                {
                    result.Skipped.Add(r.FileName + " — blocked (see message)");
                }
            }

            return result;
        }

        // ── APPROVE REQUEST — Master approves and starts new revision ─
        // Note: PendingRequestsForm now routes all single-card approvals through
        // BulkApprove. This method is kept for any direct call sites and mirrors
        // the same success-gated logic: auto-open, act, resolve only on success.
        public static void ApproveRequest(RevisionRequest request)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            ModelDoc2 doc = OpenByPath(request.FilePath);
            if (doc == null)
            {
                MessageBox.Show(
                    "Could not open:\n" + request.FileName +
                    "\n\nVerify the file exists in the vault.",
                    "BCore PDM — Approve Request",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StartNewRevision(doc);

            // Only resolve when the revision actually completed.
            if (DatabaseManager.GetFileStatus(request.FilePath) == "WIP")
            {
                DatabaseManager.ResolveRequest(request.Id, "Approved");
                EmailManager.NotifyRequestApproved(
                    string.IsNullOrEmpty(request.RequestType) ? "Revision"
                                                              : request.RequestType,
                    request.FileName, request.RequestedBy);
                AuditLogger.Log("ApproveRequest", user, request.FileName, "", "",
                    "requested by " + request.RequestedBy);
            }
        }

        // ── REJECT REQUEST — Master rejects the request ───────────────
        public static void RejectRequest(RevisionRequest request)
        {
            string user = PDMLiteAddin.CurrentUser;
            if (!IsMaster(user)) { NotMaster(); return; }

            string reqType = string.IsNullOrEmpty(request.RequestType)
                ? "Revision" : request.RequestType;

            // Prompt the Master for a rejection reason — this is what the engineer
            // receives in the rejection email (NOT their own original note echoed
            // back). Cancelling the dialog aborts the rejection.
            string reason = ShowNoteDialog(
                "Reject " + reqType + " Request",
                "Reason for rejecting \"" + request.FileName + "\" (" +
                request.RequestedBy + "):");

            if (reason == null) return; // cancelled

            DatabaseManager.ResolveRequest(request.Id, "Rejected");
            EmailManager.NotifyRequestRejected(
                reqType, request.FileName, request.RequestedBy, reason);
            AuditLogger.Log("RejectRequest", user, request.FileName, "", "",
                "requested by " + request.RequestedBy +
                (string.IsNullOrEmpty(reason) ? "" : "; reason: " + reason));

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
                        // Read from the config this drawing documents, not the
                        // model's active config (which may be any config for a
                        // multi-config model). Otherwise the released PDF gets
                        // named after the wrong configuration's DrawingNo.
                        string drwCfg = GetDrawingPrimaryConfig(drawingDoc);
                        string drawingNo = !string.IsNullOrEmpty(drwCfg)
                            ? PropertyValidator.GetProperty(refModel, "DrawingNo", drwCfg)
                            : PropertyValidator.GetProperty(refModel, "DrawingNo");
                        if (!string.IsNullOrEmpty(drawingNo))
                            return drawingNo;
                    }
                }
            }
            catch { }

            // Fallback: use drawing filename
            return Path.GetFileNameWithoutExtension(drawingDoc.GetPathName());
        }

        // ── Get PartNo of the model a drawing references ──────────────────
        // A drawing shares the PartNo of the part/assembly it documents.
        // Reads it from the referenced model (loaded as the drawing's
        // reference), falling back to the drawing's own PartNo property, then
        // "" if neither is available. Used by the task-pane Active File card.
        public static string GetDrawingPartNo(ModelDoc2 drawingDoc)
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
                        // Read PartNo from the SPECIFIC configuration this drawing
                        // documents — not the model's currently-active config. A
                        // multi-config model may have any config active (e.g. after
                        // releasing a different config's drawing switched it), so
                        // reading the active config would return the wrong PartNo.
                        string drwCfg = GetDrawingPrimaryConfig(drawingDoc);
                        string pn = !string.IsNullOrEmpty(drwCfg)
                            ? PropertyValidator.GetProperty(refModel, "PartNo", drwCfg)
                            : PropertyValidator.GetProperty(refModel, "PartNo");
                        if (!string.IsNullOrEmpty(pn)) return pn;
                    }
                }
            }
            catch { }

            // Fallback to the drawing's own PartNo property (inherited).
            try { return PropertyValidator.GetProperty(drawingDoc, "PartNo"); }
            catch { return ""; }
        }

        // ── Config the drawing's primary (first) model view references ────────
        // Returns the ReferencedConfiguration of the first model view on the
        // sheet, or "" if it can't be read (treat as "use active config").
        public static string GetDrawingPrimaryConfig(ModelDoc2 drawingDoc)
        {
            try
            {
                DrawingDoc drw = (DrawingDoc)drawingDoc;
                SolidWorks.Interop.sldworks.View sheet =
                    (SolidWorks.Interop.sldworks.View)drw.GetFirstView();
                if (sheet == null) return "";
                SolidWorks.Interop.sldworks.View v =
                    (SolidWorks.Interop.sldworks.View)sheet.GetNextView();
                if (v == null) return "";
                return v.ReferencedConfiguration ?? "";
            }
            catch { return ""; }
        }

        // ── Get referenced model path from drawing ────────────────────────
        // Public so SwAddin.OnSavePost can call it when writing drawing records
        // (it needs the model path to populate ReferencedModel in the DB).
        public static string GetDrawingReferencedModel(ModelDoc2 doc)
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

        // Returns unique config names referenced across all views on the active
        // sheet of a drawing, as a comma-separated string. Used by OnSavePost to
        // populate ReferencedConfigs in the vault DB so the DB knows which
        // configurations a drawing documents.
        // Returns "" when no config info can be read (treat as "covers all").
        public static string GetDrawingReferencedConfigs(ModelDoc2 doc)
        {
            var configs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DrawingDoc drw = (DrawingDoc)doc;
                // GetFirstView() returns the sheet/paper view (not a model view).
                // Model views start at GetNextView() from the sheet view.
                SolidWorks.Interop.sldworks.View sheet =
                    (SolidWorks.Interop.sldworks.View)drw.GetFirstView();
                if (sheet == null) return "";

                SolidWorks.Interop.sldworks.View v =
                    (SolidWorks.Interop.sldworks.View)sheet.GetNextView();
                while (v != null)
                {
                    string cfg = v.ReferencedConfiguration;
                    if (!string.IsNullOrEmpty(cfg)) configs.Add(cfg);
                    v = (SolidWorks.Interop.sldworks.View)v.GetNextView();
                }
            }
            catch { }
            return string.Join(",", configs);
        }
        // ── Move ALL exports for this part to archive on rollback ─────
        // rawPartNo: original PartNo (dots preserved) for BOM glob. When null
        // falls back to partNoClean (no-dots version used for STEP/PDF globs).
        private static void CleanupExportsOnRollback(string partNoClean,
                                                      string drawingNo,
                                                      string rawPartNo = null)
        {
            try
            {
                string pdfExport = Path.Combine(ExportRoot, "PDF");
                string stepExport = Path.Combine(ExportRoot, "STEP");
                string pdfArchive = Path.Combine(ObsFolder, "PDF");
                string stepArchive = Path.Combine(ObsFolder, "STEP");

                Directory.CreateDirectory(pdfArchive);
                Directory.CreateDirectory(stepArchive);

                // Move the BOM CSV (assemblies) — use raw PartNo so the glob
                // matches the {partNo}-R{rev}_BOM.csv filename correctly.
                string bomId = rawPartNo ?? partNoClean;
                MoveMatching(Path.Combine(ExportRoot, "BOM"),
                    Path.Combine(ObsFolder, "BOM"), bomId + "*_BOM.csv");

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

        // Builds an archive path "{baseName} REV {rev}{ext}". Multi-config files
        // are archived at FILE level but named after a single config's rev, so a
        // partial bump (or a rollback after a New Revision) can map two DIFFERENT
        // file snapshots to the same name — and ArchiveCopy is delete-then-copy,
        // so the earlier snapshot would be silently destroyed. When the target
        // already exists for a multi-config file, insert a yyyyMMdd_HHmmss stamp
        // BEFORE " REV " so both survive AND RollbackDialog.ExtractRevision (which
        // parses the token after " REV ") still reads the rev letter. Single-
        // config keeps the old overwrite behaviour (re-archiving the same rev is
        // harmless).
        private static string CollisionSafeArchivePath(
            string dir, string baseName, string rev, string ext, bool multiCfg)
        {
            string dest = Path.Combine(dir, baseName + " REV " + rev + ext);
            if (multiCfg && File.Exists(dest))
            {
                string snap = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                dest = Path.Combine(dir,
                    baseName + "_" + snap + " REV " + rev + ext);
            }
            return dest;
        }
    }
}