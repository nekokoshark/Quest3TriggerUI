using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // P1 startup preheat plus the cross-process "preheat plan" record.
    //
    // What this can and cannot do (measured; see the sceneload topic doc):
    //   * The first scene load of a fresh process pays two process-wide
    //     costs that can neither be skipped nor cached as files: Unity
    //     prefab/asset realization, and the first-time construction of the
    //     morph-bank / clothing-item tables. The IL shows the morph banks
    //     are built by Object.Instantiate and the clothing items by copying
    //     VaM's already parsed JSON into live objects - not by re-reading
    //     files, so a disk cache has nothing to win there.
    //   * Therefore the only lever is to pay those costs earlier: load one
    //     scene while the user is still at the menu, so their own scene load
    //     reuses everything that is process-wide.
    //
    // Safety: one shot per process, never while a load is running or while
    // standby holds the sim paused, never after the user already started a
    // load, everything wrapped in try/catch, and switchable with
    // Preheat/Enabled=false. The record file only stores paths and costs;
    // it never writes into VaM's own files.
    internal static class ScenePreheat
    {
        internal static ConfigFile Source;
        internal static ConfigEntry<bool> Auto;
        internal static ConfigEntry<float> DelaySeconds;

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
        private static readonly string SessionMarkerPath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.preheat-session.txt");
        private const int HistoryLimit = 20;

        private static List<Record> _history;
        private static bool _historyLoaded;
        private static bool _started;
        private static float _startedAt;
        private static bool _autoDone;
        private static bool _foreignLoadSeen;
        private static bool _running;
        private static string _lastPath = "";

        internal static bool AutoOn { get { return Auto == null || Auto.Value; } }

        // ---------- startup pass ----------

        internal static void Tick()
        {
            try
            {
                if (!_started)
                {
                    _started = true;
                    _startedAt = Time.realtimeSinceStartup;
                    if (IsHotReload())
                    {
                        _autoDone = true;
                        Log("跳过：载荷是热载入的（本进程早已启动），不打扰当前场景");
                    }
                }
                if (_autoDone) return;
                SuperController sc = SuperController.singleton;
                if (sc == null) return;
                // The HUD appearing means startup finished; before that a
                // scene load would race the engine's own initialization.
                if (sc.mainHUD == null) return;
                // A live Person atom means the game is already inside a real
                // scene: either the user is standing in one, or this payload
                // was hot-reloaded into a running session. Both cases must not
                // be preheated, so this gate also replaces any guessing about
                // how long the process has been up.
                if (HasPersonAtom())
                {
                    _autoDone = true;
                    Log("跳过：世界已有角色（本进程已经在场景里）");
                    return;
                }
                if (_running || SceneLoadAccelerator.SceneLoadActive || sc.isLoading) return;
                if (IsStandby()) return;
                if (_foreignLoadSeen)
                {
                    _autoDone = true;
                    Log("跳过：本进程已经加载过场景，无需预热");
                    return;
                }
                if (!AutoOn)
                {
                    _autoDone = true;
                    Log("跳过：Preheat/Enabled=false");
                    return;
                }
                float delay = DelaySeconds == null ? 30f : DelaySeconds.Value;
                if (delay < 0f) delay = 0f;
                if (Time.realtimeSinceStartup - _startedAt < delay) return;
                // Scene-rehearsal modes (cheapest/last/fixed) and the person
                // clone pool were removed: the startup pass only realizes the
                // Person prefab and warms the shared morph-bank caches via
                // standalone bank instances.
                _autoDone = true;
                _running = true;
                sc.StartCoroutine(RunHeadless());
            }
            catch (Exception e)
            {
                _autoDone = true;
                Log("异常：" + e.Message);
            }
        }

        // ---------- hooks (called by SceneLoadAccelerator) ----------

        internal static void NoteLoadStarted(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path)) _lastPath = path;
                if (_running) return;
                if (!string.IsNullOrEmpty(path)) _foreignLoadSeen = true;
            }
            catch { }
        }

        internal static void NoteLoadCompleted(string path, long ms)
        {
            try
            {
                bool ours = _running;
                if (ours) { _running = false; }
                if (string.IsNullOrEmpty(path)) return;
                _lastPath = path;
                LoadHistory();
                Record record = null;
                for (int i = 0; i < _history.Count; i++)
                {
                    Record candidate = _history[i];
                    if (candidate == null) continue;
                    if (string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        record = candidate;
                        break;
                    }
                }
                if (record == null)
                {
                    record = new Record();
                    record.Path = path;
                }
                else _history.Remove(record);
                record.At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                record.Ms = ms;
                record.Atoms = CountAtoms();
                if (ours) record.Preheated = true;
                _history.Insert(0, record);
                while (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
                SaveHistory();
                if (ours)
                    Log("预热加载完成 " + (ms / 1000f).ToString("F1") + "s：" + path
                        + "；此后同进程加载任何场景都不再付这部分进程级成本");
            }
            catch (Exception e) { Log("记录失败：" + e.Message); }
        }

        // True as soon as the world holds an active Person atom. Atom.type
        // is the engine's own discriminator ("Person"), the same test the
        // person-preset actions in this plugin already use.
        private static bool HasPersonAtom()
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return false;
            List<Atom> atoms = sc.GetAtoms();
            if (atoms == null) return false;
            for (int i = 0; i < atoms.Count; i++)
            {
                Atom atom = atoms[i];
                if (atom != null && atom.on && atom.type == "Person") return true;
            }
            return false;
        }

        // Hot-reloading the payload mid-session restarts every static field,
        // so a fresh "startup" would look real and would pull the user out of
        // the scene they are already in. Remember which OS process already
        // had its startup pass; a later payload generation finds that marker
        // and stands down. Returns true when this load is NOT a cold start.
        private static bool IsHotReload()
        {
            try
            {
                string started = Process.GetCurrentProcess().StartTime.Ticks.ToString();
                string previous = File.Exists(SessionMarkerPath)
                    ? File.ReadAllText(SessionMarkerPath).Trim() : "";
                File.WriteAllText(SessionMarkerPath,
                    started + "|" + DateTime.Now.Ticks.ToString(),
                    new UTF8Encoding(false));
                if (string.IsNullOrEmpty(previous)) return false;
                int split = previous.IndexOf('|');
                return split > 0 && previous.Substring(0, split) == started;
            }
            catch
            {
                return true;
            }
        }

        // ---------- headless preheat (no scene load, no person clone) ----------

        // The user asked to keep only catalogue initialisation. Evidence from
        // the IL: DAZMorphBank.Awake is `if (isPlaying) Init()`, and Init fills
        // the STATIC _dirEntryCache / _morphInitCache tables — the expensive
        // whole-VAR morph scan is shared process-wide, so one throwaway bank
        // instance warms it for every person spawned later. The selector's
        // other Init*/EarlyInit* passes only build per-selector containers
        // (they need a live selector and do not transfer), so they are not
        // worth hosting: no person clone, no scene rehearsal.
        private static System.Collections.IEnumerator RunHeadless()
        {
            long started = Stopwatch.GetTimestamp();
            Log("启动预热（无场景）：Person prefab 实现化 + 编目银行初始化");
            yield return null;
            List<GameObject> box = new List<GameObject>();
            SuperController sc2 = SuperController.singleton;
            if (sc2 == null)
            {
                _running = false;
                Log("无场景预热中止：SuperController 缺失");
                yield break;
            }
            yield return sc2.StartCoroutine(
                SceneAssetPreheat.RealizePrefab("Person", box));
            GameObject prefab = box.Count > 0 ? box[0] : null;
            if (prefab == null)
            {
                _running = false;
                Log("无场景预热：Person prefab 未实现，编目初始化跳过");
                yield break;
            }
            DAZCharacterSelector sel = null;
            try
            {
                sel = prefab.GetComponentInChildren<DAZCharacterSelector>(true);
            }
            catch (Exception e) { Log("selector 解析异常：" + e.Message); }
            if (sel == null)
            {
                _running = false;
                Log("无场景预热：prefab 上无 DAZCharacterSelector，编目初始化跳过");
                yield break;
            }
            // Holder keeps the warmed banks dormant but alive — destroying a
            // bank whose morph threads just started races OnDestroy/StopThreads;
            // a disabled object runs no Update and costs nothing per frame.
            GameObject holder = new GameObject("Q3-PreheatCatalogue");
            holder.SetActive(false);
            int banks = 0;
            FieldInfo[] fields = typeof(DAZCharacterSelector).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance);
            for (int f = 0; f < fields.Length; f++)
            {
                if (fields[f].Name.IndexOf("Prefab") < 0) continue;
                Component bankPrefab = null;
                try { bankPrefab = fields[f].GetValue(sel) as Component; }
                catch { }
                if (bankPrefab == null) continue;
                if (bankPrefab.GetComponentInChildren<DAZMorphBank>(true) == null)
                    continue;
                float t0 = Time.realtimeSinceStartup;
                string outcome = "ok";
                try
                {
                    Component inst = UnityEngine.Object.Instantiate(bankPrefab);
                    // Awake→Init already ran synchronously inside Instantiate;
                    // park under the disabled holder so nothing ticks.
                    inst.transform.SetParent(holder.transform, false);
                }
                catch (Exception e) { outcome = "失败 " + e.Message; }
                banks++;
                Log("编目银行 " + fields[f].Name + "：" + outcome + "，" +
                    ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") +
                    "ms");
                yield return null;
            }
            if (banks == 0)
                Log("无场景预热：selector 上未找到 morph bank prefab 引用");
            _running = false;
            Log("无场景预热完成 " +
                ((Stopwatch.GetTimestamp() - started) * 1000.0 /
                    Stopwatch.Frequency).ToString("F0") +
                "ms；编目缓存（_dirEntryCache/_morphInitCache）已在菜单期付掉");
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

        private static bool IsStandby()
        {
            Quest3TriggerUIPlugin plugin = Quest3TriggerUIPlugin.Instance;
            return plugin != null && plugin.StandbyActiveNow;
        }

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[预热] " + message); }
            catch { }
        }
    }
}
