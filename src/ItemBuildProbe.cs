using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // 换人时每个 item 的构建成本是这一轮的靶子：金瓶儿的 35 次
    // DAZClothingItem::InitInstance 里有 7 次超过 250ms（最大 3.08s），
    // 整段挤在一帧里（看门狗 freeze=21.4s）。但 InitInstance 自己的托管 IL
    // 是空壳，真正的活在 DAZDynamic::Load 里。
    //
    // 这个探针只回答两个问题："哪一个 item 花了多久"、"它读了多少字节"。
    // wrap=/bytes= 是 StreamReadAccel 的计数器差值——把"这个 item 的慢读"和
    // "被 64KiB 缓冲接管的读"对上号：bytes 大而 ms 小，就是加速生效；
    // bytes=+0 而 ms 大，就说明这个 item 的读根本没走 FileEntryStream。
    // 子步骤（网格 / 布料 / 头发的二进制反序列化、材质 UI、storable 恢复）
    // 由 SceneLoadAccelerator 的 [slow-call] 分账。它不改变任何行为，只记账。
    internal static class ItemBuildProbe
    {
        internal static ConfigEntry<bool> Enabled;

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const float ReportMs = 30f;
        private const long ReportBytes = 262144;
        private const float BurstGap = 4f;
        private const int ReportCap = 4000;

        private static Harmony _harmony;
        private static bool _tried;
        private static FieldInfo _storeName;
        private static FieldInfo _package;
        private static int _reports;
        private static int _burstItems;
        private static float _burstSum;
        private static float _burstMax;
        private static int _burstWraps;
        private static long _burstBytes;
        private static long _burstHeap;
        private static float _lastItem;

        private sealed class ItemState
        {
            internal float Start;
            internal int Wraps;
            internal long Bytes;
            internal long Heap;
        }

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            if (Enabled != null && !Enabled.Value) return;
            try
            {
                Assembly asm = typeof(SuperController).Assembly;
                Type dynamic = asm.GetType("MeshVR.DAZDynamic");
                if (dynamic == null) throw new MissingMemberException("MeshVR.DAZDynamic");
                MethodInfo load = dynamic.GetMethod("Load", All, null, new Type[] { typeof(bool) }, null);
                if (load == null) throw new MissingMemberException("MeshVR.DAZDynamic.Load(bool)");
                Type preset = asm.GetType("MeshVR.PresetManager");
                if (preset != null)
                {
                    _storeName = preset.GetField("storeName", All);
                    _package = preset.GetField("package", All);
                }
                _harmony = new Harmony("Quest3TriggerUI.item-build-probe");
                _harmony.Patch(load,
                    prefix: new HarmonyMethod(typeof(ItemBuildProbe).GetMethod(
                        "Before", BindingFlags.NonPublic | BindingFlags.Static)),
                    finalizer: new HarmonyMethod(typeof(ItemBuildProbe).GetMethod(
                        "After", BindingFlags.NonPublic | BindingFlags.Static)));
                Log("installed; per-item DAZDynamic::Load wall clock, reports >= " +
                    (int)ReportMs + "ms or >= " + (ReportBytes / 1024) + "KiB, name=" + (_storeName != null));
            }
            catch (Exception e) { Log("not installed: " + e.Message); }
        }

        private static void Before(ref object __state)
        {
            try
            {
                ItemState state = new ItemState();
                state.Start = Time.realtimeSinceStartup;
                state.Wraps = StreamReadAccel.WrappedCount;
                state.Bytes = StreamReadAccel.WrappedBytes;
                state.Heap = GC.GetTotalMemory(false);
                __state = state;
            }
            catch (Exception) { __state = null; }
        }

        private static Exception After(Exception __exception, object __instance, ref object __state)
        {
            try
            {
                ItemState state = __state as ItemState;
                if (state == null) return __exception;
                float now = Time.realtimeSinceStartup;
                float ms = (now - state.Start) * 1000f;
                int wraps = StreamReadAccel.WrappedCount - state.Wraps;
                long bytes = StreamReadAccel.WrappedBytes - state.Bytes;
                long heap = GC.GetTotalMemory(false) - state.Heap;
                if (_burstItems > 0 && now - _lastItem > BurstGap) FlushBurst();
                _lastItem = now;
                _burstItems++;
                _burstSum += ms;
                if (ms > _burstMax) _burstMax = ms;
                _burstWraps += wraps;
                _burstBytes += bytes;
                _burstHeap += heap;
                if ((ms >= ReportMs || bytes >= ReportBytes) && _reports < ReportCap)
                {
                    _reports++;
                    Log("item=" + Name(__instance) + " ms=" + (int)ms +
                        " wrap=+" + wraps + " bytes=+" + (bytes / 1048576.0).ToString("0.00") + "MiB" +
                        " heap=+" + (heap / 1048576.0).ToString("0.0") + "MiB" +
                        (__exception == null ? "" : " exception=" + __exception.GetType().Name));
                }
            }
            catch { }
            return __exception;
        }

        private static void FlushBurst()
        {
            Log("swap items=" + _burstItems + " sumMs=" + (int)_burstSum + " maxMs=" + (int)_burstMax +
                " wraps=" + _burstWraps + " bytes=" + (_burstBytes / 1048576.0).ToString("0.0") + "MiB" +
                " heap=+" + (_burstHeap / 1048576.0).ToString("0.0") + "MiB");
            _burstItems = 0;
            _burstSum = 0f;
            _burstMax = 0f;
            _burstWraps = 0;
            _burstBytes = 0L;
            _burstHeap = 0L;
        }

        private static string Name(object instance)
        {
            string store = null;
            string package = null;
            try { if (_storeName != null) store = _storeName.GetValue(instance) as string; } catch { }
            try { if (_package != null) package = _package.GetValue(instance) as string; } catch { }
            if (string.IsNullOrEmpty(store)) store = "?";
            if (string.IsNullOrEmpty(package)) return store;
            return package + ":" + store;
        }

        internal static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[item-build] " + message);
        }
    }
}
