using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SimpleJSON;

namespace Quest3TriggerUI
{
    // Scene-cost history survives. The obsolete startup pass did not populate
    // a reusable bank cache and left a whole extra catalogue in the hierarchy.
    internal static class ScenePreheat
    {
        private sealed class Record
        {
            internal string Path = "";
            internal string At = "";
            internal long Ms;
            internal int Atoms;
            internal bool Preheated;
        }
        private static readonly string HistoryPath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.scene-history.json");
        private const int HistoryLimit = 20;
        private static List<Record> _history;
        private static bool _historyLoaded;

        internal static void Tick() { CatalogueRetirement.Tick(); }
        internal static void NoteLoadStarted(string path) { }
        internal static void NoteLoadCompleted(string path, long ms)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                LoadHistory();
                Record record = null;
                for (int i = 0; i < _history.Count; i++)
                {
                    Record candidate = _history[i];
                    if (candidate != null && string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
                    { record = candidate; break; }
                }
                if (record == null) record = new Record { Path = path };
                else _history.Remove(record);
                record.At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                record.Ms = ms;
                record.Atoms = CountAtoms();
                _history.Insert(0, record);
                while (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
                SaveHistory();
            }
            catch (Exception e) { Log("记录失败：" + e.Message); }
        }

        // ---------- history ("preheat plan" record) ----------

        private static void LoadHistory()
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            _history = new List<Record>();
            try
            {
                if (!File.Exists(HistoryPath)) return;
                JSONClass root = JSON.Parse(File.ReadAllText(HistoryPath)).AsObject;
                JSONArray scenes = root == null ? null : root["scenes"].AsArray;
                if (scenes == null) return;
                for (int i = 0; i < scenes.Count && _history.Count < HistoryLimit; i++)
                {
                    JSONClass node = scenes[i] == null ? null : scenes[i].AsObject;
                    if (node == null) continue;
                    Record record = new Record();
                    record.Path = node["path"] == null ? "" : node["path"].Value;
                    record.At = node["at"] == null ? "" : node["at"].Value;
                    record.Ms = node["ms"] == null ? 0L : (long)node["ms"].AsInt;
                    record.Atoms = node["atoms"] == null ? 0 : node["atoms"].AsInt;
                    record.Preheated = node["preheated"] != null && node["preheated"].AsBool;
                    if (!string.IsNullOrEmpty(record.Path)) _history.Add(record);
                }
            }
            catch (Exception e)
            {
                Log("历史读取失败：" + e.Message);
                _history = new List<Record>();
            }
        }

        private static int CountAtoms()
        {
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null) return 0;
                List<Atom> atoms = sc.GetAtoms();
                return atoms == null ? 0 : atoms.Count;
            }
            catch { return 0; }
        }

        private static void SaveHistory()
        {
            try
            {
                JSONArray scenes = new JSONArray();
                for (int i = 0; i < _history.Count; i++)
                {
                    Record record = _history[i];
                    if (record == null) continue;
                    JSONClass node = new JSONClass();
                    node["path"] = record.Path == null ? "" : record.Path;
                    node["at"] = record.At == null ? "" : record.At;
                    node["ms"] = new JSONData(record.Ms);
                    node["atoms"] = new JSONData(record.Atoms);
                    node["preheated"] = new JSONData(record.Preheated);
                    scenes.Add(node);
                }
                JSONClass root = new JSONClass();
                root["version"] = new JSONData(1);
                root["scenes"] = scenes;
                File.WriteAllText(HistoryPath, root.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Log("历史写入失败：" + e.Message); }
        }

        // ---------- plumbing ----------

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[预热] " + message); }
            catch { }
        }
    }
}
