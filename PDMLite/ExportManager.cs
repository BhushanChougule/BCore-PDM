using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PDMLite
{
    public static class ExportManager
    {
        public static void ExportAll(ModelDoc2 doc, string exportRoot, string stamp)
        {
            int docType = doc.GetType();

            // ── Create subfolders ─────────────────────────────────────
            string pdfFolder = Path.Combine(exportRoot, "PDF");
            string stepFolder = Path.Combine(exportRoot, "STEP");
            string dxfFolder = Path.Combine(exportRoot, "DXF");

            Directory.CreateDirectory(pdfFolder);
            Directory.CreateDirectory(stepFolder);
            Directory.CreateDirectory(dxfFolder);

            if (docType == (int)swDocumentTypes_e.swDocDRAWING)
            {
                // Drawing → PDF (all sheets), then stamp RELEASED watermark
                string pdfPath = Path.Combine(pdfFolder, stamp + ".pdf");
                ExportDrawingPdf(doc, pdfPath);
                StampWatermark(pdfPath);
            }
            else if (docType == (int)swDocumentTypes_e.swDocPART)
            {
                // Part → STEP + flat pattern DXF (sheet metal only)
                ExportFile(doc, Path.Combine(stepFolder, stamp + ".step"));
                ExportFlatPattern(doc, Path.Combine(dxfFolder,
                    stamp + "_FLAT.dxf"));
            }
            else if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                // Assembly → STEP
                ExportFile(doc, Path.Combine(stepFolder, stamp + ".step"));
            }
        }

        // Export STEP only — called once per config during multi-config release.
        // The active configuration at call time determines the exported geometry.
        public static void ExportStepOnly(ModelDoc2 doc, string exportRoot,
            string stamp)
        {
            string stepFolder = Path.Combine(exportRoot, "STEP");
            Directory.CreateDirectory(stepFolder);
            ExportFile(doc, Path.Combine(stepFolder, stamp + ".step"));
        }

        // Export flat pattern DXF only — called for the original active config
        // after the per-config STEP loop in a multi-config release.
        public static void ExportFlatPatternOnly(ModelDoc2 doc, string exportRoot,
            string stamp)
        {
            string dxfFolder = Path.Combine(exportRoot, "DXF");
            Directory.CreateDirectory(dxfFolder);
            ExportFlatPattern(doc, Path.Combine(dxfFolder, stamp + "_FLAT.dxf"));
        }

        // ── Top-level BOM (assemblies only) ───────────────────────────────────
        // Writes EXPORTS\BOM\{partNo}-R{rev}_BOM.csv automatically on assembly release.
        // Uses the raw PartNo (dots/dashes preserved) — no filesystem-safe stripping
        // needed for a CSV filename, and the original form matches what engineers see
        // on drawings. Lists each top-level component ONCE with its quantity and the
        // config-specific identifiers read from the configuration the assembly references.
        // Top-level only — sub-assembly internals are not expanded. Purchased/Toolbox
        // hardware IS listed (a BOM needs it). Never throws: a BOM failure must not
        // block a release. The BOM reflects the assembly's ACTIVE configuration at
        // release time (matches the structure being released).
        public static void ExportBom(ModelDoc2 asmDoc, string exportRoot,
            string partNo, string rev)
        {
            try
            {
                if (asmDoc == null) return;
                if (asmDoc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

                AssemblyDoc asm = asmDoc as AssemblyDoc;
                if (asm == null) return;

                string bomFolder = Path.Combine(exportRoot, "BOM");
                Directory.CreateDirectory(bomFolder);

                // Enumerate top-level components. GetRootComponent3(true) resolves
                // the root; GetChildren() returns the direct (top-level) children
                // only — sub-assembly internals are not expanded. Falls back to
                // GetComponents(true) if the root walk yields nothing.
                var comps = new List<Component2>();
                try
                {
                    Configuration activeCfg =
                        asmDoc.GetActiveConfiguration() as Configuration;
                    Component2 root = activeCfg?.GetRootComponent3(true);
                    object[] kids = root?.GetChildren() as object[];
                    if (kids != null)
                        foreach (object o in kids)
                        {
                            Component2 c = o as Component2;
                            if (c != null) comps.Add(c);
                        }
                }
                catch { }

                if (comps.Count == 0)
                {
                    try
                    {
                        object[] gc = asm.GetComponents(true) as object[];
                        if (gc != null)
                            foreach (object o in gc)
                            {
                                Component2 c = o as Component2;
                                if (c != null) comps.Add(c);
                            }
                    }
                    catch { }
                }

                // Group identical components (same path + referenced config) so
                // each BOM line carries a quantity instead of repeating instances.
                var rows = new List<BomRow>();
                var index = new Dictionary<string, BomRow>(
                    System.StringComparer.OrdinalIgnoreCase);

                foreach (Component2 comp in comps)
                {
                    if (comp == null) continue;

                    // Use GetSuppression2(), NOT IsSuppressed(): IsSuppressed()
                    // wrongly reports LIGHTWEIGHT components as suppressed. Only a
                    // state of swComponentSuppressed means genuinely suppressed;
                    // lightweight / resolved / fully-resolved are all PRESENT.
                    int supState = -1;
                    try { supState = comp.GetSuppression2(); } catch { }
                    if (supState == (int)
                        swComponentSuppressionState_e.swComponentSuppressed)
                        continue;

                    string path = "";
                    try { path = comp.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(path)) continue;

                    string refCfg = "";
                    try { refCfg = comp.ReferencedConfiguration ?? ""; } catch { }

                    string key = path.ToLowerInvariant() + "|" +
                        refCfg.ToLowerInvariant();

                    BomRow existing;
                    if (index.TryGetValue(key, out existing))
                    {
                        existing.Qty++;
                        continue;
                    }

                    // A lightweight component may not return a model from
                    // GetModelDoc2(); GetReadableModel falls back to an open
                    // handle or a read-only open so its properties can be read.
                    bool openedHere;
                    ModelDoc2 cm = GetReadableModel(comp, path, out openedHere);

                    var row = new BomRow
                    {
                        PartNo      = ReadProp(cm, "PartNo", refCfg),
                        Description = ReadProp(cm, "Description", refCfg),
                        Revision    = ReadProp(cm, "Revision", refCfg),
                        Material    = ReadProp(cm, "Material1", refCfg),
                        PartType    = ReadProp(cm, "PartType", refCfg),
                        Qty         = 1
                    };
                    // Fall back to the filename when PartNo is unreadable.
                    if (string.IsNullOrEmpty(row.PartNo))
                        row.PartNo = Path.GetFileNameWithoutExtension(path);

                    index[key] = row;
                    rows.Add(row);

                    // Close only a doc we opened ourselves (never one the user or
                    // the assembly already had open).
                    if (openedHere)
                        try { PDMLiteAddin.SwApp.CloseDoc(path); } catch { }
                }

                var sb = new StringBuilder();
                sb.AppendLine("Item,PartNo,Description,Revision,Material,PartType,Qty");
                int item = 1;
                foreach (BomRow r in rows)
                {
                    sb.AppendLine(string.Join(",",
                        (item++).ToString(),
                        Csv(r.PartNo), Csv(r.Description), Csv(r.Revision),
                        Csv(r.Material), Csv(r.PartType), r.Qty.ToString()));
                }

                File.WriteAllText(
                    Path.Combine(bomFolder, partNo + "-R" + rev + "_BOM.csv"),
                    sb.ToString());
            }
            catch { } // a BOM failure must never block a release
        }

        // Returns a ModelDoc2 whose custom properties can be read, even for a
        // lightweight component. Priority: the component's own model → an
        // already-open document → a fresh read-only open (openedHere=true, so
        // the caller closes it). Never throws.
        private static ModelDoc2 GetReadableModel(Component2 comp, string path,
            out bool openedHere)
        {
            openedHere = false;
            ModelDoc2 cm = null;
            try { cm = comp.GetModelDoc2() as ModelDoc2; } catch { }
            if (cm != null) return cm;

            try
            {
                ISldWorks swApp = PDMLiteAddin.SwApp;
                if (swApp == null || string.IsNullOrEmpty(path) ||
                    !File.Exists(path))
                    return null;

                // Already open somewhere (incl. as a resolved component)? Reuse it
                // and DON'T mark openedHere, so we never close the user's doc.
                cm = swApp.GetOpenDocumentByName(path) as ModelDoc2;
                if (cm != null) return cm;

                int dt = path.EndsWith(".sldasm",
                        System.StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocASSEMBLY
                    : (int)swDocumentTypes_e.swDocPART;
                int e = 0, w = 0;
                cm = swApp.OpenDoc6(path, dt,
                    (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly |
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                    "", ref e, ref w) as ModelDoc2;
                openedHere = cm != null;
                return cm;
            }
            catch { return null; }
        }

        private sealed class BomRow
        {
            public string PartNo;
            public string Description;
            public string Revision;
            public string Material;
            public string PartType;
            public int Qty;
        }

        // Read a property wherever it lives, in priority order:
        //   1. the referenced configuration (config-specific "Configuration
        //      Specific" tab — where our PropertyForm writes it),
        //   2. document level (configName "" — the "Custom" tab, used when a
        //      property was entered there instead of per-config),
        //   3. the model's active configuration (last-resort fallback).
        // This is why a filled Material1 could come back empty: it was stored in
        // a different scope than the component's referenced config. Swallows
        // failures so an unreadable component never breaks the BOM.
        private static string ReadProp(ModelDoc2 model, string prop, string cfg)
        {
            if (model == null) return "";
            try
            {
                string v = !string.IsNullOrEmpty(cfg)
                    ? PropertyValidator.GetProperty(model, prop, cfg) : "";
                if (string.IsNullOrWhiteSpace(v))
                    v = PropertyValidator.GetProperty(model, prop, ""); // document level
                if (string.IsNullOrWhiteSpace(v))
                    v = PropertyValidator.GetProperty(model, prop);     // active config
                return v ?? "";
            }
            catch { return ""; }
        }

        // Minimal RFC-4180 CSV escaping (mirrors AuditLogger.Csv): quote fields
        // containing a comma, quote, or newline; double any embedded quotes.
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        // Thin wrapper that references NO PdfSharp types directly. If PdfSharp.dll
        // fails to load at runtime, the FileNotFoundException/TypeLoadException is
        // thrown when the JIT enters StampWatermarkCore and is caught HERE —
        // leaving the PDF un-stamped and the release unaffected, instead of
        // propagating up and breaking the release. (A single method referencing
        // PdfSharp types could NOT catch its own assembly-load failure.)
        private static void StampWatermark(string pdfPath)
        {
            if (!File.Exists(pdfPath)) return;
            try { StampWatermarkCore(pdfPath); }
            catch { } // missing PdfSharp.dll, or any PdfSharp error → skip stamp
        }

        // Stamp a diagonal, very transparent "RELEASED" watermark on every page
        // of the given PDF, aligned with the sheet's corner-to-corner diagonal.
        //
        // The PDF is read into memory and stamped there so PdfSharp never holds a
        // file handle on the path we then overwrite. SOLIDWORKS keeps the
        // freshly-exported PDF open for a short moment (especially on the network
        // share), which blocks an exclusive write-back even though a shared read
        // succeeds — so the write retries a few times to let that lock clear.
        private static void StampWatermarkCore(string pdfPath)
        {
            // Read the source bytes and release the handle immediately.
            byte[] srcBytes = File.ReadAllBytes(pdfPath);

            byte[] outBytes;
            using (var inMs = new MemoryStream(srcBytes))
            using (PdfDocument pdf = PdfReader.Open(inMs,
                PdfDocumentOpenMode.Modify))
            {
                // Very transparent gray (alpha 11/255 ≈ 4.5%) — reads as a subtle
                // watermark without hiding the drawing beneath. PdfSharp emits the
                // alpha as a PDF ExtGState, so true transparency works.
                XBrush brush = new XSolidBrush(XColor.FromArgb(11, 120, 120, 120));

                foreach (PdfPage page in pdf.Pages)
                {
                    double w = page.Width.Point;
                    double h = page.Height.Point;

                    // Lay the text along the sheet's bottom-left → top-right
                    // diagonal: |angle| = atan(height / width) (≈ 33° for a 17×11
                    // sheet). NEGATIVE rotates it ASCENDING to the right in
                    // PdfSharp's y-down space (positive would descend).
                    double angleDeg =
                        -System.Math.Atan2(h, w) * 180.0 / System.Math.PI;
                    double diag = System.Math.Sqrt(w * w + h * h);

                    using (XGraphics gfx = XGraphics.FromPdfPage(
                        page, XGraphicsPdfPageOptions.Append))
                    {
                        // Size the text to span ~48% of the diagonal so it scales
                        // proportionally with any sheet size (A → E).
                        XFont trial = new XFont("Arial", 100, XFontStyle.Bold);
                        XSize ts = gfx.MeasureString("RELEASED", trial);
                        double size = ts.Width > 1
                            ? 100.0 * (diag * 0.48) / ts.Width : 78.0;
                        XFont font = new XFont("Arial", size, XFontStyle.Bold);

                        // Translate to page centre, rotate to the diagonal, then
                        // draw the text centred on the origin.
                        XGraphicsState state = gfx.Save();
                        gfx.TranslateTransform(w / 2.0, h / 2.0);
                        gfx.RotateTransform(angleDeg);
                        XSize sz = gfx.MeasureString("RELEASED", font);
                        gfx.DrawString("RELEASED", font, brush,
                            new XPoint(-sz.Width / 2, sz.Height / 4));
                        gfx.Restore(state);
                    }
                }

                using (var outMs = new MemoryStream())
                {
                    pdf.Save(outMs, false); // false = leave the stream open
                    outBytes = outMs.ToArray();
                }
            }

            // Write the stamped PDF back, retrying while SOLIDWORKS still holds
            // the export open. The final attempt is outside the loop so a genuine
            // persistent lock surfaces to the wrapper's catch (PDF left un-stamped).
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.WriteAllBytes(pdfPath, outBytes);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
            File.WriteAllBytes(pdfPath, outBytes);
        }

        // Universal export — SOLIDWORKS picks format from file extension
        private static void ExportFile(ModelDoc2 doc, string outPath)
        {
            int errors = 0;
            int warnings = 0;
            doc.Extension.SaveAs(
                outPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref errors,
                ref warnings);
        }
        // Export drawing — all sheets to one PDF.
        // Suppresses SOLIDWORKS' own "View PDF after publishing" auto-open: that
        // setting launches the viewer on the UN-watermarked file DURING SaveAs,
        // before we can stamp it (which is why the auto-opened copy showed no
        // watermark). We stamp first, then open the finished PDF ourselves
        // (VaultManager, interactive release only). Falls back to the default
        // null export data if the PDF export data can't be obtained, so the
        // export itself never fails over this.
        private static void ExportDrawingPdf(ModelDoc2 doc, string outPath)
        {
            int errors = 0;
            int warnings = 0;

            object exportData = null;
            try
            {
                ISldWorks app = PDMLiteAddin.SwApp;
                ExportPdfData pdfData = app?.GetExportFileData(
                    (int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                if (pdfData != null)
                {
                    pdfData.ViewPdfAfterSaving = false;
                    // Preserve the all-sheets behaviour (null exported every sheet).
                    DrawingDoc dwg = doc as DrawingDoc;
                    string[] sheetNames = dwg?.GetSheetNames() as string[];
                    if (sheetNames != null && sheetNames.Length > 0)
                        pdfData.SetSheets(
                            (int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets,
                            sheetNames);
                    exportData = pdfData;
                }
            }
            catch { exportData = null; }

            doc.Extension.SaveAs(
                outPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                exportData,
                ref errors,
                ref warnings);
        }

        // Open a PDF in the user's default viewer. Used after release so the
        // engineer still sees the just-released drawing — but the STAMPED copy,
        // not the raw one SOLIDWORKS' auto-open would have shown mid-export.
        // Non-fatal.
        public static void OpenPdfExternally(string pdfPath)
        {
            try
            {
                if (File.Exists(pdfPath))
                    System.Diagnostics.Process.Start(pdfPath);
            }
            catch { }
        }

        // Sheet metal flat pattern DXF
        private static void ExportFlatPattern(ModelDoc2 doc, string outPath)
        {
            try
            {
                object[] features = (object[])doc.FeatureManager.GetFeatures(true);
                if (features == null) return;

                bool hasSheetMetal = false;
                foreach (object f in features)
                {
                    if (((Feature)f).GetTypeName2() == "SheetMetal")
                    {
                        hasSheetMetal = true;
                        break;
                    }
                }

                if (!hasSheetMetal) return;

                int errors = 0, warnings = 0;
                doc.Extension.SaveAs(
                    outPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    ref errors,
                    ref warnings);
            }
            catch { }
        }
    }
}