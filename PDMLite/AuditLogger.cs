using System;
using System.IO;
using System.Text;
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

        // Append entries that previously failed to reach audit.csv. Reads and
        // truncates under ONE exclusive (FileShare.None) handle so a SECOND
        // SOLIDWORKS process on the same machine+user (the spill is in per-user
        // %LOCALAPPDATA%, which the in-process _lock does NOT serialise across
        // processes) cannot append a line BETWEEN our read and our clear and
        // have it dropped. Read → write to audit.csv → TRUNCATE (not delete):
        // because no other process can hold the file during this window, the
        // truncate removes exactly what we read, never a line appended since.
        // Only truncates when the content actually landed in audit.csv; on any
        // failure the spill is left intact for the next attempt. Caller holds
        // the in-process _lock.
        private static void FlushSpill()
        {
            bool cleared = false;
            try
            {
                if (!File.Exists(SpillFile)) return;
                using (var fs = OpenSpillExclusive(FileMode.Open))
                {
                    if (fs == null) return; // briefly held by another process — next Log
                    if (fs.Length == 0) { cleared = true; } // already empty — tidy up below
                    else
                    {
                        var buf = new byte[fs.Length];
                        int read = 0;
                        while (read < buf.Length)
                        {
                            int n = fs.Read(buf, read, buf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        string pending = Encoding.UTF8.GetString(buf, 0, read);
                        if (pending.Length == 0 || AppendWithRetry(pending))
                        {
                            fs.SetLength(0); // landed — clear under the still-held handle
                            cleared = true;
                        }
                    }
                }
            }
            catch { return; } // spill stays; retried on the next Log call

            // Tidy: remove the now-empty spill so it doesn't linger forever as a
            // 0-byte file. Best-effort + a length RE-CHECK so a line a second
            // same-machine process spilled between our truncate and here is
            // never deleted — if the file is non-empty again we leave it for the
            // next flush (its content is already safely in audit.csv either way).
            if (cleared)
            {
                try
                {
                    if (File.Exists(SpillFile) &&
                        new FileInfo(SpillFile).Length == 0)
                        File.Delete(SpillFile);
                }
                catch { }
            }
        }

        // Save one line locally because audit.csv could not be written. Appends
        // under the same exclusive handle so it can never interleave with
        // another process's FlushSpill read+truncate.
        private static void Spill(string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(SpillFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var fs = OpenSpillExclusive(FileMode.OpenOrCreate))
                {
                    if (fs == null) return; // best-effort — see class summary
                    fs.Seek(0, SeekOrigin.End);
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch { } // even the spill failed — nothing more we can do
        }

        // Exclusive (FileShare.None) open of the spill, briefly retried so a
        // flush/append by a second same-machine SOLIDWORKS process is ridden
        // out rather than colliding. Returns null if the handle can't be got
        // (best-effort: a dropped spill line is the documented worst case).
        private static FileStream OpenSpillExclusive(FileMode mode)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    return new FileStream(SpillFile, mode,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { Thread.Sleep(50); }
            }
            return null;
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
