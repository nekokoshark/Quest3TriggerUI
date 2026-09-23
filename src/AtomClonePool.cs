using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // SuperController.AddAtom(Atom atom, …, bool instantiate) natively
    // supports adopting an existing object: instantiate=false skips the
    // Instantiate call and uses atom.transform directly (verified in IL;
    // AddAtomByType itself passes 0 on one branch). This pool pre-clones
    // Person prefabs while the user is still browsing — measured ~9.7 s
    // per clone on this machine — and a prefix on AddAtom swaps the
    // requested prefab for a pooled clone by rewriting the two arguments.
    // No IL rewriting: the original method body runs unchanged.
    //
    // The clone is Instantiated (paying Awake/OnEnable) and parked under an
    // inactive DontDestroyOnLoad root. When AddAtom adopts it, SetParent to
    // atomContainerTransform reactivates it — one extra OnDisable/OnEnable
    // pair versus a fresh clone. That is the known correctness risk being
    // validated.
    internal static class AtomClonePool
    {
        internal static bool Enabled = true;
        internal static int MaxPersons = 8;

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        private static Harmony _harmony;
        private static bool _installed, _failed;
        private static float _nextTry;
        private static bool _wasLoading;

        private static GameObject _root;
        private static readonly Dictionary<Atom, Stack<GameObject>> _pool =
            new Dictionary<Atom, Stack<GameObject>>();
        private static int _consumed;
        private static string _sceneTag = "";

        internal static int Stocked
        {
            get
            {
                int n = 0;
                foreach (Stack<GameObject> s in _pool.Values) n += s.Count;
                return n;
            }
        }

        // The scene the current stock was cloned for; a preheat for a
        // different scene retires the old stock via Prewarm's tag check.
        internal static bool StockedFor(string sceneTag)
        {
            return _sceneTag == sceneTag && Stocked > 0;
        }

        internal static void Tick()
        {
            bool loading = SceneLoadAccelerator.SceneLoadActive;
            if (_wasLoading && !loading) ClearLeftovers();
            _wasLoading = loading;
            if (_installed || _failed || !Enabled ||
                Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 3f;
            Install();
        }

        private static void Log(string m)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[原子池] " + m); }
            catch { }
        }

        private static void Install()
        {
            try
            {
                MethodInfo addAtom = typeof(SuperController).GetMethod(
                    "AddAtom", All, null,
                    new Type[] { typeof(Atom), typeof(string), typeof(bool),
                                 typeof(bool), typeof(bool), typeof(bool) },
                    null);
                if (addAtom == null)
                {
                    _failed = true;
                    Log("未找到 AddAtom 六参重载，克隆池未启用");
                    return;
                }
                _harmony = new Harmony("Quest3TriggerUI.atomclonepool");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(addAtom, prefix: new HarmonyMethod(
                    typeof(AtomClonePool).GetMethod("AddAtomPrefix", All)));
                _installed = true;
                Log("已挂载 AddAtom 收养旁路（instantiate 改写），池化 Person 上限 " +
                    MaxPersons);
            }
            catch (Exception e)
            {
                _failed = true;
                Log("挂载失败：" + e.Message);
            }
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("Q3AtomClonePool");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
        }

        // Called by the scene preheat after prefab realization. Each clone
        // is one ~10-20 s main-thread hitch; that is the cost being moved
        // off the load path. Idempotent: a stack that already has enough
        // clones for this prefab is left alone, and a preheat for a
        // different scene first retires the previous stock. Returns clones
        // newly made.
        internal static int Prewarm(GameObject prefab, int count,
            string sceneTag)
        {
            if (!Enabled || prefab == null || count <= 0) return 0;
            if (sceneTag != null && _sceneTag != sceneTag &&
                !string.IsNullOrEmpty(_sceneTag))
            {
                Log("切换预热场景，清掉旧池（" + _sceneTag + "）");
                Clear();
            }
            if (sceneTag != null) _sceneTag = sceneTag;
            Atom prefabAtom = prefab.GetComponent<Atom>();
            if (prefabAtom == null)
            {
                Log(prefab.name + " 无 Atom 组件，跳过池化");
                return 0;
            }
            EnsureRoot();
            Stack<GameObject> stack;
            if (!_pool.TryGetValue(prefabAtom, out stack))
            {
                stack = new Stack<GameObject>();
                _pool[prefabAtom] = stack;
            }
            if (stack.Count >= count)
            {
                Log("池已足量（" + stack.Count + "/" + count + "），跳过重复克隆");
                return 0;
            }
            count -= stack.Count;
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                float t0 = Time.realtimeSinceStartup;
                long mem0 =
                    UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                GameObject clone = null;
                try { clone = UnityEngine.Object.Instantiate(prefab); }
                catch (Exception e)
                {
                    Log("克隆失败：" + e.Message);
                    break;
                }
                if (clone == null) break;
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                long mem =
                    (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()
                        - mem0) / 1048576;
                // An inactive prefab defers Awake to first activation; pay it
                // now so the whole clone cost sits in the browse window.
                if (!clone.activeSelf) clone.SetActive(true);
                clone.transform.SetParent(_root.transform, false);
                stack.Push(clone);
                made++;
                Log("预克隆 " + prefab.name + " #" + made + "/" + count + "：" +
                    ms.ToString("F0") + "ms，占用 +" + mem + "MB");
            }
            return made;
        }

        // Harmony prefix on AddAtom: only rewrites arguments when a matching
        // dormant clone exists; otherwise the native path runs untouched.
        private static bool AddAtomPrefix(ref Atom atom, ref bool instantiate)
        {
            if (!Enabled || !instantiate || atom == null) return true;
            Stack<GameObject> stack;
            if (!_pool.TryGetValue(atom, out stack) || stack.Count == 0)
                return true;
            GameObject clone = stack.Pop();
            Atom cloneAtom = clone == null ? null : clone.GetComponent<Atom>();
            if (cloneAtom == null)
            {
                Log("池中克隆已失效，回退原生克隆");
                return true;
            }
            atom = cloneAtom;
            instantiate = false;
            _consumed++;
            Log("收养克隆 → " + clone.name + "（第 " + _consumed + " 次）");
            return true;
        }

        internal static void Clear()
        {
            foreach (KeyValuePair<Atom, Stack<GameObject>> kv in _pool)
            {
                foreach (GameObject go in kv.Value)
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                }
            }
            _pool.Clear();
            _consumed = 0;
            _sceneTag = "";
        }

        // Load finished without consuming everything (user loaded a
        // different scene, or the scene needed fewer atoms than pooled).
        private static void ClearLeftovers()
        {
            int left = 0;
            foreach (Stack<GameObject> s in _pool.Values) left += s.Count;
            if (left > 0)
            {
                Log("加载收尾清理未消耗克隆 " + left + " 个（本次收养 " +
                    _consumed + " 个）");
                Clear();
            }
            else if (_consumed > 0)
            {
                Log("本次加载收养克隆 " + _consumed + " 个");
                _consumed = 0;
            }
        }
    }
}
