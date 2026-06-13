using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;

namespace PDMLite
{
    // Broken-reference detection for the save and release gates.
    //
    // Covers ALL document types, not just assemblies (audit M4): a PART with a
    // missing external/derived reference, or a DRAWING whose referenced model
    // is gone, used to ALWAYS pass Rule 5 and even get its broken-ref flag
    // CLEARED — the dashboard then showed it as healthy. Every referenced file
    // is deduped before the on-disk File.Exists check (audit M3): a large
    // assembly listed the same shared sub-part once per instance, so a
    // thousand-instance assembly fired thousands of network File.Exists calls
    // on every save. The unique set is typically a few hundred at most.
    public static class ReferenceChecker
    {
        public static List<string> GetBrokenReferences(ModelDoc2 doc)
        {
            var errors = new List<string>();
            if (doc == null) return errors;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                int docType = doc.GetType();

                if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    // Assemblies: walk the live component list (most current —
                    // catches in-session repaths a stored dependency tree would
                    // miss), deduped by path so a shared sub-part is checked
                    // ONCE no matter how many instances reference it.
                    AssemblyDoc asm = (AssemblyDoc)doc;
                    object[] components = (object[])asm.GetComponents(false);
                    if (components != null)
                    {
                        foreach (object obj in components)
                        {
                            Component2 comp = obj as Component2;
                            if (comp == null) continue;

                            string compPath = comp.GetPathName();
                            if (string.IsNullOrEmpty(compPath)) continue;
                            if (IsToolbox(compPath)) continue; // standard hardware
                            if (!seen.Add(Normalize(compPath))) continue; // dedupe

                            if (!File.Exists(compPath))
                                errors.Add("Missing file: " +
                                    Path.GetFileName(compPath));
                        }
                    }
                }
                else
                {
                    // Parts AND drawings: check the files THIS document
                    // references (a part's external/derived references; a
                    // drawing's referenced model) exist on disk. Read the
                    // stored dependency tree from the path — the same proven
                    // primitive GetUnreleasedComponentsByPath uses, which works
                    // for every document type including drawings.
                    string path = doc.GetPathName();
                    if (!string.IsNullOrEmpty(path) &&
                        PDMLiteAddin.SwApp != null)
                    {
                        string selfNorm = Normalize(path);
                        object depsObj = PDMLiteAddin.SwApp
                            .GetDocumentDependencies2(path, true, true, false);
                        string[] deps = depsObj as string[];
                        if (deps != null)
                        {
                            // Alternating array: name, path, name, path…
                            for (int i = 1; i < deps.Length; i += 2)
                            {
                                string refPath = deps[i];
                                if (string.IsNullOrEmpty(refPath)) continue;
                                if (IsToolbox(refPath)) continue;
                                string norm = Normalize(refPath);
                                if (string.Equals(norm, selfNorm,
                                        StringComparison.OrdinalIgnoreCase))
                                    continue; // the document itself
                                if (!seen.Add(norm)) continue; // dedupe

                                if (!File.Exists(refPath))
                                    errors.Add("Missing reference: " +
                                        Path.GetFileName(refPath));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort warning — never throw out of the save/release
                // gate. A failure here returns "no broken refs" (same as the
                // old assembly-only path on any error).
            }

            return errors;
        }

        private static bool IsToolbox(string path) =>
            path.IndexOf(@"\Toolbox\", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}
