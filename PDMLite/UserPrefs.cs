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
            try { Directory.CreateDirectory(Dir); d.Save(PrefsPath); }
            catch { }
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
            lock (_lock)
            {
                try
                {
                    var d = Load();
                    var fav = Section(d, "Favorites");
                    var existing = fav.Elements("Item")
                        .Where(e => Eq((string)e.Attribute("Path"), path)).ToList();
                    bool nowFav;
                    if (existing.Count > 0)
                    { existing.ForEach(e => e.Remove()); nowFav = false; }
                    else
                    { fav.Add(new XElement("Item", new XAttribute("Path", path))); nowFav = true; }
                    Save(d);
                    return nowFav;
                }
                catch { return false; }
            }
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
            lock (_lock)
            {
                try
                {
                    var d = Load();
                    var ss = Section(d, "SavedSearches");
                    ss.Elements("Search")
                      .Where(e => Eq((string)e.Attribute("Name"), name.Trim()))
                      .ToList().ForEach(e => e.Remove());
                    ss.Add(new XElement("Search",
                        new XAttribute("Name", name.Trim()),
                        new XAttribute("Term", term.Trim())));
                    Save(d);
                }
                catch { }
            }
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
            lock (_lock)
            {
                try
                {
                    var d = Load();
                    Section(d, "SavedSearches").Elements("Search")
                        .Where(e => Eq((string)e.Attribute("Name"), name))
                        .ToList().ForEach(e => e.Remove());
                    Save(d);
                }
                catch { }
            }
        }

        // ── Recent search TERMS (query history) ─────────────────────────
        // Records a search the user actually ran, most-recent first, capped.
        public static void AddRecentSearch(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            term = term.Trim();
            if (term.Length < 2) return;   // mirrors the search box's 2-char floor
            lock (_lock)
            {
                try
                {
                    var d = Load();
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
                    Save(d);
                }
                catch { }
            }
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
            lock (_lock)
            {
                try
                {
                    var d = Load();
                    Section(d, "RecentSearches").Elements("Search")
                        .ToList().ForEach(e => e.Remove());
                    Save(d);
                }
                catch { }
            }
        }
    }
}
