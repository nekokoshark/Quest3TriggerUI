using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Gate only this character coroutine's UUA/GC after a verified no-resource-
    // change preset. Preserve parameter restore, two yields and explicit cleanup.
    internal static class PresetSweepGate
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SkipUnchangedGC;
        internal static ConfigEntry<bool> GcYieldGate;
        internal static ConfigEntry<bool> SwapGcToIdle;
        internal static ConfigEntry<bool> IdleGcAboveLine;
        internal static ConfigEntry<bool> DeferSweep;
        internal static ConfigEntry<bool> IdleGcOnExhaust;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", All);
        private static readonly FieldInfo Manager = typeof(MeshVR.PresetManagerControl).GetField("pm", All);
        private static readonly FieldInfo Merge = typeof(MeshVR.PresetManager).GetField("isMergeRestore", All);
        private static readonly FieldInfo LoadFlag = typeof(DAZCharacterSelector).GetField("onCharacterLoadedFlag", All);
        private static readonly FieldInfo Run = typeof(DAZCharacterSelector).GetField("_characterRun", All);
        private static readonly FieldInfo[] Catalogs = {
            typeof(DAZCharacterSelector).GetField("_maleClothingItems", All),
            typeof(DAZCharacterSelector).GetField("_femaleClothingItems", All),
            typeof(DAZCharacterSelector).GetField("_maleHairItems", All),
            typeof(DAZCharacterSelector).GetField("_femaleHairItems", All)
        };
        private static Harmony _harmony;
        private static Func<DAZCharacterSelector, IEnumerator> _factory;
        private static Ticket _active;
        private static readonly List<Pending> Tickets = new List<Pending>();
        private static long _activity, _released, _sweepReleased;
        private static long _imageEvents, _loadEvents, _deregisterEvents, _noopDeregisters;
        private static AsyncOperation _lastSweep;
        private static float _lastSweepTime;
        private static bool _sweepSettleLogged;
        private static int _skipped;
        private static bool _sweepPending;
        private static string _sweepPendingReason;
        private static float _sweepPendingAt, _presetEndedAt;
        // A requested sweep waits for the preset window plus a short settle.
        // The bound only stops a permanently busy texture pipeline from
        // holding the debt forever; it is not a policy on when to sweep.
        private const float PresetSettleSeconds = 5f;
        private const float SweepDeferBoundSeconds = 120f;
        private static long _lastGcBytes;
        private static float _lastGcTime;
        private static bool _hasGc;
        private static long _lastGcYield, _lastGcBefore;
        private const long GcGrowthLimit = 256L * 1024 * 1024;
        // Backstop for debris outside release-debt tracking. Debt itself always
        // forces a sweep; a quiet session pays at most one sweep per 10 minutes.
        private const float SweepHardBoundSeconds = 600f;
        private static bool _gcPending;
        private static WeakReference _gcOwner;
        private static float _gcRequestedAt, _gcQuietSince, _gcNextCheck;
        private static long _gcSeenActivity;
        private static bool _gcYieldStretchLogged;
        // Optional cold-host scheduler. UI-only builds keep their existing policy.
        internal static Func<bool, bool, string, bool> ManagedRequest;
        internal static Func<bool> CompletionReady, ManagedPending;
        internal static bool IsInstalled { get { return _harmony != null; } }
        internal static bool TransactionActive { get { return _active != null; } }
        internal static long ActivityEpoch { get { return Interlocked.Read(ref _activity); } }
        internal static bool SweepSettled { get { return _lastSweep == null || _lastSweep.isDone; } }
        internal static AsyncOperation SubmitLoadTailSweep(string reason) { return SubmitSweep("load-tail:" + reason); }
        internal static void CollectLoadTail(string reason) { RunGc(reason); }

        // Idle growth watchdog (see 问题与证据索引 11.11). Measured: an idle
        // session - nobody touching anything - allocates 0.28-0.34GB/min of
        // garbage, and it is not this plugin (10.4MB of a 592MB/120s window;
        // the rest is VaM itself and third-party plugins). Nothing here is
        // ever a timer GC, so without this the growth simply runs until the
        // RAM pressure line, the external trimmer pages the heap out, and the
        // next load has to fault all of it back in.
        //
        // Collect while there is still headroom: the mark work is identical
        // either way, but a resident heap has no page-fault storm attached.
        private const long IdleGcGrowthLimit = 1536L * 1024 * 1024;
        private const float IdleGcMinInterval = 60f;
        // Above IdlePressureLoad the growth band is raised instead of the
        // collect being refused: the 1.5GiB value produced a measured
        // 6.6-6.9GB sawtooth (peaks 26.4-26.6GB of a 28.90GB heap) because
        // the line blocked every attempt until the load happened to dip.
        private const long IdleGcAboveLineGrowth = 3L * 1024 * 1024 * 1024;
        private static long _idleBytes;
        private static float _idleAt;
        private const uint IdlePressureLoad = 80;
        private const float IdleBlockedLogEvery = 60f;
        private static float _idleBlockedLogged;
        // Fire above the load line once the managed heap itself is nearly out
        // of room: 8% of the 26.17GB heap measured on this machine is ~2.1GB,
        // which lands 30-45s before Boehm's forced collect at 0.24GB free.
        private const long HeapExhaustFreePercent = 8L;
        private const long HeapExhaustFreeFloor = 256L * 1024 * 1024;

        // Yield gate for the idle watchdog (idle logs 2026-09-30): the same
        // ~3s mark paid 2453 / 112 / 515 / 1536 / 3086MiB on five consecutive
        // idle collects. A collect returning under 5% of the heap is not
        // buying headroom, so the below-line band doubles instead of paying
        // the same mark every 1.5GiB. The load-line, heap-exhaustion and
        // pressure paths stay ungated: those fire because something else is
        // about to happen, not because a band was crossed.
        private const int IdleGcStretchMaxStrikes = 2;
        private static int _idleGcStrikes;


        private static bool IdleGcDue(float now)
        {
            long used = GC.GetTotalMemory(false);
            if (_idleAt <= 0f)
            {
                _idleBytes = used;
                _idleAt = now;
                _idleBlockedLogged = now;
                return false;
            }
            if (used - _idleBytes < IdleGcBand()) return false;
            if (now - _idleAt < IdleGcMinInterval) return false;
            // Same rule as the deferred path: never collect into a load, a
            // queued decode or a scene switch, and never collect below the
            // idle headroom line (see IdleHeadroom).
            bool gates = (Enabled == null || Enabled.Value) &&
                (SkipUnchangedGC == null || SkipUnchangedGC.Value);
            bool ready = gates && Ready();
            uint load = 100;
            ulong availablePhysical = 0UL;
            bool physicalRoom = false;
            bool room = ready && IdleHeadroom(out load, out availablePhysical, out physicalRoom);
            // The load line is the preferred trigger, but running out of heap
            // is not the same event. Measured twice in one idle session:
            // monoFree fell to 0.24-0.28GB of a 26.17GB heap while this
            // watchdog logged "blocked ... load=84%" the whole way up, and
            // Boehm then collected on its own from inside a UI frame
            // (long-frame freeze=4.2-4.5s). The mark costs the same either
            // way; the one we pick at a quiet moment is the cheaper of the two.
            long freeB = -1L;
            long capB = -1L;
            bool flagged = !room && ready && physicalRoom;
            bool exhausted = flagged &&
                (IdleGcOnExhaust == null || IdleGcOnExhaust.Value) &&
                HeapNearExhaustion(out freeB, out capB);
            // Second reason to work above the line: this machine's steady
            // state IS load=82-88% with the external trimmer off, so the
            // 1.5GiB growth gate fired long before the line let anything run
            // (measured sawtooth 19.74 -> 26.6GB, then one 4.4-4.7s mark).
            // A larger band above the line collects the same garbage at the
            // same cost, in a 1.5-3GiB window instead of 6.8GB.
            bool overLine = flagged && !exhausted &&
                (IdleGcAboveLine == null || IdleGcAboveLine.Value) &&
                used - _idleBytes >= IdleGcAboveLineGrowth;
            if (!room && !exhausted && !overLine)
            {
                // A watchdog that silently never fires is worse than none, so
                // whichever gate blocks is named, at most once a minute.
                if (now - _idleBlockedLogged >= IdleBlockedLogEvery)
                {
                    _idleBlockedLogged = now;
                    Log("idle GC blocked: growthMiB=" +
                        ((used - _idleBytes) / (1024 * 1024)) + " bandMiB=" +
                        (IdleGcBand() / (1024 * 1024)) + " gates=" + gates +
                        " ready=" + ready + " load=" + load + "% availMiB=" +
                        (availablePhysical / (1024 * 1024)));
                }
                return false;
            }
            if (exhausted)
            {
                Log("idle GC above the load line: heap freeMiB=" +
                    (freeB / (1024 * 1024)) + " of " + (capB / (1024 * 1024)) +
                    " under " + HeapExhaustFreePercent + "%; collecting now rather " +
                    "than letting Boehm pick the frame, load=" + load + "%");
            }
            else if (overLine)
            {
                Log("idle GC above the load line: growthMiB=" +
                    ((used - _idleBytes) / (1024 * 1024)) + " over the " +
                    (IdleGcAboveLineGrowth / (1024 * 1024)) + "MiB band, load=" +
                    load + "% availMiB=" + (availablePhysical / (1024 * 1024)) +
                    "; collecting at a quiet moment rather than waiting for the line");
            }
            return true;
        }

        private static long IdleGcBand()
        {
            // 1.5GiB -> 3GiB -> 6GiB, and only while consecutive idle collects
            // keep returning under the yield floor (see RunGc).
            return IdleGcGrowthLimit << _idleGcStrikes;
        }

        // This machine's steady state IS 73-76% load (GlobalMemoryStatusEx:
        // 63GB total, 73% in use, 16.9GB available), so gating the idle
        // watchdog on the load path's 75% line made it dead code on exactly
        // the machine it was written for. Idle collects below the external
        // trimmer's 80% line instead: the freeze is the same ~3s, and a
        // collect that lowers the working set beats waiting for the trimmer.
        private static bool IdleHeadroom(out uint load, out ulong availablePhysical,
            out bool physicalRoom)
        {
            load = 100;
            availablePhysical = 0UL;
            physicalRoom = false;
            var s = new MemoryStatus();
            s.length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (!GlobalMemoryStatusEx(ref s)) return false;
            load = s.load;
            availablePhysical = s.availablePhysical;
            // The floor is a real out-of-memory guard and stays in force even
            // when the escape hatch above the load line opens.
            physicalRoom = s.availablePhysical >= 2UL * 1024 * 1024 * 1024 &&
                s.availablePageFile >= 8UL * 1024 * 1024 * 1024;
            return s.load < IdlePressureLoad && physicalRoom;
        }

        // Boehm's forced collect picks an arbitrary frame - measured inside
        // UIAssist.Update, 4.2-4.5s of freeze. Firing while the heap still has
        // a little room lets that same freeze land at a quiet moment instead.
        private static bool HeapNearExhaustion(out long freeB, out long capB)
        {
            freeB = -1L;
            capB = -1L;
            long cap = MonoGcProbe.HeapBytes();
            long used = MonoGcProbe.UsedBytes();
            if (cap <= 0L || used <= 0L) return false; // probe unavailable: never guess
            capB = cap;
            freeB = cap - used;
            long threshold = cap / 100L * HeapExhaustFreePercent;
            if (threshold < HeapExhaustFreeFloor) threshold = HeapExhaustFreeFloor;
            return freeB < threshold;
        }

        private sealed class Ticket
        {
            internal Ticket previous;
            internal WeakReference owner;
            internal int[] before;
            internal long activity;
            internal long images, loads, deregisters, noops;
            internal long released;
            internal long managedBegin;
            internal string kind;
            internal System.Diagnostics.Stopwatch clock;
            internal bool completed, success, valid, consumed;
            internal List<string> events;
        }

        // This Unity profile predates ConditionalWeakTable. Weak iterator/owner
        // references also cover Unity cancelling a coroutine before its third step.
        private sealed class Pending
        {
            internal WeakReference iterator;
            internal Ticket ticket;
            internal float created;
            internal bool sweepSkipped;
            internal bool automatic;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint length, load;
            internal ulong totalPhysical, availablePhysical, totalPageFile,
                availablePageFile, totalVirtual, availableVirtual, extended;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(PresetSweepGate).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                if (Instance == null || Manager == null || Merge == null || LoadFlag == null || Run == null)
                    throw new MissingFieldException("preset sweep lifecycle fields");
                var qiType = typeof(ImageLoaderThreaded).GetNestedType("QueuedImage", All);
                _qiPathField = qiType == null ? null : qiType.GetField("imgPath", All);
                foreach (var f in Catalogs) if (f == null) throw new MissingFieldException("item catalogs");
                Type iterator = null;
                foreach (Type t in typeof(DAZCharacterSelector).GetNestedTypes(All))
                    if (t.Name.StartsWith("<UnloadUnusedAssetsDelayed>", StringComparison.Ordinal)) iterator = t;
                if (iterator == null) throw new MissingMemberException("character cleanup iterator");
                _factory = (Func<DAZCharacterSelector, IEnumerator>)Delegate.CreateDelegate(
                    typeof(Func<DAZCharacterSelector, IEnumerator>), typeof(DAZCharacterSelector).GetMethod("UnloadUnusedAssetsDelayed", All));
                _harmony = new Harmony("Quest3TriggerUI.preset-sweep-gate");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(MeshVR.PresetManager).GetMethod("LoadPresetPost", All),
                    prefix: Hook("Begin"), finalizer: Hook("End"));
                // Route callers explicitly: the tiny iterator factory can already
                // be inlined in a running game's compiled callers before hot reload.
                foreach (string caller in new[] { "OnCharacterLoaded", "UnloadInactiveObjects" })
                    _harmony.Patch(typeof(DAZCharacterSelector).GetMethod(caller, All), transpiler: Hook("RouteFactory"));
                _harmony.Patch(iterator.GetMethod("MoveNext", All), transpiler: Hook("RouteSweep"));
                _harmony.Patch(typeof(JSONStorableDynamic).GetMethod("UnloadInstance", All), prefix: Hook("BeforeUnload"));
                _harmony.Patch(typeof(JSONStorableDynamic).GetMethod("OnLoadComplete", All), prefix: Hook("LoadActivity"));
                _harmony.Patch(typeof(DAZCharacterSelector).GetMethod("set_selectedCharacter", All), prefix: Hook("BeforeCharacter"));
                _harmony.Patch(typeof(ImageLoaderThreaded).GetMethod("QueueImage", All), prefix: Hook("ImageActivity"));
                _harmony.Patch(typeof(ImageLoaderThreaded).GetMethod("DeregisterTextureUse", All), finalizer: Hook("AfterDeregister"));
                Log("installed; appearance/clothing resource-state gate; UUA release debt/600s bound, requested sweep deferred out of the preset window; GC growth 256MiB/120s; async-tail GC settlement; RAM pressure 75%; idle GC also fires above the 80% load line once the managed heap is under 8% free; manual cleanup retained");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static bool Ready()
        {
            NoteSweepSettled();
            return SuperController.singleton != null && !SuperController.singleton.isLoading &&
                !SceneLoadAccelerator.SceneLoadActive && !WardrobeJanitor.ImagesBusy();
        }

        // Resources.UnloadUnusedAssets returns before the asset GC mark phase
        // actually runs, so the Stopwatch around the call reports the submit
        // cost (0ms in practice) and not the sweep. The wall time from submit
        // to the operation reporting done is the figure that matches the
        // observed long frames, so it is logged separately the first time any
        // caller observes completion.
        private static void NoteSweepSettled()
        {
            if (_lastSweep == null || _sweepSettleLogged || !_lastSweep.isDone) return;
            _sweepSettleLogged = true;
            Log("native sweep settled: waitMs=" +
                (long)((Time.realtimeSinceStartup - _lastSweepTime) * 1000f));
        }

        private static void Activity() { Interlocked.Increment(ref _activity); }

        private static readonly FieldInfo InstanceName =
            typeof(JSONStorableDynamic).GetField("instanceName", All);
        private static FieldInfo _qiPathField;

        private static void Note(Ticket t, string kind, string what)
        {
            if (string.IsNullOrEmpty(what)) what = "?";
            what = Clean(what);
            if (what.Length > 110) what = what.Substring(0, 110);
            if (t.events == null) t.events = new List<string>();
            if (t.events.Count < 24) t.events.Add(kind + ":" + what);
            else if (t.events.Count == 24) t.events.Add("...");
        }

        private static string Clean(string s)
        {
            return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static void ImageActivity(object __0)
        {
            Interlocked.Increment(ref _imageEvents); Activity();
            Ticket t = _active;
            if (t == null || t.completed) return;
            string path = null;
            try { if (__0 != null && _qiPathField != null) path = _qiPathField.GetValue(__0) as string; }
            catch (Exception) { }
            Note(t, "queue", path);
        }

        private static void LoadActivity(JSONStorableDynamic __instance)
        {
            Interlocked.Increment(ref _loadEvents); Activity();
            Ticket t = _active;
            if (t == null || t.completed || __instance == null) return;
            string n = null;
            try { if (InstanceName != null) n = InstanceName.GetValue(__instance) as string; }
            catch (Exception) { }
            if (string.IsNullOrEmpty(n)) n = __instance.name;
            Note(t, "load", __instance.GetType().Name + "/" + n);
        }

        private static Exception AfterDeregister(Texture2D __0, bool __result, Exception __exception)
        {
            // Native false means TryGetValue failed: no count/cache/object changed.
            // Successful deregistration or an exception may change resources, even
            // when character/clothing instance IDs and _released stay identical.
            if (__result || __exception != null)
            {
                Interlocked.Increment(ref _deregisterEvents); Activity();
                Ticket t = _active;
                if (t != null && !t.completed) Note(t, "dereg", __0 != null ? __0.name : null);
            }
            else Interlocked.Increment(ref _noopDeregisters);
            return __exception;
        }

        private static void BeforeUnload(JSONStorableDynamic __instance)
        {
            if (Instance.GetValue(__instance) as Transform == null) return;
            Activity();
            Interlocked.Increment(ref _released);
        }

        private static void BeforeCharacter(DAZCharacterSelector __instance, DAZCharacter __0)
        {
            if (!ReferenceEquals(__instance.selectedCharacter, __0))
            {
                Activity();
                Ticket t = _active;
                if (t != null && !t.completed)
                    Note(t, "char", __0 != null ? __0.name : "null");
            }
        }

        private static void Begin(MeshVR.PresetManager __instance, out Ticket __state)
        {
            // Always mask nested calls, including ineligible/failed loads.
            __state = new Ticket { previous = _active, clock = System.Diagnostics.Stopwatch.StartNew() };
            if (_active != null) _active.valid = false;
            _active = __state;
            try
            {
                if ((Enabled != null && !Enabled.Value) || !Ready() || (bool)Merge.GetValue(__instance)) return;
                Atom atom = WardrobeJanitor.OwnerOf(__instance);
                if (atom == null || atom.type != "Person" || atom.isPreparingToPutBackInPool) return;
                foreach (var control in atom.presetManagerControls)
                    if (control != null && (control.name == "AppearancePresets" || control.name == "ClothingPresets") &&
                        ReferenceEquals(Manager.GetValue(control), __instance)) __state.kind = control.name;
                if (__state.kind == null) return;
                var sel = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                __state.owner = new WeakReference(sel);
                __state.activity = Interlocked.Read(ref _activity);
                __state.released = Interlocked.Read(ref _released);
                __state.images = Interlocked.Read(ref _imageEvents);
                __state.loads = Interlocked.Read(ref _loadEvents);
                __state.deregisters = Interlocked.Read(ref _deregisterEvents);
                __state.noops = Interlocked.Read(ref _noopDeregisters);
                __state.managedBegin = GC.GetTotalMemory(false);
                __state.before = Capture(sel);
                __state.valid = __state.before != null;
                Log("preset candidate: kind=" + __state.kind + " atom=" + atom.uid + " snapshot=" +
                    (__state.before == null ? "not ready" : __state.before.Length.ToString()) + " epoch=" + __state.activity);
            }
            catch (Exception e) { __state.valid = false; Log("native retained at begin: " + e.Message); }
        }

        private static Exception End(Ticket __state, bool __result, Exception __exception)
        {
            if (__state == null) return __exception;
            _active = __state.previous;
            __state.previous = null; // Tickets never keep completed parent transactions alive.
            __state.completed = true;
            __state.success = __result && __exception == null;
            if (__state.owner != null) _presetEndedAt = Time.realtimeSinceStartup;
            __state.valid &= __state.success;
            // Recheck after all Restore/LateRestore/PostRestore/events, not just
            // geometry. Activity noise (texture queue/deregister, late
            // OnLoadComplete, no-change character sets) does not create sweep
            // debt: real divergence is caught by the instance snapshot and the
            // release counter, which ignore bookkeeping-only events.
            string gates = null;
            try
            {
                if (__state.valid)
                {
                    // Evaluated without short-circuiting: a veto has to name
                    // the sub-gate that caused it, or the false positive is
                    // indistinguishable from a real change in the log.
                    bool gateReady = Ready();
                    bool gateReleased = __state.released == Interlocked.Read(ref _released);
                    bool gateSame = Equal(__state.before, Capture(__state.owner.Target as DAZCharacterSelector));
                    __state.valid = gateReady && gateReleased && gateSame;
                    gates = (gateReady ? "1" : "0") + (gateReleased ? "1" : "0") + (gateSame ? "1" : "0");
                }
            }
            catch (Exception e) { __state.valid = false; Log("native retained at completion: " + e.Message); }
            if (!__state.valid && gates != null)
                Log("preset verify veto: gates[ready/rel/eq]=" + gates +
                    " released=" + __state.released + "->" + Interlocked.Read(ref _released));
            if (__state.owner != null) Log("preset completion: kind=" + __state.kind + " verified=" + __state.valid +
                " epoch=" + __state.activity + "->" + Interlocked.Read(ref _activity) +
                " events[queue/load/deregister/noop]=" + (Interlocked.Read(ref _imageEvents) - __state.images) + "/" +
                (Interlocked.Read(ref _loadEvents) - __state.loads) + "/" +
                (Interlocked.Read(ref _deregisterEvents) - __state.deregisters) + "/" +
                (Interlocked.Read(ref _noopDeregisters) - __state.noops) +
                " heapMiB=" + ((GC.GetTotalMemory(false) - __state.managedBegin) / (1024 * 1024)) +
                " restoreMs=" + __state.clock.ElapsedMilliseconds +
                " window=[" + (__state.events == null ? "-" : string.Join(";", __state.events.ToArray())) + "]");
            __state.clock.Stop();
            if (__state.owner != null && __state.success)
                MemoryRetentionReport.Request("preset-completion:" + __state.kind);
            if (__state.owner != null && __state.success && ManagedRequest != null &&
                (__state.released != Interlocked.Read(ref _released) || __state.images != Interlocked.Read(ref _imageEvents)))
                ManagedRequest(false, true, "preset-completion");
            return __exception;
        }

        private static void TagIterator(DAZCharacterSelector __instance, IEnumerator __result)
        {
            PruneTickets();
            if (__result == null || _active == null || !_active.valid || _active.owner == null ||
                !ReferenceEquals(_active.owner.Target, __instance) || Tickets.Count >= 64) return;
            Tickets.Add(new Pending { iterator = new WeakReference(__result), ticket = _active,
                created = Time.realtimeSinceStartup });
        }

        private static IEnumerator CreateSweep(DAZCharacterSelector owner, string origin)
        {
            IEnumerator iterator = _factory(owner);
            TagIterator(owner, iterator);
            if (ManagedRequest != null && (Enabled == null || Enabled.Value) && origin == "OnCharacterLoaded" && iterator != null)
            {
                Pending pending = FindPending(iterator, false);
                if (pending != null) pending.automatic = true;
                else if (Tickets.Count < 64)
                    Tickets.Add(new Pending { iterator = new WeakReference(iterator), automatic = true,
                        created = Time.realtimeSinceStartup });
            }
            Log("request=" + origin + " presetScope=" + (_active != null && _active.valid));
            return iterator;
        }

        private static IEnumerable<CodeInstruction> RouteFactory(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            int count = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var method = code[i].operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(DAZCharacterSelector) ||
                    method.Name != "UnloadUnusedAssetsDelayed") continue;
                if (code[i].blocks.Count != 0) throw new InvalidOperationException("cleanup factory exception boundary");
                var origin = new CodeInstruction(OpCodes.Ldstr, __originalMethod.Name);
                origin.labels.AddRange(code[i].labels);
                code[i].labels.Clear();
                code[i].opcode = OpCodes.Call;
                code[i].operand = typeof(PresetSweepGate).GetMethod("CreateSweep", BindingFlags.Static | BindingFlags.NonPublic);
                code.Insert(i++, origin);
                count++;
            }
            if (count != 1) throw new InvalidOperationException("cleanup factory call anchors: " + count);
            return code;
        }

        private static void PruneTickets()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = Tickets.Count - 1; i >= 0; i--)
                if (!Tickets[i].iterator.IsAlive || (!Tickets[i].automatic && now - Tickets[i].created >= 30f)) Tickets.RemoveAt(i);
        }

        private static IEnumerable<CodeInstruction> RouteSweep(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int site = -1, gcSite = -1, calls = 0, gc = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var m = code[i].operand as MethodInfo;
                if (m == null || code[i].opcode != OpCodes.Call) continue;
                if (m.DeclaringType == typeof(GC) && m.Name == "Collect" && m.GetParameters().Length == 0)
                {
                    gc++;
                    gcSite = i;
                    code[i].operand = typeof(PresetSweepGate).GetMethod("Collect", BindingFlags.Static | BindingFlags.NonPublic);
                }
                if (m.DeclaringType == typeof(Resources) && m.Name == "UnloadUnusedAssets" && m.GetParameters().Length == 0)
                { site = i; calls++; }
            }
            // Null is valid only because this exact caller discards the AsyncOperation.
            if (calls != 1 || gc != 1 || site + 1 >= code.Count || code[site + 1].opcode != OpCodes.Pop ||
                gcSite <= site || code[site].blocks.Count != 0 || code[gcSite].blocks.Count != 0)
                throw new InvalidOperationException("character UUA/GC anchor changed");
            // Per-iterator identity, not a global skip flag: interleaved coroutines
            // and manual requests must never inherit another preset's GC decision.
            var gcLoad = new CodeInstruction(OpCodes.Ldarg_0);
            gcLoad.labels.AddRange(code[gcSite].labels);
            code[gcSite].labels.Clear();
            code.Insert(gcSite, gcLoad);
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(code[site].labels);
            code[site].labels.Clear();
            code[site].operand = typeof(PresetSweepGate).GetMethod("Sweep", BindingFlags.Static | BindingFlags.NonPublic);
            code.Insert(site, load);
            return code;
        }

        private static Pending FindPending(object iterator, bool remove)
        {
            PruneTickets();
            for (int i = Tickets.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Tickets[i].iterator.Target, iterator))
                {
                    Pending pending = Tickets[i];
                    if (remove) Tickets.RemoveAt(i);
                    return pending;
                }
            return null;
        }

        private static string GcReason(Pending pending, float now, bool headroom, long managedBytes)
        {
            if (SkipUnchangedGC != null && !SkipUnchangedGC.Value) return "GC gate disabled";
            if (pending == null || !pending.sweepSkipped)
            {
                // "A sweep ran" is not evidence that this transaction left
                // managed garbage behind: an unverified ticket is often a
                // no-op with zero releases, and a full collect on that path
                // measured 3494ms to return 84MiB. Require the same managed
                // growth the deferred branch below already requires.
                if (_hasGc && managedBytes > 0 && managedBytes - _lastGcBytes < GcGrowthLimit)
                    return null;
                return "native sweep/unscoped";
            }
            string reason = VerifyTransaction(pending.ticket, headroom);
            if (reason != null) return reason;
            if (!_hasGc) return "no GC baseline";
            if (now - _lastGcTime >= 120f) return "GC time bound";
            if (managedBytes <= 0 || managedBytes - _lastGcBytes >= GcGrowthLimit) return "managed growth/unknown";
            return null;
        }

        // Memory pressure alone is not a reason to collect: a pressure-forced
        // full GC on a 20GB+ Boehm heap has been measured taking ~5.6s and
        // freeing ~0.5GB, while pulling the whole heap back into the working
        // set. Let pressure fire only when the previous collect actually
        // returned memory; otherwise the deferred request waits for the async
        // tail or the 30s bound.
        private static bool GcPaysOff()
        {
            if (!_hasGc) return true;
            long floor = Math.Max(64L * 1024 * 1024, _lastGcBefore / 20);
            return _lastGcYield >= floor;
        }

        private static void Collect(object iterator)
        {
            Pending pending = FindPending(iterator, true);
            string reason;
            try
            {
                bool headroom = MemoryHeadroom();
                reason = GcReason(pending, Time.realtimeSinceStartup, headroom, GC.GetTotalMemory(false));
                if (reason == null)
                {
                    if (pending != null && pending.ticket != null)
                    { Log("skip redundant preset GC: kind=" + pending.ticket.kind); return; }
                    if (pending != null && pending.automatic)
                    { Log("skip redundant automatic GC: request already tracked at load tail"); return; }
                    // Preserve the previous unscoped/manual collection behavior,
                    // without depending on a null-ticket logging exception.
                    reason = "native sweep/unscoped";
                }
                if (ManagedRequest != null && pending != null && pending.automatic &&
                    ManagedRequest(false, true, "character-cleanup")) return;
                // A full mark inside the click window is the cost the user
                // still feels: 20 consecutive swaps each paid 4.0s here
                // (measured 3.9-4.1s, freeing 0.4-0.9GiB) while the 1.5GiB/60s
                // idle watchdog returns 2.2-3.8GiB for the same 4s.
                // With headroom, hand the request to the watchdog instead of
                // paying it between the click and the character appearing.
                // Memory pressure still collects immediately.
                if (SwapGcToIdle != null && SwapGcToIdle.Value)
                {
                    long swapFreeB = -1L;
                    long swapCapB = -1L;
                    if (!SwapGcMustCollect(out swapFreeB, out swapCapB))
                    {
                        Log("hand preset GC to the idle watchdog: heap freeMiB=" +
                            (swapFreeB / (1024 * 1024)) + " of " +
                            (swapCapB / (1024 * 1024)) +
                            "; no full mark inside the preset window");
                        return;
                    }
                }
                // The native coroutine fires while new images are still decoding.
                // Collecting then leaves their temporary buffers to the next click.
                // Move that SAME requested GC to the load tail, never add a timer GC.
                if (CanDeferGc(pending, headroom))
                {
                    QueueGc(pending.ticket.owner);
                    Log("defer native preset GC until async tail settles; reason=" + reason);
                    return;
                }
            }
            catch (Exception e) { reason = "GC verification " + e.GetType().Name; }
            RunGc(reason);
        }

        // The 75% load line answered the wrong question before an inline
        // collect. With the external trimmer switched off this session sits
        // at 82-88% permanently, so the hand-off above never fired and every
        // swap kept paying the full mark: 20 consecutive swaps measured
        // 3.9-4.1s of freeze inside the click window to return 0.4-1.2GiB,
        // while the same 4s at idle returns 1.5-3.8GiB. Only the heap's own
        // room and the commit backing it decide whether it has to happen
        // here; the OS load number is reported, not obeyed.
        private static bool SwapGcMustCollect(out long freeB, out long capB)
        {
            if (HeapNearExhaustion(out freeB, out capB)) return true;
            var s = new MemoryStatus();
            s.length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (!GlobalMemoryStatusEx(ref s)) return true; // unknown: keep the old behaviour
            return s.availablePhysical < 2UL * 1024 * 1024 * 1024 ||
                s.availablePageFile < 8UL * 1024 * 1024 * 1024;
        }

        private static bool CanDeferGc(Pending pending, bool headroom)
        {
            return (Enabled == null || Enabled.Value) && (SkipUnchangedGC == null || SkipUnchangedGC.Value) &&
                headroom && pending != null && pending.ticket != null && pending.ticket.completed &&
                pending.ticket.success && pending.ticket.owner != null &&
                (!Ready() || (_lastSweep != null && !_lastSweep.isDone));
        }

        private static void QueueGc(WeakReference owner)
        {
            float now = Time.realtimeSinceStartup;
            if (!_gcPending) _gcRequestedAt = now; // repeated loads never extend the deadline
            _gcPending = true;
            _gcOwner = owner;
            _gcQuietSince = -1f;
            _gcNextCheck = now;
            _gcSeenActivity = Interlocked.Read(ref _activity);
            _gcYieldStretchLogged = false;
        }

        private static string DeferredGcReason(float now, bool ready, bool headroom, long activity)
        {
            if (!_gcPending) return null;
            if (!headroom && GcPaysOff()) return "deferred GC memory pressure";
            if (now - _gcRequestedAt >= 30f) return "deferred GC 30s bound";
            if (!ready || activity != _gcSeenActivity)
            {
                _gcSeenActivity = activity;
                _gcQuietSince = -1f;
                return null;
            }
            if (_gcQuietSince < 0f) _gcQuietSince = now;
            if (now - _gcQuietSince < 0.25f) return null;
            // The tail is quiet, but the last collect barely paid for itself:
            // leave this request pending for the 30s bound, the pressure line
            // or the idle watchdog instead of paying another full mark right
            // after a swap. Measured yields for the same 3.7s: 31MiB..1.5GiB.
            if (GcYieldGate != null && GcYieldGate.Value && !GcPaysOff())
            {
                if (!_gcYieldStretchLogged)
                {
                    _gcYieldStretchLogged = true;
                    Log("defer preset GC: last collect returned " +
                        (_lastGcYield / 1048576) + "MiB against a " +
                        (_lastGcBefore / 1048576) + "MiB heap; waiting for the " +
                        "30s bound, memory pressure or idle");
                }
                return null;
            }
            return "preset async tail settled";
        }

        internal static void Tick()
        {
            // Settlement must be observed on every frame: every branch
            // below returns early in the common case, so a settle notice
            // parked inside Ready() is never reached and the submit-to-done
            // wall time - the figure the long frames are made of - is lost.
            NoteSweepSettled();
            float now = Time.realtimeSinceStartup;
            if (ManagedPending != null && ManagedPending()) return;
            if (_active != null || now < _gcNextCheck) return;
            if (CompletionReady != null && !CompletionReady()) return;
            // A submitted sweep owns the frame's mark work; do not stack the
            // idle GC on top of the same frame.
            if (_sweepPending && RunDeferredSweep(now)) return;
            if (!_gcPending)
            {
                // A finished transaction leaves nothing pending, so the
                // deferred path below has nothing to do - but the session is
                // still allocating. Checked once a second; it reads two
                // counters and does nothing else.
                _gcNextCheck = now + 1f;
                if (IdleGcDue(now)) RunGc("idle growth watchdog");
                return;
            }
            _gcNextCheck = now + 0.25f;
            string reason;
            try
            {
                bool headroom = MemoryHeadroom();
                bool enabled = (Enabled == null || Enabled.Value) && (SkipUnchangedGC == null || SkipUnchangedGC.Value);
                var owner = _gcOwner == null ? null : _gcOwner.Target as DAZCharacterSelector;
                bool ready = Ready() && (_lastSweep == null || _lastSweep.isDone) &&
                    (owner == null || Capture(owner) != null);
                reason = enabled ? DeferredGcReason(now, ready, headroom, Interlocked.Read(ref _activity)) : "GC gate disabled with pending request";
                if (reason == null) return;
            }
            catch (Exception e) { reason = "deferred GC verification " + e.GetType().Name; }
            RunGc(reason);
        }

        private static void RunGc(string reason)
        {
            MemoryRetentionReport.NoteGc(reason);
            _gcPending = false;
            _gcOwner = null;
            long before = GC.GetTotalMemory(false);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                GC.Collect();
                _lastGcBytes = GC.GetTotalMemory(false);
                _lastGcBefore = before;
                _lastGcYield = before > _lastGcBytes ? before - _lastGcBytes : 0;
                _lastGcTime = Time.realtimeSinceStartup;
                _hasGc = true;
                MemoryRetentionReport.Request("post-gc:" + reason);
                // Yield gate (see IdleGcStretchMaxStrikes): any collect
                // that pays off re-arms the idle band; only an idle
                // collect that does not pay stretches it.
                long payFloor = Math.Max(64L * 1024 * 1024, before / 20);
                if (_lastGcYield >= payFloor) _idleGcStrikes = 0;
                else if (reason == "idle growth watchdog" &&
                         _idleGcStrikes < IdleGcStretchMaxStrikes)
                {
                    _idleGcStrikes++;
                    Log("idle GC band stretched to " +
                        (IdleGcBand() / (1024 * 1024)) +
                        "MiB: last idle collect returned " +
                        (_lastGcYield / 1048576) + "MiB against a " +
                        (before / (1024 * 1024)) + "MiB heap (<5%)");
                }
                _idleBytes = _lastGcBytes;
                _idleAt = _lastGcTime;
            }
            finally { Log("native GC ms=" + clock.ElapsedMilliseconds + "; reason=" + reason +
                "; managedMiB=" + before / (1024 * 1024) + "->" + _lastGcBytes / (1024 * 1024) +
                "; freedMiB=" + ((before > _lastGcBytes ? before - _lastGcBytes : 0) / (1024 * 1024)) + MonoGcProbe.Suffix()); }
        }

        private static AsyncOperation Sweep(object iterator)
        {
            Pending pending = FindPending(iterator, false);
            if (pending != null) pending.sweepSkipped = false;
            Ticket ticket = pending == null ? null : pending.ticket;
            string reason = "unscoped/manual";
            try
            {
                reason = Reason(ticket, Time.realtimeSinceStartup, MemoryHeadroom());
                if (reason == null)
                {
                    ticket.consumed = true;
                    pending.sweepSkipped = true;
                    _skipped++;
                    Log("skip unchanged preset UUA: kind=" + ticket.kind + " count=" + _skipped + "; GC budget checked separately");
                    return null;
                }
                if (ManagedRequest != null && pending != null && pending.automatic &&
                    ManagedRequest(true, true, "character-cleanup")) return null;
                // The mark this call would run is global: its price does not
                // depend on when it runs, only on whether the user is waiting.
                // Native fires it from inside the restore, which is where the
                // frame budget is already spent (measured: 8.68s wall, 6.3s
                // freeze, inside a 10.2s no-change reload). Queue it instead
                // and submit at the next quiet moment; the debt stays on the
                // books until it really runs.
                if (DeferSweepReason(ticket))
                {
                    if (ticket != null) ticket.consumed = true;
                    if (!_sweepPending)
                    {
                        _sweepPending = true;
                        _sweepPendingReason = reason;
                        _sweepPendingAt = Time.realtimeSinceStartup;
                        LoadWindow.NoteDeferred("Sweep");
                        Log("defer native character sweep until the preset window closes: reason=" + reason);
                    }
                    return null;
                }
            }
            catch (Exception e) { reason = "verification " + e.GetType().Name; }
            if (ticket != null) ticket.consumed = true;
            return SubmitSweep(reason);
        }

        // Only a request that came from a preset transaction is moved: a manual
        // cleanup request carries no ticket and keeps its old behaviour, and so
        // does an untagged iterator (hot reload).
        private static bool DeferSweepReason(Ticket ticket)
        {
            if (DeferSweep != null && !DeferSweep.Value) return false;
            if (_sweepPending) return true;
            if (ticket == null || !ticket.completed || !ticket.success || ticket.owner == null) return false;
            return LoadWindow.PresetBusy || Time.realtimeSinceStartup - _presetEndedAt < PresetSettleSeconds;
        }

        // Native UUA is a synchronous full-heap mark, and it is the largest
        // single item in a swap's wall time. Time the submit so the accounting
        // stays complete.
        private static AsyncOperation SubmitSweep(string reason)
        {
            long released = Interlocked.Read(ref _released);
            var sweepClock = System.Diagnostics.Stopwatch.StartNew();
            var op = Resources.UnloadUnusedAssets();
            sweepClock.Stop();
            PresetCleanupCoalescer.NoteSweep(op);
            _lastSweep = op;
            _sweepReleased = released;
            _lastSweepTime = Time.realtimeSinceStartup;
            _sweepSettleLogged = false;
            _skipped = 0;
            Log("run native character sweep: " + reason + "; releases=" + released + " activity=" + Interlocked.Read(ref _activity) +
                " sweepSubmitMs=" + sweepClock.ElapsedMilliseconds);
            return op;
        }

        private static bool RunDeferredSweep(float now)
        {
            float age = now - _sweepPendingAt;
            if (age < SweepDeferBoundSeconds)
            {
                if (!Ready()) return false;
                if (LoadWindow.PresetBusy || now - _presetEndedAt < PresetSettleSeconds) return false;
                // Never stack two global marks; the settle notice is the proof
                // the previous one finished.
                if (_lastSweep != null && !_lastSweep.isDone) return false;
            }
            string reason = _sweepPendingReason;
            _sweepPending = false;
            _sweepPendingReason = null;
            SubmitSweep(reason + " (deferred " + (long)(age * 1000f) + "ms)");
            return true;
        }

        private static string Reason(Ticket ticket, float now, bool headroom)
        {
            if (ticket != null && ticket.consumed) return "consumed transaction";
            string reason = VerifyTransaction(ticket, headroom);
            if (reason != null) return reason;
            if (_lastSweep == null || !_lastSweep.isDone) return "no completed sweep";
            // Loading completion/texture refcount notifications after a prior
            // sweep do not themselves create UUA debt. Native last-user texture
            // release already Destroy()s its texture. Instance unloads can leave
            // asset/bundle references and must still be covered by a real sweep.
            if (_sweepReleased != Interlocked.Read(ref _released)) return "uncovered instance release";
            if (now - _lastSweepTime >= SweepHardBoundSeconds) return "periodic full sweep";
            return null;
        }

        private static string VerifyTransaction(Ticket ticket, bool headroom)
        {
            if (Enabled != null && !Enabled.Value) return "disabled";
            // The debt ledger below is the verdict. The begin/end gates are
            // deliberately noisy (a texture notification or a busy image
            // pipeline flips them without any resource change), and requiring
            // them here made this whole method unreachable in the veto case -
            // it always returned before the release/instance comparison.
            if (ticket == null || !ticket.completed) return "unverified transaction";
            if (!ticket.success) return "failed transaction";
            if (!Ready()) return "loading";
            // Release debt is the ledger that matters for UUA; async bookkeeping
            // events alone do not turn a same-state transaction into a real one.
            bool releasedChanged = ticket.released != Interlocked.Read(ref _released);
            bool instanceChanged = !Equal(ticket.before, Capture(ticket.owner.Target as DAZCharacterSelector));
            // No debt → nothing for UUA to reclaim, so memory pressure is not
            // a reason to burn ~5s on an empty sweep+GC.
            if (!releasedChanged && !instanceChanged) return null;
            if (!headroom) return "memory pressure/unknown";
            if (releasedChanged) return "instance release during transaction";
            return "instance change/not ready";
        }

        private static bool MemoryHeadroom()
        {
            var s = new MemoryStatus();
            s.length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            return GlobalMemoryStatusEx(ref s) && HasHeadroom(s.load, s.availablePhysical, s.availablePageFile);
        }

        private static bool HasHeadroom(uint load, ulong availablePhysical, ulong availableCommit)
        {
            // Local external trimmer starts at 80%; act BEFORE it, not at 85%.
            return load < 75 && availablePhysical >= 4UL * 1024 * 1024 * 1024 &&
                availableCommit >= 8UL * 1024 * 1024 * 1024;
        }

        private static int[] Capture(DAZCharacterSelector sel)
        {
            if (sel == null || sel.containingAtom == null || sel.containingAtom.isPreparingToPutBackInPool ||
                !sel.gameObject.activeInHierarchy || sel.selectedCharacter == null || !sel.selectedCharacter.ready) return null;
            var flag = LoadFlag.GetValue(sel) as AsyncFlag;
            if (flag != null && !flag.Raised) return null;
            var run = Run.GetValue(sel) as UnityEngine.Object;
            var instance = Instance.GetValue(sel.selectedCharacter) as Transform;
            if (run == null || instance == null) return null;
            var ids = new List<int> { sel.selectedCharacter.GetInstanceID(), instance.GetInstanceID(), run.GetInstanceID() };
            // Read backing arrays: public getters call Init(). No per-frame catalog work.
            foreach (var f in Catalogs)
            {
                var items = f.GetValue(sel) as DAZDynamicItem[];
                if (items == null) continue;
                foreach (var item in items)
                {
                    if (item == null || !item.active) continue;
                    var live = Instance.GetValue(item) as Transform;
                    if (!item.ready || !item.enabled || !item.gameObject.activeInHierarchy || live == null) return null;
                    ids.Add(item.GetInstanceID());
                    ids.Add(live.GetInstanceID());
                }
            }
            return ids.ToArray();
        }

        private static bool Equal(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _factory = null;
            _active = null;
            Tickets.Clear();
            _lastSweep = null;
            _sweepSettleLogged = false;
            _activity = _released = _sweepReleased = 0;
            _imageEvents = _loadEvents = _deregisterEvents = _noopDeregisters = 0;
            _lastSweepTime = 0;
            _skipped = 0;
            _lastGcBytes = 0;
            _lastGcTime = 0;
            _hasGc = false;
            _idleGcStrikes = 0;
            _gcPending = false;
            _gcOwner = null;
            _gcRequestedAt = _gcNextCheck = 0;
            _gcQuietSince = -1f;
            _gcSeenActivity = 0;
            _idleBytes = 0;
            _idleAt = 0f;
            _sweepPending = false;
            _sweepPendingReason = null;
            _sweepPendingAt = 0f;
            _presetEndedAt = 0f;
            ManagedRequest = null; CompletionReady = null; ManagedPending = null;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-sweep] " + message);
        }
    }
}
