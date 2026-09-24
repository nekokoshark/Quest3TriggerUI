using System;
using System.Collections.Generic;
using System.Globalization;
using SimpleJSON;

namespace Quest3TriggerUI
{
    internal sealed class BrowserTabState
    {
        internal string Id = Guid.NewGuid().ToString("N");
        internal string Dir;
        internal float GridY, TreeY, FavY;
        internal string Search = "";
        internal bool SortByDate;
        internal BrowserTabState(string dir) { Dir = dir; }
    }

    // Stored as an ordered list keyed by tab ID, never keyed/deduped by path.
    // Reads the previous active-index + directory-lines file on first upgrade.
    internal static class BrowserTabStore
    {
        internal static List<BrowserTabState> Read(string text,
            Func<string, bool> validDir, out int active)
        {
            var tabs = new List<BrowserTabState>();
            active = 0;
            if (string.IsNullOrEmpty(text)) return tabs;
            if (!text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                string[] lines = text.Replace("\r", "").Split('\n');
                int selected;
                int.TryParse(lines[0], out selected);
                for (int i = 1; i < lines.Length && tabs.Count < 12; i++)
                {
                    string dir = lines[i].Trim().Replace('\\', '/').TrimEnd('/');
                    if (dir.Length == 0 || !validDir(dir)) continue;
                    if (i - 1 == selected) active = tabs.Count;
                    tabs.Add(new BrowserTabState(dir));
                }
                return tabs;
            }
            JSONClass root = JSON.Parse(text).AsObject;
            JSONNode ver = root != null ? root["version"] : null;
            JSONNode tabsNode = root != null ? root["tabs"] : null;
            if (ver == null || ver.AsInt != 2 || tabsNode == null ||
                tabsNode.AsArray == null)
                throw new FormatException("Unrecognized browser tab state format.");
            JSONArray entries = tabsNode.AsArray;
            JSONNode aid = root["activeId"];
            string activeId = aid != null ? aid.Value : null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count && tabs.Count < 12; i++)
            {
                JSONClass entry = entries[i].AsObject;
                if (entry == null) continue;
                JSONNode dirNode = entry["dir"];
                string dir = dirNode != null && dirNode.Value != null
                    ? dirNode.Value.Replace('\\', '/').TrimEnd('/') : "";
                if (dir.Length == 0 || !validDir(dir)) continue;
                var tab = new BrowserTabState(dir);
                JSONNode idNode = entry["id"];
                string id = idNode != null ? idNode.Value : null;
                if (!string.IsNullOrEmpty(id) && ids.Add(id)) tab.Id = id;
                else ids.Add(tab.Id);
                tab.GridY = Offset(Value(entry["gridY"]));
                tab.TreeY = Offset(Value(entry["treeY"]));
                tab.FavY = Offset(Value(entry["favY"]));
                tab.Search = Value(entry["search"]) ?? "";
                JSONNode srt = entry["sortByDate"];
                tab.SortByDate = srt != null && srt.AsBool;
                if (tab.Id == activeId) active = tabs.Count;
                tabs.Add(tab);
            }
            return tabs;
        }

        private static string Value(JSONNode node)
        {
            return node != null ? node.Value : null;
        }

        private static float Offset(string value)
        {
            float result;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
                !float.IsNaN(result) && !float.IsInfinity(result) && result >= 0f ? result : 0f;
        }

        internal static string Write(List<BrowserTabState> tabs, int active)
        {
            var root = new JSONClass();
            root["version"] = new JSONData(2);
            root["activeId"] = new JSONData(tabs.Count == 0 ? "" : tabs[Math.Max(0, Math.Min(active, tabs.Count - 1))].Id);
            var entries = new JSONArray();
            foreach (BrowserTabState tab in tabs)
            {
                var entry = new JSONClass();
                entry["id"] = new JSONData(tab.Id);
                entry["dir"] = new JSONData(tab.Dir);
                entry["gridY"] = new JSONData(tab.GridY.ToString("R", CultureInfo.InvariantCulture));
                entry["treeY"] = new JSONData(tab.TreeY.ToString("R", CultureInfo.InvariantCulture));
                entry["favY"] = new JSONData(tab.FavY.ToString("R", CultureInfo.InvariantCulture));
                entry["search"] = new JSONData(tab.Search ?? "");
                entry["sortByDate"] = new JSONData(tab.SortByDate);
                entries.Add(entry);
            }
            root["tabs"] = entries;
            return root.ToString();
        }

        internal static int FindTabUnder(List<BrowserTabState> tabs, int active, string req)
        {
            // Reopening the category must not jump to the first of two
            // same-address tabs (or away from the user's active subfolder).
            if (active >= 0 && active < tabs.Count && Under(tabs[active].Dir, req)) return active;
            int best = -1, length = -1;
            for (int i = 0; i < tabs.Count; i++)
                if (Under(tabs[i].Dir, req) && tabs[i].Dir.Length > length)
                { best = i; length = tabs[i].Dir.Length; }
            return best;
        }

        private static bool Under(string dir, string req)
        {
            return string.Equals(dir, req, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(req + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
