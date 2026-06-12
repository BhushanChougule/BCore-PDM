using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PDMLite
{
    // One SOLIDWORKS configuration's identity. Config name = Part No by convention,
    // so Name and PartNo are the same value — both stored for clarity.
    public class ConfigEntry
    {
        public string Name        { get; set; } // = PartNo (config name IS the part number)
        public string PartNo      { get; set; }
        public string Description { get; set; }
        public string DrawingNo   { get; set; }
        public string Revision    { get; set; }
    }

    public class VaultFile
    {
        public string FilePath    { get; set; }
        public string FileName    { get; set; }
        // Active/primary config values — used for display and backward compatibility.
        public string PartNumber  { get; set; }
        public string Description { get; set; }
        public string Revision    { get; set; }
        // File-level: all configs share one lifecycle (OS read-only is file-level).
        public string Status      { get; set; }
        public string ModifiedBy  { get; set; }
        public DateTime ModifiedDate { get; set; }
        // Populated by GetAllFiles for the Vault Dashboard (empty when not locked).
        // Other queries leave this null — read lock state via GetLockInfo instead.
        public string LockedBy    { get; set; }
        // Populated by GetAllFiles: the most recent "Released" history entry's
        // timestamp + user (DateTime.MinValue / "" if never released). Distinct
        // from ModifiedDate/ModifiedBy (last save).
        public DateTime ReleasedDate { get; set; }
        public string ReleasedBy { get; set; }
        public bool HasBrokenRefs { get; set; }
        // Per-configuration metadata (parts/assemblies). Empty for drawings and for
        // old records that haven't been re-saved since this feature shipped.
        public List<ConfigEntry> Configurations { get; set; } = new List<ConfigEntry>();
        // Drawing-only: which model file and which of its configs this drawing documents.
        // ReferencedConfigs = "" means "all configs" (e.g. a config-table drawing).
        public string ReferencedModel   { get; set; }
        public string ReferencedConfigs { get; set; }
    }

    public class LockInfo
    {
        public bool IsLocked { get; set; }
        public string LockedBy { get; set; }
        public DateTime LockedDate { get; set; }
    }

    public class HistoryEntry
    {
        public string Status { get; set; }
        public string ChangedBy { get; set; }
        public string ChangedDate { get; set; }
        public string ChangeNote { get; set; }
    }

    public class RevisionRequest
    {
        public string Id { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string RequestedBy { get; set; }
        public string RequestDate { get; set; }
        public string Status { get; set; }
        public string Note { get; set; }
        public string RequestType { get; set; } // "Unlock", "Revision", "Release"
    }

    // One engineer's open "presence" on a file. Recorded when a WIP file is
    // opened and cleared when it closes; used to warn a second engineer that
    // someone else is already editing the same file.
    public class OpenSession
    {
        public string FilePath { get; set; }
        public string User { get; set; }
        public string Machine { get; set; }
        public DateTime OpenedDate { get; set; }
    }

    public static class DatabaseManager
    {
        private const string VaultFolder = @"N:\PDM-SolidWorks\VAULT";
        private const string DataFile = @"N:\PDM-SolidWorks\VAULT\vault.xml";
        // One-generation backup, refreshed by every successful atomic Save
        // (File.Replace's backup argument — the previous vault.xml is renamed,
        // not deleted). LoadOrCreate restores from it when vault.xml is missing
        // or corrupt, so a crash mid-write can never cost more than one save.
        private const string BackupFile = @"N:\PDM-SolidWorks\VAULT\vault.xml.bak";
        // Shared with SwAddin's Rule 2.5 so the save rules and the DB-side
        // rival checks can never disagree about what is "inside the vault".
        internal const string WipRoot = @"N:\PDM-SolidWorks\WIP";

        // Canonical-WIP test: the WIP root itself or paths under it — NOT
        // prefix-siblings like N:\PDM-SolidWorks\WIP_OLD\… (the prefix-match
        // class audit C3 fixed for export globs).
        private static bool IsUnderWip(string path)
        {
            string p = (path ?? "").Trim();
            return p.StartsWith(WipRoot + "\\", StringComparison.OrdinalIgnoreCase)
                || p.Equals(WipRoot, StringComparison.OrdinalIgnoreCase);
        }
        private const string ScrapRoot = @"N:\PDM-SolidWorks\SCRAP";

        // Cap search results so a broad term at full scale (~50k files) can
        // never render thousands of cards into the task pane and freeze it.
        private const int MaxSearchResults = 50;

        // Division subfolders under WIP — one per product line.
        // Initialize() creates these on first addin load so engineers
        // can navigate to them immediately without manual setup.
        public static readonly string[] WipDivisions = {
            "A - Aurora Shelving",
            "B - Aurora Mobile",
            "E - Cabinets",
            "G - Hardware",
            "L - Library Shelving",
            "M - Conveyor",
            "O - Oil tank",
            "X - Rotary"
        };

        private static readonly object _lock = new object();

        private static XDocument LoadOrCreate()
        {
            if (!Directory.Exists(VaultFolder))
                Directory.CreateDirectory(VaultFolder);

            if (!File.Exists(DataFile))
            {
                // A missing vault.xml on an ESTABLISHED vault (crash mid-write,
                // accidental delete) must never silently bootstrap an empty
                // database over years of records — restore the last good backup
                // instead. Only a genuinely fresh vault (no backup either)
                // creates the empty template.
                var restored = TryRestoreFromBackup("vault.xml missing");
                if (restored != null) return restored;

                // An EXISTING backup that failed to restore (corrupt .bak,
                // transient IO) means this is an ESTABLISHED vault in trouble
                // — bootstrapping an empty DB here would hand every caller a
                // blank vault, and the next Save would overwrite the .bak,
                // destroying the last (possibly hand-repairable) copy of years
                // of records. Fail the operation loudly instead; only a
                // genuinely fresh vault (no backup either) bootstraps.
                bool backupExists = false;
                try { backupExists = File.Exists(BackupFile); } catch { }
                if (backupExists)
                    throw new InvalidOperationException(
                        "vault.xml is missing and vault.xml.bak could not be " +
                        "restored. Refusing to create an empty vault over an " +
                        "established one — restore " + DataFile +
                        " manually from the backup or an archive copy.");

                var doc = new XDocument(
                    new XElement("BCorePDMVault",
                        new XElement("Files"),
                        new XElement("Users",
                            new XElement("User",
                                new XElement("Username", "bchougule"),
                                new XElement("Role", "Master")
                            ),
                            new XElement("User",
                                new XElement("Username", "rkramarz"),
                                new XElement("Role", "Master")
                            )
                        ),
                        new XElement("RevisionHistory"),
                        new XElement("RevisionRequests")
                    )
                );
                // Persist the fresh template — except in degraded mode, where
                // a READ path must never write (the in-memory doc serves this
                // call; the first real mutation persists it under the lock).
                if (!LockDegraded)
                    Save(doc); // atomic-path write, not a direct overwrite
                return doc;
            }

            // A degraded-mode writer on another machine can briefly hold
            // vault.xml open (the direct-save last resort), and the reader
            // had NO retry — the IOException propagated raw out of whatever
            // UI path triggered the read. Retry genuine sharing violations
            // briefly, mirroring the Save side.
            for (int attempt = 0; ; attempt++)
            {
            try
            {
                return XDocument.Load(DataFile);
            }
            catch (IOException io) when (
                (io.HResult & 0xFFFF) == 32 || (io.HResult & 0xFFFF) == 33)
            {
                if (attempt == 4) throw;
                System.Threading.Thread.Sleep(200);
            }
            catch (System.Xml.XmlException)
            {
                // Truncated/corrupt vault.xml (a crash inside the non-atomic
                // save fallback). Restore the last good backup rather than
                // failing every DB operation until someone hand-repairs the XML.
                var restored = TryRestoreFromBackup("vault.xml corrupt");
                if (restored != null) return restored;
                throw;
            }
            }
        }

        // Restore vault.xml from vault.xml.bak (refreshed by every successful
        // atomic Save). Returns the restored document, or null when no usable
        // backup exists. Audit-logged so a restore is never invisible.
        private static XDocument TryRestoreFromBackup(string reason)
        {
            try
            {
                if (!File.Exists(BackupFile)) return null;
                var doc = XDocument.Load(BackupFile); // proves the backup parses

                // Preserve a corrupt-but-present vault.xml once (it holds
                // exactly ONE save more than the backup — hand-repairable),
                // then DELETE it. Leaving the corrupt file in place poisons
                // the NEXT Save: File.Replace banks the on-disk vault.xml
                // into vault.xml.bak — overwriting the good backup we just
                // parsed (the degraded-mode skip below leaves the corrupt
                // file behind, and user MUTATIONS still save in degraded
                // mode). Deleting unparseable poison is not a "write over
                // other machines' data" — readers then take the missing-file
                // branch and restore from .bak; with the file gone, repeated
                // degraded reads write nothing (no .corrupt litter loop).
                // The delete only runs AFTER the preservation copy succeeded.
                try
                {
                    if (File.Exists(DataFile))
                    {
                        File.Copy(DataFile, DataFile + ".corrupt." +
                            DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                            overwrite: true);
                        File.Delete(DataFile);
                    }
                }
                catch { }

                // Write the restored copy back — best-effort, and SKIPPED in
                // degraded mode: a restore is triggered from READ paths, and a
                // degraded reader must never write over other machines' files
                // (the same rule as the janitorial writes). The parsed doc
                // still serves this call either way, and the next healthy
                // Save rewrites vault.xml via the atomic path anyway.
                if (!LockDegraded)
                {
                    try { File.Copy(BackupFile, DataFile, overwrite: true); }
                    catch { }
                }
                LogDbEvent("VaultRestoredFromBackup", reason);
                return doc;
            }
            catch { return null; }
        }

        // Write atomically: serialise to a temp file, then atomically replace the
        // live vault.xml. A crash or network blip mid-write can therefore never
        // leave a truncated/corrupt vault.xml — readers always see a whole file.
        // Always called while the cross-process lock is held (see AcquireProcessLock).
        private static void Save(XDocument doc)
        {
            // Per-process temp name (machine + PID). Under the lock only one
            // writer is ever active, but in degraded mode (lock unavailable on a
            // network blip) two machines must NOT share one .tmp — interleaved
            // writes would let File.Replace promote a corrupt temp into place.
            string tmp = DataFile + "." + Environment.MachineName + "."
                + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            doc.Save(tmp);

            // Atomic replace, with brief retries: a degraded-mode reader on
            // another machine can hold vault.xml open (share-read) for a moment,
            // which blocks Replace/Move. Most such blockers clear in well under
            // a second. File.Replace's third argument keeps the PREVIOUS
            // vault.xml as vault.xml.bak — the recovery file LoadOrCreate
            // restores from (it is a rename, so it costs nothing extra).
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(DataFile))
                        File.Replace(tmp, DataFile, BackupFile);
                    else
                        File.Move(tmp, DataFile);
                    return;
                }
                catch (IOException)
                {
                    // Transient (open handle) — retry, but don't sleep after
                    // the final attempt; go straight to the fallback.
                    if (attempt < 4) System.Threading.Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    // ACL/read-only — not transient, retrying just stalls the
                    // global lock. Go straight to the fallback.
                    break;
                }
            }

            // File.Replace genuinely unavailable (some SMB configs). The old
            // fallback here was doc.Save(DataFile) — a truncate-then-write of
            // the ONLY copy of the database, where a crash mid-write meant
            // total data loss. Instead: refresh the backup, then delete+move
            // the fully-written temp into place. The unsafe window shrinks to
            // a single rename, and even a crash inside it leaves BOTH the .bak
            // and the .tmp on disk for recovery. Audit-logged (throttled) so a
            // share that never supports atomic replace is visible, not silent.
            LogDbEvent("VaultSaveFallback", "atomic File.Replace failed");
            try
            {
                try
                {
                    if (File.Exists(DataFile))
                        File.Copy(DataFile, BackupFile, overwrite: true);
                }
                catch { }
                if (File.Exists(DataFile)) File.Delete(DataFile);
                File.Move(tmp, DataFile);
            }
            catch
            {
                // Absolute last resort — the original direct overwrite, kept so
                // a save still lands when even delete+move is blocked.
                doc.Save(DataFile);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ── Cross-process (cross-machine) write lock ──────────────────────────
        // The in-process _lock only serialises threads inside ONE SOLIDWORKS
        // instance. vault.xml lives on a shared drive hit by 10+ machines, so
        // without cross-machine mutual exclusion the read-modify-write cycle is
        // last-writer-wins: A loads, B loads, A saves, B saves → A's changes
        // (status, locks, per-config metadata) are silently lost.
        //
        // A named Mutex is machine-local and does NOT help across PCs. An
        // exclusive lock FILE does: SMB honours FileShare.None across machines,
        // so only one machine at a time can hold vault.lock. Every DB critical
        // section acquires it for the full load→save, so writes never interleave.
        // Reentrant per-thread (a DB method may call another) since a single
        // process cannot re-open its own FileShare.None handle.
        private const string LockFilePath = @"N:\PDM-SolidWorks\VAULT\vault.lock";

        [ThreadStatic] private static FileStream _procLockStream;
        [ThreadStatic] private static int _procLockDepth;
        // True while the current thread's critical section runs WITHOUT the
        // cross-machine lock (acquisition failed → degraded last-writer-wins
        // mode). Janitorial writes (stale-session purge, orphan auto-purge,
        // presence bookkeeping) check LockDegraded and skip their Save: in
        // degraded mode a mere READ must never write its stale snapshot back
        // over other machines' committed changes. User-initiated mutations
        // (UpsertFile, SetFileStatus, …) still proceed — degraded, never blocked.
        [ThreadStatic] private static bool _procLockDegraded;

        private static bool LockDegraded
            => _procLockDepth > 0 && _procLockDegraded;

        private static IDisposable AcquireProcessLock()
        {
            // Already held on this thread — just go deeper (reentrant).
            if (_procLockDepth > 0)
            {
                _procLockDepth++;
                return new LockReleaser();
            }

            // The lock is held for a full load→save of vault.xml over SMB, so
            // hold times grow with the vault — the retry budget must comfortably
            // exceed a worst-case hold, or every busy moment silently degrades
            // to last-writer-wins (the original 3 s budget did exactly that at
            // scale). 100 × 300 ms = 30 s: a save blocked for a few seconds is
            // far cheaper than a silently lost write.
            // Spin only on genuine sharing violations (another machine holds the
            // lock). Anything else (path unreachable / access denied) means the
            // network is down or misconfigured — don't waste seconds retrying;
            // fall through and let the subsequent vault.xml load surface the error.
            string failReason = "contention timeout (30s)";
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    _procLockStream = new FileStream(LockFilePath,
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _procLockDepth = 1;
                    _procLockDegraded = false;
                    return new LockReleaser();
                }
                catch (IOException ex)
                {
                    int hr = ex.HResult & 0xFFFF;
                    bool sharing = hr == 32 /* ERROR_SHARING_VIOLATION */
                                || hr == 33 /* ERROR_LOCK_VIOLATION   */;
                    if (!sharing)
                    {
                        failReason = "I/O error on vault.lock: " + ex.Message;
                        break;
                    }
                    System.Threading.Thread.Sleep(300);
                }
                catch (UnauthorizedAccessException)
                {
                    failReason = "access denied on vault.lock";
                    break;
                }
            }

            // Couldn't acquire (persistent contention or network issue). Proceed
            // WITHOUT the lock rather than blocking the user forever — degraded
            // last-writer-wins, never worse than the pre-lock behaviour. But the
            // degradation is now OBSERVABLE (audit-logged, throttled) and
            // janitorial writes are suppressed while it is active (LockDegraded).
            // depth=1 so the matching using-dispose stays balanced.
            _procLockStream = null;
            _procLockDepth = 1;
            _procLockDegraded = true;
            LogDbEvent("LockDegraded", failReason);
            return new LockReleaser();
        }

        private sealed class LockReleaser : IDisposable
        {
            private bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (_procLockDepth > 0) _procLockDepth--;
                if (_procLockDepth == 0)
                {
                    _procLockDegraded = false;
                    if (_procLockStream != null)
                    {
                        try { _procLockStream.Dispose(); } catch { }
                        _procLockStream = null;
                    }
                }
            }
        }

        // Throttled audit logging for DB-layer health events (degraded lock,
        // non-atomic save fallback, backup restore). At most one entry per
        // event type per 5 minutes per process, so a persistent condition is
        // visible in the Audit Report without flooding audit.csv. Never throws.
        private static readonly Dictionary<string, DateTime> _lastDbEventLog =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static void LogDbEvent(string action, string note)
        {
            try
            {
                lock (_lastDbEventLog)
                {
                    DateTime last;
                    if (_lastDbEventLog.TryGetValue(action, out last) &&
                        (DateTime.Now - last).TotalMinutes < 5)
                        return;
                    _lastDbEventLog[action] = DateTime.Now;
                }
                AuditLogger.Log(action, "system", "vault.xml", "", "",
                    note + " (machine " + Environment.MachineName + ")");
            }
            catch { }
        }

        public static void Initialize()
        {
            lock (_lock) using (AcquireProcessLock()) { LoadOrCreate(); }

            // Sweep stale per-process save temps (vault.xml.{machine}.{pid}
            // .tmp): normally consumed by File.Replace/Move, but a crash or
            // serialization failure strands them and nothing else ever
            // deletes them — across 10+ machines they accumulate forever.
            // Only temps older than a day are touched (a LIVE temp exists
            // for milliseconds mid-save).
            try
            {
                foreach (string tmp in Directory.GetFiles(
                    VaultFolder, "vault.xml.*.tmp"))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp)
                                > TimeSpan.FromDays(1))
                            File.Delete(tmp);
                    }
                    catch { }
                }
            }
            catch { }

            // Ensure WIP division subfolders exist so engineers can
            // navigate to them from the first day without manual setup.
            try
            {
                foreach (string div in WipDivisions)
                    Directory.CreateDirectory(Path.Combine(WipRoot, div));

                // SCRAP holds files retired via Remove from Vault — kept
                // separate from ARCHIVE (old revisions) and recoverable until
                // a Master bulk-purges it.
                Directory.CreateDirectory(ScrapRoot);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════
        // Config / drawing-reference XML helpers
        // ════════════════════════════════════════════════════════════════

        // Build the <Configurations> XElement from a list of ConfigEntry objects.
        private static XElement BuildConfigsElement(List<ConfigEntry> configs)
        {
            var el = new XElement("Configurations");
            foreach (var c in configs)
                el.Add(new XElement("Config",
                    new XElement("Name",        c.Name        ?? ""),
                    new XElement("PartNo",       c.PartNo      ?? ""),
                    new XElement("Description",  c.Description ?? ""),
                    new XElement("DrawingNo",    c.DrawingNo   ?? ""),
                    new XElement("Revision",     c.Revision    ?? "")
                ));
            return el;
        }

        // Read <Configurations> from a File element. If the element doesn't exist
        // (old record saved before multi-config shipped) synthesise a single entry
        // from the file-level PartNumber / Description / Revision fields so callers
        // never see an empty list for a tracked single-config file.
        private static List<ConfigEntry> ReadConfigs(XElement fileEl,
            string fallbackPartNo, string fallbackDesc, string fallbackRev)
        {
            var configsEl = fileEl.Element("Configurations");
            if (configsEl != null)
            {
                var list = new List<ConfigEntry>();
                foreach (var c in configsEl.Elements("Config"))
                    list.Add(new ConfigEntry
                    {
                        Name        = (string)c.Element("Name")        ?? "",
                        PartNo      = (string)c.Element("PartNo")      ?? "",
                        Description = (string)c.Element("Description") ?? "",
                        DrawingNo   = (string)c.Element("DrawingNo")   ?? "",
                        Revision    = (string)c.Element("Revision")    ?? ""
                    });
                return list;
            }
            // Old record: one implicit config whose name = PartNo.
            if (!string.IsNullOrEmpty(fallbackPartNo))
                return new List<ConfigEntry> { new ConfigEntry {
                    Name        = fallbackPartNo,
                    PartNo      = fallbackPartNo,
                    Description = fallbackDesc ?? "",
                    Revision    = fallbackRev  ?? ""
                }};
            return new List<ConfigEntry>();
        }

        // Set or add a child element on an existing XML node — safe to call on
        // old records that don't yet have the element (migration path).
        private static void SetOrAdd(XElement parent, string name, string value)
        {
            var el = parent.Element(name);
            if (el != null) el.Value = value ?? "";
            else parent.Add(new XElement(name, value ?? ""));
        }

        // ════════════════════════════════════════════════════════════════
        // File operations
        // ════════════════════════════════════════════════════════════════
        public static void UpsertFile(VaultFile f)
        {
            bool wasCreate;
            string rivalPath = null;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var files = doc.Root.Element("Files");

                // Case-insensitive path match — Windows paths are. A casing
                // difference (n:\ vs N:\, which SOLIDWORKS can produce) used
                // to miss the existing record, create a DUPLICATE, and the
                // wasCreate purge below then wiped the file's whole history.
                string targetPath = (f.FilePath ?? "").Trim();
                XElement existing = null;
                foreach (var el in files.Elements("File"))
                {
                    string elPath = ((string)el.Element("FilePath") ?? "").Trim();
                    if (elPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = el;
                        break;
                    }
                }
                wasCreate = existing == null;

                // A brand-new file must never inherit history left behind by a
                // previously-removed file of the same name/path (e.g. removed
                // before the remove-purge fix shipped). Wipe any stale entries
                // so the new file starts with a clean timeline. The Save at the
                // end of this method persists the purge.
                // LAST-LINE GUARD: Rule 2.6's pre-save check and this post-save
                // insert run in SEPARATE lock acquisitions (the lock is never
                // held across SOLIDWORKS work), so two machines first-saving
                // the same name within seconds can both get here — and
                // PurgeHistoryFor matches by FILENAME, so purging now would
                // wipe the LIVING rival's whole timeline, not stale leftovers.
                // When a same-named rival record exists, do NOT create a
                // record at all (matching the OnSavePost quarantine: a second
                // same-named record corrupts every name-keyed lookup) and do
                // NOT purge; audit the collision instead. The file stays
                // untracked on disk and Rule 2.6 blocks its next save.
                if (wasCreate)
                {
                    rivalPath = FindNameRival(doc, f.FileName, targetPath);
                    if (rivalPath == null)
                        PurgeHistoryFor(doc, f.FilePath, f.FileName);
                }

                if (rivalPath != null)
                {
                    // No record, no save — fall through to the audit row only.
                }
                else if (existing != null)
                {
                    existing.Element("FileName").Value    = f.FileName;
                    existing.Element("PartNumber").Value  = f.PartNumber  ?? "";
                    existing.Element("Description").Value = f.Description ?? "";
                    existing.Element("ModifiedBy").Value  = f.ModifiedBy  ?? "";
                    existing.Element("ModifiedDate").Value = f.ModifiedDate.ToString("o");

                    if (!string.IsNullOrEmpty(f.Status))
                        existing.Element("Status").Value = f.Status;

                    // Revision (may not exist in old records — add if missing).
                    SetOrAdd(existing, "Revision", f.Revision ?? "");

                    // Drawing reference fields (drawings only; safe no-op on models).
                    SetOrAdd(existing, "ReferencedModel",   f.ReferencedModel   ?? "");
                    SetOrAdd(existing, "ReferencedConfigs", f.ReferencedConfigs ?? "");

                    // Per-config metadata: replace the whole block on each save so
                    // adding/removing configs is always reflected correctly.
                    // BUT only when we actually have configs to write. Every real
                    // part/assembly has at least one configuration, so an empty
                    // list means enumeration failed transiently (GetConfigNames
                    // swallows exceptions) — in that case PRESERVE the existing
                    // block rather than wiping every config's PartNo/Revision and
                    // silently collapsing the file to a single phantom config.
                    if (f.Configurations != null && f.Configurations.Count > 0)
                    {
                        existing.Element("Configurations")?.Remove();
                        existing.Add(BuildConfigsElement(f.Configurations));
                    }
                }
                else
                {
                    var fileEl = new XElement("File",
                        new XElement("FilePath",        f.FilePath),
                        new XElement("FileName",        f.FileName),
                        new XElement("PartNumber",      f.PartNumber  ?? ""),
                        new XElement("Description",     f.Description ?? ""),
                        new XElement("Revision",        f.Revision    ?? ""),
                        new XElement("Status",          f.Status      ?? "WIP"),
                        new XElement("LockedBy",        ""),
                        new XElement("LockedDate",      ""),
                        new XElement("ModifiedBy",      f.ModifiedBy  ?? ""),
                        new XElement("ModifiedDate",    f.ModifiedDate.ToString("o")),
                        new XElement("HasBrokenRefs",   "false"),
                        new XElement("ReferencedModel",   f.ReferencedModel   ?? ""),
                        new XElement("ReferencedConfigs", f.ReferencedConfigs ?? "")
                    );
                    if (f.Configurations != null && f.Configurations.Count > 0)
                        fileEl.Add(BuildConfigsElement(f.Configurations));
                    files.Add(fileEl);
                }

                if (rivalPath == null)
                    Save(doc);
            }

            // Log outside the DB lock — AuditLogger holds its own file lock.
            // NOTE: never advise "Remove from Vault" for a duplicate — record
            // removal matches by FILENAME and would take the ORIGINAL's record
            // and history with it; the duplicate FILE is deleted in Explorer.
            if (rivalPath != null)
            {
                AuditLogger.Log("DuplicateNameDetected", f.ModifiedBy ?? "",
                    f.FileName, f.PartNumber ?? "", "",
                    "rival at " + rivalPath + " — record NOT created; delete " +
                    "the duplicate file in Explorer or Save As a unique name");
                return;
            }
            AuditLogger.Log(wasCreate ? "Create" : "Save",
                f.ModifiedBy ?? "", f.FileName, f.PartNumber ?? "");
        }

        public static string GetFileStatus(string filePath)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Status") ?? "";
                }
                return "";
            }
        }

        public static void SetFileStatus(string filePath, string status,
                                         string changedBy, string note = "")
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        el.Element("Status").Value = status;
                        break;
                    }
                }

                doc.Root.Element("RevisionHistory").Add(
                    new XElement("Entry",
                        new XElement("FilePath", filePath),
                        new XElement("Status", status),
                        new XElement("ChangedBy", changedBy),
                        new XElement("ChangedDate", DateTime.Now.ToString("o")),
                        new XElement("ChangeNote", note)
                    )
                );

                Save(doc);
            }
        }

        public static void SetBrokenRefFlag(string filePath, bool hasBroken)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        el.Element("HasBrokenRefs").Value =
                            hasBroken ? "true" : "false";
                        break;
                    }
                }
                Save(doc);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Remove from vault (DB record only — never touches files on disk)
        // ════════════════════════════════════════════════════════════════

        // Remove the vault.xml record(s) for a single file. Matches by FilePath
        // first, falling back to FileName so legacy duplicate records and the
        // RELEASED-copy entry are cleaned up too. The file(s) on disk are left
        // untouched. Returns the number of records removed.
        public static int RemoveFileRecord(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return 0;
            string fileName = Path.GetFileName(filePath);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var toRemove = new List<XElement>();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    bool pathMatch = string.Equals(
                        (string)el.Element("FilePath"), filePath,
                        StringComparison.OrdinalIgnoreCase);
                    bool nameMatch = string.Equals(
                        (string)el.Element("FileName"), fileName,
                        StringComparison.OrdinalIgnoreCase);
                    if (pathMatch || nameMatch) toRemove.Add(el);
                }

                foreach (var el in toRemove) el.Remove();

                // Also purge history entries for this file so a new file
                // created with the same name never inherits old history.
                bool historyPurged = PurgeHistoryFor(doc, filePath, fileName);

                if (toRemove.Count > 0 || historyPurged) Save(doc);
                return toRemove.Count;
            }
        }

        // Removes every RevisionHistory entry that belongs to the given file,
        // matched by exact FilePath or by filename (so RELEASED-copy entries and
        // legacy duplicates are caught too). Caller holds _lock and Saves.
        // Returns true if any entry was removed.
        private static bool PurgeHistoryFor(XDocument doc, string filePath,
            string fileName)
        {
            var history = doc.Root.Element("RevisionHistory");
            if (history == null) return false;

            var toRemove = new List<XElement>();
            foreach (var el in history.Elements("Entry"))
            {
                string entryPath = (string)el.Element("FilePath") ?? "";
                bool pathMatch = !string.IsNullOrEmpty(filePath) &&
                    string.Equals(entryPath, filePath,
                        StringComparison.OrdinalIgnoreCase);
                bool nameMatch = !string.IsNullOrEmpty(fileName) &&
                    string.Equals(Path.GetFileName(entryPath), fileName,
                        StringComparison.OrdinalIgnoreCase);
                if (pathMatch || nameMatch) toRemove.Add(el);
            }
            foreach (var el in toRemove) el.Remove();
            return toRemove.Count > 0;
        }

        // ════════════════════════════════════════════════════════════════
        // Lock / Unlock
        // ════════════════════════════════════════════════════════════════
        public static void LockFile(string filePath, string lockedBy)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        el.Element("LockedBy").Value = lockedBy;
                        el.Element("LockedDate").Value = DateTime.Now.ToString("o");
                        break;
                    }
                }
                Save(doc);
            }
        }

        public static void UnlockFile(string filePath)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        el.Element("LockedBy").Value = "";
                        el.Element("LockedDate").Value = "";
                        break;
                    }
                }
                Save(doc);
            }
        }

        public static LockInfo GetLockInfo(string filePath)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string lockedBy = (string)el.Element("LockedBy") ?? "";
                        if (!string.IsNullOrEmpty(lockedBy))
                        {
                            return new LockInfo
                            {
                                IsLocked = true,
                                LockedBy = lockedBy,
                                LockedDate = DateTime.Parse(
                                    (string)el.Element("LockedDate"))
                            };
                        }
                    }
                }
                return new LockInfo { IsLocked = false };
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Open sessions (multi-user conflict detection)
        // ════════════════════════════════════════════════════════════════
        // Soft presence — distinct from the hard Master Lock. Records that an
        // engineer has a WIP file open so a second engineer who opens the same
        // file is warned that their saves may overwrite each other. Backed by an
        // <OpenSessions> section in vault.xml.

        // A session older than this is treated as stale (e.g. SOLIDWORKS crashed
        // without firing DestroyNotify) and ignored/purged on read.
        private const int StaleSessionHours = 24;

        private static XElement EnsureOpenSessions(XDocument doc)
        {
            var el = doc.Root.Element("OpenSessions");
            if (el == null)
            {
                el = new XElement("OpenSessions");
                doc.Root.Add(el);
            }
            return el;
        }

        // Record (or refresh) that user@machine has filePath open.
        public static void RegisterOpenSession(string filePath, string user,
            string machine)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                // Presence is purely advisory — never worth a degraded-mode
                // write-back of a stale whole-vault snapshot.
                if (LockDegraded) return;

                var doc = LoadOrCreate();
                var sessions = EnsureOpenSessions(doc);

                XElement mine = null;
                foreach (var el in sessions.Elements("Session"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)el.Element("User"), user,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)el.Element("Machine"), machine,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        mine = el;
                        break;
                    }
                }

                if (mine != null)
                {
                    mine.Element("OpenedDate").Value = DateTime.Now.ToString("o");
                }
                else
                {
                    sessions.Add(new XElement("Session",
                        new XElement("FilePath", filePath),
                        new XElement("User", user),
                        new XElement("Machine", machine),
                        new XElement("OpenedDate", DateTime.Now.ToString("o"))
                    ));
                }
                Save(doc);
            }
        }

        // Return the sessions held by OTHER users on filePath (excludes the
        // current user, who may legitimately have it open in several windows).
        // Stale sessions are skipped and purged in the same pass.
        public static List<OpenSession> GetOtherOpenSessions(string filePath,
            string currentUser)
        {
            var others = new List<OpenSession>();
            if (string.IsNullOrWhiteSpace(filePath)) return others;

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var sessions = doc.Root.Element("OpenSessions");
                if (sessions == null) return others;

                var stale = new List<XElement>();
                foreach (var el in sessions.Elements("Session"))
                {
                    if (!string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTime opened;
                    if (!DateTime.TryParse((string)el.Element("OpenedDate"),
                            out opened) ||
                        (DateTime.Now - opened).TotalHours > StaleSessionHours)
                    {
                        stale.Add(el);
                        continue;
                    }

                    string user = (string)el.Element("User") ?? "";
                    if (string.Equals(user, currentUser,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    others.Add(new OpenSession
                    {
                        FilePath = filePath,
                        User = user,
                        Machine = (string)el.Element("Machine") ?? "",
                        OpenedDate = opened
                    });
                }

                // Janitorial: skip the stale-session purge in degraded mode —
                // a READ must never write a stale snapshot back (the entries
                // are already skipped above; the 24h backstop catches them on
                // a later, lock-held pass).
                if (stale.Count > 0 && !LockDegraded)
                {
                    foreach (var el in stale) el.Remove();
                    Save(doc);
                }
            }
            return others;
        }

        // Clear this user@machine's session for filePath (called on file close).
        public static void ClearOpenSession(string filePath, string user,
            string machine)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                if (LockDegraded) return; // advisory presence — see RegisterOpenSession

                var doc = LoadOrCreate();
                var sessions = doc.Root.Element("OpenSessions");
                if (sessions == null) return;

                var toRemove = new List<XElement>();
                foreach (var el in sessions.Elements("Session"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)el.Element("User"), user,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)el.Element("Machine"), machine,
                            StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(el);
                }

                if (toRemove.Count > 0)
                {
                    foreach (var el in toRemove) el.Remove();
                    Save(doc);
                }
            }
        }

        // Remove every session for a machine — called on add-in load and unload
        // so entries a crashed SOLIDWORKS left behind on this PC never linger
        // and falsely warn other engineers.
        public static void ClearMachineSessions(string machine)
        {
            if (string.IsNullOrWhiteSpace(machine)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                if (LockDegraded) return; // advisory presence — see RegisterOpenSession

                var doc = LoadOrCreate();
                var sessions = doc.Root.Element("OpenSessions");
                if (sessions == null) return;

                var toRemove = new List<XElement>();
                foreach (var el in sessions.Elements("Session"))
                {
                    if (string.Equals((string)el.Element("Machine"), machine,
                            StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(el);
                }

                if (toRemove.Count > 0)
                {
                    foreach (var el in toRemove) el.Remove();
                    Save(doc);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Users
        // ════════════════════════════════════════════════════════════════
        public static string GetUserRole(string username)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Users").Elements("User"))
                {
                    if (string.Equals((string)el.Element("Username"),
                        username, StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Role") ?? "Engineer";
                }
                return "Engineer";
            }
        }

        public static void AddUser(string username, string role)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var users = doc.Root.Element("Users");

                foreach (var el in users.Elements("User"))
                {
                    if (string.Equals((string)el.Element("Username"),
                        username, StringComparison.OrdinalIgnoreCase))
                    {
                        el.Element("Role").Value = role;
                        Save(doc);
                        return;
                    }
                }

                users.Add(new XElement("User",
                    new XElement("Username", username),
                    new XElement("Role", role)
                ));
                Save(doc);
            }
        }
        // ════════════════════════════════════════════════════════════════════
        // Search Files
        // ════════════════════════════════════════════════════════════════
        // Returns all tracked vault files (WIP, Locked, Released) matching the
        // search term. Status is shown on each card so the engineer can see the
        // state at a glance. WIP files must be findable after a revision bump.
        public static List<VaultFile> SearchFiles(string searchTerm)
        {
            bool truncated;
            return SearchFiles(searchTerm, out truncated);
        }

        // Search PartNumber + Description + FileName across all statuses.
        // Two behaviours matter at full scale (~50k files):
        //   1. Results are capped at MaxSearchResults; 'truncated' is set true
        //      when more matches existed, so the UI can prompt to refine.
        //   2. Records whose file no longer exists on disk are auto-purged —
        //      a manually deleted file disappears from search AND its dead
        //      record is removed, so nothing junk accumulates. Guarded: purge
        //      ONLY when the vault share is reachable (WIP root exists). If the
        //      network is down File.Exists returns false for everything, so we
        //      must never delete records in that state.
        public static List<VaultFile> SearchFiles(string searchTerm,
            out bool truncated)
        {
            truncated = false;
            var results = new List<VaultFile>();
            if (string.IsNullOrWhiteSpace(searchTerm)) return results;

            string term = searchTerm.ToLower().Trim();
            var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var purged = new List<VaultFile>();

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // Network-down guard for the auto-purge (see method summary).
                // Also never purge in degraded-lock mode: a search is a READ,
                // and writing a stale whole-vault snapshot back without the
                // cross-machine lock can erase other machines' committed
                // changes just to delete an orphan record.
                bool canPurge = false;
                try { canPurge = Directory.Exists(WipRoot) && !LockDegraded; }
                catch { }

                var toPurge = new List<XElement>();

                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (string.IsNullOrEmpty(status)) continue;

                    string fileName = (string)el.Element("FileName") ?? "";
                    string partNoRaw = (string)el.Element("PartNumber") ?? "";
                    string descRaw = (string)el.Element("Description") ?? "";

                    // Match the term against the file-level PN/desc/name, AND
                    // against every configuration's PN and description so a
                    // multi-config part is findable by any of its part numbers.
                    bool matchFound = partNoRaw.ToLower().Contains(term)
                                   || descRaw.ToLower().Contains(term)
                                   || fileName.ToLower().Contains(term);

                    if (!matchFound)
                    {
                        var cfgEl = el.Element("Configurations");
                        if (cfgEl != null)
                        {
                            foreach (var c in cfgEl.Elements("Config"))
                            {
                                if (((string)c.Element("PartNo")      ?? "").ToLower().Contains(term) ||
                                    ((string)c.Element("Description") ?? "").ToLower().Contains(term))
                                {
                                    matchFound = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!matchFound) continue;

                    // Orphan check: file gone on disk → never show it, and purge
                    // its record when we can prove the share is online.
                    string filePath = (string)el.Element("FilePath") ?? "";
                    // File.Exists returns false on ACCESS errors too (a broken
                    // ACL on one division folder, AV interference) — purging on
                    // that would delete records (and later their histories, via
                    // the re-create purge) for files that still exist. Only
                    // treat the file as gone when its PARENT FOLDER is provably
                    // reachable.
                    bool missing = string.IsNullOrEmpty(filePath);
                    if (!missing && !File.Exists(filePath))
                    {
                        try
                        {
                            missing = Directory.Exists(
                                Path.GetDirectoryName(filePath));
                        }
                        catch { }
                    }
                    if (missing)
                    {
                        if (canPurge)
                        {
                            toPurge.Add(el);
                            purged.Add(new VaultFile
                            {
                                FileName = fileName,
                                PartNumber = partNoRaw,
                                Status = status,
                                Revision = (string)el.Element("Revision") ?? ""
                            });
                        }
                        continue;
                    }

                    // Dedupe by filename (legacy double-records / RELEASED copies).
                    if (!seenFileNames.Add(fileName)) continue;

                    // Cap reached — note there were more and stop scanning so a
                    // broad term can't walk the whole 50k vault on every search.
                    if (results.Count >= MaxSearchResults)
                    {
                        truncated = true;
                        break;
                    }

                    results.Add(new VaultFile
                    {
                        FilePath    = filePath,
                        FileName    = fileName,
                        PartNumber  = partNoRaw,
                        Description = descRaw,
                        Status      = status,
                        Revision    = (string)el.Element("Revision") ?? "",
                        ReferencedModel   = (string)el.Element("ReferencedModel")   ?? "",
                        ReferencedConfigs = (string)el.Element("ReferencedConfigs") ?? "",
                        Configurations    = ReadConfigs(el, partNoRaw, descRaw,
                                               (string)el.Element("Revision") ?? "")
                    });
                }

                if (toPurge.Count > 0)
                {
                    foreach (var el in toPurge) el.Remove();
                    Save(doc);
                }
            }

            // Audit the auto-purges outside the DB lock.
            foreach (var p in purged)
                AuditLogger.Log("AutoPurgeOrphan", "system", p.FileName,
                    p.PartNumber, p.Revision, "file missing on disk");

            return results;
        }

        // Returns EVERY tracked file (all statuses) for the Vault Dashboard,
        // deduped by filename (drops legacy double-records and RELEASED-folder
        // copies). Unlike SearchFiles this is a READ-ONLY snapshot: it does NOT
        // auto-purge orphans and does NOT hit the disk per file, so opening the
        // dashboard never mutates the DB and stays fast even at full scale.
        // Populates ModifiedBy/ModifiedDate/LockedBy/HasBrokenRefs, the most
        // recent Released date (from RevisionHistory), and — for drawings, whose
        // properties live on the model — the model's PartNumber/Description.
        public static List<VaultFile> GetAllFiles()
        {
            var results = new List<VaultFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // Latest "Released" timestamp + user per file path, from history.
                var relDateByPath = new Dictionary<string, DateTime>(
                    StringComparer.OrdinalIgnoreCase);
                var relUserByPath = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                var hist = doc.Root.Element("RevisionHistory");
                if (hist != null)
                {
                    foreach (var en in hist.Elements("Entry"))
                    {
                        if (!string.Equals((string)en.Element("Status"),
                                "Released", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string fp = (string)en.Element("FilePath") ?? "";
                        if (string.IsNullOrEmpty(fp)) continue;
                        DateTime d;
                        if (!DateTime.TryParse((string)en.Element("ChangedDate") ?? "",
                                null, System.Globalization.DateTimeStyles.RoundtripKind,
                                out d))
                            continue;
                        DateTime cur;
                        if (!relDateByPath.TryGetValue(fp, out cur) || d > cur)
                        {
                            relDateByPath[fp] = d;
                            relUserByPath[fp] = (string)en.Element("ChangedBy") ?? "";
                        }
                    }
                }

                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (string.IsNullOrEmpty(status)) continue;

                    string fileName = (string)el.Element("FileName") ?? "";
                    if (string.IsNullOrEmpty(fileName)) continue;
                    if (!seen.Add(fileName)) continue;

                    DateTime modDate = DateTime.MinValue;
                    DateTime.TryParse((string)el.Element("ModifiedDate") ?? "",
                        null, System.Globalization.DateTimeStyles.RoundtripKind,
                        out modDate);

                    string filePath = (string)el.Element("FilePath") ?? "";
                    DateTime relDate;
                    if (!relDateByPath.TryGetValue(filePath, out relDate))
                        relDate = DateTime.MinValue;
                    string relBy;
                    if (!relUserByPath.TryGetValue(filePath, out relBy))
                        relBy = "";

                    results.Add(new VaultFile
                    {
                        FilePath    = filePath,
                        FileName    = fileName,
                        PartNumber  = (string)el.Element("PartNumber")  ?? "",
                        Description = (string)el.Element("Description") ?? "",
                        Revision    = (string)el.Element("Revision")    ?? "",
                        Status      = status,
                        ModifiedBy  = (string)el.Element("ModifiedBy")  ?? "",
                        ModifiedDate = modDate,
                        ReleasedDate = relDate,
                        ReleasedBy  = relBy,
                        LockedBy    = (string)el.Element("LockedBy")    ?? "",
                        ReferencedModel = (string)el.Element("ReferencedModel") ?? "",
                        ReferencedConfigs = (string)el.Element("ReferencedConfigs") ?? "",
                        HasBrokenRefs = string.Equals(
                            (string)el.Element("HasBrokenRefs"), "true",
                            StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            // Drawings carry no PartNumber/Description of their own (those props
            // live on the model). Fill them from the model in-memory: prefer the
            // explicit ReferencedModel link, else match by base filename
            // (Widget.slddrw → Widget.sldprt/.sldasm). Mirrors GetModelForDrawing
            // but without an extra DB round-trip.
            var modelByPath = new Dictionary<string, VaultFile>(
                StringComparer.OrdinalIgnoreCase);
            var modelByBase = new Dictionary<string, VaultFile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var r in results)
            {
                string ext = Path.GetExtension(r.FileName ?? "").ToLowerInvariant();
                if (ext != ".sldprt" && ext != ".sldasm") continue;
                if (!string.IsNullOrEmpty(r.FilePath))
                    modelByPath[r.FilePath] = r;
                string baseName = Path.GetFileNameWithoutExtension(r.FileName);
                if (!string.IsNullOrEmpty(baseName))
                    modelByBase[baseName] = r;
            }
            foreach (var r in results)
            {
                if (!(r.FileName ?? "").EndsWith(".slddrw",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(r.PartNumber)) continue;

                VaultFile model = null;
                if (!string.IsNullOrEmpty(r.ReferencedModel))
                    modelByPath.TryGetValue(r.ReferencedModel, out model);
                if (model == null)
                    modelByBase.TryGetValue(
                        Path.GetFileNameWithoutExtension(r.FileName), out model);
                if (model != null)
                {
                    r.PartNumber  = model.PartNumber;
                    r.Description = model.Description;
                }
            }

            return results;
        }

        // Returns WIP (releasable) files for the Bulk Release picker, optionally
        // filtered by PartNumber/Description/FileName. Released/Locked files are
        // excluded (a Released file can't be re-released). Deduped by filename,
        // capped at MaxSearchResults (truncated=true when more matched), and the
        // file must exist on disk (orphan records are skipped, not shown). Unlike
        // SearchFiles this does NOT purge orphans — the picker only reads.
        public static List<VaultFile> GetReleasableFiles(string filter,
            out bool truncated)
        {
            truncated = false;
            var results = new List<VaultFile>();
            string term = (filter ?? "").ToLower().Trim();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    // Releasable = WIP (or legacy blank). Skip Released/Locked.
                    if (!(status == "WIP" || status == ""))
                        continue;

                    string fileName  = (string)el.Element("FileName") ?? "";
                    string partNoRaw = (string)el.Element("PartNumber") ?? "";
                    string descRaw   = (string)el.Element("Description") ?? "";

                    if (term.Length > 0 &&
                        !(partNoRaw.ToLower().Contains(term) ||
                          descRaw.ToLower().Contains(term) ||
                          fileName.ToLower().Contains(term)))
                        continue;

                    string filePath = (string)el.Element("FilePath") ?? "";
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                        continue;

                    if (!seen.Add(fileName)) continue;

                    if (results.Count >= MaxSearchResults)
                    {
                        truncated = true;
                        break;
                    }

                    DateTime modDate;
                    DateTime.TryParse((string)el.Element("ModifiedDate"), out modDate);

                    results.Add(new VaultFile
                    {
                        FilePath = filePath,
                        FileName = fileName,
                        PartNumber = partNoRaw,
                        Description = descRaw,
                        Status = string.IsNullOrEmpty(status) ? "WIP" : status,
                        Revision = (string)el.Element("Revision") ?? "",
                        ModifiedBy = (string)el.Element("ModifiedBy") ?? "",
                        ModifiedDate = modDate
                    });
                }
            }
            return results;
        }

        // Find another file already using the given part number.
        // Returns the conflicting file's name, or null if no conflict.
        // Comparison is case-insensitive and trimmed; the file at
        // excludeFilePath (the one being saved) is ignored so a file never
        // conflicts with itself across re-saves, revisions, or configs.
        public static string FindPartNumberConflict(string partNo, string excludeFilePath)
        {
            if (string.IsNullOrWhiteSpace(partNo)) return null;

            string target = partNo.Trim();
            string exclude = (excludeFilePath ?? "").Trim();
            // Also exclude by filename so the RELEASED-folder copy of the same
            // file (different path, same filename) never triggers a false conflict.
            string excludeFileName = System.IO.Path.GetFileName(exclude);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string elPath = ((string)el.Element("FilePath") ?? "").Trim();
                    if (elPath.Equals(exclude, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string elFileName = ((string)el.Element("FileName") ?? "").Trim();
                    if (!string.IsNullOrEmpty(excludeFileName) &&
                        elFileName.Equals(excludeFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Check the file-level PartNumber first (single-config and primary).
                    string elPart = ((string)el.Element("PartNumber") ?? "").Trim();
                    if (elPart.Equals(target, StringComparison.OrdinalIgnoreCase))
                        return elFileName.Length > 0 ? elFileName : elPath;

                    // Also check every configuration's PartNo — a multi-config file
                    // occupies all its configs' part numbers simultaneously.
                    var cfgEl = el.Element("Configurations");
                    if (cfgEl != null)
                    {
                        foreach (var c in cfgEl.Elements("Config"))
                        {
                            string cfgPn = ((string)c.Element("PartNo") ?? "").Trim();
                            if (cfgPn.Equals(target, StringComparison.OrdinalIgnoreCase))
                                return elFileName.Length > 0 ? elFileName : elPath;
                        }
                    }
                }
            }

            return null;
        }

        // Returns the FilePath of ANOTHER tracked vault file already using this
        // file name (case-insensitive), or null. The vault keys on the file
        // name everywhere: RELEASED/ARCHIVE/SCRAP are flat folders, search and
        // the dashboard dedupe by name, drawing↔model linkage is by basename,
        // and RemoveFileRecord/PurgeHistoryFor match by name — so a second
        // "Bracket.sldprt" in a different division would overwrite the first
        // one's released snapshot and archives and hijack/delete its history.
        // ValidateSave hard-blocks on a non-null return.
        // Only canonical records under WIP count as rivals: a same-name record
        // OUTSIDE WIP is a legacy RELEASED-folder copy of the same file, and a
        // WIP record whose file is gone from disk is an orphan awaiting purge —
        // neither should block a save.
        public static string FindFileNameConflict(string fileName,
            string excludeFilePath)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                return FindNameRival(LoadOrCreate(), fileName, excludeFilePath);
            }
        }

        // Core rival scan against an already-loaded document — callers hold
        // the lock. Shared by FindFileNameConflict (the Rule 2.6 save-time
        // check) and UpsertFile (the create-time last-line guard), so the two
        // can never disagree about what counts as a rival.
        private static string FindNameRival(XDocument doc, string fileName,
            string excludeFilePath)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            string target = fileName.Trim();
            string exclude = (excludeFilePath ?? "").Trim();

            foreach (var el in doc.Root.Element("Files").Elements("File"))
            {
                string elPath = ((string)el.Element("FilePath") ?? "").Trim();
                if (elPath.Equals(exclude, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsUnderWip(elPath))
                    continue;

                string elFileName = ((string)el.Element("FileName") ?? "").Trim();
                if (!elFileName.Equals(target, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool onDisk = true;
                try { onDisk = File.Exists(elPath); } catch { }
                if (onDisk) return elPath;
            }

            return null;
        }

        // ════════════════════════════════════════════════════════════════
        private static void AddRequest(string requestType, string filePath,
            string requestedBy, string note)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                if (doc.Root.Element("RevisionRequests") == null)
                    doc.Root.Add(new XElement("RevisionRequests"));

                doc.Root.Element("RevisionRequests").Add(
                    new XElement("Request",
                        new XElement("Id", DateTime.Now.Ticks.ToString()),
                        new XElement("RequestType", requestType),
                        new XElement("FilePath", filePath),
                        new XElement("FileName", System.IO.Path.GetFileName(filePath)),
                        new XElement("RequestedBy", requestedBy),
                        new XElement("RequestDate", DateTime.Now.ToString("o")),
                        new XElement("Status", "Pending"),
                        new XElement("Note", note)
                    )
                );
                Save(doc);
            }
        }

        public static void AddRevisionRequest(string filePath,
            string requestedBy, string note = "") =>
            AddRequest("Revision", filePath, requestedBy, note);

        public static void AddUnlockRequest(string filePath,
            string requestedBy, string note = "") =>
            AddRequest("Unlock", filePath, requestedBy, note);

        public static void AddReleaseRequest(string filePath,
            string requestedBy, string note = "") =>
            AddRequest("Release", filePath, requestedBy, note);

        private static List<RevisionRequest> GetRequestsWhere(
            System.Func<XElement, bool> predicate)
        {
            var requests = new List<RevisionRequest>();
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var reqElement = doc.Root.Element("RevisionRequests");
                if (reqElement == null) return requests;

                foreach (var el in reqElement.Elements("Request"))
                {
                    if (!predicate(el)) continue;
                    requests.Add(new RevisionRequest
                    {
                        Id = (string)el.Element("Id") ?? "",
                        RequestType = (string)el.Element("RequestType") ?? "Revision",
                        FilePath = (string)el.Element("FilePath") ?? "",
                        FileName = (string)el.Element("FileName") ?? "",
                        RequestedBy = (string)el.Element("RequestedBy") ?? "",
                        RequestDate = (string)el.Element("RequestDate") ?? "",
                        Status = (string)el.Element("Status") ?? "",
                        Note = (string)el.Element("Note") ?? ""
                    });
                }
            }
            return requests;
        }

        public static List<RevisionRequest> GetPendingRequests() =>
            GetRequestsWhere(el => (string)el.Element("Status") == "Pending");

        public static List<RevisionRequest> GetRequestsByUser(string user) =>
            GetRequestsWhere(el => string.Equals(
                (string)el.Element("RequestedBy"), user,
                StringComparison.OrdinalIgnoreCase));

        public static void ResolveRequest(string id, string status)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var reqElement = doc.Root.Element("RevisionRequests");
                if (reqElement == null) return;

                foreach (var el in reqElement.Elements("Request"))
                {
                    if ((string)el.Element("Id") == id)
                    {
                        el.Element("Status").Value = status;
                        break;
                    }
                }
                Save(doc);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // File History
        // ════════════════════════════════════════════════════════════════
        public static List<HistoryEntry> GetFileHistory(string filePath)
        {
            var entries = new List<HistoryEntry>();
            string fileName = System.IO.Path.GetFileName(filePath);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                // A living rival WIP file owning this name means the query
                // path is a same-named duplicate — exact-path entries only
                // (it must not display the original's timeline). Same
                // predicate as Rule 2.6 / the quarantine / GetFileStatusByName.
                bool dupQuery = FindNameRival(doc, fileName, filePath) != null;

                foreach (var el in doc.Root
                    .Element("RevisionHistory")
                    .Elements("Entry"))
                {
                    string entryPath = (string)el.Element("FilePath") ?? "";
                    // Match by exact path first; fall back to filename so
                    // RELEASED folder copies share history with their
                    // original WIP path.
                    bool match = string.Equals(entryPath, filePath,
                                     StringComparison.OrdinalIgnoreCase)
                              || (!dupQuery && string.Equals(
                                     System.IO.Path.GetFileName(entryPath),
                                     fileName,
                                     StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;

                    entries.Add(new HistoryEntry
                    {
                        Status = (string)el.Element("Status") ?? "",
                        ChangedBy = (string)el.Element("ChangedBy") ?? "",
                        ChangedDate = (string)el.Element("ChangedDate") ?? "",
                        ChangeNote = (string)el.Element("ChangeNote") ?? ""
                    });
                }
            }

            entries.Reverse();
            return entries;
        }

        // Returns all tracked file paths whose name ends with the given
        // extension (e.g. ".sldasm"). Used to scan for parent assemblies that
        // reference a part without opening every file in the vault.
        public static List<string> GetTrackedFilePathsByExtension(string ext)
        {
            var paths = new List<string>();
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fp = (string)el.Element("FilePath") ?? "";
                    if (fp.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        paths.Add(fp);
                }
            }
            return paths;
        }

        // Returns the canonical (WIP/source) status for a file.
        // Falls back to filename match so RELEASED folder copies and legacy
        // records reflect correct status — but NEVER when a LIVING rival WIP
        // file owns this name: then the query path is a same-named duplicate
        // and must not wear the original's status (PR-A testing — the
        // quarantined twin showed the original's Released state). FindNameRival
        // is the same predicate Rule 2.6 and the quarantine use, so all three
        // agree; an orphaned/moved record (file gone from disk) is NOT a rival,
        // preserving the graceful fallback for Explorer-moved files.
        public static string GetFileStatusByName(string filePath)
        {
            string fileName = System.IO.Path.GetFileName(filePath);
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                // Exact path first
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Status") ?? "";
                }
                // A living rival owns this name → the query is a duplicate.
                if (FindNameRival(doc, fileName, filePath) != null)
                    return "";
                // Fallback: same filename, any path (RELEASED copies, legacy
                // records, orphaned records of a moved file)
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FileName"), fileName,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Status") ?? "";
                }
            }
            return "";
        }

        // Returns the full VaultFile record for an exact file path, or null if
        // the path is not tracked. Used by the Pending Requests cards to show
        // the file's PartNumber + Revision (which live on the File record, not
        // on the RevisionRequest).
        // Rename a tracked file's record IN PLACE (FilePath + FileName) and
        // re-point its RevisionHistory entries, so the timeline follows the
        // file. Used by Rule 3.6's config+drawing rename ({oldCfg}.slddrw →
        // {newCfg}.slddrw); the file on disk is renamed by the caller.
        public static void RenameFileRecord(string oldPath, string newPath,
            string user)
        {
            string oldName = System.IO.Path.GetFileName(oldPath);
            string newName = System.IO.Path.GetFileName(newPath);
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                bool changed = false;
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (!string.Equals((string)el.Element("FilePath"), oldPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    el.Element("FilePath").Value = newPath;
                    el.Element("FileName").Value = newName;
                    changed = true;
                }
                foreach (var en in doc.Root.Element("RevisionHistory")
                                          .Elements("Entry"))
                {
                    string ep = (string)en.Element("FilePath") ?? "";
                    if (string.Equals(ep, oldPath,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(System.IO.Path.GetFileName(ep), oldName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        en.Element("FilePath").Value = newPath;
                        changed = true;
                    }
                }
                if (changed) Save(doc);
            }
            AuditLogger.Log("FileRenamed", user, newName, "", "",
                "renamed from " + oldName + " (config rename)");
        }

        public static VaultFile GetFileRecord(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string pn  = (string)el.Element("PartNumber")  ?? "";
                        string dsc = (string)el.Element("Description") ?? "";
                        string rev = (string)el.Element("Revision")    ?? "";
                        return new VaultFile
                        {
                            FilePath          = (string)el.Element("FilePath") ?? "",
                            FileName          = (string)el.Element("FileName") ?? "",
                            PartNumber        = pn,
                            Description       = dsc,
                            Revision          = rev,
                            Status            = (string)el.Element("Status")           ?? "",
                            ReferencedModel   = (string)el.Element("ReferencedModel")  ?? "",
                            ReferencedConfigs = (string)el.Element("ReferencedConfigs")?? "",
                            Configurations    = ReadConfigs(el, pn, dsc, rev)
                        };
                    }
                }
            }
            return null;
        }

        // Returns the part/assembly VaultFile that this drawing documents.
        // Lookup order: (1) explicit ReferencedModel link written at save time
        // (reliable for multi-config drawings); (2) basename convention fallback
        // for drawings saved before multi-config shipped (Widget.slddrw →
        // Widget.sldprt). Returns null if not found.
        public static VaultFile GetModelForDrawing(string drawingFilePath)
        {
            if (string.IsNullOrEmpty(drawingFilePath)) return null;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // ── Pass 1: reference-based ──────────────────────────────
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fp = (string)el.Element("FilePath") ?? "";
                    if (!string.Equals(fp, drawingFilePath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    string refModel = (string)el.Element("ReferencedModel") ?? "";
                    if (string.IsNullOrEmpty(refModel)) break; // no ref → try basename

                    // Find the model record by path or by filename.
                    string refName = System.IO.Path.GetFileName(refModel);
                    foreach (var mel in doc.Root.Element("Files").Elements("File"))
                    {
                        string mfp = (string)mel.Element("FilePath") ?? "";
                        string mfn = (string)mel.Element("FileName") ?? "";
                        string mExt = System.IO.Path.GetExtension(mfn).ToLower();
                        if ((mExt == ".sldprt" || mExt == ".sldasm") &&
                            (string.Equals(mfp, refModel,
                                 StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(mfn, refName,
                                 StringComparison.OrdinalIgnoreCase)))
                        {
                            string pn  = (string)mel.Element("PartNumber")  ?? "";
                            string dsc = (string)mel.Element("Description") ?? "";
                            string rev = (string)mel.Element("Revision")    ?? "";
                            return new VaultFile
                            {
                                FilePath          = mfp,
                                FileName          = mfn,
                                PartNumber        = pn,
                                Description       = dsc,
                                Revision          = rev,
                                Status            = (string)mel.Element("Status") ?? "",
                                Configurations    = ReadConfigs(mel, pn, dsc, rev)
                            };
                        }
                    }
                    break;
                }

                // ── Pass 2: basename fallback ────────────────────────────
                string baseName = System.IO.Path.GetFileNameWithoutExtension(drawingFilePath);
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn    = (string)el.Element("FileName") ?? "";
                    string fnBase = System.IO.Path.GetFileNameWithoutExtension(fn);
                    string fnExt  = System.IO.Path.GetExtension(fn).ToLower();
                    if (string.Equals(fnBase, baseName,
                            StringComparison.OrdinalIgnoreCase) &&
                        (fnExt == ".sldprt" || fnExt == ".sldasm"))
                    {
                        string pn  = (string)el.Element("PartNumber")  ?? "";
                        string dsc = (string)el.Element("Description") ?? "";
                        string rev = (string)el.Element("Revision")    ?? "";
                        return new VaultFile
                        {
                            FilePath       = (string)el.Element("FilePath") ?? "",
                            FileName       = fn,
                            PartNumber     = pn,
                            Description    = dsc,
                            Revision       = rev,
                            Status         = (string)el.Element("Status") ?? "",
                            Configurations = ReadConfigs(el, pn, dsc, rev)
                        };
                    }
                }
            }
            return null;
        }

        // Returns the WIP path of the primary drawing for a part/assembly, or null.
        // "Primary" = the shared config-table drawing named after the model basename
        // (Widget.slddrw). For config-specific drawings use GetDrawingsForConfig.
        // Lookup order: (1) ReferencedModel reference (reliable); (2) basename.
        public static string GetDrawingPathForModel(string modelFilePath)
        {
            if (string.IsNullOrEmpty(modelFilePath)) return null;
            string modelName = System.IO.Path.GetFileName(modelFilePath);
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // ── Pass 1: reference-based ──────────────────────────────
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn  = (string)el.Element("FileName") ?? "";
                    if (!System.IO.Path.GetExtension(fn)
                            .Equals(".slddrw", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string refModel = (string)el.Element("ReferencedModel") ?? "";
                    if (string.IsNullOrEmpty(refModel)) continue;

                    if (string.Equals(System.IO.Path.GetFileName(refModel),
                            modelName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(refModel, modelFilePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string fp = (string)el.Element("FilePath") ?? "";
                        if (!string.IsNullOrEmpty(fp)) return fp;
                    }
                }

                // ── Pass 2: basename fallback ────────────────────────────
                string baseName = System.IO.Path.GetFileNameWithoutExtension(modelFilePath);
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn    = (string)el.Element("FileName") ?? "";
                    string fnBase = System.IO.Path.GetFileNameWithoutExtension(fn);
                    string fnExt  = System.IO.Path.GetExtension(fn).ToLower();
                    if (string.Equals(fnBase, baseName,
                            StringComparison.OrdinalIgnoreCase) &&
                        fnExt == ".slddrw")
                    {
                        string fp = (string)el.Element("FilePath") ?? "";
                        return string.IsNullOrEmpty(fp) ? null : fp;
                    }
                }
            }
            return null;
        }

        // Returns all WIP drawing paths that document a specific configuration of
        // a model. Used by the per-config search card and by release/new-revision
        // to locate config-specific drawings.
        // Match logic: drawing's ReferencedModel == modelFilePath AND
        //   ReferencedConfigs is blank (covers all) OR contains configName.
        // Falls back to basename convention for old/unsaved drawings.
        // Mirrors the sanitisation in VaultManager.OpenOrCreateDrawing: a config
        // name used as a drawing filename has Windows-illegal characters replaced
        // with '_'. Kept here so config→drawing lookup agrees with creation.
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string(name.Select(
                c => System.Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        public static List<string> GetDrawingsForConfig(string modelFilePath, string configName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(modelFilePath)) return result;

            string modelName = System.IO.Path.GetFileName(modelFilePath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn  = (string)el.Element("FileName") ?? "";
                    if (!System.IO.Path.GetExtension(fn)
                            .Equals(".slddrw", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fp = (string)el.Element("FilePath") ?? "";
                    if (string.IsNullOrEmpty(fp) || seen.Contains(fp)) continue;

                    string refModel   = (string)el.Element("ReferencedModel")   ?? "";
                    string refConfigs = (string)el.Element("ReferencedConfigs") ?? "";

                    if (!string.IsNullOrEmpty(refModel) &&
                        (string.Equals(refModel, modelFilePath,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(System.IO.Path.GetFileName(refModel),
                             modelName, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Drawing references our model. Check config coverage.
                        bool coversAll     = string.IsNullOrEmpty(refConfigs);
                        bool coversThis    = !coversAll && refConfigs
                            .Split(',')
                            .Any(c => string.Equals(c.Trim(), configName,
                                          StringComparison.OrdinalIgnoreCase));
                        if (coversAll || coversThis)
                        {
                            result.Add(fp);
                            seen.Add(fp);
                        }
                        continue; // don't also match by basename
                    }

                    // Basename fallback: drawing named after the model (shared) or
                    // after the config PartNo (config-specific) — pre-reference era.
                    string fnBase = System.IO.Path.GetFileNameWithoutExtension(fn);
                    string modelBase   = System.IO.Path.GetFileNameWithoutExtension(modelFilePath);
                    bool sharedMatch   = string.Equals(fnBase, modelBase,
                                            StringComparison.OrdinalIgnoreCase);
                    // Config-specific drawings are saved with a filename-SAFE
                    // version of the config name (illegal chars → '_'), so match
                    // the raw config name OR its sanitised form — otherwise a
                    // PartNo containing a slash/quote/etc. would never resolve its
                    // drawing here and New Revision would skip bumping it.
                    bool configMatch   = !string.IsNullOrEmpty(configName) &&
                                        (string.Equals(fnBase, configName,
                                            StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(fnBase, SanitizeFileName(configName),
                                            StringComparison.OrdinalIgnoreCase));
                    if ((sharedMatch || configMatch) && !seen.Contains(fp))
                    {
                        result.Add(fp);
                        seen.Add(fp);
                    }
                }
            }
            return result;
        }

        // Returns all configuration entries for a tracked file path.
        // For old single-config records returns a synthesised single-entry list.
        public static List<ConfigEntry> GetConfigsForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return new List<ConfigEntry>();
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string pn  = (string)el.Element("PartNumber")  ?? "";
                        string dsc = (string)el.Element("Description") ?? "";
                        string rev = (string)el.Element("Revision")    ?? "";
                        return ReadConfigs(el, pn, dsc, rev);
                    }
                }
            }
            return new List<ConfigEntry>();
        }
    }
}