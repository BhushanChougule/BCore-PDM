using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;

namespace PDMLite
{
    // One captured "as-released" baseline of an assembly: the exact resolved set
    // of child files (and their revisions) the assembly was released against, so
    // "the precise file set of ASM-100 REV C" can always be reconstructed later.
    // Persisted in vault.xml under <Baselines> (see DatabaseManager).
    public class AssemblyBaseline
    {
        public string AssemblyPath { get; set; }
        public string AssemblyName { get; set; }
        public string PartNo       { get; set; }
        public string Revision     { get; set; }
        public string Config       { get; set; } // active config at release time
        public string ReleasedBy   { get; set; }
        public string ReleasedDate { get; set; } // "yyyy-MM-dd HH:mm:ss"
        public List<BaselineComponent> Components { get; set; }
            = new List<BaselineComponent>();
    }

    // One child file inside a captured baseline. PartNo/Revision/Status are the
    // values the child carried AT RELEASE TIME (snapshotted, never live-read on
    // viewing) so the baseline is a true historical record. The list is stored in
    // DEPTH-FIRST tree order; Level is the indent depth (0 = a top-level child of
    // the assembly, 1 = inside a sub-assembly, …) so the viewer can show the
    // structure. Qty is the per-immediate-parent instance count.
    public class BaselineComponent
    {
        public string Path     { get; set; }
        public string Name     { get; set; }
        public string PartNo   { get; set; }
        public string Revision { get; set; }
        public string Status   { get; set; }
        public int    Qty      { get; set; }
        public int    Level    { get; set; }
    }

    // Captures as-released baselines for assemblies. At release the assembly is
    // open and resolved, so the structure is read from the LIVE component tree
    // (true hierarchy + per-parent quantities — same enumeration ExportBom uses,
    // extended to recurse). If the live tree can't be read it falls back to a
    // flat disk dependency walk. The DB read/write lives in DatabaseManager so it
    // shares the cross-machine lock and atomic save with every other mutation.
    public static class BaselineManager
    {
        private const int MaxDepth = 12; // runaway-recursion guard

        // Walk the LIVE assembly component tree depth-first and return every
        // component (part or sub-assembly) with its indent Level and per-parent
        // Qty, in tree order. PartNo/Revision/Status are filled later from the
        // File records (DatabaseManager.SaveAssemblyBaseline). Returns empty if
        // the doc isn't a resolvable assembly (caller falls back to the disk walk).
        public static List<BaselineComponent> ResolveTree(ModelDoc2 doc)
        {
            var output = new List<BaselineComponent>();
            try
            {
                if (doc == null) return output;
                var cfg = doc.GetActiveConfiguration() as Configuration;
                Component2 root = cfg?.GetRootComponent3(true);
                if (root == null) return output;
                WalkChildren(root, 0, output);
            }
            catch { }
            return output;
        }

        // Append the direct children of `parent` (grouped to one row per unique
        // path+config with an instance Qty, first-seen order preserved), then
        // recurse into each sub-assembly child. Mirrors ExportBom's filters
        // (GetSuppression2 not IsSuppressed; honour ExcludeFromBOM; skip Toolbox).
        private static void WalkChildren(Component2 parent, int level,
            List<BaselineComponent> output)
        {
            if (level > MaxDepth) return;
            object[] kids = null;
            try { kids = parent.GetChildren() as object[]; }
            catch { }
            if (kids == null) return;

            var order = new List<string>();
            var group = new Dictionary<string, BaselineComponent>(
                StringComparer.OrdinalIgnoreCase);
            var firstComp = new Dictionary<string, Component2>(
                StringComparer.OrdinalIgnoreCase);

            foreach (object o in kids)
            {
                Component2 c = o as Component2;
                if (c == null) continue;

                // Suppressed components are not in the product — skip. Use
                // GetSuppression2 (IsSuppressed false-positives on lightweight).
                int sup = -1;
                try { sup = c.GetSuppression2(); }
                catch { }
                if (sup == (int)swComponentSuppressionState_e.swComponentSuppressed)
                    continue;

                try { if (c.ExcludeFromBOM) continue; } catch { }

                string path = "";
                try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path)) continue;

                // Skip Toolbox / standard hardware — not vault-managed.
                if (path.IndexOf("\\Toolbox\\",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                string ext = Path.GetExtension(path).ToLower();
                if (ext != ".sldprt" && ext != ".sldasm") continue;

                string norm;
                try { norm = Path.GetFullPath(path); }
                catch { norm = path; }

                string refCfg = "";
                try { refCfg = c.ReferencedConfiguration ?? ""; } catch { }

                string key = norm.ToLowerInvariant() + "|" +
                    refCfg.ToLowerInvariant();

                BaselineComponent bc;
                if (group.TryGetValue(key, out bc)) { bc.Qty++; continue; }

                bc = new BaselineComponent
                {
                    Path = norm,
                    Name = Path.GetFileName(norm),
                    Level = level,
                    Qty = 1
                };
                group[key] = bc;
                firstComp[key] = c;
                order.Add(key);
            }

            foreach (string key in order)
            {
                output.Add(group[key]);
                // Recurse into sub-assemblies (one representative instance).
                if (Path.GetExtension(group[key].Name)
                        .Equals(".sldasm", StringComparison.OrdinalIgnoreCase))
                    WalkChildren(firstComp[key], level + 1, output);
            }
        }

        // Flat fallback: walk the assembly's FULL dependency tree from disk and
        // return each unique component once (Level 0, occurrence-count Qty). Used
        // only when the live tree can't be read. Toolbox skipped; drawings excluded.
        public static List<BaselineComponent> ResolveComponents(string asmPath)
        {
            var result = new List<BaselineComponent>();
            var index = new Dictionary<string, BaselineComponent>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath))
                    return result;

                string asmNorm;
                try { asmNorm = Path.GetFullPath(asmPath); }
                catch { asmNorm = asmPath; }

                // Alternating array: name, path, name, path…
                object depsObj = PDMLiteAddin.SwApp.GetDocumentDependencies2(
                    asmPath, true, true, false);
                string[] deps = depsObj as string[];
                if (deps == null) return result;

                for (int i = 1; i < deps.Length; i += 2)
                {
                    string path = deps[i];
                    if (string.IsNullOrEmpty(path)) continue;

                    if (path.IndexOf("\\Toolbox\\",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    string norm;
                    try { norm = Path.GetFullPath(path); }
                    catch { norm = path; }

                    if (string.Equals(norm, asmNorm,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    string ext = Path.GetExtension(norm).ToLower();
                    if (ext != ".sldprt" && ext != ".sldasm") continue;

                    BaselineComponent existing;
                    if (index.TryGetValue(norm, out existing))
                    {
                        existing.Qty++;
                        continue;
                    }
                    var comp = new BaselineComponent
                    {
                        Path = norm,
                        Name = Path.GetFileName(norm),
                        Level = 0,
                        Qty = 1
                    };
                    index[norm] = comp;
                    result.Add(comp);
                }
            }
            catch { }
            return result;
        }

        // Capture + persist the as-released baseline for an assembly. NON-FATAL:
        // any failure is swallowed and audit-logged so it can never block a
        // release (same contract as ExportBom). Called from ReleaseFile right
        // after the assembly's status flips to Released — the release gate has
        // already guaranteed every tracked child is Released, so each child's
        // File record carries its released revision.
        public static void CaptureAssemblyBaseline(ModelDoc2 doc, string asmPath,
            string partNo, string rev, string config, string user)
        {
            try
            {
                // Prefer the live tree (true hierarchy + quantities); fall back
                // to the flat disk walk if the doc isn't a resolvable assembly.
                var comps = ResolveTree(doc);
                if (comps.Count == 0)
                    comps = ResolveComponents(asmPath);

                DatabaseManager.SaveAssemblyBaseline(asmPath,
                    Path.GetFileName(asmPath), partNo, rev, config, user, comps);
                AuditLogger.Log("BaselineCaptured", user,
                    Path.GetFileName(asmPath), partNo, rev,
                    comps.Count + " components");
            }
            catch (Exception ex)
            {
                try
                {
                    AuditLogger.Log("BaselineFailed", user,
                        Path.GetFileName(asmPath), partNo, rev, ex.Message);
                }
                catch { }
            }
        }
    }
}
