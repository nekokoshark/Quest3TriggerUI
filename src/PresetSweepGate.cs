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
        private static int _skipped;
        private static long _lastGcBytes;
        private static float _lastGcTime;
        private static bool _hasGc;
        private const long GcGrowthLimit = 256L * 1024 * 1024;
        private static bool _gcPending;
        private static WeakReference _gcOwner;
        private static float _gcRequestedAt, _gcQuietSince, _gcNextCheck;
        private static long _gcSeenActivity;

        private sealed class Ticket
        {
            internal Ticket previous;
            internal WeakReference owner;
            internal int[] before;
            internal long activity;
            internal long images, loads, deregisters, noops;
            internal long released;
            internal ResourceLedger.ReleaseSnapshot releaseSnapshot;
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
        }

        // A proof that was blocked only by "ledger still walking" is parked
        // here and re-evaluated from Tick once the ledger drains; a timeout
        // or any new release debt falls back to a real sweep — the deferred
        // request is never silently dropped.
        // The wait is bounded by liveness, not the clock: keep waiting while
        // the ledger is still producing evidence (revisions/pending moving),
        // and sweep once the pipeline has clearly stalled. ProofWaitSeconds
        // remains only as a far outer bound for pathological churn.
        private const float ProofWaitSeconds = 60f;
        private const float ProofStallSeconds = 6f;
        // Skipped proofs tolerate a handful of unverifiable assets each; the
        // running total is real UUA debt (orphans only UUA can reach). The
        // budget caps it and the next proof pays a sweep; an idle catch-up
        // clears the tab when the scene has been quiet for a minute.
        private const int DebtBudgetAssets = 96;
        private const float IdleSweepSeconds = 60f;
        private static int _skippedDebt;
        private static float _idleSince = -1f;
        private static long _idleActivity = -1;
        private static Ticket _proofTicket;
        private static long _proofReleased, _proofSignature;
        private static float _proofDeadline, _proofNext, _proofStalledAt;
        private static string _proofLastReason;

        // While a proof is parked the release-audit must run even when the
        // ledger is not settled — otherwise pending-reconciliation never
        // resolves inside the deadline and the deferral always times out.
        internal static bool ProofPending { get { return _proofTicket != null; } }

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
                Log("installed; appearance/clothing resource-state gate; UUA release debt/600s bound; GC growth 256MiB/120s; async-tail GC settlement; RAM pressure 75%; manual cleanup retained");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static bool Ready()
        {
            return SuperController.singleton != null && !SuperController.singleton.isLoading &&
                !SceneLoadAccelerator.SceneLoadActive && !WardrobeJanitor.ImagesBusy();
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
                __state.before = Capture(sel);
                __state.releaseSnapshot = ResourceLedger.BeginReleaseSnapshot(sel);
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
            __state.valid &= __state.success;
            // Recheck after all Restore/LateRestore/PostRestore/events, not just
            // geometry. Activity noise (texture queue/deregister, late
            // OnLoadComplete, no-change character sets) does not create sweep
            // debt: real divergence is caught by the instance snapshot and the
            // release counter, which ignore bookkeeping-only events.
            try
            {
                if (__state.valid) __state.valid = Ready() &&
                    __state.released == Interlocked.Read(ref _released) &&
                    Equal(__state.before, Capture(__state.owner.Target as DAZCharacterSelector));
            }
            catch (Exception e) { __state.valid = false; Log("native retained at completion: " + e.Message); }
            if (__state.owner != null) Log("preset completion: kind=" + __state.kind + " verified=" + __state.valid +
                " epoch=" + __state.activity + "->" + Interlocked.Read(ref _activity) +
                " events[queue/load/deregister/noop]=" + (Interlocked.Read(ref _imageEvents) - __state.images) + "/" +
                (Interlocked.Read(ref _loadEvents) - __state.loads) + "/" +
                (Interlocked.Read(ref _deregisterEvents) - __state.deregisters) + "/" +
                (Interlocked.Read(ref _noopDeregisters) - __state.noops) +
                " restoreMs=" + __state.clock.ElapsedMilliseconds +
                " window=[" + (__state.events == null ? "-" : string.Join(";", __state.events.ToArray())) + "]");
            __state.clock.Stop();
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
                if (!Tickets[i].iterator.IsAlive || now - Tickets[i].created >= 30f) Tickets.RemoveAt(i);
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
            if (pending == null || !pending.sweepSkipped) return "native sweep/unscoped";
            string reason = VerifyTransaction(pending.ticket, true);
            if (reason != null) return reason;
            // A verified no-change transaction produced zero garbage — there
            // is nothing for a forced GC to time well, and at a ~32GB heap
            // !headroom is permanently true, so the pressure bound fired a
            // 6s+ GC on every no-op load. Real pressure is still collected by
            // the runtime's own allocation-driven GC.
            return null;
        }

        private static void Collect(object iterator)
        {
            Pending pending = FindPending(iterator, true);
            string reason;
            try
            {
                bool headroom = MemoryHeadroom();
                reason = GcReason(pending, Time.realtimeSinceStartup, headroom, GC.GetTotalMemory(false));
                if (reason == null) { Log("skip redundant preset GC: kind=" + pending.ticket.kind); return; }
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
        }

        private static string DeferredGcReason(float now, bool ready, bool headroom, long activity)
        {
            if (!_gcPending) return null;
            if (!headroom) return "deferred GC memory pressure";
            if (now - _gcRequestedAt >= 30f) return "deferred GC 30s bound";
            if (!ready || activity != _gcSeenActivity)
            {
                _gcSeenActivity = activity;
                _gcQuietSince = -1f;
                return null;
            }
            if (_gcQuietSince < 0f) _gcQuietSince = now;
            return now - _gcQuietSince >= 0.25f ? "preset async tail settled" : null;
        }

        internal static void Tick()
        {
            float tickNow = Time.realtimeSinceStartup;
            UuaSweepTelemetry.Tick(Interlocked.Read(ref _released));
            if (_proofTicket != null && tickNow >= _proofNext)
            {
                _proofNext = tickNow + 0.25f;
                ResolveDeferredProof(tickNow);
            }
            // Idle catch-up: tolerated stragglers are real debt only UUA can
            // reach. Verified no-change loads never collect it, so when the
            // scene has been quiet for a minute, settle the tab ourselves —
            // a sweep while the user stands still in VR costs nothing felt.
            long act = Interlocked.Read(ref _activity);
            if (_skippedDebt > 0 && _proofTicket == null &&
                (_lastSweep == null || _lastSweep.isDone) &&
                Ready() && !WardrobeJanitor.ImagesBusy())
            {
                if (act != _idleActivity) { _idleActivity = act; _idleSince = tickNow; }
                else if (tickNow - _idleSince >= IdleSweepSeconds)
                {
                    _idleSince = tickNow;
                    SubmitProofSweep(null, "idle debt catch-up; skippedDebt=" + _skippedDebt);
                }
            }
            else { _idleActivity = act; _idleSince = tickNow; }
            // No pending native request: no polling, scans or spontaneous GC.
            if (!_gcPending || Time.realtimeSinceStartup < _gcNextCheck || _active != null) return;
            float now = Time.realtimeSinceStartup;
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

        // "Evidence still being gathered" is not a rejection: both strings
        // describe the ledger's own async pipelines not having finished yet.
        private static bool ProofStillSettling(string proof)
        {
            return proof != null && (proof.IndexOf("ledger still walking",
                    StringComparison.Ordinal) == 0 ||
                proof.IndexOf("pending-reconciliation",
                    StringComparison.Ordinal) >= 0 ||
                proof.IndexOf("still settling", StringComparison.Ordinal) >= 0);
        }

        private static void ResolveDeferredProof(float now)
        {
            Ticket ticket = _proofTicket;
            long released = Interlocked.Read(ref _released);
            // Debt that appeared after the snapshot cannot be covered by this
            // proof — abandon it and pay the sweep now.
            if (released != _proofReleased)
            {
                SubmitProofSweep(ticket, "deferred proof cancelled: new release debt +" +
                    (released - _proofReleased));
                return;
            }
            if (_lastSweep != null && !_lastSweep.isDone)
            {
                // An in-flight sweep submitted after the deferral already
                // covers the parked debt — drop the proof, no second pass.
                if (_sweepReleased >= _proofReleased)
                {
                    _proofTicket = null;
                    Log("deferred UUA proof dropped: in-flight sweep covers the debt");
                    return;
                }
                // It predates the debt — wait it out, then re-evaluate.
                return;
            }
            string proof;
            int uncovered;
            try
            {
                if (ResourceLedger.TryProveRelease(
                    ticket == null ? null : ticket.releaseSnapshot, out proof, out uncovered))
                {
                    // Covered but over budget: the tolerated stragglers are
                    // real debt only a sweep can reach — pay it now.
                    if (_skippedDebt + uncovered > DebtBudgetAssets)
                    {
                        SubmitProofSweep(ticket, "release debt budget " +
                            (_skippedDebt + uncovered) + ">" + DebtBudgetAssets);
                        return;
                    }
                    _skippedDebt += uncovered;
                    _proofTicket = null;
                    _skipped++;
                    Log("skip covered character UUA: " + proof + "; debt=" +
                        _skippedDebt + " (deferred proof settled)");
                    return;
                }
            }
            catch (Exception e) { proof = "proof threw " + e.GetType().Name; }
            // Both transient states mean "evidence still being gathered" —
            // the ledger walking the new owners, or the release audit not
            // finished with a retired asset yet. Wait while that evidence is
            // demonstrably still arriving; a stalled pipeline (or the far
            // outer bound) pays the sweep instead of waiting forever.
            if (ProofStillSettling(proof))
            {
                // Surface what is actually holding the proof — the last load
                // burned the full outer bound and the logs could not say
                // whether a churned owner or unsettled assets were the cause.
                if (proof != _proofLastReason)
                { _proofLastReason = proof; Log("deferred proof waiting: " + proof); }
                // Every "still walking" blocker is an EXTERNAL wait, not a
                // pipeline stall: an unwalkable owner waits on the character
                // load, and a dirty owner is re-dirtied by the texture-event
                // tail (or the ledger Tick is frozen by isLoading) — either
                // way its walk restarts without consuming nodes, so the work
                // signature cannot see it. Only the outer bound applies.
                bool waitsOnLoad = proof.IndexOf("ledger still walking", StringComparison.Ordinal) >= 0;
                long sig = ResourceLedger.ProgressSignature;
                if (sig != _proofSignature) { _proofSignature = sig; _proofStalledAt = now; }
                if (now < _proofDeadline &&
                    (waitsOnLoad || now - _proofStalledAt < ProofStallSeconds))
                    return;
                SubmitProofSweep(ticket, waitsOnLoad ? "deferred proof wait bound; last=" + proof :
                    now - _proofStalledAt >= ProofStallSeconds
                    ? "deferred proof stalled: " + proof : "deferred proof wait bound; last=" + proof);
                return;
            }
            SubmitProofSweep(ticket, "deferred proof rejected: " + proof);
        }

        private static void SubmitProofSweep(Ticket ticket, string why)
        {
            _proofTicket = null;
            long released = Interlocked.Read(ref _released);
            var sample = UuaSweepTelemetry.Begin("character",
                ticket == null ? null : ticket.kind,
                why, released, released - _sweepReleased);
            AsyncOperation op;
            try { op = Resources.UnloadUnusedAssets(); }
            catch { UuaSweepTelemetry.Failed(sample); throw; }
            UuaSweepTelemetry.Submitted(sample, op);
            _lastSweep = op;
            _sweepReleased = released;
            _lastSweepTime = Time.realtimeSinceStartup;
            _skipped = 0;
            _skippedDebt = 0;
            Log("run native character sweep: " + why + "; releases=" + released +
                " activity=" + Interlocked.Read(ref _activity));
        }

        private static void RunGc(string reason)
        {
            _gcPending = false;
            _gcOwner = null;
            long before = GC.GetTotalMemory(false);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                GC.Collect();
                _lastGcBytes = GC.GetTotalMemory(false);
                _lastGcTime = Time.realtimeSinceStartup;
                _hasGc = true;
            }
            finally { Log("native GC ms=" + clock.ElapsedMilliseconds + "; reason=" + reason +
                "; managedMiB=" + before / (1024 * 1024) + "->" + _lastGcBytes / (1024 * 1024)); }
        }

        private static AsyncOperation Sweep(object iterator)
        {
            Pending pending = FindPending(iterator, false);
            if (pending != null) pending.sweepSkipped = false;
            Ticket ticket = pending == null ? null : pending.ticket;
            string reason = "unscoped/manual";
            try
            {
                bool proofDeferred;
                reason = Reason(ticket, Time.realtimeSinceStartup, MemoryHeadroom(), out proofDeferred);
                if (proofDeferred)
                {
                    ticket.consumed = true;
                    if (pending != null) pending.sweepSkipped = true;
                    Log("defer covered character UUA: ledger still settling; releases=" +
                        Interlocked.Read(ref _released) + " activity=" + Interlocked.Read(ref _activity));
                    return null;
                }
                if (reason == null)
                {
                    ticket.consumed = true;
                    pending.sweepSkipped = true;
                    _skipped++;
                    Log("skip unchanged preset UUA: kind=" + ticket.kind + " count=" + _skipped + "; GC budget checked separately");
                    return null;
                }
            }
            catch (Exception e) { reason = "verification " + e.GetType().Name; }
            if (ticket != null) ticket.consumed = true;
            // Avoid submitting a second global mark pass while the previous
            // one is still in flight. The new release debt remains uncovered
            // because _sweepReleased is intentionally not advanced here.
            if (ticket != null && _lastSweep != null && !_lastSweep.isDone)
            {
                Log("reuse in-flight character sweep; release debt remains uncovered=" +
                    (Interlocked.Read(ref _released) - _sweepReleased));
                return _lastSweep;
            }
            long released = Interlocked.Read(ref _released);
            var sample = UuaSweepTelemetry.Begin("character", ticket == null ? null : ticket.kind + ":" +
                (ticket.before == null || ticket.before.Length == 0 ? "unknown" : ticket.before[0].ToString()),
                reason, released, released - _sweepReleased);
            AsyncOperation op;
            try { op = Resources.UnloadUnusedAssets(); }
            catch { UuaSweepTelemetry.Failed(sample); throw; }
            UuaSweepTelemetry.Submitted(sample, op);
            _lastSweep = op;
            _sweepReleased = released;
            _lastSweepTime = Time.realtimeSinceStartup;
            _skipped = 0;
            _skippedDebt = 0;
            // A real submission covers every release up to now — any parked
            // proof is superseded.
            if (_proofTicket != null)
            {
                Log("deferred UUA proof superseded by sweep submission");
                _proofTicket = null;
            }
            Log("run native character sweep: " + reason + "; releases=" + released + " activity=" + Interlocked.Read(ref _activity));
            return op;
        }

        private static string Reason(Ticket ticket, float now, bool headroom, out bool deferred)
        {
            deferred = false;
            if (ticket != null && ticket.consumed) return "consumed transaction";
            string reason = VerifyTransaction(ticket, headroom);
            if (reason != null)
            {
                // End marks the ticket invalid when the character actually
                // changed.  That is exactly the transaction for which the
                // release ledger can prove that UUA is covered; requiring
                // VerifyTransaction.valid here made the proof branch
                // unreachable and every real replacement stayed "unverified".
                bool proofLifecycleReady = SuperController.singleton != null &&
                    !SuperController.singleton.isLoading && !SceneLoadAccelerator.SceneLoadActive;
                bool proofOwnerAlive = ticket != null && ticket.owner != null && ticket.owner.IsAlive;
                if ((reason == "unverified transaction" ||
                     reason == "instance release during transaction" ||
                     reason == "instance change/not ready") &&
                    ticket != null && ticket.completed && ticket.success &&
                    proofOwnerAlive && proofLifecycleReady)
                {
                    bool releaseChanged = ticket.released != Interlocked.Read(ref _released);
                    bool instanceChanged = false;
                    try { instanceChanged = !Equal(ticket.before, Capture(ticket.owner.Target as DAZCharacterSelector)); }
                    catch (Exception e) { Log("covered character UUA proof unavailable: capture " + e.GetType().Name); }
                    string proof; int uncovered;
                    if (releaseChanged || instanceChanged)
                    {
                    if (ResourceLedger.TryProveRelease(ticket == null ? null : ticket.releaseSnapshot, out proof, out uncovered))
                    {
                        if (_skippedDebt + uncovered > DebtBudgetAssets)
                            return "release debt budget " + (_skippedDebt + uncovered) + ">" + DebtBudgetAssets;
                        _skippedDebt += uncovered;
                        Log("skip covered character UUA: " + proof + "; debt=" + _skippedDebt +
                            "; releases=" + Interlocked.Read(ref _released));
                        return null;
                    }
                    // UUA fires exactly while the ledger is still walking the
                    // newly loaded person and the release audit has not
                    // reconciled retired assets yet — those are timing
                    // artifacts, not evidence.  Park the proof and let Tick
                    // re-evaluate once the pipelines drain; every other
                    // rejection is real and the sweep proceeds.
                    if (ProofStillSettling(proof) && _proofTicket == null)
                    {
                        _proofTicket = ticket;
                        _proofLastReason = null;
                        _proofReleased = Interlocked.Read(ref _released);
                        _proofSignature = ResourceLedger.ProgressSignature;
                        _proofStalledAt = now;
                        _proofDeadline = now + ProofWaitSeconds;
                        _proofNext = now;
                        deferred = true;
                        return null;
                    }
                    Log("covered character UUA proof rejected: " + proof);
                    }
                    else if (reason == "unverified transaction")
                    {
                        Log("covered character UUA proof unavailable: no release or instance delta");
                    }
                }
                else if ((reason == "unverified transaction" ||
                          reason == "instance release during transaction" ||
                          reason == "instance change/not ready") && ticket != null)
                {
                    Log("covered character UUA proof gate skipped: ticket=" + ticket.completed +
                        " success=" + ticket.success + " ownerAlive=" + proofOwnerAlive +
                        " lifecycleReady=" + proofLifecycleReady + " imagesBusy=" + WardrobeJanitor.ImagesBusy());
                }
                return reason;
            }
            // Verified no-change: this transaction released nothing, so it
            // must not be the bill collector for tolerated-straggler debt —
            // a self-load sweeping releases=21 orphaned it into exactly the
            // stall the gate exists to prevent. Debt is bounded by
            // DebtBudgetAssets on skipping loads plus the idle catch-up.
            return null;
        }

        private static string VerifyTransaction(Ticket ticket, bool headroom)
        {
            if (Enabled != null && !Enabled.Value) return "disabled";
            if (ticket == null || !ticket.completed || !ticket.valid) return "unverified transaction";
            if (!Ready()) return "loading";
            // Release debt is the ledger that matters for UUA; async bookkeeping
            // events alone do not turn a same-state transaction into a real one.
            if (ticket.released != Interlocked.Read(ref _released)) return "instance release during transaction";
            if (!Equal(ticket.before, Capture(ticket.owner.Target as DAZCharacterSelector))) return "instance change/not ready";
            return null;
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
            UuaSweepTelemetry.Shutdown();
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _factory = null;
            _active = null;
            Tickets.Clear();
            _lastSweep = null;
            _activity = _released = _sweepReleased = 0;
            _imageEvents = _loadEvents = _deregisterEvents = _noopDeregisters = 0;
            _lastSweepTime = 0;
            _skipped = 0;
            _skippedDebt = 0;
            _idleSince = -1f;
            _idleActivity = -1;
            _lastGcBytes = 0;
            _lastGcTime = 0;
            _hasGc = false;
            _gcPending = false;
            _gcOwner = null;
            _gcRequestedAt = _gcNextCheck = 0;
            _gcQuietSince = -1f;
            _gcSeenActivity = 0;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-sweep] " + message);
        }
    }
}
