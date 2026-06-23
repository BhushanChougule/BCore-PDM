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
            bool walkCompleted;
            return GetBrokenReferences(doc, out walkCompleted);
        }

        // walkCompleted = true ONLY when the reference walk ran to the end without
        // throwing. A caller that CLEARS a broken-ref flag on an empty result MUST
        // gate on this: the catch below swallows a transient COM/network failure
        // (e.g. GetDocumentDependencies2 throwing) and returns an EMPTY list, which
        // otherwise looks like "no broken refs" and would wrongly clear a genuine
        // flag — and on a Released file (which can't be re-saved) the flag would
        // then stay hidden until its next revision. The save-gate caller ignores
        // it (its fail-closed behaviour is unchanged).
        public static List<string> GetBrokenReferences(ModelDoc2 doc,
            out bool walkCompleted)
        {
            walkCompleted = false;
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
                        walkCompleted = true; // got the live component list
                    }
                }
                else
                {
                    // Parts AND drawings: check the files THIS document
                    // DIRECTLY references (a part's external/derived parents; a
                    // drawing's referenced model) exist on disk.
                    // traverseFlag=FALSE → TOP-LEVEL references only, NOT the
                    // whole tree: a drawing's broken-ref is "is my model there?",
                    // not "is every deep sub-part of my model there?" (that is
                    // the model's OWN save-gate). Top-level also bounds the cost
                    // — a drawing of a 5000-component assembly would otherwise
                    // stat every unique sub-part on every save.
                    string path = doc.GetPathName();
                    if (!string.IsNullOrEmpty(path) &&
                        PDMLiteAddin.SwApp != null)
                    {
                        string selfNorm = Normalize(path);
                        object depsObj = PDMLiteAddin.SwApp
                            .GetDocumentDependencies2(path, false, true, false);
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
                            walkCompleted = true; // got the dependency list
                        }
                    }
                }
                // walkCompleted is set inside each branch ONLY after the
                // enumeration source (GetComponents / GetDocumentDependencies2)
                // came back non-null — a SILENT null result (not just a thrown
                // exception) therefore leaves it false, so a clear-on-empty caller
                // never mistakes "couldn't read the refs" for "no broken refs".
            }
            catch
            {
                // Best-effort warning — never throw out of the save/release
                // gate. A failure here returns "no broken refs" (same as the
                // old assembly-only path on any error). walkCompleted stays
                // false so a clear-on-empty caller knows the walk didn't finish.
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
