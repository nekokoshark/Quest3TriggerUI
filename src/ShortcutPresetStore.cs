using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Quest3TriggerUI
{
    // Named-slot persistence for temporary shortcut bindings. One flat text
    // file holds all slots; each line is
    //   slot|gesture|K|vk|extended|label      (virtual key target)
    //   slot|gesture|A|actionId|label         (quick-action target)
    // A slot with no lines is empty. Saving an empty binding list clears it.
    internal static class ShortcutPresetStore
    {
        internal const int SlotCount = 8;

        internal sealed class Entry
        {
            internal int Gesture;
            internal bool IsAction;
            internal ushort VirtualKey;
            internal bool Extended;
            internal string ActionId;
            internal string Label;
        }

        private static string Dir()
        {
            return Path.Combine(
                BepInEx.Paths.ConfigPath, "Quest3TriggerUI.shortcuts");
        }

        private static string DataFile()
        {
            return Path.Combine(Dir(), "presets.txt");
        }

        // slot index -> entries (may be empty list)
        internal static List<Entry>[] LoadAll()
        {
            var slots = new List<Entry>[SlotCount];
            for (int i = 0; i < SlotCount; i++) slots[i] = new List<Entry>();
            try
            {
                string f = DataFile();
                if (!File.Exists(f)) return slots;
                foreach (string line in File.ReadAllLines(f))
                {
                    string[] x = line.Split('|');
                    if (x.Length < 3) continue;
                    int slot, gesture;
                    if (!int.TryParse(x[0], out slot) ||
                        !int.TryParse(x[1], out gesture)) continue;
                    if (slot < 0 || slot >= SlotCount) continue;
                    if (gesture < 0 || gesture >= ShortcutGestureBank.GestureCount)
                        continue;
                    Entry e = new Entry { Gesture = gesture };
                    if (x[2] == "K" && x.Length >= 6)
                    {
                        ushort vk;
                        if (!ushort.TryParse(x[3], out vk)) continue;
                        e.VirtualKey = vk;
                        e.Extended = x[4] == "1";
                        e.Label = Unescape(x[5]);
                    }
                    else if (x[2] == "A" && x.Length >= 5)
                    {
                        e.IsAction = true;
                        e.ActionId = Unescape(x[3]);
                        e.Label = x.Length >= 6 ? Unescape(x[5]) : e.ActionId;
                    }
                    else continue;
                    slots[slot].Add(e);
                }
            }
            catch (Exception ex)
            {
                Log("preset load failed: " + ex.Message);
            }
            return slots;
        }

        internal static void SaveSlot(
            int slot, Dictionary<TemporaryShortcutGesture,
                List<ShortcutBindingTarget>> bindings)
        {
            try
            {
                List<Entry>[] all = LoadAll();
                List<Entry> entries = new List<Entry>();
                if (bindings != null)
                {
                    foreach (KeyValuePair<TemporaryShortcutGesture,
                        List<ShortcutBindingTarget>> kv in bindings)
                    {
                        List<ShortcutBindingTarget> targets = kv.Value;
                        if (targets == null) continue;
                        for (int i = 0; i < targets.Count; i++)
                        {
                            ShortcutBindingTarget t = targets[i];
                            Entry e = new Entry
                            {
                                Gesture = (int)kv.Key,
                                IsAction = t.IsAction,
                                VirtualKey = t.VirtualKey,
                                Extended = t.Extended,
                                ActionId = t.ActionId,
                                Label = t.Label
                            };
                            // Action targets without an id cannot be restored;
                            // skip them rather than writing a dead entry.
                            if (e.IsAction && string.IsNullOrEmpty(e.ActionId))
                                continue;
                            entries.Add(e);
                        }
                    }
                }
                all[slot] = entries;
                Directory.CreateDirectory(Dir());
                var lines = new List<string>();
                var inv = CultureInfo.InvariantCulture;
                for (int s = 0; s < SlotCount; s++)
                {
                    for (int i = 0; i < all[s].Count; i++)
                    {
                        Entry e = all[s][i];
                        if (e.IsAction)
                            lines.Add(string.Format(inv,
                                "{0}|{1}|A|{2}|{3}", s, e.Gesture,
                                Escape(e.ActionId), Escape(e.Label)));
                        else
                            lines.Add(string.Format(inv,
                                "{0}|{1}|K|{2}|{3}|{4}", s, e.Gesture,
                                e.VirtualKey, e.Extended ? "1" : "0",
                                Escape(e.Label)));
                    }
                }
                File.WriteAllLines(DataFile(), lines.ToArray());
            }
            catch (Exception ex)
            {
                Log("preset save failed: " + ex.Message);
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("|", "\\p")
                .Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\n", "\n").Replace("\\p", "|")
                .Replace("\\\\", "\\");
        }

        private static void Log(string m)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogWarning("Q3 shortcuts " + m);
        }
    }
}
