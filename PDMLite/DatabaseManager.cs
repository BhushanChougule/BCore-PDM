using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace PDMLite
{
    public class VaultFile
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string PartNumber { get; set; }
        public string Description { get; set; }
        public string Revision { get; set; }
        public string Status { get; set; }
        public string ModifiedBy { get; set; }
        public DateTime ModifiedDate { get; set; }
        public bool HasBrokenRefs { get; set; }
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
        private const string WipRoot = @"N:\PDM-SolidWorks\WIP";
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
                doc.Save(DataFile);
                return doc;
            }

            return XDocument.Load(DataFile);
        }

        private static void Save(XDocument doc)
        {
            doc.Save(DataFile);
        }

        public static void Initialize()
        {
            lock (_lock) { LoadOrCreate(); }

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
        // File operations
        // ════════════════════════════════════════════════════════════════
        public static void UpsertFile(VaultFile f)
        {
            bool wasCreate;
            lock (_lock)
            {
                var doc = LoadOrCreate();
                var files = doc.Root.Element("Files");

                XElement existing = null;
                foreach (var el in files.Elements("File"))
                {
                    if ((string)el.Element("FilePath") == f.FilePath)
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
                if (wasCreate)
                    PurgeHistoryFor(doc, f.FilePath, f.FileName);

                if (existing != null)
                {
                    existing.Element("FileName").Value = f.FileName;
                    existing.Element("PartNumber").Value = f.PartNumber ?? "";
                    existing.Element("Description").Value = f.Description ?? "";
                    existing.Element("ModifiedBy").Value = f.ModifiedBy ?? "";
                    existing.Element("ModifiedDate").Value = f.ModifiedDate.ToString("o");

                    if (!string.IsNullOrEmpty(f.Status))
                        existing.Element("Status").Value = f.Status;
                }
                else
                {
                    files.Add(new XElement("File",
                        new XElement("FilePath", f.FilePath),
                        new XElement("FileName", f.FileName),
                        new XElement("PartNumber", f.PartNumber ?? ""),
                        new XElement("Description", f.Description ?? ""),
                        new XElement("Status", f.Status ?? "WIP"),
                        new XElement("LockedBy", ""),
                        new XElement("LockedDate", ""),
                        new XElement("ModifiedBy", f.ModifiedBy ?? ""),
                        new XElement("ModifiedDate", f.ModifiedDate.ToString("o")),
                        new XElement("HasBrokenRefs", "false")
                    ));
                }

                Save(doc);
            }

            // Log outside the DB lock — AuditLogger holds its own file lock.
            AuditLogger.Log(wasCreate ? "Create" : "Save",
                f.ModifiedBy ?? "", f.FileName, f.PartNumber ?? "");
        }

        public static string GetFileStatus(string filePath)
        {
            lock (_lock)
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
            lock (_lock)
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
            lock (_lock)
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

            lock (_lock)
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
            lock (_lock)
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
            lock (_lock)
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
            lock (_lock)
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
            lock (_lock)
            {
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

            lock (_lock)
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

                if (stale.Count > 0)
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
            lock (_lock)
            {
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
            lock (_lock)
            {
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
            lock (_lock)
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
            lock (_lock)
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

            lock (_lock)
            {
                var doc = LoadOrCreate();

                // Network-down guard for the auto-purge (see method summary).
                bool vaultOnline = false;
                try { vaultOnline = Directory.Exists(WipRoot); } catch { }

                var toPurge = new List<XElement>();

                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (string.IsNullOrEmpty(status)) continue;

                    string fileName = (string)el.Element("FileName") ?? "";
                    string partNoRaw = (string)el.Element("PartNumber") ?? "";
                    string descRaw = (string)el.Element("Description") ?? "";

                    if (!(partNoRaw.ToLower().Contains(term) ||
                          descRaw.ToLower().Contains(term) ||
                          fileName.ToLower().Contains(term)))
                        continue;

                    // Orphan check: file gone on disk → never show it, and purge
                    // its record when we can prove the share is online.
                    string filePath = (string)el.Element("FilePath") ?? "";
                    bool missing = string.IsNullOrEmpty(filePath) ||
                                   !File.Exists(filePath);
                    if (missing)
                    {
                        if (vaultOnline)
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
                        FilePath = filePath,
                        FileName = fileName,
                        PartNumber = partNoRaw,
                        Description = descRaw,
                        Status = status,
                        Revision = (string)el.Element("Revision") ?? ""
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

            lock (_lock)
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

                    string elPart = ((string)el.Element("PartNumber") ?? "").Trim();
                    if (elPart.Equals(target, StringComparison.OrdinalIgnoreCase))
                        return elFileName.Length > 0 ? elFileName : elPath;
                }
            }

            return null;
        }

        // ════════════════════════════════════════════════════════════════
        private static void AddRequest(string requestType, string filePath,
            string requestedBy, string note)
        {
            lock (_lock)
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
            lock (_lock)
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
            lock (_lock)
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

            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root
                    .Element("RevisionHistory")
                    .Elements("Entry"))
                {
                    string entryPath = (string)el.Element("FilePath") ?? "";
                    // Match by exact path first; fall back to filename so RELEASED
                    // folder copies share history with their original WIP path
                    bool match = string.Equals(entryPath, filePath,
                                     StringComparison.OrdinalIgnoreCase)
                              || string.Equals(
                                     System.IO.Path.GetFileName(entryPath),
                                     fileName,
                                     StringComparison.OrdinalIgnoreCase);
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
            lock (_lock)
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
        // Falls back to filename match so RELEASED folder copies reflect correct status.
        public static string GetFileStatusByName(string filePath)
        {
            string fileName = System.IO.Path.GetFileName(filePath);
            lock (_lock)
            {
                var doc = LoadOrCreate();
                // Exact path first
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FilePath"), filePath,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Status") ?? "";
                }
                // Fallback: same filename, any path
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if (string.Equals((string)el.Element("FileName"), fileName,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)el.Element("Status") ?? "";
                }
            }
            return "";
        }

        // Returns the part/assembly VaultFile that shares the same base filename
        // as the given drawing (PartNo, Description, Status etc. live on the
        // model, not the drawing). e.g. "TEST 1.SLDDRW" → finds "TEST 1.SLDPRT"
        // or "TEST 1.SLDASM" and returns its record. Returns null if not found.
        // Used by the merged search card to populate the model's details when a
        // search matched the drawing.
        public static VaultFile GetModelForDrawing(string drawingFilePath)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(
                drawingFilePath);
            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn = (string)el.Element("FileName") ?? "";
                    string fnBase = System.IO.Path.GetFileNameWithoutExtension(fn);
                    string fnExt = System.IO.Path.GetExtension(fn).ToLower();
                    if (string.Equals(fnBase, baseName,
                            StringComparison.OrdinalIgnoreCase) &&
                        (fnExt == ".sldprt" || fnExt == ".sldasm"))
                    {
                        return new VaultFile
                        {
                            FilePath = (string)el.Element("FilePath") ?? "",
                            FileName = fn,
                            PartNumber = (string)el.Element("PartNumber") ?? "",
                            Description = (string)el.Element("Description") ?? "",
                            Revision = (string)el.Element("Revision") ?? "",
                            Status = (string)el.Element("Status") ?? ""
                        };
                    }
                }
            }
            return null;
        }

        // Returns the WIP path of the .slddrw that shares the same base filename
        // as the given part/assembly (the drawing that documents it), or null if
        // no drawing is tracked. Used by the merged search card to wire the
        // "Open Drawing" button when a search matched only the model.
        public static string GetDrawingPathForModel(string modelFilePath)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(
                modelFilePath);
            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string fn = (string)el.Element("FileName") ?? "";
                    string fnBase = System.IO.Path.GetFileNameWithoutExtension(fn);
                    string fnExt = System.IO.Path.GetExtension(fn).ToLower();
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
    }
}