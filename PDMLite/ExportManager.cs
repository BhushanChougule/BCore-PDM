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

        // Append-only diagnostic log so we can SEE why a watermark didn't appear
        // (the work is otherwise silently swallowed). TEMPORARY — remove once the
        // watermark is confirmed working in production.
        private static void WatermarkLog(string msg)
        {
            try
            {
                File.AppendAllText(
                    @"N:\PDM-SolidWorks\VAULT\watermark_debug.log",
                    System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    "  " + msg + System.Environment.NewLine);
            }
            catch { }
        }

        // Thin wrapper that references NO PdfSharp types, so if PdfSharp.dll fails
        // to load at runtime the resulting FileNotFoundException/TypeLoadException
        // is caught HERE (when the JIT enters StampWatermarkCore) and logged,
        // instead of vanishing. This is how we tell "DLL not deployed" apart from
        // "PdfSharp couldn't process the PDF".
        private static void StampWatermark(string pdfPath)
        {
            if (!File.Exists(pdfPath))
            {
                WatermarkLog("SKIP: pdf does not exist: " + pdfPath);
                return;
            }
            try
            {
                WatermarkLog("START: " + pdfPath);
                StampWatermarkCore(pdfPath);
                WatermarkLog("DONE: " + pdfPath);
            }
            catch (System.Exception ex)
            {
                WatermarkLog("FAIL (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        // Stamp a diagonal "RELEASED" watermark on every page of the given PDF.
        // Uses a fully opaque light gray — PdfSharp 1.50.x (net20) does not
        // reliably apply alpha to the PDF content stream, so a solid light color
        // is used instead.
        private static void StampWatermarkCore(string pdfPath)
        {
            {
                using (PdfDocument pdf = PdfReader.Open(pdfPath,
                    PdfDocumentOpenMode.Modify))
                {
                    WatermarkLog("OPENED, pages=" + pdf.PageCount);
                    XFont  font  = new XFont("Arial", 72, XFontStyle.Bold);
                    // Light gray (170/255): visible as a watermark stamp without
                    // obscuring drawing lines, which are typically near-black.
                    XBrush brush = new XSolidBrush(
                        XColor.FromArgb(255, 170, 170, 170));

                    foreach (PdfPage page in pdf.Pages)
                    {
                        using (XGraphics gfx = XGraphics.FromPdfPage(
                            page, XGraphicsPdfPageOptions.Append))
                        {
                            // Translate to page centre, rotate -45°, then draw
                            // the text centred on the origin.
                            XGraphicsState state = gfx.Save();
                            gfx.TranslateTransform(
                                page.Width.Point  / 2.0,
                                page.Height.Point / 2.0);
                            gfx.RotateTransform(-45);
                            XSize sz = gfx.MeasureString("RELEASED", font);
                            gfx.DrawString("RELEASED", font, brush,
                                new XPoint(-sz.Width / 2, sz.Height / 4));
                            gfx.Restore(state);
                        }
                    }
                    pdf.Save(pdfPath);
                }
            }
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
        // Export drawing — all sheets to one PDF
        private static void ExportDrawingPdf(ModelDoc2 doc, string outPath)
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