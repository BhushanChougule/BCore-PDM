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
    // viewing) so the baseline is a true historical record.
    public class BaselineComponent
    {
        public string Path     { get; set; }
        public string Name     { get; set; }
        public string PartNo   { get; set; }
        public string Revision { get; set; }
        public string Status   { get; set; }
        public int    Qty      { get; set; }
    }

    // Captures as-released baselines for assemblies. The expensive part — walking
    // the assembly's dependency tree — is done here from disk (no UI open needed,
    // same primitive GetUnreleasedComponentsByPath / GetParentAssemblies use);
    // the DB read/write lives in DatabaseManager so it shares the cross-machine
    // lock and atomic save with every other vault mutation.
    public static class BaselineManager
    {
        // Walk the assembly's FULL dependency tree from disk and return each
        // unique component file (part or sub-assembly) with an occurrence count.
        // PartNo/Revision/Status are left blank here — DatabaseManager fills them
        // from the live File records in ONE load when it persists the baseline
        // (so we never re-acquire the vault lock per component). Toolbox/standard
        // hardware is skipped (not vault-managed); drawings are not components.
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

                // traverse=true → the WHOLE multi-level tree (every descendant),
                // which is exactly the resolved set a baseline must record.
                // Alternating array: name, path, name, path…
                object depsObj = PDMLiteAddin.SwApp.GetDocumentDependencies2(
                    asmPath, true, true, false);
                string[] deps = depsObj as string[];
                if (deps == null) return result;

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

                    // Skip the assembly itself.
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
        // after the assembly's status flips to Released — at which point the
        // release gate has already guaranteed every tracked child is Released,
        // so each child's File record carries its released revision.
        public static void CaptureAssemblyBaseline(string asmPath, string partNo,
            string rev, string config, string user)
        {
            try
            {
                var comps = ResolveComponents(asmPath);
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
