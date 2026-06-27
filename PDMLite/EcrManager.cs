using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace PDMLite
{
    // ── ENGINEERING CHANGE REQUEST (ECR) ───────────────────────────────────
    // Formalises an engineer change request into a trackable ECR RECORD — the
    // PLM/ECM front of the change-control workflow (SOLIDWORKS Manage / Aras /
    // Teamcenter all start a change here). An ECR captures WHAT change is being
    // asked for and WHO asked, then a Master REVIEWS it (Accept / Reject) and
    // optionally CONVERTS it to a downstream ECO (Engineering Change Order) when
    // the ECO layer is present. Distinct from the lightweight RevisionRequest
    // (vault.xml <RevisionRequests>) which only drives the
    // Unlock/Revision/Release approval queue — the ECR adds the formal,
    // durable, dispositioned change history those leave open.
    //
    // PERSISTENCE: its OWN file ecrs.xml under the VAULT root, NOT vault.xml —
    // so this feature is self-contained and never contends for / bloats the
    // shared vault.xml under the cross-machine lock (mirrors AuditLogger ->
    // audit.csv, UserPrefs -> prefs.xml, EmailManager -> email.config). The
    // read-modify-write is serialised in-process by _lock AND cross-process by
    // a session-local named Mutex (mirrors UserPrefs), and Save is ATOMIC
    // (per-process temp + File.Replace/Move) so a concurrent reader never sees a
    // truncated file. Every operation is NON-FATAL (swallows IO errors) — an ECR
    // hiccup must never disrupt a save / release / any workflow.
    internal static class EcrManager
    {
        private const string VaultFolder = @"N:\PDM-SolidWorks\VAULT";
        private static string EcrPath => Path.Combine(VaultFolder, "ecrs.xml");

        private static readonly object _lock = new object();
        private const string MutexName = "BCorePDM.Ecrs";

        // ── The ECR record (POCO) ──────────────────────────────────────────
        // State machine: Submitted -> UnderReview -> Accepted | Rejected
        //                Accepted   -> Converted (an ECO was created from it)
        public sealed class Ecr
        {
            public string Id { get; set; }              // GUID "N"
            public string Number { get; set; }          // "ECR-0001"
            public string Type { get; set; }            // Revision/Release/Unlock/General
            public string Description { get; set; }
            public string AffectedFilePath { get; set; }
            public string AffectedPartNo { get; set; }
            public string Requester { get; set; }
            public DateTime CreatedDate { get; set; }
            public string State { get; set; }           // Submitted/UnderReview/Accepted/Rejected/Converted
            public string Reviewer { get; set; }
            public DateTime ReviewedDate { get; set; }  // MinValue until reviewed
            public string Disposition { get; set; }     // reviewer's note (reason/details)
            public string LinkedEcoNumber { get; set; } // set on Convert-to-ECO
        }

        public const string TypeRevision = "Revision";
        public const string TypeRelease  = "Release";
        public const string TypeUnlock   = "Unlock";
        public const string TypeGeneral  = "General";

        public const string StateSubmitted   = "Submitted";
        public const string StateUnderReview = "UnderReview";
        public const string StateAccepted    = "Accepted";
        public const string StateRejected    = "Rejected";
        public const string StateConverted   = "Converted";

        // Categorised reason codes the EcrForm offers (the change-request reason
        // taxonomy, like the ReasonForChangeForm codes). General-purpose, not
        // shop-specific — a default set any customer can use as-is.
        public static string[] ReasonCodes()
        {
            return new[]
            {
                "Design Change", "Customer Request", "Cost Reduction",
                "Manufacturing Improvement", "Supplier Change",
                "Document/Drawing Error", "Regulatory/Compliance",
                "Quality/Field Issue", "Other"
            };
        }

        // ── Load / Save (atomic, cross-process, non-fatal) ──────────────────
        private static XDocument Load()
        {
            try { if (File.Exists(EcrPath)) return XDocument.Load(EcrPath); }
            catch { }
            return new XDocument(new XElement("Ecrs"));
        }

        private static XElement Root(XDocument d)
        {
            if (d.Root == null) { d.Add(new XElement("Ecrs")); }
            return d.Root;
        }

        private static void Save(XDocument d)
        {
            string tmp = EcrPath + "." +
                System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                Directory.CreateDirectory(VaultFolder);
                SweepStaleTemps();
                // ATOMIC write: serialise to a per-process temp then swap it into
                // place so a concurrent reader never sees a TRUNCATED ecrs.xml
                // (a partial read would XmlException -> Load's empty-doc fallback
                // -> the next Save persisting that empty doc, losing every ECR).
                // File.Replace is atomic on NTFS; the per-PID temp name keeps two
                // instances from sharing one temp even if the mutex wait timed out.
                d.Save(tmp);
                if (File.Exists(EcrPath)) File.Replace(tmp, EcrPath, null);
                else File.Move(tmp, EcrPath);
            }
            catch
            {
                // File.Replace/File.Move (and a failed d.Save to the temp) ALL
                // leave the existing ecrs.xml UNCHANGED, so the old data survives.
                // We deliberately do NOT fall back to a truncating in-place
                // d.Save(EcrPath). The lost write is re-attempted on the next
                // mutation; just clear the stray temp.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static bool _sweptTemps;
        private static void SweepStaleTemps()
        {
            if (_sweptTemps) return;
            _sweptTemps = true;
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var f in Directory.GetFiles(VaultFolder, "ecrs.xml.*.tmp"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        // Read-modify-write under BOTH the in-process _lock AND the cross-process
        // MutexName so concurrent SOLIDWORKS instances can't interleave their
        // Load->change->Save. Best-effort 50ms wait (mirrors UserPrefs/RecentFiles):
        // if a peer holds it, proceed unsynchronised rather than stall a UI click.
        // WHOLE body non-fatal — the mutex ctor / lock can throw and the callers
        // run inside WinForms Click handlers with no try/catch of their own.
        private static T Mutate<T>(Func<XDocument, T> change)
        {
            try
            {
                lock (_lock)
                using (var mtx = new Mutex(false, MutexName))
                {
                    bool held = false;
                    try { held = mtx.WaitOne(50); }
                    catch (AbandonedMutexException) { held = true; }
                    try
                    {
                        var d = Load();
                        T result = change(d);
                        Save(d);
                        return result;
                    }
                    finally { if (held) try { mtx.ReleaseMutex(); } catch { } }
                }
            }
            catch { return default(T); }
        }

        // ── Date helpers (InvariantCulture everywhere — house rule) ──────────
        private const string DateFmt = "yyyy-MM-dd HH:mm:ss";

        private static string FmtDate(DateTime d) =>
            d == DateTime.MinValue ? "" :
            d.ToString(DateFmt, CultureInfo.InvariantCulture);

        private static DateTime ParseDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            DateTime dt;
            if (DateTime.TryParseExact(s, DateFmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt)) return dt;
            return DateTime.MinValue;
        }

        private static string A(XElement e, string name) =>
            (string)e.Attribute(name) ?? "";

        private static Ecr FromElement(XElement e)
        {
            return new Ecr
            {
                Id               = A(e, "Id"),
                Number           = A(e, "Number"),
                Type             = A(e, "Type"),
                Description      = A(e, "Description"),
                AffectedFilePath = A(e, "AffectedFilePath"),
                AffectedPartNo   = A(e, "AffectedPartNo"),
                Requester        = A(e, "Requester"),
                CreatedDate      = ParseDate(A(e, "CreatedDate")),
                State            = A(e, "State"),
                Reviewer         = A(e, "Reviewer"),
                ReviewedDate     = ParseDate(A(e, "ReviewedDate")),
                Disposition      = A(e, "Disposition"),
                LinkedEcoNumber  = A(e, "LinkedEcoNumber")
            };
        }

        private static void ToElement(XElement e, Ecr r)
        {
            SetAttr(e, "Id", r.Id);
            SetAttr(e, "Number", r.Number);
            SetAttr(e, "Type", r.Type);
            SetAttr(e, "Description", r.Description);
            SetAttr(e, "AffectedFilePath", r.AffectedFilePath);
            SetAttr(e, "AffectedPartNo", r.AffectedPartNo);
            SetAttr(e, "Requester", r.Requester);
            SetAttr(e, "CreatedDate", FmtDate(r.CreatedDate));
            SetAttr(e, "State", r.State);
            SetAttr(e, "Reviewer", r.Reviewer);
            SetAttr(e, "ReviewedDate", FmtDate(r.ReviewedDate));
            SetAttr(e, "Disposition", r.Disposition);
            SetAttr(e, "LinkedEcoNumber", r.LinkedEcoNumber);
        }

        private static void SetAttr(XElement e, string name, string val)
        {
            e.SetAttributeValue(name, val ?? "");
        }

        // ── Number allocation ("ECR-0001") ─────────────────────────────────
        // Computed under the SAME Mutate lock that adds the record, so two
        // machines can't allocate the same number concurrently. Scans every
        // existing "ECR-NNNN" for the max, then +1 (so a deleted hole isn't
        // re-used — append-only numbering, the ECR/ECO convention).
        private static string NextNumber(XElement root)
        {
            int max = 0;
            foreach (var e in root.Elements("Ecr"))
            {
                string num = A(e, "Number");
                if (num.StartsWith("ECR-", StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    if (int.TryParse(num.Substring(4), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out n) && n > max)
                        max = n;
                }
            }
            return "ECR-" + (max + 1).ToString("D4", CultureInfo.InvariantCulture);
        }

        // ── CreateEcr ───────────────────────────────────────────────────────
        // Creates a new ECR in the Submitted state and returns it (its Number
        // and Id assigned). Returns null on any failure (non-fatal).
        public static Ecr CreateEcr(string type, string description,
            string affectedFilePath, string affectedPartNo, string requester)
        {
            return Mutate(d =>
            {
                var root = Root(d);
                var rec = new Ecr
                {
                    Id               = Guid.NewGuid().ToString("N"),
                    Number           = NextNumber(root),
                    Type             = string.IsNullOrEmpty(type) ? TypeGeneral : type,
                    Description      = description ?? "",
                    AffectedFilePath = affectedFilePath ?? "",
                    AffectedPartNo   = affectedPartNo ?? "",
                    Requester        = requester ?? "",
                    CreatedDate      = DateTime.Now,
                    State            = StateSubmitted,
                    Reviewer         = "",
                    ReviewedDate     = DateTime.MinValue,
                    Disposition      = "",
                    LinkedEcoNumber  = ""
                };
                var e = new XElement("Ecr");
                ToElement(e, rec);
                root.Add(e);
                return rec;
            });
        }

        // ── GetEcrs(filter) ─────────────────────────────────────────────────
        // Returns ECRs, most recent first (newest CreatedDate at the top).
        // stateFilter "" / null = all states; otherwise an exact (case-insensitive)
        // state match (e.g. "Submitted" for the Master's review queue).
        public static List<Ecr> GetEcrs(string stateFilter = "")
        {
            lock (_lock)
            {
                try
                {
                    var list = Load().Root.Elements("Ecr")
                        .Select(FromElement)
                        .Where(r => string.IsNullOrEmpty(stateFilter) ||
                            string.Equals(r.State, stateFilter,
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.CreatedDate)
                        .ToList();
                    return list;
                }
                catch { return new List<Ecr>(); }
            }
        }

        // ECRs raised by one engineer (for "My ECRs"), most recent first.
        public static List<Ecr> GetEcrsByRequester(string requester)
        {
            lock (_lock)
            {
                try
                {
                    return Load().Root.Elements("Ecr")
                        .Select(FromElement)
                        .Where(r => string.Equals(r.Requester, requester ?? "",
                            StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.CreatedDate)
                        .ToList();
                }
                catch { return new List<Ecr>(); }
            }
        }

        // ── SetEcrState ─────────────────────────────────────────────────────
        // Transitions an ECR (by Id) to a new state, stamping the reviewer +
        // review date + disposition. Returns the updated record, or null when
        // the Id isn't found / on failure. Idempotent-safe (re-setting the same
        // state just refreshes the stamp).
        public static Ecr SetEcrState(string id, string newState,
            string reviewer, string disposition)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Mutate(d =>
            {
                var e = Root(d).Elements("Ecr")
                    .FirstOrDefault(x => string.Equals(A(x, "Id"), id,
                        StringComparison.OrdinalIgnoreCase));
                if (e == null) return (Ecr)null;
                SetAttr(e, "State", newState);
                SetAttr(e, "Reviewer", reviewer);
                SetAttr(e, "ReviewedDate", FmtDate(DateTime.Now));
                if (!string.IsNullOrEmpty(disposition))
                    SetAttr(e, "Disposition", disposition);
                return FromElement(e);
            });
        }

        // ── ConvertToEco ────────────────────────────────────────────────────
        // Accepts the ECR and, if a downstream ECO layer is present (PR9's
        // EcoManager), creates an ECO from it via reflection so THIS branch
        // builds + runs whether or not PR9 is merged (graceful feature-detect:
        // we look up the type by name and invoke a CreateEco-style method).
        // When no ECO layer exists the ECR is simply marked Accepted (the change
        // is approved; an ECO can be raised manually later). Returns the linked
        // ECO number ("" when none was created), and sets the ECR state to
        // Converted (with the ECO number) or Accepted accordingly.
        public static string ConvertToEco(string id, string reviewer,
            string disposition)
        {
            if (string.IsNullOrEmpty(id)) return "";

            // Snapshot the ECR first (outside Mutate) so we have its fields for
            // the ECO create call.
            Ecr ecr = GetEcrs("").FirstOrDefault(r =>
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ecr == null) return "";

            string ecoNumber = TryCreateEco(ecr);

            string finalState = string.IsNullOrEmpty(ecoNumber)
                ? StateAccepted : StateConverted;

            Mutate(d =>
            {
                var e = Root(d).Elements("Ecr")
                    .FirstOrDefault(x => string.Equals(A(x, "Id"), id,
                        StringComparison.OrdinalIgnoreCase));
                if (e == null) return (object)null;
                SetAttr(e, "State", finalState);
                SetAttr(e, "Reviewer", reviewer);
                SetAttr(e, "ReviewedDate", FmtDate(DateTime.Now));
                if (!string.IsNullOrEmpty(disposition))
                    SetAttr(e, "Disposition", disposition);
                if (!string.IsNullOrEmpty(ecoNumber))
                    SetAttr(e, "LinkedEcoNumber", ecoNumber);
                return null;
            });

            return ecoNumber ?? "";
        }

        // Feature-detect PR9's EcoManager and create an ECO from the ECR. Pure
        // reflection so this file references NO PR9 symbol and the branch
        // compiles standalone (PR9 may not be merged). Looks for a static
        // PDMLite.EcoManager with a CreateEco-style method; tries a couple of
        // plausible signatures, then reads the returned ECO's Number. Any
        // failure (no type / no method / throw) returns "" -> caller marks the
        // ECR Accepted instead of Converted. NON-FATAL.
        private static string TryCreateEco(Ecr ecr)
        {
            try
            {
                Type ecoMgr = FindType("PDMLite.EcoManager");
                if (ecoMgr == null) return "";

                // Candidate factory methods, most-specific first. We pass the
                // ECR's own fields so the ECO inherits the change context.
                object eco = InvokeFirst(ecoMgr, new[]
                    {
                        "CreateEcoFromEcr", "CreateFromEcr", "CreateEco", "Create"
                    },
                    new object[]
                    {
                        ecr.Description, ecr.AffectedFilePath,
                        ecr.AffectedPartNo, ecr.Requester
                    });
                if (eco == null) return "";

                // Read the created ECO's number from a "Number" property/field.
                string num = ReadStringMember(eco, "Number");
                if (string.IsNullOrEmpty(num))
                    num = ReadStringMember(eco, "EcoNumber");
                return num ?? "";
            }
            catch { return ""; }
        }

        private static Type FindType(string fullName)
        {
            try
            {
                // The whole add-in is one assembly, so EcoManager (if PR9 merged)
                // lives in this same assembly.
                Type t = typeof(EcrManager).Assembly.GetType(fullName, false);
                if (t != null) return t;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType(fullName, false); if (t != null) return t; }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Try each named static method; for each, attempt the full arg set then
        // progressively fewer leading args, since PR9's exact signature is
        // unknown. Returns the first non-null result.
        private static object InvokeFirst(Type type, string[] methodNames,
            object[] fullArgs)
        {
            foreach (string name in methodNames)
            {
                for (int take = fullArgs.Length; take >= 0; take--)
                {
                    try
                    {
                        var args = new object[take];
                        Array.Copy(fullArgs, args, take);
                        var argTypes = args.Select(a => typeof(string)).ToArray();
                        var mi = type.GetMethod(name,
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Static,
                            null, argTypes, null);
                        if (mi == null) continue;
                        return mi.Invoke(null, args);
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string ReadStringMember(object obj, string member)
        {
            if (obj == null) return "";
            try
            {
                var t = obj.GetType();
                var p = t.GetProperty(member);
                if (p != null) return Convert.ToString(p.GetValue(obj, null)) ?? "";
                var f = t.GetField(member);
                if (f != null) return Convert.ToString(f.GetValue(obj)) ?? "";
            }
            catch { }
            return "";
        }
    }
}
