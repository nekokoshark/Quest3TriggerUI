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
        internal static ConfigEntry<string> TargetScene;
        internal static ConfigEntry<string> Mode;

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
        private static string _runningPath = "";
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
                        Log("跳过：载荷是热载入的（本进程早已启动），不打扰当前场景"
                            + PreviewTarget());
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
                if (CurrentMode() == "headless")
                {
                    _autoDone = true;
                    _running = true;
                    sc.StartCoroutine(RunHeadless());
                    return;
                }
                _autoDone = true;
                string reason;
                string target = ResolveTarget(out reason);
                if (string.IsNullOrEmpty(target))
                {
                    Log("跳过：" + reason);
                    return;
                }
                Start(target, "启动自动预热（" + reason + "）");
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
                if (ours) { _running = false; _runningPath = ""; }
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

        // Log-only. A hot-reload generation must not touch the scene, but the
        // user still needs to know whether the next cold start has a usable
        // preheat target, so the resolution is reported without acting on it.
        internal static string PreviewTarget()
        {
            try
            {
                string reason;
                string target = ResolveTarget(out reason);
                if (string.IsNullOrEmpty(target))
                    return "；下次冷启动无预热目标（" + reason + "）";
                return "；下次冷启动将预热 " + Path.GetFileName(target) + "（" + reason + "）";
            }
            catch (Exception e) { return "；预热目标解析失败：" + e.Message; }
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

        // "cheapest" - pay the process-wide layer on the lightest scene there
        //              is: VaM's own single-character container when present,
        //              otherwise the cheapest recorded scene with characters
        //              (best when the next scene is not known yet). Default.
        // "last"     - the scene the user opened most recently (the greedy
        //              best case: if they open it again they save the whole
        //              cold cost).
        // "fixed"    - the path in TargetScene.
        internal static string CurrentMode()
        {
            string mode = Mode == null || Mode.Value == null ? "" : Mode.Value.Trim();
            if (string.IsNullOrEmpty(mode))
                mode = "headless";
            if (mode != "headless" && mode != "last" && mode != "cheapest" &&
                mode != "fixed") mode = "headless";
            return mode;
        }

        // ---------- headless preheat (no scene load) ----------

        // Pays the process-wide costs directly instead of borrowing a scene
        // load: invoke the catalogue builders on every DAZCharacterSelector
        // (they are self-contained "instantiate prefab, fill table" passes,
        // each internally guarded by isPlaying and already-built checks),
        // then realize the generic Person prefab and pre-clone a fixed stock
        // into the atom clone pool. What it deliberately does NOT pay: the
        // scene-load machinery itself and the per-scene JSON restore work —
        // those are per-load costs a warm pass cannot remove anyway.
        // Measured (v162): RefreshPackageMorphs/RefreshDynamicClothes/
        // RefreshDynamicHair build per-selector lists that do NOT survive
        // adoption — scene restore re-scans anyway, so pre-paying them on
        // dormant clones costs the same ~22s twice (menu + load). They are
        // deliberately absent from this list; the Init/Early methods left
        // here are cheap no-ops when the clone's Awake already paid them.
        private static readonly string[] CatalogueInitMethods = new string[]
        {
            "InitMorphBanks", "EarlyInitCharacters", "InitCharacters",
            "EarlyInitClothingItems", "InitClothingItems",
            "EarlyInitHairItems", "InitHairItems",
        };

        private static System.Collections.IEnumerator RunHeadless()
        {
            long started = Stopwatch.GetTimestamp();
            Log("启动预热（无场景）：预克隆 Person ×3 + 克隆体内编目初始化");
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
            if (box.Count > 0 && box[0] != null)
            {
                try { AtomClonePool.Prewarm(box[0], 3, "startup-headless"); }
                catch (Exception e) { Log("预克隆异常：" + e.Message); }
            }
            else Log("无场景预热：Person prefab 未实现，克隆池为空");
            // Menu-time there is no DAZCharacterSelector instance at all —
            // but each dormant clone carries one (DAZCharacterRun.character
            // Selector), so catalogue init runs on the clones after prewarm.
            UnityEngine.Object[] selectors = null;
            try
            {
                selectors =
                    Resources.FindObjectsOfTypeAll(typeof(DAZCharacterSelector));
            }
            catch (Exception e)
            {
                Log("编目初始化异常：" + e.Message);
            }
            if (selectors == null || selectors.Length == 0)
            {
                Log("未找到 DAZCharacterSelector，编目初始化跳过");
            }
            else
            {
                for (int s = 0; s < selectors.Length; s++)
                {
                    Component sel = selectors[s] as Component;
                    // FindObjectsOfTypeAll also returns prefab-asset
                    // components; only live scene objects may be inited.
                    if (sel == null || !sel.gameObject.scene.IsValid())
                        continue;
                    for (int m = 0; m < CatalogueInitMethods.Length; m++)
                    {
                        string name = CatalogueInitMethods[m];
                        float t0 = Time.realtimeSinceStartup;
                        string outcome = "ok";
                        try
                        {
                            MethodInfo mi = sel.GetType().GetMethod(name,
                                BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance);
                            if (mi == null) { outcome = "无此方法"; }
                            else mi.Invoke(sel, null);
                        }
                        catch (Exception e)
                        {
                            outcome = "失败 " + e.Message;
                        }
                        Log("编目初始化 " + sel.name + "." + name + "：" +
                            outcome + "，" +
                            ((Time.realtimeSinceStartup - t0) * 1000f)
                                .ToString("F0") + "ms");
                        yield return null;
                    }
                }
            }
            _running = false;
            Log("无场景预热完成 " +
                ((Stopwatch.GetTimestamp() - started) * 1000.0 /
                    Stopwatch.Frequency).ToString("F0") +
                "ms；编目与克隆成本已在菜单期付掉");
        }

        // ---------- target resolution ----------

        private static string ResolveTarget(out string reason)
        {
            string mode = CurrentMode();
            string fixedTarget = TargetScene == null || TargetScene.Value == null
                ? "" : TargetScene.Value.Trim();
            if (mode == "fixed" && !string.IsNullOrEmpty(fixedTarget))
            {
                if (!UsablePath(fixedTarget))
                {
                    reason = "固定目标不存在：" + fixedTarget;
                    return null;
                }
                reason = "固定目标";
                return fixedTarget;
            }
            LoadHistory();
            if (mode == "cheapest")
            {
                // VaM ships a container scene that holds a single Person atom
                // and references no package (Saves\scene\default.json, 66 KB):
                // it pays the shared process-wide layer and almost no artwork
                // of its own, so it is the cheapest target there is and is
                // preferred whenever it is present. Its real cost is recorded
                // like any other load, so the recorded comparison converges on
                // it as well.
                string builtin = BuiltinCheapScene();
                if (!string.IsNullOrEmpty(builtin))
                {
                    reason = "内置轻场景 default.json（单人物、无包依赖）";
                    return builtin;
                }
                Record cheapest = null;
                for (int i = 0; i < _history.Count; i++)
                {
                    Record candidate = _history[i];
                    if (!Usable(candidate)) continue;
                    if (candidate.Atoms < 3) continue;
                    if (cheapest == null || candidate.Ms < cheapest.Ms) cheapest = candidate;
                }
                if (cheapest != null)
                {
                    reason = "最省的人物场景（记录 " + (cheapest.Ms / 1000f).ToString("F1") + "s）";
                    return cheapest.Path;
                }
            }
            for (int i = 0; i < _history.Count; i++)
            {
                Record record = _history[i];
                if (!Usable(record)) continue;
                reason = "上次打开的场景";
                return record.Path;
            }
            // Everything recorded is unusable (its package was removed or
            // the path went away). Say so instead of the generic message.
            if (_history.Count > 0)
            {
                Log("历史 " + _history.Count + " 条，均不可用，首条：" + _history[0].Path);
                reason = "历史场景不可用（包已删或路径失效）：" + _history[0].Path;
                return null;
            }
            reason = "没有可用的历史场景（先手动打开一次场景，或把 Preheat/Mode 设为 fixed 并填 TargetScene）";
            return null;
        }

        // The container scene VaM itself ships with. Resolved against the
        // game root first and the process working directory second (VaM runs
        // from its own folder), so a non-standard install still finds it.
        private const string BuiltinCheapRelative = "Saves\\scene\\default.json";

        private static string BuiltinCheapScene()
        {
            try
            {
                string rooted = Path.Combine(BepInEx.Paths.GameRootPath,
                    BuiltinCheapRelative);
                if (File.Exists(rooted)) return rooted;
            }
            catch { }
            try
            {
                string local = Path.GetFullPath(BuiltinCheapRelative);
                if (File.Exists(local)) return local;
            }
            catch { }
            return "";
        }

        // A recorded load path is whatever the caller handed to
        // SuperController.Load: either a plain path, or a package url of the
        // form "uid:/internal/path", which is what the scene browser passes
        // and what the dependency cache stores (there with backslashes).
        // File.Exists only answers for the first kind, so a url is accepted
        // while the package holding it is still registered; a url for a
        // deleted package is dropped instead of becoming a failed load.
        private static bool UsablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = path.Trim();
            if (File.Exists(path)) return true;
            int colon = path.IndexOf(":/", StringComparison.Ordinal);
            if (colon <= 1) colon = path.IndexOf(":\\", StringComparison.Ordinal);
            if (colon <= 1) return false;
            string uid = path.Substring(0, colon);
            if (!SceneLoadAccelerator.LooksLikeUid(uid)) return false;
            try { return FileManager.GetPackage(uid) != null; }
            catch { return false; }
        }

        private static bool Usable(Record record)
        {
            return record != null && UsablePath(record.Path);
        }

        private static void Start(string target, string origin)
        {
            if (!SceneLoadAccelerator.LoadSceneFast(target))
            {
                Log(origin + "未执行：场景加速已关闭或已有加载在跑");
                return;
            }
            _running = true;
            _runningPath = target;
            Log(origin + "：" + target);
        }

        // ---------- history ("preheat plan" record) ----------

        private static void LoadHistory()
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            _history = new List<Record>();
            try
            {
                // A fresh install has no history file yet. The dependency
                // cache already knows every scene this install has loaded
                // before the plugin ever ran, so seed from it instead of
                // returning with an empty plan (that made the first
                // automatic pass a no-op even though a target was known).
                if (!File.Exists(HistoryPath))
                {
                    SeedFromDependencyCache();
                    return;
                }
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
                if (_history.Count == 0) SeedFromDependencyCache();
            }
            catch (Exception e)
            {
                Log("历史读取失败：" + e.Message);
                _history = new List<Record>();
            }
        }

        // First run on an existing install: the scene-dependency cache already
        // knows every scene that was ever loaded plus what it cost. Take the
        // most-loaded entry as the seed "last scene" so the automatic pass has a
        // target without waiting for the user to open something.
        private static void SeedFromDependencyCache()
        {
            try
            {
                string path = Path.Combine(BepInEx.Paths.ConfigPath,
                    "Quest3TriggerUI.scene-deps.json");
                if (!File.Exists(path)) return;
                JSONClass root = JSON.Parse(File.ReadAllText(path)).AsObject;
                JSONClass scenes = root == null ? null : root["scenes"].AsObject;
                if (scenes == null)
                {
                    Log("依赖缓存没有 scenes 段：" + path);
                    return;
                }
                Record best = null;
                int bestHits = -1;
                int scanned = 0;
                foreach (KeyValuePair<string, JSONNode> pair in scenes)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    scanned++;
                    JSONClass node = pair.Value == null ? null : pair.Value.AsObject;
                    if (node == null) continue;
                    Record record = new Record();
                    record.Path = pair.Key;
                    record.At = "（来自场景依赖缓存）";
                    record.Ms = node["ms"] == null ? 0L : (long)node["ms"].AsInt;
                    record.Atoms = node["atoms"] == null ? 0 : node["atoms"].AsInt;
                    int hits = node["hits"] == null ? 0 : node["hits"].AsInt;
                    if (!Usable(record)) continue;
                    _history.Add(record);
                    if (hits > bestHits) { bestHits = hits; best = record; }
                }
                if (_history.Count == 0 || best == null)
                {
                    Log("依赖缓存 " + scanned + " 条条目均不可用（包已删或路径失效）");
                    return;
                }
                _history.Remove(best);
                _history.Insert(0, best);
                Log("历史为空，已从场景依赖缓存载入 " + _history.Count + " 条候选（首选 "
                    + Path.GetFileName(best.Path) + "）");
            }
            catch (Exception e) { Log("候选载入失败：" + e.Message); }
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
