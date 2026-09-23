using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // One remembering record per scene path: the VAR-library stamp the record
    // was captured under, the package uids the scene referenced, how many
    // atoms it had, and what the load cost. Only the stamp decides freshness,
    // so a changed library invalidates every record at once.
    internal sealed class SceneDepRecord
    {
        internal string Stamp = "";
        internal List<string> Deps = new List<string>();
        internal int Atoms;
        internal long LoadMs;
        internal int Hits;
    }

    // Scene-load acceleration. Three parts:
    //
    //  1. Stage timing — the load request and the settled scene are both
    //     observed, so a real duration is logged instead of a guess.
    //  2. Dependency cache — the first load of a scene records the package
    //     uids it needs; a later load of the same scene whose library stamp
    //     still matches reuses that record instead of re-deriving it.
    //  3. In-process switch — LoadSceneFast releases standby and calls the
    //     native loader, keeping the VAR registry, the plugin instances and
    //     the VR runtime resident. Nothing is restarted.
    //
    // The BA accelerator reads SceneLoadActive so a package rescan started by
    // a scene change can take the same unchanged-library skip the rescan
    // button already takes. Nothing here touches VaM's own files.
    internal static class SceneLoadAccelerator
    {
        internal static bool Enabled = true;
        internal static bool FastSwitch = true;

        internal static bool SceneLoadActive { get; private set; }
        internal static bool DependenciesWarm { get; private set; }

        // Exposed so the preheat pass can name the scene it is standing on.
        internal static string CurrentScenePath { get { return _loadPath; } }

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;
        private static readonly string CachePath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.scene-deps.json");

        private static Harmony _harmony;
        private static bool _installed, _failed;
        private static float _nextTry;

        private static Dictionary<string, SceneDepRecord> _cache;
        private static bool _cacheLoaded;

        private static string _loadPath;
        private static float _loadStart;
        private static bool _sawLoading;
        private static float _idleSince;
        private static long _lastLoadMs;

        private static string _snapshotScene;
        private static float _snapshotAt;
        private static bool _snapshotArmed;

        // ---------- load-stage capture (PerfLog) ----------
        //
        // VaM's load pipeline reports each stage through
        // SuperController.PerfLog, which is an empty stub in this build, so the
        // stage table never reaches any log. The stub is left in place; we only
        // copy the message out while a scene load is in progress. Every line
        // carries the time since the previous line, which is what turns the
        // report into a stall attribution.
        private static readonly string PerfLogPath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.loadperf.log");
        private static readonly string PerfLogSelfTestPath = Path.Combine(
            BepInEx.Paths.ConfigPath, "Quest3TriggerUI.loadperf.selftest.log");
        private static readonly object _perfLogLock = new object();
        private static string _perfLogTarget = PerfLogPath;
        private static float _perfLogStart;
        private static float _perfLogLast;
        private static int _perfLogCount;
        private static bool _perfLogOpen;
        private static bool _perfLogSelfTest;

        // The load pipeline is one coroutine: every MoveNext call runs one
        // segment of it, so a stall is attributed by the segment it happened in
        // without needing any of the game's own timing output.
        private static FieldInfo _loadCoStateField;
        private static int _loadCoSeq;
        private static float _loadCoEnterAt;
        private static int _loadCoEnterState;

        // Each atom is created by its own coroutine on the same main thread, so
        // a stall during the parallel-create window belongs to one of them.
        private static FieldInfo _atomStateField;
        private static FieldInfo _atomUidField;
        private static int _atomSeq;
        private static float _atomEnterAt;
        private static int _atomEnterState;
        private static string _atomUid = "";

        // Coarse brackets around the load's heavy known stages. The payload is
        // only the per-method aggregate, which keeps it at a dozen lines per load
        // instead of one line per call.
        private sealed class BracketStat
        {
            public int Count;
            public float Total;
            public float Max;
        }

        private static readonly string[] BracketTargets = new string[]
        {
            "DAZCharacterSelector|InitMorphBanks",
            "DAZCharacterSelector|InitFemaleMorphBanks",
            "DAZCharacterSelector|InitMaleMorphBanks",
            "DAZCharacterSelector|InitFemaleMorphBank",
            "DAZCharacterSelector|EnsureCurrentGenderMorphBanksInitialized",
            "DAZCharacterSelector|EnsureOtherGenderMorphBanksInitialized",
            "DAZCharacterSelector|RefreshPackageMorphs",
            "DAZCharacterSelector|RefreshRuntimeMorphs",
            "DAZCharacterSelector|ResyncMorphs",
            "DAZCharacterSelector|ResetMorphsToDefault",
            "DAZCharacterSelector|Init",
            "DAZCharacterSelector|InitBones",
            "DAZCharacterSelector|InitComponents",
            "DAZCharacterSelector|InitCharacters",
            "DAZCharacterSelector|InitClothingItems",
            "DAZCharacterSelector|InitHairItems",
            "DAZCharacterSelector|EarlyInitClothingItems",
            "DAZCharacterSelector|EarlyInitHairItems",
            "DAZCharacterSelector|RefreshDynamicClothes",
            "DAZCharacterSelector|RefreshDynamicHair",
            "DAZCharacterSelector|RefreshDynamicItems",
            "DAZCharacterSelector|SyncCustomItems",
            "DAZCharacterSelector|SyncClothingItem",
            "DAZCharacterSelector|SyncHairItem",
            "DAZCharacterSelector|OnCharacterLoaded",
            "DAZMorphBank|Init",
            "DAZMorphBank|InitMorphs",
            "DAZMorphBank|BuildMorphsList",
            "DAZMorphBank|ReInit",
            "DAZMorphBank|ResetMorphs",
            "DAZMorphBank|ResetMorphsFast",
            "DAZMorphBank|ApplyMorphs",
            "DAZMorphBank|ApplyMorphsImmediate",
            "DAZMorphBank|ApplyMorphsInternal",
            "DAZMorphBank|CloneMorphsFromCache",
            "DAZMorphBank|SaveMorphSnapshots",
            "DAZMorphBank|LoadTransientMorphs",
            "DAZMorphBank|ClearPackageMorphs",
            "DAZMorphBank|RefreshPackageMorphs",
            "DAZMorphBank|RefreshRuntimeMorphs",
            "DAZMorphBank|WarmupDirectoryCache",
            "DAZSkinV2|Init",
            "DAZSkinV2|InitMesh",
            "DAZSkinV2|InitBones",
            "DAZSkinV2|InitPhysicsObjects",
            "DAZSkinV2|SkinMeshGPUInit",
            "DAZSkinV2|StartSubThreads",
            "DAZMesh|Init",
            "DAZMesh|DeriveMeshes",
            "DAZMesh|LoadFromBinaryReader",
            "DAZMesh|InitMaterials",
            "DAZClothingItem|InitInstance",
            "DAZClothingItem|RefreshClothingItems",
            "SuperController|HoldLoadComplete",
            "SuperController|LoadInternal",
            "ImageLoaderThreaded|PreloadImage",
            "ImageLoaderThreaded|ProcessImageImmediate",
            "ImageLoaderThreaded|QueueImage",
            // 由本机 IL 调用点核对过名字的阶段（见场景加载归因文档 §7.5）
            "SuperController|AddAtom",
            "SuperController|InitAtom",
            "SuperController|_ScanJsonForTextureKeysAndPaths",
            "SuperController|_ScanJsonForTexturePaths",
            "SuperController|CheckHoldLoad",
            "Atom|Restore",
            "Atom|LateRestore",
            "Atom|PostRestore",
            "Atom|ResetSimulation",
            "MemoryOptimizer|OptimizeMemoryUsageCallbacksOnly",
            "DAZCharacterSelector|EnableSyncCustomItemsCache",
            // 第四趟：把停顿真正落在谁身上 —— AB 加载协程、AssetLoader 队列、FileManager 目录扫描、
            // 服装/头发选择器 UI 重建。
            "SuperController+<LoadAtomFromBundleAsync>d__1669|MoveNext",
            "MeshVR.AssetLoader+<AssetBundleFromFileQueueWorker>d__22|MoveNext",
            "MeshVR.AssetLoader+<LoadBundleFileAsync>d__20|MoveNext",
            "GenerateDAZDynamicSelectorUI|Resync",
            "GenerateDAZDynamicSelectorUI|ResyncItems",
            "GenerateDAZDynamicSelectorUI|ResyncUI",
            "GenerateDAZDynamicSelectorUI|ResyncUIInternal",
            "GenerateDAZDynamicSelectorUI|InstantiateControl",
            "GenerateDAZDynamicSelectorUI|RefreshThumbnails",
            "MVR.FileManagement.FileManager|FindVarDirectories",
            "MVR.FileManagement.FileManager|GetDirectoryEntry",
            "MVR.FileManagement.FileManager|FindAllFiles",
        };
        private static readonly object _bracketLock = new object();
        private static readonly Dictionary<string, BracketStat> _bracketStats =
            new Dictionary<string, BracketStat>();
        private static readonly Dictionary<MethodBase, string> _bracketNames =
            new Dictionary<MethodBase, string>();
        private static readonly Dictionary<MethodBase, float> _bracketEnter =
            new Dictionary<MethodBase, float>();

        // 冷加载的三大块都是单帧 50+s。逐方法聚合只回答"谁吃了时间"，
        // 不回答主线程当时在跑还是在等，也不回答它当时停在哪个函数里。
        // 后台采样线程只在一个加载在飞的时候活着。
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static readonly object _stallLock = new object();
        private static volatile bool _stallFrozen = true;
        private static Thread _stallThread;
        private static System.Diagnostics.Process _stallSelf;
        private static long _mainTickTicks;
        private static uint _mainOsThreadId;
        private static volatile string _bracketNow = "";
        private static int _bracketDepth;
        private static readonly Dictionary<string, int> _stallBrackets =
            new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _stallWaits =
            new Dictionary<string, int>();
        private static readonly Dictionary<int, long> _stallThreadCpu =
            new Dictionary<int, long>();
        private static readonly Dictionary<int, long> _stallThreadLast =
            new Dictionary<int, long>();
        private static long _stallWallMs, _stallCpuMs, _stallMaxMs, _stallSamples;
        private static long _stallRunning, _stallWaiting;
        private static long _sampleLastWall, _sampleLastCpu;
        internal static void Tick()
        {
            if (!Enabled) return;
            Interlocked.Exchange(ref _mainTickTicks, DateTime.UtcNow.Ticks);
            if (_mainOsThreadId == 0) { try { _mainOsThreadId = GetCurrentThreadId(); } catch { } }
            if (!_installed && !_failed && Time.unscaledTime >= _nextTry)
            {
                _nextTry = Time.unscaledTime + 3f;
                TryInstall();
            }
            ObserveLoad();
            CapturePendingSnapshot();
        }

        private static void Log(string message)
        {
            Quest3TriggerUIPlugin.Log.LogInfo("[场景加速] " + message);
        }

        // ---------- install ----------

        private static void TryInstall()
        {
            try
            {
                MethodInfo load = typeof(SuperController).GetMethod(
                    "Load", All, null, new[] { typeof(string) }, null);
                if (load == null) { _failed = true; Log("未找到 SuperController.Load，功能未启用"); return; }
                _harmony = new Harmony("Quest3TriggerUI.sceneload");
                // A hot reload leaves the previous payload's prefixes attached
                // under the same owner id; two prefixes would double-log.
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(load,
                    prefix: new HarmonyMethod(typeof(SceneLoadAccelerator)
                        .GetMethod("LoadPrefix", All)),
                    finalizer: new HarmonyMethod(typeof(SceneLoadAccelerator)
                        .GetMethod("LoadFinalizer", All)));
                MethodInfo perfLog = typeof(SuperController).GetMethod(
                    "PerfLog", All, null, new[] { typeof(string) }, null);
                MethodInfo launchPerfLog = typeof(SuperController).GetMethod(
                    "LaunchPerfLog", All, null, new[] { typeof(string) }, null);
                MethodInfo perfPrefix = typeof(SceneLoadAccelerator).GetMethod(
                    "PerfLogPrefix", All);
                if (perfPrefix != null)
                {
                    if (perfLog != null)
                        _harmony.Patch(perfLog, prefix: new HarmonyMethod(perfPrefix));
                    if (launchPerfLog != null)
                        _harmony.Patch(launchPerfLog,
                            prefix: new HarmonyMethod(perfPrefix));
                }
                Type loadCo = FindStateMachine("<LoadCo>");
                MethodInfo loadCoMoveNext = loadCo == null
                    ? null : loadCo.GetMethod("MoveNext", All);
                if (loadCoMoveNext != null)
                {
                    _loadCoStateField = loadCo.GetField("<>1__state",
                        BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public);
                    _harmony.Patch(loadCoMoveNext,
                        prefix: new HarmonyMethod(typeof(SceneLoadAccelerator)
                            .GetMethod("LoadCoPrefix", All)),
                        postfix: new HarmonyMethod(typeof(SceneLoadAccelerator)
                            .GetMethod("LoadCoPostfix", All)));
                }
                Type atomCreate = FindStateMachine("<_ParallelAtomCreate>");
                MethodInfo atomMoveNext = atomCreate == null
                    ? null : atomCreate.GetMethod("MoveNext", All);
                if (atomMoveNext != null)
                {
                    _atomStateField = atomCreate.GetField("<>1__state",
                        BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public);
                    _atomUidField = atomCreate.GetField("uid",
                        BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public);
                    _harmony.Patch(atomMoveNext,
                        prefix: new HarmonyMethod(typeof(SceneLoadAccelerator)
                            .GetMethod("AtomPrefix", All)),
                        postfix: new HarmonyMethod(typeof(SceneLoadAccelerator)
                            .GetMethod("AtomPostfix", All)));
                }
                int bracketCount = InstallBrackets();
                _installed = true;
                Log("已挂载场景加载计时与依赖缓存（Load 前置/终结）"
                    + (perfLog != null
                        ? " + 加载阶段采集（PerfLog → Quest3TriggerUI.loadperf.log）"
                        : "（未找到 PerfLog，阶段采集关闭）")
                    + (loadCoMoveNext != null
                        ? " + LoadCo 分段计时"
                        : "（未找到 LoadCo 状态机，分段计时关闭）")
                    + (atomMoveNext != null
                        ? " + 并行建体分段计时"
                        : "（未找到并行建体状态机，该项关闭）")
                    + " + 重阶段区间计时 " + bracketCount + " 处");
                if (perfLog != null && perfPrefix != null)
                    RunPerfLogSelfTest(perfLog, launchPerfLog);
            }
            catch (Exception e)
            {
                _failed = true;
                Log("挂载失败：" + e.Message);
            }
        }

        // The prefix only marks the request. Load returns before the scene has
        // finished building, so the duration is taken in ObserveLoad once
        // isLoading has been true and then stayed false.
        private static void LoadPrefix(string __0)
        {
            try
            {
                SceneLoadActive = true;
                _loadPath = __0;
                ScenePreheat.NoteLoadStarted(__0);
                _loadStart = Time.realtimeSinceStartup;
                _sawLoading = false;
                _idleSince = 0f;
                OpenPerfCapture(__0);
                StartStallProbe();
                DependenciesWarm = false;
                LoadCache();
                SceneDepRecord record;
                if (_cache.TryGetValue(Key(__0), out record) &&
                    IsRecordFresh(record, BrowserAssistScanAccelerator.LibraryStamp))
                {
                    DependenciesWarm = true;
                    record.Hits++;
                    SaveCache();
                    Log("依赖缓存命中：" + record.Deps.Count + " 个依赖包、"
                        + record.Atoms + " 个原子（第 " + record.Hits
                        + " 次加载）");
                }
                else
                {
                    Log("依赖缓存未命中（" + (record == null ? "无记录" : "库指纹已变化")
                        + "），本趟按原生流程解析");
                }
            }
            catch (Exception e) { Log("加载前置异常：" + e.Message); }
        }

        private static Exception LoadFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                SceneLoadActive = false;
                Log("场景加载请求抛异常：" + __exception.GetType().Name);
                WriteStallSummary();
                StopStallProbe();
                ClosePerfCapture((long)((Time.realtimeSinceStartup - _loadStart) * 1000f));
            }
            return __exception;
        }

        private static void ObserveLoad()
        {
            if (!SceneLoadActive) return;
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            float now = Time.realtimeSinceStartup;
            if (sc.isLoading)
            {
                _sawLoading = true;
                _idleSince = 0f;
                return;
            }
            // Wait for isLoading to appear before believing it is done: Load
            // starts the coroutine a frame or two later.
            if (!_sawLoading && now - _loadStart < 3f) return;
            if (_idleSince == 0f) { _idleSince = now; return; }
            if (now - _idleSince < 0.75f) return;

            SceneLoadActive = false;
            _lastLoadMs = (long)((now - _loadStart) * 1000f);
            ScenePreheat.NoteLoadCompleted(_loadPath, _lastLoadMs);
            Log("场景加载完成 " + (_lastLoadMs / 1000f).ToString("F1") + "s"
                + (DependenciesWarm ? "（依赖缓存命中）" : "")
                + "，未重启游戏");
            _snapshotScene = _loadPath;
            _snapshotAt = now + 3f;
            _snapshotArmed = true;
            ClosePerfCapture(_lastLoadMs);
        }

        // ---------- load-stage capture ----------

        private static void OpenPerfCapture(string scenePath)
        {
            lock (_perfLogLock)
            {
                _perfLogTarget = PerfLogPath;
                lock (_bracketLock)
                {
                    _bracketStats.Clear();
                    _bracketEnter.Clear();
                }
                _perfLogStart = Time.realtimeSinceStartup;
                _perfLogLast = _perfLogStart;
                _perfLogCount = 0;
                _perfLogOpen = true;
                try
                {
                    File.WriteAllText(_perfLogTarget,
                        "# Quest3TriggerUI load-stage capture\n"
                        + "# scene: " + (scenePath == null ? "" : scenePath) + "\n"
                        + "# library stamp: "
                        + (BrowserAssistScanAccelerator.LibraryStamp == null
                            ? "" : BrowserAssistScanAccelerator.LibraryStamp) + "\n"
                        + "# each line: +<ms since previous line> | +<ms since load> | stage\n");
                }
                catch { _perfLogOpen = false; }
            }
        }

        private static void ClosePerfCapture(long totalMs)
        {
            WriteStallSummary();
            StopStallProbe();
            WriteBracketSummary();
            lock (_perfLogLock)
            {
                if (!_perfLogOpen) return;
                _perfLogOpen = false;
                try
                {
                    File.AppendAllText(_perfLogTarget, "# load finished: "
                        + (totalMs / 1000f).ToString("F1") + "s, "
                        + _perfLogCount + " stage lines\n");
                }
                catch { }
            }
        }

        private static void AppendCapture(string line)
        {
            lock (_perfLogLock)
            {
                if (!_perfLogOpen) return;
                _perfLogCount++;
                if (_perfLogCount > 20000) return;
                try { File.AppendAllText(_perfLogTarget, line + "\n"); }
                catch { _perfLogOpen = false; }
            }
        }

        // Prefix for SuperController.PerfLog(string) and LaunchPerfLog(string).
        // The original body is a single empty ret in this build; if the JIT
        // inlined it the call sites never reach this prefix and the capture
        // stays empty, which RunPerfLogSelfTest reports once at install time.
        private static bool PerfLogPrefix(string msg)
        {
            if (!_perfLogOpen) return true;
            if (!SceneLoadActive && !_perfLogSelfTest) return true;
            float now = Time.realtimeSinceStartup;
            float delta = now - _perfLogLast;
            _perfLogLast = now;
            string text = msg == null ? "" : msg;
            if (text.Length > 500) text = text.Substring(0, 500);
            AppendCapture("+" + (delta * 1000f).ToString("F1") + "ms | +"
                + ((now - _perfLogStart) * 1000f).ToString("F1")
                + "ms | " + text);
            return true;
        }

        // ---------- load coroutine brackets ----------

        private static Type FindStateMachine(string namePrefix)
        {
            Type[] nested = typeof(SuperController).GetNestedTypes(All);
            Type fallback = null;
            foreach (Type type in nested)
            {
                if (!type.Name.StartsWith(namePrefix)) continue;
                if (type.GetMethod("MoveNext", All) != null) return type;
                if (fallback == null) fallback = type;
            }
            return fallback;
        }

        private static void LoadCoPrefix(object __instance)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                _loadCoSeq++;
                _loadCoEnterAt = Time.realtimeSinceStartup;
                _loadCoEnterState = _loadCoStateField == null
                    ? -99 : (int)_loadCoStateField.GetValue(__instance);
            }
            catch { }
        }

        // A segment that eats tens of seconds is the answer we are after: the
        // state number names the segment, the delta names its cost.
        private static void LoadCoPostfix(object __instance)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                int next = _loadCoStateField == null
                    ? -99 : (int)_loadCoStateField.GetValue(__instance);
                float ms = (now - _loadCoEnterAt) * 1000f;
                string line = "#LoadCo[" + _loadCoSeq + "] state="
                    + _loadCoEnterState + " next=" + next + " segment="
                    + ms.ToString("F1") + "ms window=+"
                    + ((now - _perfLogStart) * 1000f).ToString("F1") + "ms";
                AppendCapture(line);
                Log(line);
            }
            catch { }
        }

        // ---------- parallel atom create brackets ----------

        private static void AtomPrefix(object __instance)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                _atomSeq++;
                _atomEnterAt = Time.realtimeSinceStartup;
                _atomEnterState = _atomStateField == null
                    ? -99 : (int)_atomStateField.GetValue(__instance);
                object uid = _atomUidField == null
                    ? null : _atomUidField.GetValue(__instance);
                _atomUid = uid == null ? "?" : uid.ToString();
            }
            catch { }
        }

        private static void AtomPostfix(object __instance)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                float ms = (now - _atomEnterAt) * 1000f;
                string line = "#Atom[" + _atomSeq + "] " + _atomUid
                    + " state=" + _atomEnterState + " segment="
                    + ms.ToString("F1") + "ms window=+"
                    + ((now - _perfLogStart) * 1000f).ToString("F1") + "ms";
                AppendCapture(line);
                if (ms >= 50f) Log(line);
            }
            catch { }
        }

        // Harmony patching a body that is nothing but ret only pays off if the
        // call sites still reach the method. This probe calls the patched method
        // itself once, so the answer arrives at install time instead of after
        // another scene load.
        private static void RunPerfLogSelfTest(
            MethodInfo perfLog, MethodInfo launchPerfLog)
        {
            try
            {
                Patches patches = Harmony.GetPatchInfo(perfLog);
                int patchOwners = patches == null || patches.Owners == null
                    ? -1 : patches.Owners.Count;
                _perfLogTarget = PerfLogSelfTestPath;
                _perfLogSelfTest = true;
                lock (_perfLogLock)
                {
                    _perfLogStart = Time.realtimeSinceStartup;
                    _perfLogLast = _perfLogStart;
                    _perfLogCount = 0;
                    _perfLogOpen = true;
                    try
                    {
                        File.WriteAllText(_perfLogTarget,
                            "# PerfLog harmony self-test\n");
                    }
                    catch { }
                }
                perfLog.Invoke(null, new object[] { "SELFTEST-PerfLog" });
                int afterPerfLog = _perfLogCount;
                if (launchPerfLog != null)
                    launchPerfLog.Invoke(null,
                        new object[] { "SELFTEST-LaunchPerfLog" });
                int afterLaunch = _perfLogCount;
                Log("阶段采集自检：patchOwners=" + patchOwners
                    + "，PerfLog 命中=" + afterPerfLog
                    + "，LaunchPerfLog 命中=" + (afterLaunch - afterPerfLog)
                    + (afterPerfLog > 0
                        ? "（前缀有效，加载窗口内可采到阶段行）"
                        : "（前缀未命中，PerfLog 视为已被内联，改用 LoadCo 分段计时）"));
            }
            catch (Exception e) { Log("阶段采集自检异常：" + e.Message); }
            finally
            {
                _perfLogSelfTest = false;
                lock (_perfLogLock) { _perfLogOpen = false; }
                _perfLogTarget = PerfLogPath;
            }
        }

        // ---------- coarse stage brackets ----------

        private static string typeNameOf(string target)
        {
            int split = target.IndexOf('|');
            return split <= 0 ? target : target.Substring(0, split);
        }

        private static int InstallBrackets()
        {
            int installed = 0;
            foreach (string target in BracketTargets)
            {
                try
                {
                    int split = target.IndexOf('|');
                    if (split <= 0) continue;
                    Type type = typeof(SuperController).Assembly.GetType(
                        typeNameOf(target));
                    if (type == null)
                    {
                        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try { type = asm.GetType(typeNameOf(target)); } catch { type = null; }
                            if (type != null) break;
                        }
                    }
                    if (type == null) { Log("区间目标类型未找到 " + typeNameOf(target)); continue; }
                    string methodName = target.Substring(split + 1);
                    int hits = 0;
                    foreach (MethodInfo method in type.GetMethods(All))
                    {
                        if (method.Name != methodName) continue;
                        hits++;
                        MethodInfo pre = typeof(SceneLoadAccelerator)
                            .GetMethod("BracketPrefix", All);
                        MethodInfo post = typeof(SceneLoadAccelerator)
                            .GetMethod("BracketPostfix", All);
                        _bracketNames[method] = type.Name + "::" + methodName;
                        _harmony.Patch(method,
                            prefix: new HarmonyMethod(pre),
                            postfix: new HarmonyMethod(post));
                        installed++;
                    }
                    if (hits == 0) Log("区间目标方法未找到 " + target);
                }
                catch (Exception e)
                {
                    Log("区间计时挂载失败 " + target + "：" + e.Message);
                }
            }
            return installed;
        }

        private static bool IsMainThread()
        {
            if (_mainOsThreadId == 0) return true;
            try { return GetCurrentThreadId() == _mainOsThreadId; } catch { return true; }
        }

        private static void BracketPrefix(MethodBase __originalMethod)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                lock (_bracketLock)
                    _bracketEnter[__originalMethod] = Time.realtimeSinceStartup;
                if (!_stallFrozen && IsMainThread())
                {
                    string now; 
                    if (_bracketNames.TryGetValue(__originalMethod, out now))
                    {
                        _bracketDepth++;
                        _bracketNow = now;
                    }
                }
            }
            catch { }
        }

        private static void BracketPostfix(MethodBase __originalMethod)
        {
            if (!_perfLogOpen || !SceneLoadActive) return;
            try
            {
                if (IsMainThread() && _bracketDepth > 0)
                {
                    _bracketDepth--;
                    if (_bracketDepth == 0) _bracketNow = "";
                }
                string name;
                float ms;
                lock (_bracketLock)
                {
                    if (_bracketDepth == 0) _bracketNow = "";
                    float started;
                    if (!_bracketEnter.TryGetValue(__originalMethod, out started)) return;
                    _bracketEnter.Remove(__originalMethod);
                    if (!_bracketNames.TryGetValue(__originalMethod, out name)) return;
                    ms = (Time.realtimeSinceStartup - started) * 1000f;
                    BracketStat stat;
                    if (!_bracketStats.TryGetValue(name, out stat))
                    {
                        stat = new BracketStat();
                        _bracketStats[name] = stat;
                    }
                    stat.Count++;
                    stat.Total += ms;
                    if (ms > stat.Max) stat.Max = ms;
                }
            }
            catch { }
        }

        private static void WriteBracketSummary()
        {
            if (!_perfLogOpen) return;
            List<string> lines = new List<string>();
            lock (_bracketLock)
            {
                foreach (KeyValuePair<string, BracketStat> pair in _bracketStats)
                {
                    BracketStat stat = pair.Value;
                    lines.Add("$" + pair.Key + " x" + stat.Count
                        + " total=" + stat.Total.ToString("F1")
                        + "ms max=" + stat.Max.ToString("F1") + "ms");
                }
                _bracketStats.Clear();
                _bracketEnter.Clear();
            }
            lines.Sort();
            foreach (string line in lines)
            {
                AppendCapture(line);
                Log(line);
            }
        }

        // ---------- 停顿采样 ----------

        private static void StartStallProbe()
        {
            _stallWallMs = 0; _stallCpuMs = 0; _stallMaxMs = 0; _stallSamples = 0;
            _stallRunning = 0; _stallWaiting = 0;
            _bracketDepth = 0; _bracketNow = "";
            lock (_stallLock)
            {
                _stallBrackets.Clear();
                _stallWaits.Clear();
                _stallThreadCpu.Clear();
                _stallThreadLast.Clear();
            }
            _sampleLastWall = DateTime.UtcNow.Ticks;
            _sampleLastCpu = 0;
            try
            {
                if (_stallSelf == null)
                    _stallSelf = System.Diagnostics.Process.GetCurrentProcess();
                _sampleLastCpu = _stallSelf.TotalProcessorTime.Ticks;
            }
            catch { _stallSelf = null; }
            _stallFrozen = false;
            // 采样线程整个进程只起一次；它每 200ms 醒一轮，只在加载在飞时取值，
            // 所以既不会和加载收尾抢时序，空闲时也不占 CPU。
            if (_stallThread == null)
            {
                try
                {
                    Thread probe = new Thread(StallLoop);
                    probe.IsBackground = true;
                    probe.Name = "Quest3TriggerUI.stall";
                    probe.Start();
                    _stallThread = probe;
                }
                catch { _stallThread = null; }
            }
        }

        private static void StopStallProbe()
        {
            _stallFrozen = true;
        }

        private static void StallLoop()
        {
            while (true)
            {
                try { Thread.Sleep(200); } catch { }
                if (_stallFrozen) continue;
                try { SampleStall(); } catch { }
            }
        }
        // 停顿归因关键：Mono 上拿不到主线程的 ThreadState/WaitReason（实测恒为 0），
        // 但每个线程的 CPU 时间是可信的。按线程累计，即可区分主线程自己在跑，还是主线程在等某个工作线程。
        private static void SampleThreadCpu()
        {
            if (_stallSelf == null) return;
            try
            {
                foreach (System.Diagnostics.ProcessThread pt in _stallSelf.Threads)
                {
                    int tid = pt.Id;
                    long ticks;
                    try { ticks = pt.TotalProcessorTime.Ticks; } catch { continue; }
                    lock (_stallLock)
                    {
                        long acc;
                        long last;
                        _stallThreadCpu.TryGetValue(tid, out acc);
                        if (_stallThreadLast.TryGetValue(tid, out last))
                        {
                            long delta = ticks - last;
                            if (delta > 0) acc += delta;
                        }
                        _stallThreadCpu[tid] = acc;
                        _stallThreadLast[tid] = ticks;
                    }
                }
            }
            catch { }
        }

        private static void SampleStall()
        {
            if (_stallFrozen) return;
            if (!SceneLoadActive) return;
            if (_stallSelf == null) return;
            long now = DateTime.UtcNow.Ticks;
            long cpu;
            try { cpu = _stallSelf.TotalProcessorTime.Ticks; } catch { return; }
            long wallMs = (now - _sampleLastWall) / TimeSpan.TicksPerMillisecond;
            long cpuMs = (cpu - _sampleLastCpu) / TimeSpan.TicksPerMillisecond;
            _sampleLastWall = now;
            _sampleLastCpu = cpu;
            if (wallMs <= 0 || cpuMs < 0) return;
            long gapMs = (now - Interlocked.Read(ref _mainTickTicks))
                / TimeSpan.TicksPerMillisecond;
            if (gapMs < 1000) return;
            _stallSamples++;
            _stallWallMs += wallMs;
            _stallCpuMs += cpuMs;
            SampleThreadCpu();
            if (gapMs > _stallMaxMs) _stallMaxMs = gapMs;
            string bracket = _bracketNow;
            if (!string.IsNullOrEmpty(bracket))
            {
                lock (_stallLock)
                {
                    int seen;
                    _stallBrackets.TryGetValue(bracket, out seen);
                    _stallBrackets[bracket] = seen + 1;
                }
            }
            if (_mainOsThreadId == 0) return;
            try
            {
                System.Diagnostics.ProcessThread main = null;
                foreach (System.Diagnostics.ProcessThread pt in _stallSelf.Threads)
                    if ((uint)pt.Id == _mainOsThreadId) { main = pt; break; }
                if (main == null) return;
                if (main.ThreadState == System.Diagnostics.ThreadState.Running)
                {
                    _stallRunning++;
                }
                else
                {
                    _stallWaiting++;
                    string key = main.WaitReason.ToString();
                    lock (_stallLock)
                    {
                        int seen;
                        _stallWaits.TryGetValue(key, out seen);
                        _stallWaits[key] = seen + 1;
                    }
                }
            }
            catch { }
        }

        private static void WriteStallSummary()
        {
            if (_stallSamples <= 0) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("[停顿采样] 采样 ").Append(_stallSamples)
                .Append(" 点；主线程停顿合计 ")
                .Append((_stallWallMs / 1000f).ToString("F1"))
                .Append("s，同期进程 CPU ").Append((_stallCpuMs / 1000f).ToString("F1"))
                .Append("s（")
                .Append((_stallWallMs > 0
                    ? (float)((double)_stallCpuMs / _stallWallMs) : 0f).ToString("F2"))
                .Append(" 核）；最长单帧停顿 ")
                .Append((_stallMaxMs / 1000f).ToString("F1")).Append("s");
            AppendCapture(sb.ToString());
            Log(sb.ToString());
            sb = new StringBuilder();
            sb.Append("[停顿采样] 主线程状态 Running=").Append(_stallRunning)
                .Append(" Waiting=").Append(_stallWaiting);
            lock (_stallLock)
            {
                foreach (KeyValuePair<string, int> pair in _stallWaits)
                    sb.Append(" ").Append(pair.Key).Append("=").Append(pair.Value);
            }
            AppendCapture(sb.ToString());
            Log(sb.ToString());
            List<string> ranked = new List<string>();
            lock (_stallLock)
            {
                foreach (KeyValuePair<string, int> pair in _stallBrackets)
                    ranked.Add(pair.Value.ToString("D6") + " " + pair.Key);
            }
            ranked.Sort();
            sb = new StringBuilder();
            sb.Append("[停顿采样] 停顿期间最内层区间：");
            if (ranked.Count == 0)
            {
                sb.Append("（无——停顿在未挂区间的代码里）");
            }
            else
            {
                for (int i = 0; i < ranked.Count && i < 8; i++)
                {
                    if (i > 0) sb.Append(", ");
                    int sp = ranked[i].IndexOf(' ');
                    sb.Append(ranked[i].Substring(sp + 1))
                        .Append("x").Append(int.Parse(ranked[i].Substring(0, sp)));
                }
            }
            AppendCapture(sb.ToString());
            Log(sb.ToString());
            // 停顿期间 CPU 烧在哪个线程：主线程 = VaM/Unity 主线程代码或原生同步调用；
            // 其它线程 = 主线程其实在等它（异步加载／工作线程）。
            List<string> threadCpu = new List<string>();
            lock (_stallLock)
            {
                foreach (KeyValuePair<int, long> pair in _stallThreadCpu)
                    threadCpu.Add(pair.Value.ToString("D19") + " " + pair.Key);
            }
            threadCpu.Sort();
            sb = new StringBuilder();
            sb.Append("[停顿采样] 停顿期间 CPU 归属（秒@线程ID）:");
            if (threadCpu.Count == 0) sb.Append("（无数据）");
            for (int i = threadCpu.Count - 1; i >= 0 && i > threadCpu.Count - 7; i--)
            {
                int sp2 = threadCpu[i].IndexOf(" ");
                long cpuTicks = long.Parse(threadCpu[i].Substring(0, sp2));
                int tid2 = int.Parse(threadCpu[i].Substring(sp2 + 1));
                sb.Append(" ").Append((cpuTicks / (float)TimeSpan.TicksPerSecond).ToString("F1"))
                    .Append("s@").Append(tid2)
                    .Append(tid2 == (int)_mainOsThreadId ? "(主)" : "");
            }
            AppendCapture(sb.ToString());
            Log(sb.ToString());
        }
        // ---------- dependency snapshot ----------

        private static void CapturePendingSnapshot()
        {
            if (!_snapshotArmed || Time.realtimeSinceStartup < _snapshotAt) return;
            _snapshotArmed = false;
            string scenePath = string.IsNullOrEmpty(_snapshotScene)
                ? null : _snapshotScene;
            if (string.IsNullOrEmpty(scenePath)) return;
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading) return;
            try
            {
                Capture(scenePath);
            }
            catch (Exception e) { Log("依赖快照失败：" + e.Message); }
        }

        private static void Capture(string scenePath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var deps = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SuperController sc = SuperController.singleton;
            int atoms = 0;
            AddUrl(scenePath, seen, deps);
            foreach (Atom atom in sc.GetAtoms())
            {
                if (atom == null) continue;
                atoms++;
                AddPluginUrls(atom, seen, deps);
                try
                {
                    List<string> names = atom.GetUrlParamNames();
                    if (names == null) continue;
                    for (int i = 0; i < names.Count; i++)
                        AddUrl(atom.GetUrlParamValue(names[i]), seen, deps);
                }
                catch { }
            }
            deps.Sort(StringComparer.OrdinalIgnoreCase);
            SceneDepRecord record;
            SceneDepRecord fresh = new SceneDepRecord();
            fresh.Stamp = BrowserAssistScanAccelerator.LibraryStamp ?? "";
            fresh.Deps = deps;
            fresh.Atoms = atoms;
            fresh.LoadMs = _lastLoadMs;
            fresh.Hits = _cache.TryGetValue(Key(scenePath), out record) && record != null
                ? record.Hits : 0;
            _cache[Key(scenePath)] = fresh;
            SaveCache();
            Log("依赖快照：" + atoms + " 个原子 / " + deps.Count + " 个依赖包，耗时 "
                + sw.ElapsedMilliseconds + "ms（库指纹 " + fresh.Stamp + "）");
        }

        // VaM's plugin list and its requested-package set are not public
        // members, so they are read reflectively. Every failure is silent: a
        // missing dependency only costs a cache miss, never a wrong load.
        private static FieldInfo _pluginsField;
        private static FieldInfo _pluginUrlField;
        private static FieldInfo _requestedField;
        private static bool _membersResolved;

        private static void ResolveMembers()
        {
            if (_membersResolved) return;
            _membersResolved = true;
            try
            {
                _pluginsField = typeof(MVRPluginManager).GetField("plugins",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                Type pluginType = typeof(MVRPluginManager).Assembly
                    .GetType("MVRPlugin");
                if (pluginType != null)
                {
                    _pluginUrlField = pluginType.GetField("pluginURLJSON",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    _requestedField = pluginType.GetField("requestedPackages",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                }
            }
            catch { }
        }

        private static void AddPluginUrls(Atom atom, HashSet<string> seen,
            List<string> deps)
        {
            try
            {
                ResolveMembers();
                if (_pluginsField == null) return;
                MVRPluginManager manager =
                    atom.GetStorableByID("PluginManager") as MVRPluginManager;
                if (manager == null) return;
                IList plugins = _pluginsField.GetValue(manager) as IList;
                if (plugins == null) return;
                for (int i = 0; i < plugins.Count; i++)
                {
                    object plugin = plugins[i];
                    if (plugin == null) continue;
                    if (_pluginUrlField != null)
                    {
                        JSONStorableUrl url =
                            _pluginUrlField.GetValue(plugin) as JSONStorableUrl;
                        AddUrl(url == null ? null : url.val, seen, deps);
                    }
                    if (_requestedField != null)
                    {
                        IEnumerable requested =
                            _requestedField.GetValue(plugin) as IEnumerable;
                        if (requested == null) continue;
                        foreach (object uid in requested)
                            AddUrl((uid as string) + ":/", seen, deps);
                    }
                }
            }
            catch { }
        }

        // A dependency is a package uid that a URL points into. Normal paths
        // (Custom/..., Saves/...) and the file browser's own urls carry no
        // "uid:/" prefix and are skipped.
        private static void AddUrl(string url, HashSet<string> seen, List<string> deps)
        {
            if (string.IsNullOrEmpty(url)) return;
            int colon = url.IndexOf(":/", StringComparison.Ordinal);
            if (colon <= 1) return;
            string uid = url.Substring(0, colon).Trim();
            if (!LooksLikeUid(uid)) return;
            if (seen.Add(uid)) deps.Add(uid);
        }

        internal static bool LooksLikeUid(string uid)
        {
            if (string.IsNullOrEmpty(uid) || uid.Length > 80) return false;
            if (uid.IndexOf('.') <= 0) return false;
            for (int i = 0; i < uid.Length; i++)
            {
                char c = uid[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        // ---------- in-process switch ----------

        internal static bool LoadSceneFast(string path)
        {
            if (!Enabled || !FastSwitch || string.IsNullOrEmpty(path)) return false;
            SuperController sc = SuperController.singleton;
            if (sc == null) { Log("快速切换：SuperController 未就绪"); return false; }
            if (sc.isLoading) { Log("快速切换：已有场景正在加载"); return false; }
            try
            {
                Quest3TriggerUIPlugin plugin = Quest3TriggerUIPlugin.Instance;
                if (plugin != null && plugin.ReleaseStandbyForSceneLoad())
                    Log("快速切换：已退出待机，帧率限制与暂停的物理已还原");
                LoadCache();
                sc.Load(path);
                Log("同进程快速切换 → " + path +
                    "（VAR 注册表、插件实例、VR runtime 全部保留，不重启游戏）");
                return true;
            }
            catch (Exception e)
            {
                Log("快速切换失败，请改用原生场景浏览器：" + e.Message);
                return false;
            }
        }

        // ---------- persistence (pure, unit-checked) ----------

        internal static string SerializeSceneDeps(
            Dictionary<string, SceneDepRecord> map)
        {
            JSONClass scenes = new JSONClass();
            if (map != null)
            {
                foreach (KeyValuePair<string, SceneDepRecord> pair in map)
                {
                    SceneDepRecord record = pair.Value;
                    if (record == null || string.IsNullOrEmpty(pair.Key)) continue;
                    JSONClass node = new JSONClass();
                    node["stamp"] = record.Stamp == null ? "" : record.Stamp;
                    node["atoms"] = new JSONData(record.Atoms);
                    node["ms"] = new JSONData(record.LoadMs);
                    node["hits"] = new JSONData(record.Hits);
                    JSONArray deps = new JSONArray();
                    if (record.Deps != null)
                        for (int i = 0; i < record.Deps.Count; i++)
                            deps.Add(new JSONData(record.Deps[i] == null
                                ? "" : record.Deps[i]));
                    node["deps"] = deps;
                    scenes[pair.Key] = node;
                }
            }
            JSONClass root = new JSONClass();
            root["scenes"] = scenes;
            root["version"] = new JSONData(1);
            return root.ToString();
        }

        internal static Dictionary<string, SceneDepRecord> DeserializeSceneDeps(
            string json)
        {
            var map = new Dictionary<string, SceneDepRecord>(
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return map;
            try
            {
                JSONClass root = JSON.Parse(json).AsObject;
                JSONClass scenes = root == null ? null : root["scenes"].AsObject;
                if (scenes == null) return map;
                foreach (KeyValuePair<string, JSONNode> pair in scenes)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    JSONClass node = pair.Value == null ? null : pair.Value.AsObject;
                    if (node == null) continue;
                    SceneDepRecord record = new SceneDepRecord();
                    record.Stamp = node["stamp"] == null ? "" : node["stamp"].Value;
                    record.Atoms = node["atoms"] == null ? 0 : node["atoms"].AsInt;
                    record.LoadMs = node["ms"] == null
                        ? 0L : (long)node["ms"].AsInt;
                    record.Hits = node["hits"] == null ? 0 : node["hits"].AsInt;
                    JSONArray deps = node["deps"] == null ? null : node["deps"].AsArray;
                    if (deps != null)
                        for (int i = 0; i < deps.Count; i++)
                        {
                            string value = deps[i] == null ? null : deps[i].Value;
                            if (!string.IsNullOrEmpty(value)) record.Deps.Add(value);
                        }
                    map[pair.Key] = record;
                }
            }
            catch
            {
                return new Dictionary<string, SceneDepRecord>(
                    StringComparer.OrdinalIgnoreCase);
            }
            return map;
        }

        // Freshness is one comparison: same library stamp, nothing else.
        internal static bool IsRecordFresh(SceneDepRecord record, string stamp)
        {
            return record != null && !string.IsNullOrEmpty(stamp) &&
                string.Equals(record.Stamp, stamp, StringComparison.Ordinal);
        }

        private static string Key(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Trim().Replace('/', '\\');
        }

        private static void LoadCache()
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;
            try
            {
                _cache = File.Exists(CachePath)
                    ? DeserializeSceneDeps(File.ReadAllText(CachePath))
                    : new Dictionary<string, SceneDepRecord>(
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _cache = new Dictionary<string, SceneDepRecord>(
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveCache()
        {
            try
            {
                if (_cache == null) return;
                File.WriteAllText(CachePath, SerializeSceneDeps(_cache),
                    new UTF8Encoding(false));
            }
            catch (Exception e) { Log("场景缓存写入失败：" + e.Message); }
        }
    }
}
