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
                            try { _aceProbeHarmony.Patch(m, prefix: gcWatch, postfix: dedupe, transpiler: transpiler); }
                            catch (Exception e) { Log("[ACE] RefreshACE patch failed: " + e.Message); }

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
                            try { _aceProbeHarmony.Patch(m, postfix: prune); }
                            catch (Exception e) { Log("[ACE] UpdateACE patch failed: " + e.Message); }

                    // ACEPlusDisplay.CreateUI clears the static clothingPresets
                    // cache; the next RefreshACE then pays RefreshClothingPresets
                    // -> FileManagerSecure enumeration per clothing row (~72k
                    // object comparisons measured on a 68-item wardrobe) and the
                    // bill grows with every person loaded into the session.
                    // UI rebuilds do not invalidate preset lists keyed by live
                    // clothing items — carry those entries across the clear.
                    HarmonyMethod pre = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceCreateUiPrefix", Flags));
                    HarmonyMethod post = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceCreateUiPostfix", Flags));
                    foreach (MethodInfo m in acePlus.GetMethods(Flags | BindingFlags.DeclaredOnly))
                        if (m.Name == "CreateUI")
                            try { _aceProbeHarmony.Patch(m, prefix: pre, postfix: post); }
                            catch (Exception e) { Log("[ACE] CreateUI patch failed: " + e.Message); }

                    // Panel open calls RefreshACE once per clothing row
                    // (~292 calls / ~1.2s measured). Every call rebuilds the
                    // same grid; only the last sees the final state. Skip all
                    // same-frame calls and run ONE invocation at LateUpdate
                    // carrying the last call's arguments.
                    HarmonyMethod coalesce = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("RefreshAceCoalesce", Flags));
                    foreach (MethodInfo m in acePlus.GetMethods(Flags | BindingFlags.DeclaredOnly))
                        if (m.Name == "RefreshACE")
                            try { _aceProbeHarmony.Patch(m, prefix: coalesce); }
                            catch (Exception e) { Log("[ACE] coalesce patch failed: " + e.Message); }

                    // The stall is a rare single RefreshACE doing ~75k Unity
                    // object comparisons (eq=75836/op=125862) while the
                    // clothing list stays at 70 — the work hides in a callee.
                    // Count calls to the suspects inside each refresh window.
                    HarmonyMethod countPost = new HarmonyMethod(
                        typeof(UiAssistHudLink).GetMethod("AceInnerCallPostfix", Flags));
                    PatchCount(asm.GetType("JayJayWon.ACEIDPlus"), "RefreshACEItem", countPost);
                    PatchCount(acePlus, "RefreshClothingPresets", countPost);
                    PatchCount(acePlus, "AddClothingPresets", countPost);
                    PatchCount(asm.GetType("JayJayWon.ActiveClothingList"),
                        "GetDisplayNameWithAvailableBAAlias", countPost);
                    PatchCount(typeof(DAZDynamicItem), "GetThumbnail", countPost, true);
                    PatchCount(asm.GetType("JayJayWon.ImageUtils"),
                        "QueueLoadTexture", countPost, true);
                    PatchCount(typeof(ImageLoaderThreaded), "QueueImage", countPost, true);
                    PatchCount(typeof(ImageLoaderThreaded), "QueueThumbnail", countPost, true);
                    PatchCount(typeof(ImageLoaderThreaded), "GetCachedThumbnail",
                        countPost, true);
                    Type fms = typeof(SuperController).Assembly.GetType(
                        "MVR.FileManagementSecure.FileManagerSecure");
                    PatchCount(fms, "GetFiles", countPost);
                    PatchCount(fms, "FileExists", countPost, true);
                    PatchCount(fms, "DirectoryExists", countPost);
                    PatchCount(fms, "NormalizePath", countPost);
                    PatchCount(fms, "IsFileInPackage", countPost);
                    PatchCount(fms, "GetDirectoryName", countPost);
                }
            }
            catch (Exception e) { Error(e); }
        }

        private static void PatchAceTimer(Type type, string name)
        {
            foreach (MethodInfo m in type.GetMethods(Flags | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || m.ContainsGenericParameters) continue;
                // A corrupted detour chain on one method (e.g. left behind by a
                // failed patch in a previous hot-load generation) must not
                // abort every other timer.
                try
                {
                    _aceProbeHarmony.Patch(m,
                        prefix: new HarmonyMethod(
                            typeof(UiAssistHudLink).GetMethod("AceProbePrefix", Flags)),
                        postfix: new HarmonyMethod(
                            typeof(UiAssistHudLink).GetMethod("AceProbePostfix", Flags)));
                }
                catch (Exception e) { Log("[ACE] timer patch failed: " + type.Name + "." + name + ": " + e.Message); }
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

        private static bool _inRefreshACE;

        private static void GcWatchPrefix()
        {
            _gcAtRefreshStart = GC.CollectionCount(0);
            _inRefreshACE = true;
            _aceInnerCalls.Clear();
            _aceInnerMs.Clear();
        }

        private static readonly System.Collections.Generic.Dictionary<string, int>
            _aceInnerCalls = new System.Collections.Generic.Dictionary<string, int>();

        private static void AceInnerCallPostfix(MethodBase __originalMethod)
        {
            if (!_inRefreshACE) return;
            string k = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            int n;
            _aceInnerCalls[k] = _aceInnerCalls.TryGetValue(k, out n) ? n + 1 : 1;
        }

        private static void PatchCount(Type type, string name, HarmonyMethod post,
            bool timed = false)
        {
            if (type == null) return;
            foreach (MethodInfo m in type.GetMethods(Flags | BindingFlags.DeclaredOnly))
                if (m.Name == name && !m.ContainsGenericParameters)
                    try
                    {
                        _aceProbeHarmony.Patch(m, postfix: post);
                        if (timed)
                            _aceProbeHarmony.Patch(m,
                                prefix: new HarmonyMethod(
                                    typeof(UiAssistHudLink).GetMethod("AceInnerWatchPrefix", Flags)),
                                postfix: new HarmonyMethod(
                                    typeof(UiAssistHudLink).GetMethod("AceInnerWatchPostfix", Flags)));
                    }
                    catch (Exception e)
                    {
                        Log("[ACE] count patch failed: " + type.Name + "." + name +
                            ": " + e.Message);
                    }
        }

        private static readonly System.Collections.Generic.Dictionary<string, long>
            _aceInnerMs = new System.Collections.Generic.Dictionary<string, long>();

        private static void AceInnerWatchPrefix(out long __state)
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void AceInnerWatchPostfix(MethodBase __originalMethod,
            long __state)
        {
            if (!_inRefreshACE) return;
            string k = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            long ms;
            _aceInnerMs[k] = (_aceInnerMs.TryGetValue(k, out ms) ? ms : 0) +
                ElapsedMs(__state);
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



        private static void DedupeAceScrollbar(object __instance)
        {
            try
            {
                _inRefreshACE = false;
                int gcDelta = _gcAtRefreshStart < 0 ? 0 :
                    GC.CollectionCount(0) - _gcAtRefreshStart;
                if (gcDelta > 0 || _gcDiag < 10)
                {
                    _gcDiag++;
                    Log("[ACE] RefreshACE gc=" + gcDelta +
                        AceListSizes(__instance) + AceInnerSummary());
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

        // Growth evidence for the "slow after several person loads, fast after
        // editor restart" symptom: count every atom's _activeClothingDCIs plus
        // the static clothingPresets registry.
        private static string AceListSizes(object instance)
        {
            try
            {
                var lists = Read(instance.GetType(), instance, "_atomACEClothingLists")
                    as System.Collections.IDictionary;
                int atoms = 0, dcis = 0;
                if (lists != null)
                    foreach (object v in lists.Values)
                    {
                        atoms++;
                        var d = Read(v.GetType(), v, "_activeClothingDCIs") as System.Collections.IList;
                        if (d != null) dcis += d.Count;
                    }
                var presets = Read(instance.GetType(), null, "clothingPresets")
                    as System.Collections.IDictionary;
                return " lists=" + atoms + " dcis=" + dcis +
                    " presets=" + (presets == null ? -1 : presets.Count);
            }
            catch { return ""; }
        }

        private static string AceInnerSummary()
        {
            if (_aceInnerCalls.Count == 0) return "";
            var sb = new System.Text.StringBuilder(" inner:");
            foreach (var kv in _aceInnerCalls)
                if (kv.Value >= 5) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            foreach (var kv in _aceInnerMs)
                if (kv.Value >= 5) sb.Append(' ').Append(kv.Key).Append("Ms=").Append(kv.Value);
            return sb.ToString();
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

        // --- RefreshACE same-frame coalesce ---------------------------------

        private static bool _aceFlushing;
        private static int _aceCoalesced;
        // (instance, MethodInfo) pairs; RefreshACE() takes no parameters.
        private static readonly List<object[]> _acePending = new List<object[]>();

        private static bool RefreshAceCoalesce(object __instance,
            MethodBase __originalMethod)
        {
            if (_aceFlushing) return true;
            for (int i = 0; i < _acePending.Count; i++)
            {
                if (ReferenceEquals(_acePending[i][0], __instance) &&
                    ReferenceEquals(_acePending[i][1], __originalMethod))
                {
                    _aceCoalesced++;
                    return false;
                }
            }
            _acePending.Add(new object[] { __instance, __originalMethod });
            _aceCoalesced++;
            return false;
        }

        // Runs at LateUpdate: after every Update-path caller has spoken, so
        // each instance+overload fires once.
        internal static void FlushAceRefresh()
        {
            if (_acePending.Count == 0) return;
            object[][] pending = _acePending.ToArray();
            _acePending.Clear();
            _aceFlushing = true;
            try
            {
                int ran = 0;
                bool loading = SuperController.singleton != null &&
                    SuperController.singleton.isLoading;
                foreach (object[] p in pending)
                {
                    // During a scene load the ACE rows these refreshes target
                    // are being destroyed — RefreshACEGroupGeneral touches a
                    // dead component and NREs. The rebuilt UI refreshes itself
                    // after load, so queued calls are dropped, not deferred.
                    if (loading) continue;
                    try { ((MethodInfo)p[1]).Invoke(p[0], null); ran++; }
                    catch (Exception e) { Error(e); }
                }
                if (_aceCoalesced > ran)
                    Log("[ACE] coalesced RefreshACE: " + _aceCoalesced +
                        " calls -> " + ran + " run(s)");
                _aceCoalesced = 0;
            }
            finally { _aceFlushing = false; }
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

        private static System.Collections.IDictionary _aceSavedPresets;

        private static void AceCreateUiPrefix(object __instance)
        {
            try
            {
                var d = Read(__instance.GetType(), null, "clothingPresets")
                    as System.Collections.IDictionary;
                _aceSavedPresets = d == null || d.Count == 0
                    ? null : new System.Collections.Hashtable(d);
            }
            catch { _aceSavedPresets = null; }
        }

        private static void AceCreateUiPostfix(object __instance)
        {
            try
            {
                var saved = _aceSavedPresets;
                _aceSavedPresets = null;
                if (saved == null) return;
                var d = Read(__instance.GetType(), null, "clothingPresets")
                    as System.Collections.IDictionary;
                if (d == null) return;
                int restored = 0;
                foreach (System.Collections.DictionaryEntry e in saved)
                {
                    object k = e.Key;
                    if (k == null || (UnityEngine.Object)k == null ||
                        e.Value == null || d.Contains(k)) continue;
                    d.Add(k, e.Value);
                    restored++;
                }
                if (restored > 0 || saved.Count > 0)
                    Log("[ACE] clothingPresets preserved across UI rebuild: " +
                        restored + "/" + saved.Count);
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
