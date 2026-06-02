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

    public static class DatabaseManager
    {
        private const string VaultFolder = @"N:\PDM-SolidWorks\vault";
        private const string DataFile = @"N:\PDM-SolidWorks\vault\vault.xml";

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
        }

        // ════════════════════════════════════════════════════════════════
        // File operations
        // ════════════════════════════════════════════════════════════════
        public static void UpsertFile(VaultFile f)
        {
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
        }

        public static string GetFileStatus(string filePath)
        {
            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if ((string)el.Element("FilePath") == filePath)
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
                    if ((string)el.Element("FilePath") == filePath)
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
                    if ((string)el.Element("FilePath") == filePath)
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
        // Lock / Unlock
        // ════════════════════════════════════════════════════════════════
        public static void LockFile(string filePath, string lockedBy)
        {
            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    if ((string)el.Element("FilePath") == filePath)
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
                    if ((string)el.Element("FilePath") == filePath)
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
                    if ((string)el.Element("FilePath") == filePath)
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
        private const string ReleasedFolder = @"N:\PDM-SolidWorks\RELEASED";

        public static List<VaultFile> SearchFiles(string searchTerm)
        {
            var results = new List<VaultFile>();
            if (string.IsNullOrWhiteSpace(searchTerm)) return results;

            string term = searchTerm.ToLower().Trim();
            // Deduplicate by filename: vault.xml may have both a source-path entry
            // and a RELEASED-folder entry for the same file after a release.
            var seenFileNames = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                var doc = LoadOrCreate();
                foreach (var el in doc.Root.Element("Files").Elements("File"))
                {
                    string status = (string)el.Element("Status") ?? "";
                    if (status != "Released") continue;

                    string fileName = (string)el.Element("FileName") ?? "";

                    // Skip if we already have a result for this filename
                    if (!seenFileNames.Add(fileName)) continue;

                    string partNo = ((string)el.Element("PartNumber") ?? "").ToLower();
                    string desc = ((string)el.Element("Description") ?? "").ToLower();
                    string fileNameLower = fileName.ToLower();

                    if (partNo.Contains(term) || desc.Contains(term) || fileNameLower.Contains(term))
                    {
                        // Always open Released files from the RELEASED folder,
                        // not from wherever the engineer originally saved the file.
                        string releasedPath = System.IO.Path.Combine(ReleasedFolder, fileName);
                        string returnPath = File.Exists(releasedPath)
                            ? releasedPath
                            : (string)el.Element("FilePath") ?? "";

                        results.Add(new VaultFile
                        {
                            FilePath = returnPath,
                            FileName = fileName,
                            PartNumber = (string)el.Element("PartNumber") ?? "",
                            Description = (string)el.Element("Description") ?? "",
                            Status = status,
                            Revision = (string)el.Element("Revision") ?? ""
                        });
                    }
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
    }
}