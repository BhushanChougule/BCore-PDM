using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PDMLite
{
    // Per-user, per-machine UI preferences: saved (named) quick searches,
    // recent search TERMS (query history), and starred favourites. Stored
    // LOCALLY at %LOCALAPPDATA%\BCorePDM\prefs.xml (NOT vault.xml) — these are
    // personal, so they must never contend for the cross-machine vault lock or
    // bloat the shared DB. Mirrors the audit spill's local-folder choice. Every
    // operation is NON-FATAL (swallows IO errors) — a preferences hiccup must
    // never disrupt a workflow.
    // NOTE: the "Recent files" list is NOT here — it's the shared RecentFiles
    // store (recent.txt, populated on every doc activation), which both Quick
    // Access and Advanced Search read, so there is ONE recent list, not two.
    // Recent SEARCHES (the query strings) ARE here: they're personal and don't
    // need cross-instance sharing the way opened files do.
    public static class UserPrefs
    {
        public class SavedSearch { public string Name; public string Term; }

        // How many recent search terms to keep (query history, like the search
        // field of every PDM/browser).
        private const int RecentSearchCap = 10;

        private static readonly object _lock = new object();
        // prefs.xml is per-user but SHARED across every SOLIDWORKS instance that
        // user runs in one session, so the in-process _lock alone can't stop two
        // instances clobbering each other's read-modify-write — a session-local
        // named Mutex serialises the writers cross-process (same discipline as the
        // sibling RecentFiles store's "BCorePDM.RecentFiles" mutex).
        private const string MutexName = "BCorePDM.UserPrefs";

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BCorePDM");
        private static string PrefsPath => Path.Combine(Dir, "prefs.xml");

        private static XDocument Load()
        {
            try { if (File.Exists(PrefsPath)) return XDocument.Load(PrefsPath); }
            catch { }
            return new XDocument(new XElement("Prefs",
                new XElement("SavedSearches"),
                new XElement("RecentSearches"),
                new XElement("Favorites")));
        }

        private static XElement Section(XDocument d, string name)
        {
            var s = d.Root.Element(name);
            if (s == null) { s = new XElement(name); d.Root.Add(s); }
            return s;
        }

        private static void Save(XDocument d)
        {
            string tmp = PrefsPath + "." +
                System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                Directory.CreateDirectory(Dir);
                SweepStaleTemps();   // once per process, before we add a temp
                // ATOMIC write: serialise to a per-process temp then swap it into
                // place, so a concurrent reader in another SOLIDWORKS instance never
                // sees a TRUNCATED prefs.xml. A partial read would XmlException →
                // Load's empty-doc fallback → the next Save persisting that empty doc,
                // wiping ALL saved searches + favourites. File.Replace is atomic on
                // NTFS; the per-process temp name keeps two instances from sharing one
                // temp even if the cross-process mutex wait timed out. Mirrors
                // DatabaseManager's vault.xml temp-then-replace.
                d.Save(tmp);
                if (File.Exists(PrefsPath)) File.Replace(tmp, PrefsPath, null);
                else File.Move(tmp, PrefsPath);
            }
            catch
            {
                // The swap failed — but File.Replace/File.Move (and a failed d.Save to
                // the temp) ALL leave the existing prefs.xml UNCHANGED, so the old data
                // survives. We deliberately do NOT fall back to a truncating in-place
                // d.Save(PrefsPath): that would re-introduce the exact torn-write this
                // method exists to prevent. The lost write is re-attempted on the next
                // mutation (best-effort, non-fatal); just clear the stray temp.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // One-time best-effort sweep of temps stranded by a crash/kill between the
        // temp write and the swap (the success and catch paths both consume/delete
        // their own temp, so only a hard crash leaks one, and the per-PID name means
        // a dead process's temp is never reclaimed otherwise). Mirrors the vault.xml
        // temp sweep in DatabaseManager.Initialize. Runs under Save's _lock, so the
        // flag check/set is process-safe; >1-day age guard never touches a live
        // instance's in-flight temp.
        private static bool _sweptTemps;
        private static void SweepStaleTemps()
        {
            if (_sweptTemps) return;
            _sweptTemps = true;   // set first so a failing scan never re-runs every Save
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var f in Directory.GetFiles(Dir, "prefs.xml.*.tmp"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        // Runs a read-modify-write of prefs.xml under BOTH the in-process _lock AND
        // the cross-process MutexName, so concurrent SOLIDWORKS instances can't
        // interleave their Load→change→Save. Best-effort 50ms wait (mirrors
        // RecentFiles): if a peer holds it, proceed unsynchronised rather than stall
        // a UI click — the write is best-effort and non-fatal. Returns the change's
        // result (default(T) on any failure).
        private static T Mutate<T>(Func<XDocument, T> change)
        {
            // WHOLE body non-fatal (mirrors RecentFiles.Add): even the Mutex ctor and
            // lock acquisition can throw (WaitHandleCannotBeOpenedException on a
            // session-name collision, ACL/IO), and the mutator callers run inside
            // WinForms Click handlers with NO try/catch — an escape here would be an
            // unhandled exception on SOLIDWORKS' UI thread. The inner finally still
            // releases the mutex before this outer catch returns.
            try
            {
                lock (_lock)
                using (var mtx = new System.Threading.Mutex(false, MutexName))
                {
                    bool held = false;
                    try { held = mtx.WaitOne(50); }
                    catch (System.Threading.AbandonedMutexException) { held = true; }
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

        private static void Mutate(Action<XDocument> change)
        {
            Mutate<object>(d => { change(d); return null; });
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ── Favourites ─────────────────────────────────────────────────
        public static bool IsFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock)
            {
                try
                {
                    return Section(Load(), "Favorites").Elements("Item")
                        .Any(e => Eq((string)e.Attribute("Path"), path));
                }
                catch { return false; }
            }
        }

        // Returns the NEW favourite state (true = now a favourite).
        public static bool ToggleFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Mutate(d =>
            {
                var fav = Section(d, "Favorites");
                var existing = fav.Elements("Item")
                    .Where(e => Eq((string)e.Attribute("Path"), path)).ToList();
                bool nowFav;
                if (existing.Count > 0)
                { existing.ForEach(e => e.Remove()); nowFav = false; }
                else
                { fav.Add(new XElement("Item", new XAttribute("Path", path))); nowFav = true; }
                return nowFav;
            });
        }

        public static List<string> GetFavorites()
        {
            lock (_lock)
            {
                try
                {
                    return Section(Load(), "Favorites").Elements("Item")
                        .Select(e => (string)e.Attribute("Path"))
                        .Where(p => !string.IsNullOrEmpty(p)).ToList();
                }
                catch { return new List<string>(); }
            }
        }

        // ── Saved (named) searches ──────────────────────────────────────
        public static void SaveSearch(string name, string term)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(term)) return;
            Mutate(d =>
            {
                var ss = Section(d, "SavedSearches");
                ss.Elements("Search")
                  .Where(e => Eq((string)e.Attribute("Name"), name.Trim()))
                  .ToList().ForEach(e => e.Remove());
                ss.Add(new XElement("Search",
                    new XAttribute("Name", name.Trim()),
                    new XAttribute("Term", term.Trim())));
            });
        }

        public static List<SavedSearch> GetSavedSearches()
        {
            lock (_lock)
            {
                try
                {
                    return Section(Load(), "SavedSearches").Elements("Search")
                        .Select(e => new SavedSearch
                        {
                            Name = (string)e.Attribute("Name") ?? "",
                            Term = (string)e.Attribute("Term") ?? ""
                        })
                        .Where(s => !string.IsNullOrEmpty(s.Name)).ToList();
                }
                catch { return new List<SavedSearch>(); }
            }
        }

        public static void DeleteSavedSearch(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            Mutate(d =>
                Section(d, "SavedSearches").Elements("Search")
                    .Where(e => Eq((string)e.Attribute("Name"), name))
                    .ToList().ForEach(e => e.Remove()));
        }

        // ── Recent search TERMS (query history) ─────────────────────────
        // Records a search the user actually ran, most-recent first, capped.
        public static void AddRecentSearch(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            term = term.Trim();
            if (term.Length < 2) return;   // mirrors the search box's 2-char floor
            Mutate(d =>
            {
                var rs = Section(d, "RecentSearches");
                // Collapse the incremental-typing chain: drop any existing term
                // that is an exact (case-insensitive) duplicate OR a PREFIX of
                // this one ("ste","stee" when "steel" lands), so the list holds
                // the final queries rather than every keystroke. The floor keeps
                // the shortest stored term at 2 chars.
                rs.Elements("Search")
                  .Where(e =>
                  {
                      string t = (string)e.Attribute("Term") ?? "";
                      return Eq(t, term) ||
                             (t.Length > 0 &&
                              term.StartsWith(t, StringComparison.OrdinalIgnoreCase));
                  })
                  .ToList().ForEach(e => e.Remove());
                rs.AddFirst(new XElement("Search", new XAttribute("Term", term)));
                rs.Elements("Search").Skip(RecentSearchCap)
                  .ToList().ForEach(e => e.Remove());
            });
        }

        public static List<string> GetRecentSearches()
        {
            lock (_lock)
            {
                try
                {
                    return Section(Load(), "RecentSearches").Elements("Search")
                        .Select(e => (string)e.Attribute("Term"))
                        .Where(t => !string.IsNullOrEmpty(t)).ToList();
                }
                catch { return new List<string>(); }
            }
        }

        public static void ClearRecentSearches()
        {
            Mutate(d =>
                Section(d, "RecentSearches").Elements("Search")
                    .ToList().ForEach(e => e.Remove()));
        }
    }
}
