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
        // Extra properties indexed PER CONFIG for the Advanced (property-wide)
        // search popup — Material / Finish / DrawnBy (drafter) / PartType
        // (Manufactured|Purchased). Captured at save time (SwAddin.OnSavePost)
        // and round-tripped through the <Config> block; empty on legacy records
        // until re-saved (graceful — no migration). NOT part of the file's
        // identity, so they never gate a save or feed the quick-find box.
        public string Material    { get; set; }
        public string FinishType  { get; set; }
        public string DrawnBy     { get; set; }
        public string PartType    { get; set; }
    }

    // One DrawingNo + Revision that names a CURRENT released drawing PDF
    // (EXPORTS\PDF\{DrawingNo} REV {Revision}.pdf). Returned by
    // DatabaseManager.GetDrawingExportRefs for the dashboard's batch-print.
    public class DrawingExportRef
    {
        public string DrawingNo { get; set; }
        public string Revision  { get; set; }
    }

    // One DIRECT child of an assembly, captured at SAVE time (the assembly is open
    // then, so this is cheap and needs no file opening): which file it is, which
    // CONFIG of that file the assembly references, and how many instances. Stored
    // per assembly in <Components>; powers config-accurate Where Used for WIP
    // assemblies (which have no baseline yet). Config == Part No by convention.
    public class ComponentRef
    {
        public string Path   { get; set; }
        public string Config { get; set; }
        public int    Qty    { get; set; }
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
        // Populated by GetAllFiles: the ChangeNote of that most-recent "Released"
        // history entry — the reason-for-change of the latest release ("" if none).
        public string ReleaseReason { get; set; }
        // Obsolete supersession: the Part No of the file that REPLACES this one
        // (set when a Master marks it Obsolete; "" if none / not obsolete). Read
        // from the record by GetAllFiles + SearchFiles. ObsoleteReason = the
        // ChangeNote of the most-recent "Obsolete" history entry (GetAllFiles).
        public string SupersededBy { get; set; }
        public string ObsoleteReason { get; set; }
        public bool HasBrokenRefs { get; set; }
        // Per-configuration metadata (parts/assemblies). Empty for drawings and for
        // old records that haven't been re-saved since this feature shipped.
        public List<ConfigEntry> Configurations { get; set; } = new List<ConfigEntry>();
        // Direct (top-level) child components captured at save (ASSEMBLIES only) —
        // {child path, referenced config, qty}. Drives config-accurate Where Used
        // for WIP assemblies. Empty for parts/drawings and old records.
        public List<ComponentRef> Components { get; set; } = new List<ComponentRef>();
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

    // Everything the task-pane Active File card needs about one file, gathered
    // in a SINGLE vault.xml load. Refresh previously made three separate loads
    // (GetFileStatusByName + GetLockInfo + GetFileHistory) on every document
    // and configuration switch (audit M3).
    public class ActiveFileInfo
    {
        public string Status = "";                 // by-name semantics (GetFileStatusByName)
        public LockInfo Lock = new LockInfo { IsLocked = false };
        public List<HistoryEntry> History = new List<HistoryEntry>();
        public bool HasDuplicateRival;             // a living rival WIP file owns this name
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

    // A live exclusive claim on a vault operation (Release / New Revision /
    // Rollback / Remove / Unlock) — see TryBeginOperation. Distinct from both
    // the hard Master Lock (a stored state) and OpenSessions (read presence):
    // this guards the MINUTES-long check-then-act window of a Master command,
    // so two Masters can't interleave Release/Remove/New-Revision on the same
    // file while confirmation dialogs sit open.
    public class ActiveOperation
    {
        public string FilePath { get; set; }
        public string User { get; set; }
        public string Machine { get; set; }
        public string Operation { get; set; }
        public DateTime StartedDate { get; set; }
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
                    new XElement("Revision",     c.Revision    ?? ""),
                    // Indexed for the Advanced (property-wide) search.
                    new XElement("Material",     c.Material    ?? ""),
                    new XElement("FinishType",   c.FinishType  ?? ""),
                    new XElement("DrawnBy",      c.DrawnBy     ?? ""),
                    new XElement("PartType",     c.PartType    ?? "")
                ));
            return el;
        }

        // <Components><Comp Path Config Qty/>…</Components> — the assembly's direct
        // children + the config each uses, captured at save (attributes, compact).
        private static XElement BuildComponentsElement(List<ComponentRef> comps)
        {
            var el = new XElement("Components");
            foreach (var c in comps)
                el.Add(new XElement("Comp",
                    new XAttribute("Path",   c.Path   ?? ""),
                    new XAttribute("Config", c.Config ?? ""),
                    new XAttribute("Qty",    c.Qty)));
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
                        Revision    = (string)c.Element("Revision")    ?? "",
                        // Advanced-search index (empty on legacy records).
                        Material    = (string)c.Element("Material")    ?? "",
                        FinishType  = (string)c.Element("FinishType")  ?? "",
                        DrawnBy     = (string)c.Element("DrawnBy")     ?? "",
                        PartType    = (string)c.Element("PartType")    ?? ""
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

        // Parse a round-trip ("o") timestamp; MinValue when absent/unparseable.
        private static DateTime ParseRoundtrip(string s)
        {
            DateTime dt = DateTime.MinValue;
            DateTime.TryParse(s ?? "", null,
                System.Globalization.DateTimeStyles.RoundtripKind, out dt);
            return dt;
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
                    // SetOrAdd throughout — a legacy/hand-repaired record
                    // missing any of these elements NRE'd the whole upsert
                    // (.Element(x).Value on a null element).
                    SetOrAdd(existing, "FileName",     f.FileName);
                    SetOrAdd(existing, "PartNumber",   f.PartNumber  ?? "");
                    SetOrAdd(existing, "Description",  f.Description ?? "");
                    SetOrAdd(existing, "ModifiedBy",   f.ModifiedBy  ?? "");
                    SetOrAdd(existing, "ModifiedDate", f.ModifiedDate.ToString("o"));

                    if (!string.IsNullOrEmpty(f.Status))
                        SetOrAdd(existing, "Status", f.Status);

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

                    // Direct-children snapshot (assemblies). Same preserve-on-empty
                    // rule as Configurations: an empty list means the capture failed
                    // transiently (or this isn't an assembly), so keep the old block
                    // rather than wiping the config-accurate Where Used data.
                    if (f.Components != null && f.Components.Count > 0)
                    {
                        existing.Element("Components")?.Remove();
                        existing.Add(BuildComponentsElement(f.Components));
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
                    if (f.Components != null && f.Components.Count > 0)
                        fileEl.Add(BuildComponentsElement(f.Components));
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
                        SetOrAdd(el, "Status", status); // legacy-safe (no NRE)
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

        // Obsolete supersession link: the Part No of the file that REPLACES this
        // one. Set when a Master marks a file Obsolete (and picks a replacement),
        // cleared (empty) on Reinstate. Stored as <SupersededBy> on the File
        // record. SetOrAdd (legacy-safe). User-initiated mutation → saves even in
        // degraded-lock mode, like SetFileStatus.
        public static void SetSupersededBy(string filePath, string supersededByPartNo)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        SetOrAdd(el, "SupersededBy", supersededByPartNo ?? "");
                        break;
                    }
                }
                Save(doc);
            }
        }

        // Reads the SupersededBy Part No for a file ("" if none / untracked).
        // Light single-record read — used by ValidateSave's Obsolete block and
        // the obsolete-in-assembly open warning to name the replacement.
        public static string GetSupersededBy(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("SupersededBy") ?? "";
                }
            }
            return "";
        }

        public static void SetBrokenRefFlag(string filePath, bool hasBroken)
            => SetBrokenRefFlag(filePath, hasBroken, false);

        // suppressInDegraded: the open-triggered cosmetic CLEAR
        // (SwAddin.ClearStaleBrokenRefFlag) passes true so it is SKIPPED in
        // degraded-lock mode — a file OPEN is a read path and must never write a
        // stale whole-vault snapshot back over other machines' committed changes
        // (the same rule presence bookkeeping / orphan purge / restore follow).
        // The save-time SET/CLEAR (ValidateSave Rule 5) passes false: it is part
        // of a user SAVE and mutates regardless, alongside UpsertFile/SetFileStatus.
        public static void SetBrokenRefFlag(string filePath, bool hasBroken,
            bool suppressInDegraded)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                if (suppressInDegraded && LockDegraded) return;
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        SetOrAdd(el, "HasBrokenRefs",   // legacy-safe (no NRE)
                            hasBroken ? "true" : "false");
                        break;
                    }
                }
                Save(doc);
            }
        }

        // Cheap read of a record's broken-ref flag (one load, no disk walk).
        // The open-time stale-flag recheck (SwAddin.ClearStaleBrokenRefFlag)
        // calls this FIRST so the expensive ReferenceChecker walk runs only for
        // files that are actually flagged (the rare case). Untracked → false.
        public static bool GetBrokenRefFlag(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        return string.Equals((string)el.Element("HasBrokenRefs"),
                            "true", StringComparison.OrdinalIgnoreCase);
                return false;
            }
        }

        // Update a file record's top-level Revision (matched by FilePath, case-
        // insensitively). Used by rollback to sync a rolled-back DRAWING's record to
        // the target rev: the model's identity is synced via the full reopen +
        // UpsertFile in rollback Step 8.5, but a drawing carries no PartNo/Description
        // of its own (the dashboard fills those from the model) — only its Revision
        // needs updating so search/dashboard stop showing the pre-rollback rev. DB
        // record only; never touches the file on disk; no-op if the path isn't tracked.
        public static void SetFileRevision(string filePath, string revision)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var rev = el.Element("Revision");
                        if (rev == null)
                            el.Add(new XElement("Revision", revision ?? ""));
                        else
                            rev.Value = revision ?? "";
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
                        // SetOrAdd, not .Element(x).Value: a legacy/hand-
                        // repaired record missing the element NRE'd here.
                        SetOrAdd(el, "LockedBy", lockedBy);
                        SetOrAdd(el, "LockedDate", DateTime.Now.ToString("o"));
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
                        SetOrAdd(el, "LockedBy", "");
                        SetOrAdd(el, "LockedDate", "");
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
                            // TryParse — a hand-edited/legacy LockedDate used to
                            // THROW out of DateTime.Parse (it was the only
                            // unguarded date parse in the file) and break every
                            // caller, task-pane refresh included. An
                            // unparseable date reads as MinValue; the lock
                            // itself still reports correctly.
                            DateTime lockedDate;
                            DateTime.TryParse(
                                (string)el.Element("LockedDate"),
                                out lockedDate);
                            return new LockInfo
                            {
                                IsLocked = true,
                                LockedBy = lockedBy,
                                LockedDate = lockedDate
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
                    SetOrAdd(mine, "OpenedDate",        // legacy-safe (no NRE)
                        DateTime.Now.ToString("o"));
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
        // and falsely warn other engineers. Also clears this machine's
        // operation CLAIMS (see TryBeginOperation) in the same pass, so a
        // crashed SOLIDWORKS never wedges a file shut for the full 30-minute
        // staleness window.
        public static void ClearMachineSessions(string machine)
        {
            if (string.IsNullOrWhiteSpace(machine)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                if (LockDegraded) return; // advisory presence — see RegisterOpenSession

                var doc = LoadOrCreate();
                var toRemove = new List<XElement>();

                var sessions = doc.Root.Element("OpenSessions");
                if (sessions != null)
                    foreach (var el in sessions.Elements("Session"))
                    {
                        if (string.Equals((string)el.Element("Machine"), machine,
                                StringComparison.OrdinalIgnoreCase))
                            toRemove.Add(el);
                    }

                var ops = doc.Root.Element("ActiveOperations");
                if (ops != null)
                    foreach (var el in ops.Elements("Operation"))
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
        // Active operations (cross-machine Master-command claims)
        // ════════════════════════════════════════════════════════════════
        // Release / New Revision / Rollback / Remove are check-then-act flows
        // that hold confirmation dialogs open for minutes between the status
        // checks and the final DB write. The cross-machine vault.lock only
        // serialises individual DB calls — it is never held across SOLIDWORKS
        // work — so two Masters could interleave operations on the SAME file
        // (double release, lock steal, remove-during-release). A claim marks
        // the file "operation in progress" for the WHOLE flow. Advisory, like
        // OpenSessions: backed by an <ActiveOperations> section in vault.xml,
        // stale after StaleOperationMinutes (crash backstop — claims are also
        // wiped per-machine by ClearMachineSessions on add-in load/unload).

        private const int StaleOperationMinutes = 30;

        private static XElement EnsureActiveOperations(XDocument doc)
        {
            var el = doc.Root.Element("ActiveOperations");
            if (el == null)
            {
                el = new XElement("ActiveOperations");
                doc.Root.Add(el);
            }
            return el;
        }

        // Claim filePath for an exclusive vault operation. TRUE = the claim is
        // ours (recorded, or refreshed if we already held it — nested/chained
        // flows like a drawing release chaining its model's release re-claim
        // harmlessly). FALSE = another user/machine holds a live claim; holder
        // describes it so the caller can say who/what/when.
        //
        // Degraded-lock mode: the claim can neither be trusted nor safely
        // written (a stale whole-vault snapshot must never be saved without
        // the cross-machine lock), so the operation is allowed through
        // UNGUARDED — never worse than before claims existed (non-fatal
        // philosophy, same rule as the janitorial writes).
        public static bool TryBeginOperation(string filePath, string user,
            string machine, string operation, out ActiveOperation holder)
        {
            holder = null;
            if (string.IsNullOrWhiteSpace(filePath)) return true;
            lock (_lock) using (AcquireProcessLock())
            {
                if (LockDegraded) return true;

                var doc = LoadOrCreate();
                var ops = EnsureActiveOperations(doc);

                XElement mine = null;
                var stale = new List<XElement>();
                foreach (var el in ops.Elements("Operation"))
                {
                    if (!string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTime started;
                    if (!DateTime.TryParse((string)el.Element("StartedDate"),
                            out started) ||
                        (DateTime.Now - started).TotalMinutes >
                            StaleOperationMinutes)
                    {
                        stale.Add(el);
                        continue;
                    }

                    if (string.Equals((string)el.Element("User"), user,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)el.Element("Machine"), machine,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        mine = el;
                    }
                    else
                    {
                        holder = new ActiveOperation
                        {
                            FilePath = filePath,
                            User = (string)el.Element("User") ?? "",
                            Machine = (string)el.Element("Machine") ?? "",
                            Operation = (string)el.Element("Operation") ?? "",
                            StartedDate = started
                        };
                        // Conflict — leave the vault untouched (stale entries
                        // are purged on a later successful claim).
                        return false;
                    }
                }

                foreach (var el in stale) el.Remove();

                if (mine != null)
                {
                    SetOrAdd(mine, "Operation", operation);
                    SetOrAdd(mine, "StartedDate", DateTime.Now.ToString("o"));
                }
                else
                {
                    ops.Add(new XElement("Operation",
                        new XElement("FilePath", filePath),
                        new XElement("User", user),
                        new XElement("Machine", machine),
                        new XElement("Operation", operation),
                        new XElement("StartedDate", DateTime.Now.ToString("o"))
                    ));
                }
                Save(doc);
                return true;
            }
        }

        // Release this user@machine's claim on filePath. Called from the
        // operation's finally block so even an aborted/thrown flow releases
        // its claim; a crash is covered by the staleness window + the
        // per-machine wipe on add-in load.
        public static void EndOperation(string filePath, string user,
            string machine)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                if (LockDegraded) return; // see TryBeginOperation

                var doc = LoadOrCreate();
                var ops = doc.Root.Element("ActiveOperations");
                if (ops == null) return;

                var toRemove = new List<XElement>();
                foreach (var el in ops.Elements("Operation"))
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

        // ════════════════════════════════════════════════════════════════
        // Users / roles
        // ════════════════════════════════════════════════════════════════
        // Roles come from TWO sources, in priority order (audit H13):
        //  1. N:\PDM-SolidWorks\VAULT\roles.config — OPTIONAL override file:
        //       <Roles>
        //         <User><Username>bchougule</Username><Role>Master</Role></User>
        //       </Roles>
        //     "Master only" is otherwise pure client-side data in vault.xml,
        //     which every engineer can edit in Notepad (self-promotion to
        //     Master). IT can set NTFS ACLs on roles.config (read-only for
        //     engineers, writable by the Masters/IT), turning the role list
        //     into OS-ENFORCED protection. When present and readable it is
        //     AUTHORITATIVE — vault.xml's <Users> is ignored entirely.
        //  2. vault.xml <Users> — the original behaviour (fallback, and the
        //     default until IT creates roles.config). See SECURITY.md.
        // Lookups are CACHED for 5 minutes per process: IsMaster runs on
        // every Master button press and each call was a full vault.xml load
        // over SMB. AddUser invalidates the cache; a role change made on
        // another machine lands within the cache window.
        private const string RolesFile = @"N:\PDM-SolidWorks\VAULT\roles.config";
        private const int RoleCacheSeconds = 300;
        private static readonly object _roleCacheLock = new object();
        private static Dictionary<string, string> _roleCache;
        private static DateTime _roleCacheAt;

        // Normalise a stored role to canonical case. The role gates around the
        // app compare case-SENSITIVELY (GetUserRole(...) == "Master" in
        // VaultManager/TaskPaneControl/SwAddin), but roles.config is hand-edited
        // by IT in Notepad — a "master"/"MASTER" typo would otherwise silently
        // demote that user everywhere the gates run while GetMasterUsernames
        // (OrdinalIgnoreCase) still emailed them. Canonicalising at the single
        // map-build chokepoint fixes every gate at once. An unrecognised role is
        // returned verbatim (every gate treats non-"Master" as Engineer anyway).
        private static string CanonicalRole(string role)
        {
            string r = (role ?? "").Trim();
            if (r.Equals("Master", StringComparison.OrdinalIgnoreCase)) return "Master";
            if (r.Equals("Engineer", StringComparison.OrdinalIgnoreCase)) return "Engineer";
            return r;
        }

        private static Dictionary<string, string> GetRoleMap()
        {
            lock (_roleCacheLock)
            {
                if (_roleCache != null &&
                    (DateTime.Now - _roleCacheAt).TotalSeconds < RoleCacheSeconds)
                    return _roleCache;

                var map = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                // 1) roles.config — authoritative when present and readable.
                bool fromFile = false;
                try
                {
                    if (File.Exists(RolesFile))
                    {
                        var rolesDoc = XDocument.Load(RolesFile);
                        foreach (var el in rolesDoc.Root.Elements("User"))
                        {
                            string u = ((string)el.Element("Username") ?? "").Trim();
                            string r = ((string)el.Element("Role") ?? "").Trim();
                            if (u.Length > 0 && r.Length > 0) map[u] = CanonicalRole(r);
                        }
                        // An EMPTY but present file is treated as unreadable
                        // (falls back) rather than demoting everyone at once.
                        fromFile = map.Count > 0;
                    }
                }
                catch
                {
                    // Corrupt roles.config → fall back to vault.xml below so a
                    // bad edit can't lock both Masters out; the throttled
                    // health log makes the problem visible.
                    map.Clear();
                    LogDbEvent("RolesFileUnreadable",
                        "roles.config exists but could not be parsed — " +
                        "falling back to vault.xml <Users>");
                }

                // 2) vault.xml <Users> fallback.
                if (!fromFile)
                {
                    lock (_lock) using (AcquireProcessLock())
                    {
                        var doc = LoadOrCreate();
                        foreach (var el in doc.Root.Element("Users").Elements("User"))
                        {
                            string u = ((string)el.Element("Username") ?? "").Trim();
                            string r = (string)el.Element("Role") ?? "Engineer";
                            if (u.Length > 0) map[u] = CanonicalRole(r);
                        }
                    }
                }

                _roleCache = map;
                _roleCacheAt = DateTime.Now;
                return map;
            }
        }

        public static string GetUserRole(string username)
        {
            string role;
            return GetRoleMap().TryGetValue((username ?? "").Trim(), out role)
                ? role : "Engineer";
        }

        // Every username whose role is Master — used by EmailManager so
        // request notifications reach the LIVE Master list instead of a
        // hardcoded pair. Empty when the role source is unreachable (the
        // caller falls back).
        public static List<string> GetMasterUsernames()
        {
            var masters = new List<string>();
            foreach (var kv in GetRoleMap())
                if (string.Equals(kv.Value, "Master",
                        StringComparison.OrdinalIgnoreCase))
                    masters.Add(kv.Key);
            return masters;
        }

        public static void AddUser(string username, string role)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var users = doc.Root.Element("Users");

                bool updated = false;
                foreach (var el in users.Elements("User"))
                {
                    if (string.Equals((string)el.Element("Username"),
                        username, StringComparison.OrdinalIgnoreCase))
                    {
                        SetOrAdd(el, "Role", role); // legacy-safe (no NRE)
                        updated = true;
                        break;
                    }
                }

                if (!updated)
                    users.Add(new XElement("User",
                        new XElement("Username", username),
                        new XElement("Role", role)
                    ));
                Save(doc);
            }
            // OUTSIDE the DB lock: GetRoleMap locks _roleCacheLock → _lock, so
            // taking _roleCacheLock while holding _lock would invert the
            // ordering and risk a deadlock.
            InvalidateRoleCache();
        }

        private static void InvalidateRoleCache()
        {
            lock (_roleCacheLock) { _roleCache = null; }
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

            // ToLowerInvariant throughout the scan: culture-sensitive ToLower
            // breaks matching on locales with special casing (Turkish dotless
            // I turns "FIle" ≠ "file") and rebinds the culture per call.
            string term = searchTerm.ToLowerInvariant().Trim();
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
                    bool matchFound = partNoRaw.ToLowerInvariant().Contains(term)
                                   || descRaw.ToLowerInvariant().Contains(term)
                                   || fileName.ToLowerInvariant().Contains(term);

                    if (!matchFound)
                    {
                        var cfgEl = el.Element("Configurations");
                        if (cfgEl != null)
                        {
                            foreach (var c in cfgEl.Elements("Config"))
                            {
                                if (((string)c.Element("PartNo")      ?? "").ToLowerInvariant().Contains(term) ||
                                    ((string)c.Element("Description") ?? "").ToLowerInvariant().Contains(term))
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
                        SupersededBy      = (string)el.Element("SupersededBy")      ?? "",
                        // Card hover-tooltip + Locked-card owner (read in this same
                        // scan — no extra I/O; LockedBy meaningful only when Locked).
                        ModifiedBy        = (string)el.Element("ModifiedBy")        ?? "",
                        ModifiedDate      = ParseRoundtrip(
                                               (string)el.Element("ModifiedDate")),
                        LockedBy          = (string)el.Element("LockedBy")          ?? "",
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

        // Returns the tracked records for a SPECIFIC set of file paths in ONE
        // vault.xml load, IN THE INPUT ORDER (paths not found on the records are
        // skipped — e.g. a since-removed file). Backs the task-pane "recently
        // opened" list: a small fixed set, so one load is far cheaper than a
        // GetFileRecord per path. Read-only; never purges. Populates the same
        // fields as SearchFiles (incl. ModifiedBy/Date/LockedBy + Configurations)
        // so the recent cards render exactly like search cards.
        public static List<VaultFile> GetFilesByPaths(IList<string> paths)
        {
            var result = new List<VaultFile>();
            if (paths == null || paths.Count == 0) return result;
            var want = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, VaultFile>(
                StringComparer.OrdinalIgnoreCase);
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (string.IsNullOrEmpty(status)) continue;
                    string filePath = (string)el.Element("FilePath") ?? "";
                    if (!want.Contains(filePath) || byPath.ContainsKey(filePath))
                        continue;
                    string partNoRaw = (string)el.Element("PartNumber") ?? "";
                    string descRaw   = (string)el.Element("Description") ?? "";
                    byPath[filePath] = new VaultFile
                    {
                        FilePath    = filePath,
                        FileName    = (string)el.Element("FileName") ?? "",
                        PartNumber  = partNoRaw,
                        Description = descRaw,
                        Status      = status,
                        Revision    = (string)el.Element("Revision") ?? "",
                        ReferencedModel   = (string)el.Element("ReferencedModel")   ?? "",
                        ReferencedConfigs = (string)el.Element("ReferencedConfigs") ?? "",
                        SupersededBy      = (string)el.Element("SupersededBy")      ?? "",
                        ModifiedBy        = (string)el.Element("ModifiedBy")        ?? "",
                        ModifiedDate      = ParseRoundtrip(
                                               (string)el.Element("ModifiedDate")),
                        LockedBy          = (string)el.Element("LockedBy")          ?? "",
                        Configurations    = ReadConfigs(el, partNoRaw, descRaw,
                                               (string)el.Element("Revision") ?? "")
                    };
                }
            }
            foreach (var p in paths)
            {
                VaultFile vf;
                if (p != null && byPath.TryGetValue(p, out vf)) result.Add(vf);
            }
            return result;
        }

        // Advanced (property-wide) search backing the AdvancedSearchForm popup.
        // Unlike the quick SearchFiles (one box, OR over PN/Description/FileName)
        // this AND-combines whatever fields the user filled and matches them PER
        // CONFIGURATION — so the quick-find box is never flooded with low-
        // cardinality category hits (typing "ste" no longer drags in every STEEL
        // part; that noise is exactly why property search lives behind its own
        // popup). Each filled field NARROWS the result; an empty field is ignored;
        // at least one must be non-empty (else an empty result).
        //
        //   mainTerm — substring over the file name + file-level PN/Description
        //              AND the config's PN/Description/DrawingNo (so a part is
        //              findable by its drawing number too)
        //   drawnBy  — substring over the config's DrawnBy (drafter initials)
        //   material — EXACT (case-insensitive) match on the config's Material1
        //   finish   — EXACT match on the config's FinishType
        //   partType — EXACT match on the config's PartType (Manufactured|Purchased)
        //   statusFilter — EXACT match on the FILE status (WIP/Released/Locked/
        //              Obsolete), file-level (all configs share one status)
        //   fileType — "Part" (.sldprt) | "Assembly" (.sldasm) | "" (either)
        //
        // Returns PARTS/ASSEMBLIES only — the four indexed properties live on the
        // model, not on drawings (the result card's Open DRW still reaches the
        // drawing via the DrawingIndex). Each returned file's Configurations list
        // is TRIMMED to only the configs that passed every active filter, so the
        // popup renders exactly the matching config cards, never "all configs of a
        // file that matched". Capped at maxResults files (default MaxSearchResults;
        // truncated=true when more matched) — the CSV export passes a large cap to
        // dump the FULL matching set while the on-screen card list stays capped for
        // UI performance. READ-ONLY: orphans (file gone on disk) are skipped when
        // the share is reachable but NEVER purged (no write — safe in degraded-
        // lock mode; the quick SearchFiles owns orphan cleanup).
        public static List<VaultFile> SearchFilesAdvanced(
            string mainTerm, string drawnBy, string material, string finish,
            string partType, string statusFilter, string fileType,
            out bool truncated, int maxResults = MaxSearchResults)
        {
            truncated = false;
            var results = new List<VaultFile>();

            string main = (mainTerm ?? "").ToLowerInvariant().Trim();
            string drw  = (drawnBy  ?? "").ToLowerInvariant().Trim();
            string mat  = (material ?? "").Trim();
            string fin  = (finish   ?? "").Trim();
            string pt   = (partType ?? "").Trim();
            string st   = (statusFilter ?? "").Trim(); // file-level status (WIP/Released/Locked/Obsolete)
            string ft   = (fileType ?? "").Trim();      // "Part" | "Assembly" | ""

            // Nothing to search — every field blank.
            if (main.Length == 0 && drw.Length == 0 && mat.Length == 0 &&
                fin.Length == 0 && pt.Length == 0 && st.Length == 0 &&
                ft.Length == 0)
                return results;

            var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // Only treat a file as orphaned (skip it) when the share is
                // provably reachable; with the network down File.Exists returns
                // false for everything and would hide the whole vault.
                bool shareUp = false;
                try { shareUp = Directory.Exists(WipRoot); } catch { }

                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (string.IsNullOrEmpty(status)) continue;

                    // Lifecycle filter (file-level — all configs share one status).
                    if (st.Length > 0 &&
                        !string.Equals(status, st, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileName = (string)el.Element("FileName") ?? "";

                    // Parts / assemblies only — the indexed props live on models.
                    string ext = "";
                    try { ext = Path.GetExtension(fileName).ToLowerInvariant(); }
                    catch { }
                    if (ext != ".sldprt" && ext != ".sldasm") continue;

                    // File-type filter (Part / Assembly) from the extension.
                    if (ft.Length > 0)
                    {
                        if (string.Equals(ft, "Part", StringComparison.OrdinalIgnoreCase)
                                && ext != ".sldprt") continue;
                        if (string.Equals(ft, "Assembly", StringComparison.OrdinalIgnoreCase)
                                && ext != ".sldasm") continue;
                    }

                    // A FILENAME hit widens the main term to EVERY config — you
                    // matched the whole file, mirroring the quick search's card
                    // expansion (AddModelConfigCards widens on a filename match
                    // only). File-level PartNumber/Description are deliberately
                    // NOT folded in: they are just the primary config's values,
                    // already covered by the per-config loop below, and folding
                    // them in surfaced SIBLING configs when searching one config's
                    // own Part No (e.g. "BRK-100" also returned BRK-200) — found
                    // in the adversarial pre-merge review.
                    bool fileMain = main.Length == 0
                                 || fileName.ToLowerInvariant().Contains(main);

                    var configs = ReadConfigs(el,
                        (string)el.Element("PartNumber") ?? "",
                        (string)el.Element("Description") ?? "",
                        (string)el.Element("Revision") ?? "");

                    var passed = new List<ConfigEntry>();
                    foreach (var c in configs)
                    {
                        // Main term also matches the config's Drawing No, so a part
                        // is findable by its drawing number from the main box.
                        bool mainOK = fileMain
                            || (c.PartNo ?? "").ToLowerInvariant().Contains(main)
                            || (c.Description ?? "").ToLowerInvariant().Contains(main)
                            || (c.DrawingNo ?? "").ToLowerInvariant().Contains(main);
                        if (!mainOK) continue;
                        if (drw.Length > 0 &&
                            !(c.DrawnBy ?? "").ToLowerInvariant().Contains(drw))
                            continue;
                        if (mat.Length > 0 &&
                            !string.Equals((c.Material ?? "").Trim(), mat,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (fin.Length > 0 &&
                            !string.Equals((c.FinishType ?? "").Trim(), fin,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (pt.Length > 0 &&
                            !string.Equals((c.PartType ?? "").Trim(), pt,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        passed.Add(c);
                    }
                    if (passed.Count == 0) continue;

                    // Orphan skip (no purge — this is a read-only search).
                    string filePath = (string)el.Element("FilePath") ?? "";
                    bool missing = string.IsNullOrEmpty(filePath);
                    if (!missing && shareUp && !File.Exists(filePath))
                    {
                        try
                        {
                            missing = Directory.Exists(
                                Path.GetDirectoryName(filePath));
                        }
                        catch { }
                    }
                    if (missing) continue;

                    // Dedupe by filename (legacy double-records / RELEASED copies).
                    if (!seenFileNames.Add(fileName)) continue;

                    if (results.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    results.Add(new VaultFile
                    {
                        FilePath     = filePath,
                        FileName     = fileName,
                        PartNumber   = (string)el.Element("PartNumber")  ?? "",
                        Description  = (string)el.Element("Description") ?? "",
                        Status       = status,
                        Revision     = (string)el.Element("Revision")    ?? "",
                        SupersededBy = (string)el.Element("SupersededBy") ?? "",
                        Configurations = passed   // TRIMMED to the matching configs
                    });
                }
            }

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
                var relReasonByPath = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                // Latest "Obsolete" reason per path (parallel to the Released maps)
                // for the dashboard tooltip on Obsolete rows.
                var obsDateByPath = new Dictionary<string, DateTime>(
                    StringComparer.OrdinalIgnoreCase);
                var obsReasonByPath = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                var hist = doc.Root.Element("RevisionHistory");
                if (hist != null)
                {
                    foreach (var en in hist.Elements("Entry"))
                    {
                        string est = (string)en.Element("Status") ?? "";
                        bool isRel = string.Equals(est, "Released",
                            StringComparison.OrdinalIgnoreCase);
                        bool isObs = string.Equals(est, "Obsolete",
                            StringComparison.OrdinalIgnoreCase);
                        if (!isRel && !isObs) continue;
                        string fp = (string)en.Element("FilePath") ?? "";
                        if (string.IsNullOrEmpty(fp)) continue;
                        DateTime d;
                        if (!DateTime.TryParse((string)en.Element("ChangedDate") ?? "",
                                null, System.Globalization.DateTimeStyles.RoundtripKind,
                                out d))
                            continue;
                        DateTime cur;
                        if (isRel && (!relDateByPath.TryGetValue(fp, out cur) || d > cur))
                        {
                            relDateByPath[fp] = d;
                            relUserByPath[fp] = (string)en.Element("ChangedBy") ?? "";
                            relReasonByPath[fp] = (string)en.Element("ChangeNote") ?? "";
                        }
                        else if (isObs && (!obsDateByPath.TryGetValue(fp, out cur) || d > cur))
                        {
                            obsDateByPath[fp] = d;
                            obsReasonByPath[fp] = (string)en.Element("ChangeNote") ?? "";
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
                    string relReason;
                    if (!relReasonByPath.TryGetValue(filePath, out relReason))
                        relReason = "";
                    string obsReason;
                    if (!obsReasonByPath.TryGetValue(filePath, out obsReason))
                        obsReason = "";

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
                        ReleaseReason = relReason,
                        SupersededBy = (string)el.Element("SupersededBy") ?? "",
                        ObsoleteReason = obsReason,
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
            // ToLowerInvariant — same culture-safety as SearchFiles.
            string term = (filter ?? "").ToLowerInvariant().Trim();
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
                        !(partNoRaw.ToLowerInvariant().Contains(term) ||
                          descRaw.ToLowerInvariant().Contains(term) ||
                          fileName.ToLowerInvariant().Contains(term)))
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
                        // GUID, not DateTime.Now.Ticks: two machines submitting
                        // within the same tick collided, and ResolveRequest
                        // then resolved the WRONG engineer's request.
                        new XElement("Id", Guid.NewGuid().ToString("N")),
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

        // Resolve a request, but ONLY while it is still Pending — two Masters
        // working from stale popup snapshots used to both resolve (and both
        // ACT ON) the same request. Returns false when the request was already
        // resolved by someone else (or no longer exists), so callers can skip
        // the duplicate action instead of running it twice.
        public static bool ResolveRequest(string id, string status)
        {
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var reqElement = doc.Root.Element("RevisionRequests");
                if (reqElement == null) return false;

                foreach (var el in reqElement.Elements("Request"))
                {
                    if ((string)el.Element("Id") == id)
                    {
                        if ((string)el.Element("Status") != "Pending")
                            return false; // already handled elsewhere
                        SetOrAdd(el, "Status", status);
                        Save(doc);
                        return true;
                    }
                }
                return false;
            }
        }

        // Fresh is-it-still-pending check, read at ACTION time (not popup-load
        // time) so a request another Master just approved/rejected is skipped
        // rather than double-executed.
        public static bool IsRequestPending(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var reqElement = doc.Root.Element("RevisionRequests");
                if (reqElement == null) return false;

                foreach (var el in reqElement.Elements("Request"))
                {
                    if ((string)el.Element("Id") == id)
                        return (string)el.Element("Status") == "Pending";
                }
                return false;
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

        // Status (by-name) + lock (exact path) + history (name-fallback gated)
        // for the active file, in ONE load. Mirrors GetFileStatusByName +
        // GetLockInfo + GetFileHistory exactly, sharing the FindNameRival gate
        // so the combined result can never disagree with the individual calls.
        public static ActiveFileInfo GetActiveFileInfo(string filePath)
        {
            var info = new ActiveFileInfo();
            if (string.IsNullOrEmpty(filePath)) return info;
            string fileName = System.IO.Path.GetFileName(filePath);

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var filesEl = doc.Root.Element("Files");

                // Exact-path record (status + lock both prefer it).
                XElement exact = null;
                foreach (var el in filesEl.Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        exact = el;
                        break;
                    }
                }

                bool rival = FindNameRival(doc, fileName, filePath) != null;
                // The card's "DUPLICATE" sentinel: a living rival AND no exact
                // record of our own (the original is never its own duplicate).
                info.HasDuplicateRival = rival && exact == null;

                if (exact != null)
                {
                    info.Status = (string)exact.Element("Status") ?? "";

                    // Lock — exact-path only, like GetLockInfo (NOT name-fallback).
                    string lockedBy = (string)exact.Element("LockedBy") ?? "";
                    if (!string.IsNullOrEmpty(lockedBy))
                    {
                        DateTime lockedDate;
                        DateTime.TryParse((string)exact.Element("LockedDate"),
                            out lockedDate);
                        info.Lock = new LockInfo
                        {
                            IsLocked = true,
                            LockedBy = lockedBy,
                            LockedDate = lockedDate
                        };
                    }
                }
                else if (!rival)
                {
                    // Status name-fallback (GetFileStatusByName) — but only when
                    // no living rival owns the name.
                    foreach (var el in filesEl.Elements("File"))
                    {
                        if (string.Equals((string)el.Element("FileName"), fileName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            info.Status = (string)el.Element("Status") ?? "";
                            break;
                        }
                    }
                }

                // History — same name-fallback gate as GetFileHistory.
                foreach (var el in doc.Root.Element("RevisionHistory").Elements("Entry"))
                {
                    string entryPath = (string)el.Element("FilePath") ?? "";
                    bool match = string.Equals(entryPath, filePath,
                                     StringComparison.OrdinalIgnoreCase)
                              || (!rival && string.Equals(
                                     System.IO.Path.GetFileName(entryPath),
                                     fileName, StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                    info.History.Add(new HistoryEntry
                    {
                        Status      = (string)el.Element("Status")     ?? "",
                        ChangedBy   = (string)el.Element("ChangedBy")  ?? "",
                        ChangedDate = (string)el.Element("ChangedDate")?? "",
                        ChangeNote  = (string)el.Element("ChangeNote") ?? ""
                    });
                }
                info.History.Reverse(); // most recent first, like GetFileHistory
            }
            return info;
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
                // Only fall back to a FILENAME match for history entries when NO
                // living WIP rival owns this name — otherwise an unrelated same-named
                // file's timeline could be silently re-pointed. Same FindNameRival
                // guard the codebase uses everywhere it matches history by basename
                // (GetFileHistory / GetFileStatusByName / RemoveFileRecord).
                bool nameRival = FindNameRival(doc, oldName, oldPath) != null;
                bool changed = false;
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (!string.Equals((string)el.Element("FilePath"), oldPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    // SetOrAdd, not .Element(x).Value: a legacy record with a
                    // FilePath but no FileName element would NRE here (audit
                    // M11 — same class fixed across the file).
                    SetOrAdd(el, "FilePath", newPath);
                    SetOrAdd(el, "FileName", newName);
                    changed = true;
                }
                foreach (var en in doc.Root.Element("RevisionHistory")
                                          .Elements("Entry"))
                {
                    string ep = (string)en.Element("FilePath") ?? "";
                    if (string.Equals(ep, oldPath,
                            StringComparison.OrdinalIgnoreCase) ||
                        (!nameRival &&
                         string.Equals(System.IO.Path.GetFileName(ep), oldName,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        SetOrAdd(en, "FilePath", newPath); // legacy-safe (audit M11)
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
                                SupersededBy      = (string)mel.Element("SupersededBy") ?? "",
                                ModifiedBy        = (string)mel.Element("ModifiedBy") ?? "",
                                ModifiedDate      = ParseRoundtrip((string)mel.Element("ModifiedDate")),
                                LockedBy          = (string)mel.Element("LockedBy") ?? "",
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
                            SupersededBy   = (string)el.Element("SupersededBy") ?? "",
                            ModifiedBy     = (string)el.Element("ModifiedBy") ?? "",
                            ModifiedDate   = ParseRoundtrip((string)el.Element("ModifiedDate")),
                            LockedBy       = (string)el.Element("LockedBy") ?? "",
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
        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string(name.Select(
                c => System.Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        // One drawing record, snapshotted into a DrawingIndex.
        public sealed class DrawingRec
        {
            public string FilePath;
            public string FileName;
            public string FileBase;          // filename without extension
            public string ReferencedModel;
            public string ReferencedConfigs;
        }

        // An in-memory snapshot of EVERY tracked drawing, read with a SINGLE
        // vault.xml load. The per-config search card needs the drawings for
        // each model+config; calling GetDrawingsForConfig per card was up to
        // ~50 full SMB loads per search (audit M3). RunSearch now builds ONE
        // DrawingIndex and resolves all cards against it in memory.
        public sealed class DrawingIndex
        {
            private readonly List<DrawingRec> _drawings;
            internal DrawingIndex(List<DrawingRec> drawings) { _drawings = drawings; }

            // The drawings that document a specific configuration of a model —
            // identical match logic to GetDrawingsForConfig, run in memory.
            public List<string> DrawingsForConfig(string modelFilePath,
                string configName)
            {
                var result = new List<string>();
                if (string.IsNullOrEmpty(modelFilePath)) return result;
                string modelName = System.IO.Path.GetFileName(modelFilePath);
                string modelBase =
                    System.IO.Path.GetFileNameWithoutExtension(modelFilePath);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var d in _drawings)
                {
                    if (string.IsNullOrEmpty(d.FilePath) ||
                        seen.Contains(d.FilePath)) continue;
                    if (DrawingMatchesConfig(d, modelFilePath, modelName,
                            modelBase, configName))
                    {
                        result.Add(d.FilePath);
                        seen.Add(d.FilePath);
                    }
                }
                return result;
            }
        }

        // Build the drawing snapshot in one load. Cheap to reuse across many
        // lookups in a single operation (search card expansion, bulk scans).
        public static DrawingIndex BuildDrawingIndex()
        {
            var list = new List<DrawingRec>();
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn = (string)el.Element("FileName") ?? "";
                    if (!System.IO.Path.GetExtension(fn)
                            .Equals(".slddrw", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string fp = (string)el.Element("FilePath") ?? "";
                    if (string.IsNullOrEmpty(fp)) continue;
                    list.Add(new DrawingRec
                    {
                        FilePath          = fp,
                        FileName          = fn,
                        FileBase          = System.IO.Path
                                              .GetFileNameWithoutExtension(fn),
                        ReferencedModel   = (string)el.Element("ReferencedModel")   ?? "",
                        ReferencedConfigs = (string)el.Element("ReferencedConfigs") ?? ""
                    });
                }
            }
            return new DrawingIndex(list);
        }

        // The single source of truth for "does this drawing document this
        // model+config" — used by BOTH GetDrawingsForConfig (one DB load) and
        // DrawingIndex (in-memory), so the two can never disagree.
        private static bool DrawingMatchesConfig(DrawingRec d,
            string modelFilePath, string modelName, string modelBase,
            string configName)
        {
            string refModel   = d.ReferencedModel ?? "";
            string refConfigs = d.ReferencedConfigs ?? "";

            if (!string.IsNullOrEmpty(refModel) &&
                (string.Equals(refModel, modelFilePath,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(System.IO.Path.GetFileName(refModel),
                     modelName, StringComparison.OrdinalIgnoreCase)))
            {
                // Drawing references our model. Check config coverage.
                bool coversAll  = string.IsNullOrEmpty(refConfigs);
                bool coversThis = !coversAll && refConfigs
                    .Split(',')
                    .Select(c => c.Trim())
                    .Any(c => string.Equals(c, configName,
                                  StringComparison.OrdinalIgnoreCase) ||
                              // Legacy records (saved before the flat-pattern
                              // filter) may store the raw "{config}SM-FLAT-
                              // PATTERN" name; tolerate it so the drawing still
                              // resolves (self-heals on the next save).
                              (c.EndsWith("SM-FLAT-PATTERN",
                                  StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(c.Substring(0, c.Length -
                                  "SM-FLAT-PATTERN".Length), configName,
                                  StringComparison.OrdinalIgnoreCase)));
                return coversAll || coversThis;
            }

            // Basename fallback: drawing named after the model (shared) or
            // after the config PartNo (config-specific) — pre-reference era.
            bool sharedMatch = string.Equals(d.FileBase, modelBase,
                                  StringComparison.OrdinalIgnoreCase);
            // Config-specific drawings are saved with a filename-SAFE version of
            // the config name (illegal chars → '_'), so match the raw config
            // name OR its sanitised form — otherwise a PartNo containing a
            // slash/quote/etc. would never resolve its drawing here.
            bool configMatch = !string.IsNullOrEmpty(configName) &&
                (string.Equals(d.FileBase, configName,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(d.FileBase, SanitizeFileName(configName),
                     StringComparison.OrdinalIgnoreCase));
            return sharedMatch || configMatch;
        }

        public static List<string> GetDrawingsForConfig(string modelFilePath, string configName)
        {
            if (string.IsNullOrEmpty(modelFilePath)) return new List<string>();
            // One DB load (BuildDrawingIndex), then the shared in-memory matcher
            // — same single-load cost callers had before.
            return BuildDrawingIndex().DrawingsForConfig(modelFilePath, configName);
        }

        // BATCHED drawing-export lookup for the dashboard's batch-print: for the
        // given file paths, return the (DrawingNo, Revision) of every config with
        // a non-empty DrawingNo, in ONE vault.xml load (no per-file round-trip).
        // A DRAWING path (no DrawingNo of its own) resolves to the MODEL it
        // references and yields the model's config DrawingNos. Deduped by
        // DrawingNo+Revision (case-insensitive). READ-ONLY (never writes/purges).
        // LIMITATION: a LEGACY record saved before the <Configurations> block
        // existed has no per-config DrawingNo, so it yields no ref and the batch
        // print reports it as "no current PDF (skipped)" even if a PDF is on
        // disk. Self-healing — any save / New Revision by the current add-in
        // writes <Configurations> (UpsertFile), permanently fixing the record;
        // the PR deliberately avoids opening the doc to recover it.
        public static List<DrawingExportRef> GetDrawingExportRefs(
            IEnumerable<string> filePaths)
        {
            var result = new List<DrawingExportRef>();
            if (filePaths == null) return result;
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in filePaths)
                if (!string.IsNullOrEmpty(p)) want.Add(p);
            if (want.Count == 0) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var filesEl = doc.Root.Element("Files");
                if (filesEl == null) return result;

                // Index every File element by FilePath (for ReferencedModel
                // resolution of drawing rows).
                var byPath = new Dictionary<string, XElement>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var el in filesEl.Elements("File"))
                {
                    string fp = (string)el.Element("FilePath") ?? "";
                    if (fp.Length > 0 && !byPath.ContainsKey(fp)) byPath[fp] = el;
                }

                foreach (string path in want)
                {
                    XElement el;
                    if (!byPath.TryGetValue(path, out el)) continue;

                    XElement modelEl = el;
                    // A drawing has no DrawingNo of its own — the PDF is named by
                    // the MODEL's DrawingNo, so resolve via ReferencedModel.
                    if (path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
                    {
                        string refModel = (string)el.Element("ReferencedModel") ?? "";
                        XElement m;
                        if (string.IsNullOrEmpty(refModel) ||
                            !byPath.TryGetValue(refModel, out m))
                            continue; // can't resolve the model → no DrawingNo
                        modelEl = m;
                    }

                    string pn  = (string)modelEl.Element("PartNumber")  ?? "";
                    string dsc = (string)modelEl.Element("Description") ?? "";
                    string rev = (string)modelEl.Element("Revision")    ?? "";
                    foreach (var cfg in ReadConfigs(modelEl, pn, dsc, rev))
                    {
                        if (string.IsNullOrWhiteSpace(cfg.DrawingNo)) continue;
                        string key = cfg.DrawingNo.ToLowerInvariant() + "|" +
                            (cfg.Revision ?? "").ToLowerInvariant();
                        if (!seen.Add(key)) continue;
                        result.Add(new DrawingExportRef
                        {
                            DrawingNo = cfg.DrawingNo,
                            Revision  = cfg.Revision ?? ""
                        });
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

        // ── As-Released Baselines ─────────────────────────────────────────
        // A point-in-time snapshot of the exact resolved child file set (and
        // their revisions) an assembly was released against, stored under a
        // lazily-created <Baselines> section. Append-only HISTORY: every
        // release adds a <Baseline> so the whole release lineage survives; a
        // re-release of the SAME assembly+rev+config replaces just that one
        // entry (so it never duplicates). Each component's PartNo/Revision/
        // Status is read HERE, from this single load — at release time every
        // tracked child is Released, so its record carries the released rev.
        // This is a user-initiated mutation (part of Release), so it SAVES
        // even in degraded-lock mode, exactly like SetFileStatus/UpsertFile.
        public static void SaveAssemblyBaseline(string asmPath, string asmName,
            string partNo, string rev, string config, string user,
            List<BaselineComponent> components, string reason = null)
        {
            if (string.IsNullOrEmpty(asmPath)) return;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var root = doc.Root;

                var baselines = root.Element("Baselines");
                if (baselines == null)
                {
                    baselines = new XElement("Baselines");
                    root.Add(baselines);
                }

                // Replace any prior baseline for the SAME release identity
                // (assembly path + revision + config); otherwise this is a new
                // historical entry kept alongside every earlier release.
                baselines.Elements("Baseline")
                    .Where(b =>
                        string.Equals((string)b.Attribute("AssemblyPath"), asmPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)b.Attribute("Revision") ?? "",
                            rev ?? "", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)b.Attribute("Config") ?? "",
                            config ?? "", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                    .ForEach(b => b.Remove());

                var bEl = new XElement("Baseline",
                    new XAttribute("AssemblyPath", asmPath),
                    new XAttribute("AssemblyName", asmName ?? ""),
                    new XAttribute("PartNo", partNo ?? ""),
                    new XAttribute("Revision", rev ?? ""),
                    new XAttribute("Config", config ?? ""),
                    new XAttribute("ReleasedBy", user ?? ""),
                    new XAttribute("ReleasedDate",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    new XAttribute("Reason", reason ?? ""));

                var files = root.Element("Files");
                foreach (var comp in components ?? new List<BaselineComponent>())
                {
                    string cPartNo = comp.PartNo ?? "";
                    string cRev    = comp.Revision ?? "";
                    string cStatus = comp.Status ?? "";
                    string cDesc   = comp.Description ?? "";

                    // Enrich the un-filled fields (PartNo/Rev/Status/Description)
                    // from the child's File record (same load, no extra lock).
                    if ((string.IsNullOrEmpty(cPartNo) || string.IsNullOrEmpty(cRev)
                         || string.IsNullOrEmpty(cStatus) || string.IsNullOrEmpty(cDesc))
                        && files != null)
                    {
                        // Match by FilePath first; fall back to FileName (the
                        // vault enforces vault-wide unique filenames, so this is
                        // unambiguous) so a path-normalisation difference between
                        // the component path and the stored record path can't
                        // leave a tracked child stamped "Untracked" with blanks.
                        string compName = System.IO.Path.GetFileName(comp.Path ?? "");
                        var fr = files.Elements("File").FirstOrDefault(f =>
                                     string.Equals((string)f.Element("FilePath"), comp.Path,
                                         StringComparison.OrdinalIgnoreCase))
                              ?? files.Elements("File").FirstOrDefault(f =>
                                     string.Equals((string)f.Element("FileName"), compName,
                                         StringComparison.OrdinalIgnoreCase));
                        if (fr != null)
                        {
                            // Prefer the component's CONFIG-SPECIFIC identity: a
                            // multi-config child has a DIFFERENT PartNo/Revision/
                            // Description per config, and the file-level
                            // PartNumber/Revision/Description is only the PRIMARY
                            // config (so without this every config row showed the
                            // same Part No). Match the child's <Configurations> by
                            // the referenced config; fall back to the file-level
                            // value when there's no config block / no match / the
                            // config value is blank.
                            XElement cfgEl = null;
                            string compCfg = (comp.Config ?? "").Trim();
                            if (compCfg.Length > 0)
                            {
                                var cfgsEl = fr.Element("Configurations");
                                if (cfgsEl != null)
                                    cfgEl = cfgsEl.Elements("Config").FirstOrDefault(ce =>
                                        string.Equals(((string)ce.Element("Name") ?? "").Trim(),
                                            compCfg, StringComparison.OrdinalIgnoreCase));
                            }
                            if (string.IsNullOrEmpty(cPartNo))
                            {
                                string v = cfgEl != null ? ((string)cfgEl.Element("PartNo") ?? "") : "";
                                cPartNo = string.IsNullOrEmpty(v) ? ((string)fr.Element("PartNumber") ?? "") : v;
                            }
                            if (string.IsNullOrEmpty(cRev))
                            {
                                string v = cfgEl != null ? ((string)cfgEl.Element("Revision") ?? "") : "";
                                cRev = string.IsNullOrEmpty(v) ? ((string)fr.Element("Revision") ?? "") : v;
                            }
                            if (string.IsNullOrEmpty(cStatus))
                                cStatus = (string)fr.Element("Status") ?? ""; // status is file-level
                            if (string.IsNullOrEmpty(cDesc))
                            {
                                string v = cfgEl != null ? ((string)cfgEl.Element("Description") ?? "") : "";
                                cDesc = string.IsNullOrEmpty(v) ? ((string)fr.Element("Description") ?? "") : v;
                            }
                        }
                    }

                    bEl.Add(new XElement("Component",
                        new XAttribute("Path", comp.Path ?? ""),
                        new XAttribute("Name", comp.Name ?? ""),
                        new XAttribute("PartNo", cPartNo),
                        new XAttribute("Description", cDesc),
                        new XAttribute("Config", comp.Config ?? ""),
                        new XAttribute("Revision", cRev),
                        new XAttribute("Status",
                            string.IsNullOrEmpty(cStatus) ? "Untracked" : cStatus),
                        new XAttribute("Qty", comp.Qty),
                        new XAttribute("Level", comp.Level),
                        new XAttribute("Weight",
                            comp.Weight.ToString("0.###",
                                System.Globalization.CultureInfo.InvariantCulture))));
                }

                baselines.Add(bEl);
                Save(doc);
            }
        }

        // Returns every captured baseline for an assembly path, MOST RECENT
        // FIRST. Read-only — never writes, never purges. Empty list if the
        // assembly has never been released (or vault.xml has no <Baselines>).
        public static List<AssemblyBaseline> GetBaselines(string asmPath)
        {
            var result = new List<AssemblyBaseline>();
            if (string.IsNullOrEmpty(asmPath)) return result;
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var baselines = doc.Root.Element("Baselines");
                if (baselines == null) return result;

                foreach (var b in baselines.Elements("Baseline"))
                {
                    if (!string.Equals((string)b.Attribute("AssemblyPath"), asmPath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ab = new AssemblyBaseline
                    {
                        AssemblyPath = (string)b.Attribute("AssemblyPath") ?? "",
                        AssemblyName = (string)b.Attribute("AssemblyName") ?? "",
                        PartNo       = (string)b.Attribute("PartNo")       ?? "",
                        Revision     = (string)b.Attribute("Revision")     ?? "",
                        Config       = (string)b.Attribute("Config")       ?? "",
                        ReleasedBy   = (string)b.Attribute("ReleasedBy")   ?? "",
                        ReleasedDate = (string)b.Attribute("ReleasedDate") ?? "",
                        Reason       = (string)b.Attribute("Reason")       ?? ""
                    };
                    foreach (var c in b.Elements("Component"))
                    {
                        int qty;
                        int.TryParse((string)c.Attribute("Qty"), out qty);
                        int level; // absent on pre-indent baselines → 0 (flat)
                        int.TryParse((string)c.Attribute("Level"), out level);
                        double weight; // absent on older baselines → 0 (blank)
                        double.TryParse((string)c.Attribute("Weight"),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out weight);
                        ab.Components.Add(new BaselineComponent
                        {
                            Path        = (string)c.Attribute("Path")        ?? "",
                            Name        = (string)c.Attribute("Name")        ?? "",
                            PartNo      = (string)c.Attribute("PartNo")      ?? "",
                            Description = (string)c.Attribute("Description") ?? "",
                            Config      = (string)c.Attribute("Config")      ?? "",
                            Revision    = (string)c.Attribute("Revision")    ?? "",
                            Status      = (string)c.Attribute("Status")      ?? "",
                            Qty         = qty,
                            Level       = level,
                            Weight      = weight
                        });
                    }
                    result.Add(ab);
                }
            }
            // ReleasedDate is "yyyy-MM-dd HH:mm:ss" → ordinal compare IS
            // chronological. Newest release first.
            result.Sort((a, b) =>
                string.Compare(b.ReleasedDate, a.ReleasedDate,
                    StringComparison.Ordinal));
            return result;
        }

        // WHERE-USED CONFIG FILTER source (so Where Used works by PART NUMBER, not
        // just file). A multi-config part is ONE file used as a specific CONFIG
        // (= Part No) in each assembly; the dependency walk returns paths with no
        // per-instance config. Two STORED sources record it, neither needing a file
        // open:
        //   1. <Components> on each assembly's File record — the CURRENT direct
        //      children + config, captured at EVERY save (WIP and Released alike).
        //      Authoritative.
        //   2. <Baselines> — the as-released snapshot; FALLBACK only for assemblies
        //      with no <Components> block yet (records saved before this shipped).
        // Classifies each parent against a target configuration:
        //   ParentsUsingTarget         = it uses the child under targetConfig.
        //   ParentsWithDifferentConfig = it uses the child under a KNOWN (non-empty)
        //     config that is NOT the target — provably a different one.
        // A parent in NEITHER set has no data for this child — the where-used filter
        // KEEPS it (unverified) rather than hiding a possible real usage.
        //
        // Matches on the component CONFIG only (NOT PartNo): a baseline's PartNo is
        // enriched from the child's PRIMARY record (same for every config); the
        // referenced Config tells configs apart. Config == Part No by convention, so
        // the target IS the config name. Keys are normalised FULL paths so they
        // compare cleanly with the disk-walk parents.
        public class ChildConfigUsage
        {
            public HashSet<string> ParentsUsingTarget =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ParentsWithDifferentConfig =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static ChildConfigUsage GetChildConfigUsage(string childFilePath,
            string targetConfig)
        {
            var usage = new ChildConfigUsage();
            if (string.IsNullOrEmpty(childFilePath) ||
                string.IsNullOrEmpty(targetConfig)) return usage;
            string childFull;
            try { childFull = Path.GetFullPath(childFilePath); }
            catch { childFull = childFilePath; }
            string childName;
            try { childName = Path.GetFileName(childFilePath); }
            catch { childName = childFilePath ?? ""; }   // bad path char → degrade, don't abort
            string target = targetConfig.Trim();

            // Classify one parent from a set of component elements (each carrying a
            // "Path" + "Config" attribute) into the usage sets.
            Action<string, IEnumerable<XElement>> classify = (apFull, comps) =>
            {
                bool usesTarget = false, knownOther = false;
                foreach (var c in comps)
                {
                    string cp = (string)c.Attribute("Path") ?? "";
                    if (cp.Length == 0) continue;
                    string cpFull;
                    try { cpFull = Path.GetFullPath(cp); } catch { cpFull = cp; }
                    bool match = string.Equals(cpFull, childFull,
                                     StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Path.GetFileName(cp), childName,
                                     StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    string cfg = ((string)c.Attribute("Config") ?? "").Trim();
                    if (cfg.Length == 0) continue;   // no config captured → can't classify
                    if (string.Equals(cfg, target, StringComparison.OrdinalIgnoreCase))
                        usesTarget = true;
                    else
                        knownOther = true;
                }
                // Using the target in ANY instance wins, even if it ALSO uses others.
                if (usesTarget) usage.ParentsUsingTarget.Add(apFull);
                else if (knownOther) usage.ParentsWithDifferentConfig.Add(apFull);
            };

            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();

                // PASS 1 — CURRENT composition from <Components> (all statuses, WIP
                // included). Authoritative; covers everything saved since shipping.
                var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = doc.Root.Element("Files");
                if (files != null)
                    foreach (var fileEl in files.Elements("File"))
                    {
                        var compsEl = fileEl.Element("Components");
                        if (compsEl == null) continue;
                        var comps = compsEl.Elements("Comp").ToList();
                        if (comps.Count == 0) continue;  // empty block (hand-edited) → let baseline handle
                        string ap = (string)fileEl.Element("FilePath") ?? "";
                        if (ap.Length == 0) continue;
                        string apFull;
                        try { apFull = Path.GetFullPath(ap); } catch { apFull = ap; }
                        covered.Add(apFull); // has current data — baseline must not override
                        classify(apFull, comps);
                    }

                // PASS 2 — BASELINE fallback, only for assemblies with no
                // <Components> block yet (old records). Latest baseline per assembly.
                var baselines = doc.Root.Element("Baselines");
                if (baselines != null)
                {
                    var latest = new Dictionary<string, XElement>(
                        StringComparer.OrdinalIgnoreCase);
                    var latestDate = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var b in baselines.Elements("Baseline"))
                    {
                        string ap = (string)b.Attribute("AssemblyPath") ?? "";
                        if (ap.Length == 0) continue;
                        string date = (string)b.Attribute("ReleasedDate") ?? "";
                        string prev;
                        if (!latestDate.TryGetValue(ap, out prev) ||
                            string.Compare(date, prev, StringComparison.Ordinal) > 0)
                        { latest[ap] = b; latestDate[ap] = date; }
                    }
                    foreach (var kv in latest)
                    {
                        string apFull;
                        try { apFull = Path.GetFullPath(kv.Key); } catch { apFull = kv.Key; }
                        if (covered.Contains(apFull)) continue; // current data wins
                        // DIRECT (Level 0) baseline components only — mirror the
                        // top-level <Components> snapshot, so a child used by a
                        // DEEPER sub-assembly can't false-classify this direct parent.
                        classify(apFull, kv.Value.Elements("Component").Where(c =>
                        {
                            int lv; int.TryParse((string)c.Attribute("Level"), out lv);
                            return lv == 0;
                        }));
                    }
                }
            }
            return usage;
        }

        // Direct-child instance counts (filename → summed Qty) for an assembly's
        // CURRENT <Components> snapshot, or null when it has none (caller falls back
        // to the baseline). Powers Where Used Qty for WIP assemblies too.
        // Per-direct-child quantities for an assembly's CURRENT <Components>
        // snapshot. ByName = filename → total qty across configs (file-level);
        // ByNameConfig = "filename|config" → qty for that SPECIFIC config, so Where
        // Used can show a per-config quantity (MAX.03 ×2 vs MAX.02 ×1) instead of
        // the file total. null when the assembly has no snapshot (caller falls back
        // to the baseline). Keys are case-insensitive; "|" is a safe separator
        // (illegal in Windows filenames/configs).
        public class ComponentQtyMap
        {
            public Dictionary<string, int> ByName =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> ByNameConfig =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public static ComponentQtyMap GetComponentQtys(string asmPath)
        {
            if (string.IsNullOrEmpty(asmPath)) return null;
            string target = asmPath.Trim();
            lock (_lock) using (AcquireProcessLock())
            {
                var doc = LoadOrCreate();
                var files = doc.Root.Element("Files");
                if (files == null) return null;
                foreach (var fileEl in files.Elements("File"))
                {
                    if (!string.Equals(((string)fileEl.Element("FilePath") ?? "").Trim(),
                            target, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var compsEl = fileEl.Element("Components");
                    if (compsEl == null) return null;   // no snapshot → caller uses baseline
                    var m = new ComponentQtyMap();
                    foreach (var c in compsEl.Elements("Comp"))
                    {
                        string cn = Path.GetFileName((string)c.Attribute("Path") ?? "");
                        if (string.IsNullOrEmpty(cn)) continue;
                        int q; int.TryParse((string)c.Attribute("Qty"), out q);
                        if (q < 0) q = 0;
                        int cur; m.ByName.TryGetValue(cn, out cur);
                        m.ByName[cn] = cur + q;
                        string cfg = ((string)c.Attribute("Config") ?? "").Trim();
                        if (cfg.Length > 0)
                        {
                            string key = cn + "|" + cfg;
                            int curc; m.ByNameConfig.TryGetValue(key, out curc);
                            m.ByNameConfig[key] = curc + q;
                        }
                    }
                    // Empty/all-unusable block (hand-edited) → null so the caller
                    // falls back to the baseline, matching GetChildConfigUsage PASS 1.
                    return m.ByName.Count > 0 ? m : null;
                }
            }
            return null;
        }
    }
}