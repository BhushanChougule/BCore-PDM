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
                // Drawing → PDF only (all sheets in one file)
                ExportDrawingPdf(doc, Path.Combine(pdfFolder, stamp + ".pdf"));
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
        // Writes EXPORTS\BOM\{stamp}_BOM.csv automatically on assembly release.
        // Lists each top-level component ONCE with its quantity and the config-
        // specific identifiers (Part No, Description, Revision, Material, PartType)
        // read from the configuration the assembly references. Top-level only —
        // sub-assembly internals are not expanded. Purchased/Toolbox hardware IS
        // listed (a BOM needs it). Never throws: a BOM failure must not block a
        // release. The BOM reflects the assembly's ACTIVE configuration at release
        // time (matches the structure being released).
        public static void ExportBom(ModelDoc2 asmDoc, string exportRoot,
            string stamp)
        {
            try
            {
                if (asmDoc == null) return;
                if (asmDoc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return;

                AssemblyDoc asm = asmDoc as AssemblyDoc;
                if (asm == null) return;

                string bomFolder = Path.Combine(exportRoot, "BOM");
                Directory.CreateDirectory(bomFolder);

                // Top-level components only (toplevelOnly = true).
                object[] comps = asm.GetComponents(true) as object[];

                // Group identical components (same path + referenced config) so
                // each BOM line carries a quantity instead of repeating instances.
                var rows = new List<BomRow>();
                var index = new Dictionary<string, BomRow>(
                    System.StringComparer.OrdinalIgnoreCase);

                if (comps != null)
                {
                    foreach (object o in comps)
                    {
                        Component2 comp = o as Component2;
                        if (comp == null) continue;
                        if (comp.IsSuppressed()) continue;

                        string path = comp.GetPathName();
                        if (string.IsNullOrEmpty(path)) continue;

                        string refCfg = comp.ReferencedConfiguration ?? "";
                        string key = path.ToLowerInvariant() + "|" +
                            refCfg.ToLowerInvariant();

                        BomRow existing;
                        if (index.TryGetValue(key, out existing))
                        {
                            existing.Qty++;
                            continue;
                        }

                        ModelDoc2 cm = comp.GetModelDoc2() as ModelDoc2;
                        var row = new BomRow
                        {
                            PartNo      = ReadProp(cm, "PartNo", refCfg),
                            Description = ReadProp(cm, "Description", refCfg),
                            Revision    = ReadProp(cm, "Revision", refCfg),
                            Material    = ReadProp(cm, "Material1", refCfg),
                            PartType    = ReadProp(cm, "PartType", refCfg),
                            Qty         = 1
                        };
                        // Fall back to the filename when PartNo is unreadable
                        // (lightweight/suppressed) so a line is never blank.
                        if (string.IsNullOrEmpty(row.PartNo))
                            row.PartNo = Path.GetFileNameWithoutExtension(path);

                        index[key] = row;
                        rows.Add(row);
                    }
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
                    Path.Combine(bomFolder, stamp + "_BOM.csv"), sb.ToString());
            }
            catch { } // a BOM failure must never block a release
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

        // Config-aware property read; "" config falls back to the active config.
        // Swallows failures so an unreadable component never breaks the BOM.
        private static string ReadProp(ModelDoc2 model, string prop, string cfg)
        {
            if (model == null) return "";
            try
            {
                return !string.IsNullOrEmpty(cfg)
                    ? PropertyValidator.GetProperty(model, prop, cfg)
                    : PropertyValidator.GetProperty(model, prop);
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