using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace PDMLite
{
    // Per-user, per-machine UI preferences: saved (named) quick searches, the
    // recently-opened file list, and starred favourites. Stored LOCALLY at
    // %LOCALAPPDATA%\BCorePDM\prefs.xml (NOT vault.xml) — these are personal and
    // high-churn (Recent updates on every open), so they must never contend for
    // the cross-machine vault lock or bloat the shared DB. Mirrors the audit
    // spill's local-folder choice. Every operation is NON-FATAL (swallows IO
    // errors) — a preferences hiccup must never disrupt a workflow.
    public static class UserPrefs
    {
        public class SavedSearch { public string Name; public string Term; }

        private const int RecentCap = 15;
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
                new XElement("Recent"),
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

        // ── Recent files (most-recent-first, capped, deduped) ───────────
        public static void AddRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_lock)
            {
                try
                {
                    var d = Load();
                    var rec = Section(d, "Recent");
                    rec.Elements("Item")
                       .Where(e => Eq((string)e.Attribute("Path"), path))
                       .ToList().ForEach(e => e.Remove());
                    rec.AddFirst(new XElement("Item", new XAttribute("Path", path)));
                    var items = rec.Elements("Item").ToList();
                    for (int i = RecentCap; i < items.Count; i++) items[i].Remove();
                    Save(d);
                }
                catch { }
            }
        }

        public static List<string> GetRecent()
        {
            lock (_lock)
            {
                try
                {
                    return Section(Load(), "Recent").Elements("Item")
                        .Select(e => (string)e.Attribute("Path"))
                        .Where(p => !string.IsNullOrEmpty(p)).ToList();
                }
                catch { return new List<string>(); }
            }
        }

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
    }
}
