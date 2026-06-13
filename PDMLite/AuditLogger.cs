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

        // Local per-machine SPILL file for entries that could not reach
        // audit.csv (the design explicitly invites a Master to open the log
        // in Excel, whose write lock can hold for HOURS — the old 5×100ms
        // retry then dropped the event forever). Spilled lines are flushed
        // back to audit.csv at the start of the next successful Log call, so
        // events are delayed, never lost. Local %LOCALAPPDATA% on purpose:
        // when the NETWORK is the problem, a network-side spill would fail
        // for the same reason. Flushed lines keep their original timestamps,
        // so they may land slightly out of file order — the Audit Report
        // sorts by Timestamp, so the VIEW stays chronological.
        private static readonly string SpillFile = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "BCorePDM", "audit.pending.csv");

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
                    FlushSpill();
                    if (!AppendWithRetry(line + Environment.NewLine))
                        Spill(line + Environment.NewLine);
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

        // Append entries that previously failed to reach audit.csv. Only when
        // the whole spill content lands is the spill file deleted; on any
        // failure it simply stays for the next attempt. Caller holds _lock.
        private static void FlushSpill()
        {
            try
            {
                if (!File.Exists(SpillFile)) return;
                string pending = File.ReadAllText(SpillFile);
                if (pending.Length == 0 || AppendWithRetry(pending))
                    File.Delete(SpillFile);
            }
            catch { } // spill stays; retried on the next Log call
        }

        // Save one line locally because audit.csv could not be written.
        private static void Spill(string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(SpillFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(SpillFile, text);
            }
            catch { } // even the spill failed — nothing more we can do
        }

        // Open for append with an exclusive write lock (FileShare.Read lets
        // someone view the log, but no other writer in). If another machine
        // holds the write lock, wait briefly and retry — 5 × 100ms easily
        // covers a 10-engineer team. Returns FALSE when every attempt failed
        // (e.g. the file is open in Excel with a write lock) so the caller
        // can SPILL the entry locally instead of dropping it forever.
        private static bool AppendWithRetry(string text)
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
                    return true;
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
            return false;
        }

        // Minimal RFC-4180 CSV escaping: quote fields containing a comma,
        // quote, or newline; double any embedded quotes. Also neutralises
        // spreadsheet FORMULA INJECTION — the log is designed to be opened
        // directly in Excel, which EXECUTES a field starting with = + - @
        // (a request note of "=HYPERLINK(...)" would run on the Master's
        // machine). Such fields get a leading apostrophe: Excel renders them
        // as text. The apostrophe is visible in raw text viewers — the
        // accepted cost of opening untrusted CSV safely.
        private static string Csv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            char c0 = field[0];
            if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' ||
                c0 == '\t' || c0 == '\r')
                field = "'" + field;
            if (field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
