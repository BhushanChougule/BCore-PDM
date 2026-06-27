using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PDMLite
{
    // ── Engineering Change Bulletin (ECB) subsystem — persistence layer ──────
    //
    // An ECB is the formal "engineering change" record industry PDM/PLM systems
    // (SOLIDWORKS PDM/Manage, Windchill, Teamcenter, Aras) put ABOVE the per-file
    // revision: it captures the WHY of a change, the affected items + their
    // before/after revisions, a workflow state, and who/when. BCore already
    // captures a per-release reason-for-change (ReasonForChangeForm) and an
    // as-released baseline; the ECB ties those together into a single auditable
    // change package and is the foundation for an ECR→ECB flow (PR10).
    //
    // PERSISTENCE: stored in its OWN file under the VAULT root (ecbs.xml), NOT
    // vault.xml — so the PR is self-contained and never contends on vault.xml's
    // schema. Mirrors the atomic-write discipline of UserPrefs / DatabaseManager:
    //   - serialise to a per-process temp then File.Replace/Move into place, so a
    //     concurrent reader on another machine never sees a TRUNCATED ecbs.xml;
    //   - an optional cross-machine lock file (ecbs.lock, FileShare.None — SMB
    //     honours it across PCs, a named Mutex would not) serialises the
    //     read-modify-write so two machines can't last-writer-wins each other.
    //     If the lock can't be taken it proceeds WITHOUT it (degrades to
    //     last-writer-wins, never worse) rather than blocking — the non-fatal
    //     philosophy of the rest of the codebase.
    // Every operation is NON-FATAL (swallows IO/parse errors) — an ECB hiccup must
    // never block a save / release / UI message loop.
    internal static class EcbManager
    {
        // Hard-coded VAULT root, mirroring AuditLogger.LogFile (DatabaseManager's
        // VaultFolder is private, and the rest of the codebase pins these paths in
        // code as well — see VaultManager's WipFolder/ExportRoot).
        private const string VaultFolder = @"N:\PDM-SolidWorks\VAULT";
        private static string EcbsFile => Path.Combine(VaultFolder, "ecbs.xml");
        private static string LockFilePath => Path.Combine(VaultFolder, "ecbs.lock");

        // Workflow states (ordered). Draft → Open → Approved → Implemented → Closed.
        public static readonly string[] States =
        {
            "Draft", "Open", "Approved", "Implemented", "Closed"
        };

        // Reason codes shared with the ECB dialog (mirrors VaultManager's
        // ReleaseReasonCodes shape; kept here so the PR is self-contained).
        public static readonly string[] ReasonCodes =
        {
            "Design Change", "Customer Request", "Cost Reduction",
            "Manufacturing Improvement", "Supplier Change",
            "Document / Drawing Error", "Quality / Corrective Action",
            "Regulatory / Compliance", "Other"
        };

        private static readonly object _lock = new object();

        // ── Cross-machine lock (best-effort) ──────────────────────────────
        // Mirrors DatabaseManager.AcquireProcessLock at a smaller scale: an
        // exclusive FileShare.None handle held for the whole read-modify-write.
        // Returns null when it can't be taken (degraded — proceed unsynchronised).
        private static FileStream AcquireLock()
        {
            try { Directory.CreateDirectory(VaultFolder); } catch { }
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    return new FileStream(LockFilePath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    // Sharing violation (held by another machine) — wait and retry.
                    System.Threading.Thread.Sleep(150);
                }
                catch
                {
                    // Path unreachable / access denied (network down) — fail fast,
                    // proceed unsynchronised rather than spin a down network.
                    break;
                }
            }
            return null;
        }

        // ── Load / Save ───────────────────────────────────────────────────
        private static XDocument Load()
        {
            try
            {
                if (File.Exists(EcbsFile))
                {
                    // FileShare.ReadWrite so an in-progress writer on another machine
                    // never blocks the read (mirrors AuditReportForm's audit.csv read).
                    using (var fs = new FileStream(EcbsFile, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite))
                        return XDocument.Load(fs);
                }
            }
            catch { }
            return new XDocument(new XElement("Ecbs"));
        }

        private static void Save(XDocument d)
        {
            string tmp = EcbsFile + "." +
                System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                Directory.CreateDirectory(VaultFolder);
                // ATOMIC: temp then swap, so a concurrent reader never sees a torn
                // file (a partial read → XmlException → empty-doc fallback → the
                // next Save wiping every ECB). Per-process temp name so two machines
                // never share one temp even if the cross-machine lock was unavailable.
                d.Save(tmp);
                if (File.Exists(EcbsFile)) File.Replace(tmp, EcbsFile, null);
                else File.Move(tmp, EcbsFile);
            }
            catch
            {
                // The swap failed — File.Replace/Move (and a failed d.Save to the
                // temp) all leave the existing ecbs.xml UNCHANGED, so the old data
                // survives. Do NOT fall back to a truncating in-place d.Save: that
                // re-introduces the torn write this method exists to prevent.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // Run a read-modify-write under the in-process lock + the best-effort
        // cross-machine file lock. Whole body non-fatal — callers are UI handlers
        // with no try/catch, so an escape would be unhandled on SOLIDWORKS' thread.
        private static T Mutate<T>(Func<XDocument, T> change)
        {
            try
            {
                lock (_lock)
                {
                    FileStream lk = AcquireLock();
                    try
                    {
                        var d = Load();
                        T result = change(d);
                        Save(d);
                        return result;
                    }
                    finally { try { lk?.Dispose(); } catch { } }
                }
            }
            catch { return default(T); }
        }

        // ── Number sequence ───────────────────────────────────────────────
        // Per-vault running number "ECB-0001". Computed from the max existing
        // number so a gap (deleted ECB) never re-issues a used number. Called
        // inside Mutate (lock held), so two machines can't issue the same number.
        private const string NumberPrefix = "ECB-";
        private static string NextNumber(XDocument d)
        {
            int max = 0;
            foreach (var e in d.Root.Elements("Ecb"))
            {
                string n = (string)e.Attribute("Number") ?? "";
                if (n.StartsWith(NumberPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(n.Substring(NumberPrefix.Length),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                        && v > max)
                        max = v;
                }
            }
            return NumberPrefix + (max + 1).ToString("D4", CultureInfo.InvariantCulture);
        }

        private const string DateFmt = "yyyy-MM-dd HH:mm:ss"; // ISO so it sorts

        // ── Element <-> POCO round-trip ───────────────────────────────────
        private static XElement ToElement(Ecb ecb)
        {
            var el = new XElement("Ecb",
                new XAttribute("Id", ecb.Id ?? ""),
                new XAttribute("Number", ecb.Number ?? ""),
                new XAttribute("State", ecb.State ?? "Draft"),
                new XAttribute("CreatedBy", ecb.CreatedBy ?? ""),
                new XAttribute("CreatedDate", ecb.CreatedDate ?? ""),
                new XAttribute("ClosedBy", ecb.ClosedBy ?? ""),
                new XAttribute("ClosedDate", ecb.ClosedDate ?? ""),
                new XAttribute("BaselineAssemblyPath", ecb.BaselineAssemblyPath ?? ""),
                new XAttribute("BaselineRev", ecb.BaselineRev ?? ""),
                new XElement("Title", ecb.Title ?? ""),
                new XElement("Description", ecb.Description ?? ""),
                new XElement("Reason", ecb.Reason ?? ""));
            var items = new XElement("Items");
            if (ecb.Items != null)
                foreach (var it in ecb.Items)
                    items.Add(new XElement("Item",
                        new XAttribute("FilePath", it.FilePath ?? ""),
                        new XAttribute("PartNo", it.PartNo ?? ""),
                        new XAttribute("FromRev", it.FromRev ?? ""),
                        new XAttribute("ToRev", it.ToRev ?? "")));
            el.Add(items);
            return el;
        }

        private static Ecb FromElement(XElement el)
        {
            var ecb = new Ecb
            {
                Id = (string)el.Attribute("Id") ?? "",
                Number = (string)el.Attribute("Number") ?? "",
                State = (string)el.Attribute("State") ?? "Draft",
                CreatedBy = (string)el.Attribute("CreatedBy") ?? "",
                CreatedDate = (string)el.Attribute("CreatedDate") ?? "",
                ClosedBy = (string)el.Attribute("ClosedBy") ?? "",
                ClosedDate = (string)el.Attribute("ClosedDate") ?? "",
                BaselineAssemblyPath = (string)el.Attribute("BaselineAssemblyPath") ?? "",
                BaselineRev = (string)el.Attribute("BaselineRev") ?? "",
                Title = (string)el.Element("Title") ?? "",
                Description = (string)el.Element("Description") ?? "",
                Reason = (string)el.Element("Reason") ?? "",
                Items = new List<EcbAffectedItem>()
            };
            var items = el.Element("Items");
            if (items != null)
                foreach (var it in items.Elements("Item"))
                    ecb.Items.Add(new EcbAffectedItem
                    {
                        FilePath = (string)it.Attribute("FilePath") ?? "",
                        PartNo = (string)it.Attribute("PartNo") ?? "",
                        FromRev = (string)it.Attribute("FromRev") ?? "",
                        ToRev = (string)it.Attribute("ToRev") ?? ""
                    });
            return ecb;
        }

        // ── Public API ────────────────────────────────────────────────────

        // Persists a NEW ECB (assigns Id + Number + CreatedBy/Date if absent),
        // returns the saved Ecb (with its assigned Number/Id), or null on failure.
        // Audit-logged "EcbCreated".
        public static Ecb CreateEcb(Ecb ecb)
        {
            if (ecb == null) return null;
            var saved = Mutate(d =>
            {
                if (string.IsNullOrEmpty(ecb.Id))
                    ecb.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(ecb.Number))
                    ecb.Number = NextNumber(d);
                if (string.IsNullOrEmpty(ecb.CreatedBy))
                    ecb.CreatedBy = PDMLiteAddin.CurrentUser;
                if (string.IsNullOrEmpty(ecb.CreatedDate))
                    ecb.CreatedDate = DateTime.Now.ToString(DateFmt,
                        CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(ecb.State)) ecb.State = "Draft";
                d.Root.Add(ToElement(ecb));
                return ecb;
            });
            if (saved != null)
                AuditLogger.Log("EcbCreated", PDMLiteAddin.CurrentUser,
                    saved.Number ?? "", "", "",
                    (saved.Title ?? "") +
                    (saved.Items != null && saved.Items.Count > 0
                        ? "  (" + saved.Items.Count + " items)" : ""));
            return saved;
        }

        // Returns all ECBs, MOST RECENT FIRST (CreatedDate ISO sorts), optionally
        // filtered to one state ("" / null = all). Read-only; never writes.
        public static List<Ecb> GetEcbs(string stateFilter = null)
        {
            lock (_lock)
            {
                try
                {
                    var list = Load().Root.Elements("Ecb").Select(FromElement);
                    if (!string.IsNullOrEmpty(stateFilter))
                        list = list.Where(e => string.Equals(e.State, stateFilter,
                            StringComparison.OrdinalIgnoreCase));
                    return list.OrderByDescending(e => e.CreatedDate ?? "",
                        StringComparer.Ordinal).ToList();
                }
                catch { return new List<Ecb>(); }
            }
        }

        // Returns one ECB by Id, or null.
        public static Ecb GetEcb(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                try
                {
                    var el = Load().Root.Elements("Ecb").FirstOrDefault(e =>
                        string.Equals((string)e.Attribute("Id"), id,
                            StringComparison.OrdinalIgnoreCase));
                    return el == null ? null : FromElement(el);
                }
                catch { return null; }
            }
        }

        // Replaces an existing ECB in place (matched by Id), preserving its
        // Number/CreatedBy/CreatedDate. Returns true on success. Audit "EcbUpdated".
        public static bool UpdateEcb(Ecb ecb)
        {
            if (ecb == null || string.IsNullOrEmpty(ecb.Id)) return false;
            bool ok = Mutate(d =>
            {
                var el = d.Root.Elements("Ecb").FirstOrDefault(e =>
                    string.Equals((string)e.Attribute("Id"), ecb.Id,
                        StringComparison.OrdinalIgnoreCase));
                if (el == null) return false;
                // Preserve immutable identity fields from the stored record.
                if (string.IsNullOrEmpty(ecb.Number))
                    ecb.Number = (string)el.Attribute("Number") ?? "";
                if (string.IsNullOrEmpty(ecb.CreatedBy))
                    ecb.CreatedBy = (string)el.Attribute("CreatedBy") ?? "";
                if (string.IsNullOrEmpty(ecb.CreatedDate))
                    ecb.CreatedDate = (string)el.Attribute("CreatedDate") ?? "";
                el.ReplaceWith(ToElement(ecb));
                return true;
            });
            if (ok)
                AuditLogger.Log("EcbUpdated", PDMLiteAddin.CurrentUser,
                    ecb.Number ?? "", "", "", ecb.Title ?? "");
            return ok;
        }

        // Flips an ECB's State (matched by Id). On "Closed" stamps ClosedBy/Date.
        // Returns true on success. Audit "EcbClosed" when closing, else "EcbUpdated".
        public static bool SetEcbState(string id, string state)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(state)) return false;
            bool closing = string.Equals(state, "Closed",
                StringComparison.OrdinalIgnoreCase);
            string number = null;
            bool ok = Mutate(d =>
            {
                var el = d.Root.Elements("Ecb").FirstOrDefault(e =>
                    string.Equals((string)e.Attribute("Id"), id,
                        StringComparison.OrdinalIgnoreCase));
                if (el == null) return false;
                number = (string)el.Attribute("Number") ?? "";
                SetAttr(el, "State", state);
                if (closing)
                {
                    SetAttr(el, "ClosedBy", PDMLiteAddin.CurrentUser);
                    SetAttr(el, "ClosedDate", DateTime.Now.ToString(DateFmt,
                        CultureInfo.InvariantCulture));
                }
                else
                {
                    // Re-opening clears the closed stamp so it never lies.
                    SetAttr(el, "ClosedBy", "");
                    SetAttr(el, "ClosedDate", "");
                }
                return true;
            });
            if (ok)
                AuditLogger.Log(closing ? "EcbClosed" : "EcbUpdated",
                    PDMLiteAddin.CurrentUser, number ?? "", "", "",
                    "State → " + state);
            return ok;
        }

        private static void SetAttr(XElement el, string name, string value)
        {
            var a = el.Attribute(name);
            if (a == null) el.Add(new XAttribute(name, value ?? ""));
            else a.Value = value ?? "";
        }
    }

    // ── POCOs (top-level, like VaultManager.WhereUsedEntry / BatchResult) ──

    // One Engineering Change Bulletin. Id is a GUID "N" (collision-free across
    // machines, like RevisionRequest ids); Number is the per-vault running
    // "ECB-0001"; dates are ISO "yyyy-MM-dd HH:mm:ss" (sort chronologically).
    public class Ecb
    {
        public string Id;                    // Guid "N"
        public string Number;                // "ECB-0001"
        public string Title;
        public string Description;
        public string Reason;                // categorised reason code (+ detail)
        public string State = "Draft";       // Draft/Open/Approved/Implemented/Closed
        public string CreatedBy;
        public string CreatedDate;           // ISO
        public string ClosedBy;
        public string ClosedDate;            // ISO
        public List<EcbAffectedItem> Items = new List<EcbAffectedItem>();
        // Optional link to the assembly whose as-released baseline anchors this
        // change (so the ECB can be tied to a specific released file set).
        public string BaselineAssemblyPath;
        public string BaselineRev;
    }

    // One affected item on an ECB: the file and its before/after revision.
    public class EcbAffectedItem
    {
        public string FilePath;
        public string PartNo;
        public string FromRev;
        public string ToRev;
    }
}
