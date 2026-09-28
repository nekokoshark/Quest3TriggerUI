using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Timing attribution for the "clothing editor open stalls" complaint:
    //   1) click -> editor visible: UIAssist's own build cost, measured by
    //      patch timers on GridsDisplay.CreateUIButtons / RefreshACE /
    //      RefreshACEAndScrollbars (installed lazily once the JayJayWon
    //      assembly is resolvable).
    //   2) editor visible -> side bars appear: our build path, timed per
    //      phase in UpdatePresetButtons + per tick in the cell pumps.
    // Each run logs one line; steady state stays silent.
    internal static partial class UiAssistHudLink
    {
        private static Harmony _aceProbeHarmony;
        private static bool _aceProbeTried;

        private static long Mark()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static long ElapsedMs(long since)
        {
            if (since == 0L) return 0L;
            return (System.Diagnostics.Stopwatch.GetTimestamp() - since) *
                1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        private static long DeltaMs(long from, long to)
        {
            return (to - from) * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        // Polled from Observe; AssemblyCatalog caches the assembly scan for
        // a second so this costs ~nothing once UIAssist is present.
        private static void EnsureAceOpenProbe()
        {
            if (_aceProbeHarmony != null || _aceProbeTried) return;
            Assembly asm = AssemblyCatalog.FindByType("JayJayWon.GridsDisplay");
            if (asm == null) return;
            _aceProbeTried = true;
            try
            {
                _aceProbeHarmony = new Harmony("Quest3TriggerUI.ace-open-probe");
                // CreateUIButtons lives on GridsDisplay; RefreshACE on the
                // editor object (ActiveClothingEditorDisplay/ACEPlusDisplay) —
                // probe every candidate type so a refactor only loses one timer.
                foreach (string tn in new string[] {
                    "JayJayWon.GridsDisplay",
                    "JayJayWon.SingleGridDisplay",
                    "JayJayWon.ActiveClothingEditorDisplay",
                    "JayJayWon.ACEPlusDisplay",
                    "JayJayWon.ACEIDPlus",
                    "JayJayWon.ActiveClothingEditorItemDisplay",
                    "JayJayWon.ActiveClothingList",
                    "JayJayWon.ClothItemPresetSelector",
                    "JayJayWon.TargetAtomSelectorDisplay",
                    "JayJayWon.ExternalButtonSelectorDisplay",
                    "JayJayWon.GameControlUI",
                    "JayJayWon.UIAssist" })
                {
                    Type t = asm.GetType(tn);
                    if (t == null) continue;
                    PatchAceTimer(t, "CreateUIButtons");
                    PatchAceTimer(t, "DestroyUIButtons");
                    PatchAceTimer(t, "RefreshACE");
                    PatchAceTimer(t, "RefreshACEAndScrollbars");
                    PatchAceTimer(t, "RefreshACEGroup");
                    PatchAceTimer(t, "RefreshACEGroupGeneral");
                    PatchAceTimer(t, "RefreshACEItem");
                    PatchAceTimer(t, "RefreshClothingPresets");
                    PatchAceTimer(t, "SetClothingItemCount");
                    PatchAceTimer(t, "AddACEID");
                    PatchAceTimer(t, "GetActiveClothingSelected");
                    PatchAceTimer(t, "RefreshPersonAtomNames");
                    PatchAceTimer(t, "UpdateActiveClothing");
                    PatchAceTimer(t, "RefreshACL");
                    PatchAceTimer(t, "OnEnable");
                    PatchFrameTimer(t, "Update");
                    PatchFrameTimer(t, "UpdateButtonDistances");
                    PatchFrameTimer(t, "UpdateUIAButtonGrid");
                    PatchFrameTimer(t, "UpdateGASScrollList");
                    PatchFrameTimer(t, "UpdateACEGazeTouchDistances");
                    PatchFrameTimer(t, "UpdateGazeTouchDistances");
                    PatchFrameTimer(t, "UpdateTASGazeTouchDistances");
                    PatchFrameTimer(t, "UpdateExtGazeTouchDistances");
                    PatchFrameTimer(t, "UpdateLeftRightButtons");
                    PatchFrameTimer(t, "UpdateSelectedAtom");
                    PatchFrameTimer(t, "UpdateGazeTargets");
                }

                // ACEPlusDisplay.RefreshACE leaks one runtime listener onto
                // scrollRect.verticalScrollbar.onValueChanged per call (no
                // matching RemoveListener exists in UIAssist 93). Preset loads
                // fan out refreshes, the listener list grows unboundedly, and
                // every scroll tick then re-triggers RefreshACE N times.
                // Dedupe the runtime call list after each RefreshACE.
                Type acePlus = asm.GetType("JayJayWon.ACEPlusDisplay");
                if (acePlus != null)
                {
                    HarmonyMethod dedupe = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("DedupeAceScrollbar", Flags));
                    HarmonyMethod gcWatch = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("GcWatchPrefix", Flags));
                    HarmonyMethod transpiler = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("RefreshACETranspiler", Flags));
                    foreach (MethodInfo m in acePlus.GetMethods(Flags | BindingFlags.DeclaredOnly))
                        if (m.Name == "RefreshACE")
                            _aceProbeHarmony.Patch(m, prefix: gcWatch,
                                postfix: dedupe, transpiler: transpiler);

                    // UpdateACE only ADDS dcis to _atomACEClothingLists and never
                    // removes ones destroyed by person-preset loads. The corpses
                    // keep every RefreshACE pass paying FileExists/GetThumbnail
                    // checks per scroll row. Prune Unity-destroyed entries after
                    // each UpdateACE (disabled-but-alive items stay — the Wear
                    // feature needs them).
                    HarmonyMethod prune = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("PruneDeadAceItems", Flags));
                    foreach (MethodInfo m in acePlus.GetMethods(Flags | BindingFlags.DeclaredOnly))
                        if (m.Name == "UpdateACE")
                            _aceProbeHarmony.Patch(m, postfix: prune);

                    // RefreshACE per row does _activeClothingDCIs.IndexOf(dci):
                    // ~N²/2 UnityEngine.Object equality calls per refresh. Count
                    // Equals/op_Equality invocations during the RefreshACE window.
                    Type uo = typeof(UnityEngine.Object);
                    MethodInfo eq = uo.GetMethod("Equals", new Type[] { typeof(object) });
                    if (eq != null)
                        _aceProbeHarmony.Patch(eq, postfix: new HarmonyMethod(
                            typeof(UiAssistHudLink).GetMethod("EqCountPostfix", Flags)));
                    MethodInfo op = uo.GetMethod("op_Equality",
                        BindingFlags.Public | BindingFlags.Static);
                    if (op != null)
                        _aceProbeHarmony.Patch(op, postfix: new HarmonyMethod(
                            typeof(UiAssistHudLink).GetMethod("OpCountPostfix", Flags)));
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void PatchAceTimer(Type type, string name)
        {
            foreach (MethodInfo m in type.GetMethods(Flags | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || m.ContainsGenericParameters) continue;
                _aceProbeHarmony.Patch(m,
                    prefix: new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceProbePrefix", Flags)),
                    postfix: new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceProbePostfix", Flags)));
            }
        }

        // Per-frame methods (Update): only log when a single call exceeds 30ms,
        // throttled to one line per method per second to avoid spam.
        private static readonly System.Collections.Generic.Dictionary<string, long> _frameLogAt =
            new System.Collections.Generic.Dictionary<string, long>();

        private static void PatchFrameTimer(Type type, string name)
        {
            foreach (MethodInfo m in type.GetMethods(Flags | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || m.ContainsGenericParameters) continue;
                _aceProbeHarmony.Patch(m,
                    prefix: new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceProbePrefix", Flags)),
                    postfix: new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("FrameProbePostfix", Flags)));
            }
        }

        private static void FrameProbePostfix(MethodBase __originalMethod,
            long __state)
        {
            long ms = ElapsedMs(__state);
            if (ms < 30) return;
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            long now = Environment.TickCount;
            long last;
            if (_frameLogAt.TryGetValue(key, out last) && now - last < 1000) return;
            _frameLogAt[key] = now;
            Log("[ACE] frame " + key + " " + ms + "ms");
        }

        // --- ACE scrollbar listener dedupe (UIAssist 93 leak) ---------------

        private static readonly FieldInfo _unityCalls =
            typeof(UnityEngine.Events.UnityEventBase).GetField(
                "m_Calls", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _runtimeCalls;
        private static long _dedupeRemoved;
        private static int _dedupeDiag;
        private static bool _pruneDiagLogged;
        private static int _gcAtRefreshStart = -1;
        private static int _gcDiag;

        private static int _eqCalls;
        private static int _opCalls;
        private static bool _inRefreshACE;

        private static void GcWatchPrefix()
        {
            _gcAtRefreshStart = GC.CollectionCount(0);
            _inRefreshACE = true;
            _eqCalls = 0;
            _opCalls = 0;
        }

        // RefreshACE iterates rows calling _activeClothingDCIs.IndexOf(dci):
        // O(N²) UnityEngine.Object.Equals → native instanceID compare, ~7000
        // calls ≈ 500ms at N=84. MonoBehaviours have a unique managed wrapper,
        // so ReferenceEquals is equivalent and free.
        private static IEnumerable<CodeInstruction> RefreshACETranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo fast = typeof(UiAssistHudLink).GetMethod("FastIndexOf", Flags);
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                MethodInfo mi = ins.operand as MethodInfo;
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) &&
                    mi != null && mi.Name == "IndexOf" &&
                    mi.DeclaringType != null && mi.DeclaringType.IsGenericType &&
                    mi.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    Type itemType = mi.DeclaringType.GetGenericArguments()[0];
                    ins.opcode = OpCodes.Call;
                    ins.operand = fast.MakeGenericMethod(itemType);
                    swapped++;
                }
                yield return ins;
            }
            if (swapped > 0)
                Log("[ACE] transpiler swapped " + swapped + " IndexOf call(s) to reference scan");
        }

        private static int FastIndexOf<T>(List<T> list, T item) where T : class
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        private static void EqCountPostfix()
        {
            if (_inRefreshACE) _eqCalls++;
        }

        private static void OpCountPostfix()
        {
            if (_inRefreshACE) _opCalls++;
        }

        private static void DedupeAceScrollbar(object __instance)
        {
            try
            {
                _inRefreshACE = false;
                int gcDelta = _gcAtRefreshStart < 0 ? 0 :
                    GC.CollectionCount(0) - _gcAtRefreshStart;
                if (gcDelta > 0 || _eqCalls > 500 || _gcDiag < 10)
                {
                    _gcDiag++;
                    Log("[ACE] RefreshACE gc=" + gcDelta + " eq=" + _eqCalls +
                        " op=" + _opCalls);
                }
                string stage = "ok";
                int count = -1;
                if (_unityCalls == null) stage = "no-m_Calls";
                else
                {
                    object scrollRect = Read(__instance.GetType(), __instance, "scrollRect");
                    if (scrollRect == null) stage = "no-scrollRect";
                    else
                    {
                        object scrollbar = Read(scrollRect.GetType(), scrollRect, "verticalScrollbar");
                        if (scrollbar == null) stage = "no-vsb";
                        else
                        {
                            object evt = Read(scrollbar.GetType(), scrollbar, "onValueChanged");
                            if (evt == null) stage = "no-event";
                            else
                            {
                                if (_runtimeCalls == null)
                                    _runtimeCalls = _unityCalls.FieldType.GetField(
                                        "m_RuntimeCalls", BindingFlags.NonPublic | BindingFlags.Instance);
                                var calls = _runtimeCalls == null ? null :
                                    _runtimeCalls.GetValue(_unityCalls.GetValue(evt)) as System.Collections.IList;
                                if (calls == null) stage = "no-list";
                                else
                                {
                                    count = calls.Count;
                                    if (count >= 2) { DedupeRuntimeCalls(calls); return; }
                                }
                            }
                        }
                    }
                }
                if (_dedupeDiag < 5)
                {
                    _dedupeDiag++;
                    Log("[ACE] dedupe diag stage=" + stage + " count=" + count);
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void DedupeRuntimeCalls(System.Collections.IList calls)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            int removed = 0;
            for (int i = calls.Count - 1; i >= 0; i--)
            {
                Delegate d = GetCallDelegate(calls[i]);
                string key = d == null ? null : d.Target + "\x1" + d.Method;
                if (key != null && !seen.Add(key)) { calls.RemoveAt(i); removed++; }
            }
            if (removed > 0)
            {
                _dedupeRemoved += removed;
                Log("[ACE] scrollbar listener dedupe: removed " + removed +
                    " duplicate(s), total " + _dedupeRemoved);
            }
            else if (_dedupeDiag < 5)
            {
                _dedupeDiag++;
                Log("[ACE] dedupe diag stage=list count=" + calls.Count + " distinct-kept");
            }
        }

        // --- Dead DCI prune (UpdateACE never removes destroyed items) ------

        private static readonly string[] _aceDciDicts = new string[]
        {
            "_activeClothingSelected", "_activeClothingDisplayNames",
            "_activeClothingTags", "_activeClothingCSC",
            "_activeClothingHAFileNameByDCI", "_activeClothingBAAliasByDCI"
        };

        private static void PruneDeadAceItems(object __instance, string atomName)
        {
            try
            {
                if (string.IsNullOrEmpty(atomName)) return;
                var lists = Read(__instance.GetType(), __instance, "_atomACEClothingLists")
                    as System.Collections.IDictionary;
                if (lists == null || !lists.Contains(atomName)) return;
                object acl = lists[atomName];
                Type aclType = acl.GetType();
                var dcis = Read(aclType, acl, "_activeClothingDCIs") as System.Collections.IList;
                if (dcis == null) return;

                var dead = new System.Collections.Generic.List<object>();
                for (int i = dcis.Count - 1; i >= 0; i--)
                {
                    object dci = dcis[i];
                    if (dci != null && (UnityEngine.Object)dci == null)
                    {
                        dead.Add(dci);
                        dcis.RemoveAt(i);
                    }
                }
                if (!_pruneDiagLogged)
                {
                    _pruneDiagLogged = true;
                    Log("[ACE] prune diag atom='" + atomName + "' dcis=" + dcis.Count +
                        " dead=" + dead.Count);
                }
                if (dead.Count == 0) return;

                foreach (string field in _aceDciDicts)
                    RemoveDeadKeys(Read(aclType, acl, field) as System.Collections.IDictionary);
                RemoveDeadKeys(Read(__instance.GetType(), null, "clothingPresets")
                    as System.Collections.IDictionary);
                Log("[ACE] pruned " + dead.Count + " destroyed dci(s) from '" +
                    atomName + "' editor list (was " + (dcis.Count + dead.Count) +
                    ", now " + dcis.Count + ")");
            }
            catch (Exception e) { Error(e); }
        }

        private static void RemoveDeadKeys(System.Collections.IDictionary dict)
        {
            if (dict == null || dict.Count == 0) return;
            var dead = new System.Collections.Generic.List<object>();
            foreach (object key in dict.Keys)
                if (key != null && (UnityEngine.Object)key == null) dead.Add(key);
            foreach (object key in dead) dict.Remove(key);
        }

        private static Delegate GetCallDelegate(object invokableCall)
        {
            if (invokableCall == null) return null;
            for (Type t = invokableCall.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("Delegate", Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(invokableCall) as Delegate;
                PropertyInfo p = t.GetProperty("Delegate", Flags | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(invokableCall, null) as Delegate;
            }
            return null;
        }

        // --------------------------------------------------------------------

        private static void AceProbePrefix(out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static readonly System.Collections.Generic.Dictionary<string, long[]> _nativeStats =
            new System.Collections.Generic.Dictionary<string, long[]>();
        private static long _nativeStatsLastFlush;

        private static void AceProbePostfix(MethodBase __originalMethod,
            long __state)
        {
            long ms = ElapsedMs(__state);
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            long[] s;
            if (!_nativeStats.TryGetValue(key, out s))
                _nativeStats[key] = s = new long[2];
            s[0]++; s[1] += ms;
            if (ms >= 10)
                Log("[ACE] native " + key + " " + ms + "ms");
            long now = Environment.TickCount;
            if (now - _nativeStatsLastFlush > 30000)
            {
                _nativeStatsLastFlush = now;
                var sb = new System.Text.StringBuilder("[ACE] stats:");
                foreach (var kv in _nativeStats)
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value[0])
                      .Append("x/").Append(kv.Value[1]).Append("ms");
                _nativeStats.Clear();
                Log(sb.ToString());
            }
        }
    }
}
