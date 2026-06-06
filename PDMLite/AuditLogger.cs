using System;
using System.IO;
using System.Threading;

namespace PDMLite
{
    // Append-only audit trail for every meaningful vault event (file create,
    // save, lock, unlock, release, new revision, rollback, requests, removals,
    // and automatic orphan purges). Written as CSV to a file SEPARATE from
    // vault.xml so the database stays lean and the log can grow independently
    // and be opened directly in Excel.
    //
    // Design points:
    //  - Append-only (never reads/rewrites the whole file) → stays fast no
    //    matter how large it gets, even at 50k+ parts over the product lifetime.
    //  - Cross-process safe: opens with an exclusive write handle and retries on
    //    a sharing violation (two machines writing at the same instant).
    //  - Never fatal: every failure is swallowed so logging can never block or
    //    break a save / release / any other workflow.
    internal static class AuditLogger
    {
        private const string LogFile = @"N:\PDM-SolidWorks\VAULT\audit.csv";
        private const string Header =
            "Timestamp,User,Action,FileName,PartNo,Revision,Note";

        // Guards same-process threads; cross-process collisions are handled by
        // the exclusive-open retry loop in AppendWithRetry.
        private static readonly object _lock = new object();

        public static void Log(string action, string user, string fileName,
            string partNo = "", string revision = "", string note = "")
        {
            try
            {
                string line = string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Csv(user), Csv(action), Csv(fileName),
                    Csv(partNo), Csv(revision), Csv(note));

                lock (_lock)
                {
                    EnsureFile();
                    AppendWithRetry(line + Environment.NewLine);
                }
            }
            catch { } // logging must never break a workflow
        }

        private static void EnsureFile()
        {
            if (File.Exists(LogFile)) return;
            string dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            AppendWithRetry(Header + Environment.NewLine);
        }

        // Open for append with an exclusive write lock (FileShare.Read lets
        // someone view the log, but no other writer in). If another machine
        // holds the write lock, wait briefly and retry — 5 × 100ms easily
        // covers a 10-engineer team. If it still fails (e.g. the file is open
        // in Excel with a write lock), the entry is dropped silently rather
        // than blocking the user.
        private static void AppendWithRetry(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(LogFile, FileMode.Append,
                        FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.Write(text);
                    }
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        // Minimal RFC-4180 CSV escaping: quote fields containing a comma,
        // quote, or newline; double any embedded quotes.
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
