using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // A scene load rebuilds every dynamic selector UI (clothing / hair) once
    // per caller, and VaM calls Resync* for every selector of every Person it
    // creates. On this library one 190 s load contains 24 rebuilds / 45.4 s of
    // main-thread work, and every rebuild after the first is rebuilding a
    // catalogue that is still growing: only the state left by the last rebuild
    // is what the user ever sees.
    //
    // So while a load window is open, the first rebuild of each
    // (instance, entry point) is allowed and the rest are skipped. When the
    // load settles, every instance that was skipped is handed to the existing
    // "rebuild this panel when it is next visible" queue, which rebuilds it on
    // the frame it becomes visible (or on the next frame if it already is).
    // A panel that is never opened costs nothing.
    //
    // Outside a load window nothing is skipped, so editing a scene by hand
    // behaves exactly as it did before.
    internal static class SceneResyncCoalesce
    {
        internal static bool Enabled = true;

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        // One record per selector instance touched inside the current window;
        // a method name is added once, so the second call of the same entry
        // point on the same instance is the one that gets dropped.
        private sealed class Entry
        {
            internal object Inst;
            internal readonly HashSet<string> Allowed = new HashSet<string>();
            internal bool Skipped;
        }

        private static Harmony _harmony;
        private static bool _installed, _failed;
        private static float _nextTry;
        private static MethodInfo _resync;
        private static int _mainThreadId;

        private static bool _window;
        private static bool _replaying;
        private static int _passed, _skipped;
        private static readonly List<Entry> _entries = new List<Entry>();

        internal static void Tick()
        {
            bool active = SceneLoadAccelerator.SceneLoadActive;
            if (active != _window)
            {
                _window = active;
                if (active)
                {
                    _entries.Clear();
                    _passed = 0;
                    _skipped = 0;
                }
                else Settle();
            }
            if (_installed || _failed || !Enabled || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 3f;
            Install();
        }

        private static void Log(string message)
        {
            try { Quest3TriggerUIPlugin.Log.LogInfo("[Resync 合并] " + message); } catch { }
        }

        private static void Install()
        {
            try
            {
                Type type = FindType("GenerateDAZDynamicSelectorUI");
                if (type == null)
                {
                    _failed = true;
                    Log("未找到 GenerateDAZDynamicSelectorUI，重建合并未启用");
                    return;
                }
                _resync = type.GetMethod("Resync", All, null, Type.EmptyTypes, null);
                if (_resync == null)
                {
                    _failed = true;
                    Log("未找到 Resync，重建合并未启用");
                    return;
                }
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _harmony = new Harmony("Quest3TriggerUI.resynccoalesce");
                // A hot reload leaves the previous payload's prefixes attached
                // under the same owner id; two of them would drop two calls in
                // a row.
                _harmony.UnpatchAll(_harmony.Id);
                MethodInfo prefix = typeof(SceneResyncCoalesce)
                    .GetMethod("ResyncPrefix", All);
                int patched = 0;
                foreach (MethodInfo method in type.GetMethods(All))
                {
                    if (method.IsStatic) continue;
                    if (method.GetParameters().Length != 0) continue;
                    if (method.Name != "Resync" && method.Name != "ResyncUI" &&
                        method.Name != "ResyncUIIfActiveFilterOn" &&
                        method.Name != "Generate") continue;
                    _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                    patched++;
                }
                if (patched == 0)
                {
                    _failed = true;
                    Log("未找到可挂载的重建入口，重建合并未启用");
                    return;
                }
                _installed = true;
                Log("已挂载选择器 UI 重建合并（" + patched + " 个入口），只在场景加载窗口内生效");
            }
            catch (Exception e)
            {
                _failed = true;
                Log("挂载失败：" + e.Message);
            }
        }

        private static Type FindType(string name)
        {
            try
            {
                Type type = typeof(SuperController).Assembly.GetType(name);
                if (type != null) return type;
            }
            catch { }
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = asm.GetType(name);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        // Only the main thread is coalesced: another thread calling in must
        // keep the native behaviour rather than lose its rebuild.
        private static bool ResyncPrefix(object __instance, MethodBase __originalMethod)
        {
            if (!Enabled || !_window || _replaying) return true;
            if (__instance == null) return true;
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return true;
                Entry entry = Find(__instance);
                if (entry == null)
                {
                    entry = new Entry();
                    entry.Inst = __instance;
                    _entries.Add(entry);
                }
                // A rebuild of a panel nobody can see during the load is
                // pure cost: skip every entry point on it, not just repeats.
                // Settle() still hands it to the visible-time queue, so the
                // panel rebuilds the frame it is actually opened.
                Component comp = __instance as Component;
                if (comp != null &&
                    BrowserAssistScanAccelerator.IsPanelHidden(comp))
                {
                    entry.Skipped = true;
                    _skipped++;
                    return false;
                }
                string name = __originalMethod == null
                    ? "?" : __originalMethod.Name;
                if (entry.Allowed.Add(name))
                {
                    _passed++;
                    return true;
                }
                entry.Skipped = true;
                _skipped++;
                return false;
            }
            catch { return true; }
        }

        private static Entry Find(object instance)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Inst, instance)) return _entries[i];
            }
            return null;
        }

        // The load is over, so the world is complete and one rebuild per
        // skipped instance is enough. Handing it to the visibility queue
        // instead of running it here keeps the whole cost off the load for
        // panels that are closed, which is every panel a VR user has not
        // opened yet.
        private static void Settle()
        {
            if (_entries.Count == 0) return;
            int deferred = 0, gone = 0;
            _replaying = true;
            long started = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry entry = _entries[i];
                    if (!entry.Skipped) continue;
                    Component component = entry.Inst as Component;
                    if (component == null) { gone++; continue; }
                    try
                    {
                        BrowserAssistScanAccelerator.DeferResyncToOpen(component);
                        deferred++;
                    }
                    catch (Exception e) { Log("挂起失败：" + e.Message); }
                }
            }
            finally
            {
                _replaying = false;
                _entries.Clear();
            }
            Log("加载窗口内选择器 UI 重建：放行 " + _passed + " 次、压掉 " +
                _skipped + " 次；收尾把 " + deferred + " 个面板挂起到可见时补建" +
                (gone > 0 ? "、" + gone + " 个对象已消失" : "") +
                "（收尾耗时 " + Ms(Stopwatch.GetTimestamp() - started) + "ms）");
            _passed = 0;
            _skipped = 0;
        }

        private static string Ms(long ticks)
        {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F0");
        }
    }
}
