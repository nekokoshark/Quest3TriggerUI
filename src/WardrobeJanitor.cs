using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Native OnDisable already unloads many items. This is a fallback for
    // genuinely resident inactive instances, plus coalesced post-preset asset
    // cleanup. Catalog entries with ready=false are not necessarily loading.
    internal static class WardrobeJanitor
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IdleSeconds;
        internal static ConfigEntry<int> MaxPerFrame;
        internal static ConfigEntry<bool> PurgeOnLoad;
        internal static ConfigEntry<float> PurgeDelaySeconds;
        internal static ConfigEntry<bool> PurgeMorphDeltas;

        private const float ScanEvery = 2f;
        // Catalog entries checked per frame while a scan/purge walk is in
        // flight — the walk itself is cheap, but 34k entries must not run
        // in a single frame. Unload calls use the caller's shared budget.
        private const int CheckPerFrame = 4000;
        private const int MaxPopsPerFrame = 64;

        private static readonly Dictionary<JSONStorableDynamic, float> InactiveSince =
            new Dictionary<JSONStorableDynamic, float>();
        // FIFO: the old LIFO pop + unconditional re-queue let a small set
        // of tail items churn forever while earlier candidates starved.
        private static readonly Queue<JSONStorableDynamic> Pending =
            new Queue<JSONStorableDynamic>();
        private static readonly HashSet<JSONStorableDynamic> PendingSet =
            new HashSet<JSONStorableDynamic>();
        // Items already unloaded. Without this, a successful unload left
        // the stale timestamp in InactiveSince, so every scan re-queued the
        // same dead items and the queue never converged. Re-worn items are
        // removed from Done so their next instance can be reclaimed again.
        private static readonly HashSet<JSONStorableDynamic> Done =
            new HashSet<JSONStorableDynamic>();
        // This contains the complete clothing/hair catalogue, not just worn
        // items. List.Contains in the tracking sweep made each scan O(N^2).
        private static readonly HashSet<JSONStorableDynamic> Scratch =
            new HashSet<JSONStorableDynamic>();
        private static float _nextScan;
        private static bool _wasLoading;

        // Resumable catalogue scan: atom index + item index + phase persist
        // across frames so one scan spreads over ~tens of frames instead of
        // a single ~10ms spike every interval.
        private static List<Atom> _scanAtoms;
        private static DAZCharacterSelector _scanSel;
        private static int _scanAtomIdx, _scanItemIdx, _scanPhase;
        private static bool _scanActive;

        internal static void Tick()
        {
            StaleTextureRequestGuard.Install();
            PresetCleanupCoalescer.Install();
            PresetInstanceReuse.Install();
            BumpNormalRowConverter.Install();
            TryPatch();
            TextureOrphanSweeper.Install();
            var sc = SuperController.singleton;
            // Scene restore can reactivate currently inactive items. Neither
            // catalogue scanning nor destruction belongs in this window.
            if (sc == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive)
            {
                // Rising edge: drop every reference we hold to the previous
                // scene's objects — atoms, preset managers and their JSON
                // graphs must become collectable, not pinned by our statics.
                if (!_wasLoading) { ClearRuntime(); _wasLoading = true; }
                _nextScan = Time.realtimeSinceStartup + ScanEvery;
                return;
            }
            _wasLoading = false;
            int budget = MaxPerFrame != null
                ? Mathf.Max(1, MaxPerFrame.Value) : 2;
            // The preset-load purge lane is independent of AutoUnload —
            // it has its own switch and batches post-preset cleanup.
            DrainPurge(ref budget);
            // Orphaned textureCache entries — cached textures whose
            // requester never registered a use count can't be reclaimed by
            // refcount or UnloadUnusedAssets while the cache roots them.
            TextureOrphanSweeper.Tick();
            if (Enabled == null || !Enabled.Value)
            {
                Pending.Clear(); PendingSet.Clear();
                InactiveSince.Clear(); Done.Clear();
                _scanActive = false;
                _scanAtoms = null; _scanSel = null;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!ImagesBusy()) DrainPending(budget);
            if (_scanActive)
            {
                ScanSlice(now);
                return;
            }
            if (now < _nextScan) return;
            StartScan(sc);
        }

        private static void StartScan(SuperController sc)
        {
            List<Atom> atoms;
            try { atoms = sc.GetAtoms(); } catch { return; }
            if (atoms == null) return;
            _scanAtoms = atoms;
            _scanAtomIdx = 0; _scanItemIdx = 0; _scanPhase = 0;
            _scanSel = null;
            Scratch.Clear();
            _scanActive = true;
        }

        private static void ScanSlice(float now)
        {
            int checkBudget = CheckPerFrame;
            while (checkBudget > 0 && _scanAtoms != null &&
                _scanAtomIdx < _scanAtoms.Count)
            {
                Atom atom = _scanAtoms[_scanAtomIdx];
                if (atom == null || !atom.on || atom.type != "Person" ||
                    !atom.gameObject.activeInHierarchy)
                { Advance(); continue; }
                if (_scanSel == null)
                {
                    _scanSel = atom.GetStorableByID("geometry")
                        as DAZCharacterSelector;
                    // A selector mid-restore reactivates items — skip this
                    // atom entirely this round rather than race it.
                    if (_scanSel == null || Busy(_scanSel))
                    { _scanSel = null; Advance(); continue; }
                }
                DAZDynamicItem[] items = _scanPhase == 0
                    ? (DAZDynamicItem[])_scanSel.clothingItems
                    : (DAZDynamicItem[])_scanSel.hairItems;
                if (items == null || _scanItemIdx >= items.Length)
                { Advance(); continue; }
                var item = items[_scanItemIdx++];
                checkBudget--;
                Collect(item, now);
            }
            if (_scanAtoms == null || _scanAtomIdx >= _scanAtoms.Count)
            {
                _scanActive = false;
                _scanAtoms = null;
                _scanSel = null;
                DropStale();
                Scratch.Clear();
                // Schedule from completion: the next full scan starts one
                // interval after this one finished.
                _nextScan = Time.realtimeSinceStartup + ScanEvery;
            }
        }

        private static void Advance()
        {
            if (_scanPhase == 0) { _scanPhase = 1; _scanItemIdx = 0; }
            else
            {
                _scanPhase = 0; _scanItemIdx = 0;
                _scanAtomIdx++; _scanSel = null;
            }
        }

        private static void Collect(JSONStorableDynamic item, float now)
        {
            if (item == null) return;
            Scratch.Add(item);
            var dyn = item as DAZDynamicItem;
            if (dyn != null && dyn.active)
            {
                // Worn again — its new instance becomes reclaimable later.
                Done.Remove(item);
                InactiveSince.Remove(item);
                return;
            }
            // ready=false means no instantiated resources exist — nothing
            // to reclaim, and queueing it would only burn unload budget.
            if (!item.ready || Done.Contains(item)) return;
            float since;
            if (!InactiveSince.TryGetValue(item, out since))
            {
                InactiveSince[item] = now;
                return;
            }
            if (now - since >= (IdleSeconds != null ? IdleSeconds.Value : 60f) &&
                PendingSet.Add(item))
                Pending.Enqueue(item);
        }

        private static void DropStale()
        {
            if (InactiveSince.Count == 0 && Done.Count == 0) return;
            _dropKeys.Clear();
            _dropDone.Clear();
            foreach (var kv in InactiveSince)
            {
                var item = kv.Key;
                var dyn = item as DAZDynamicItem;
                if (item == null || (dyn != null && dyn.active) ||
                    !Scratch.Contains(item))
                    _dropKeys.Add(item);
            }
            foreach (var item in Done)
                if (item == null || !Scratch.Contains(item))
                    _dropDone.Add(item);
            foreach (var k in _dropKeys) InactiveSince.Remove(k);
            foreach (var k in _dropDone) Done.Remove(k);
            _dropKeys.Clear(); _dropDone.Clear();
        }

        private static readonly List<JSONStorableDynamic> _dropKeys =
            new List<JSONStorableDynamic>();
        private static readonly List<JSONStorableDynamic> _dropDone =
            new List<JSONStorableDynamic>();

        private static void DrainPending(int budget)
        {
            int pops = 0, unloaded = 0;
            while (budget > 0 && Pending.Count > 0 && pops < MaxPopsPerFrame)
            {
                var item = Pending.Peek();
                pops++;
                var dyn = item as DAZDynamicItem;
                if (item == null || (dyn != null && dyn.active))
                {
                    // Destroyed already / worn again — keep it.
                    if (item != null) InactiveSince.Remove(item);
                    Pending.Dequeue(); PendingSet.Remove(item);
                    continue;
                }
                if (!item.ready)
                {
                    // No instance to reclaim — never queue it again.
                    Done.Add(item); InactiveSince.Remove(item);
                    Pending.Dequeue(); PendingSet.Remove(item);
                    continue;
                }
                // Restore in progress on the owning selector — wait it out.
                // Head-of-line blocking is fine: restore windows are seconds.
                var sel = OwnerSelector(item);
                if (sel != null && Busy(sel)) break;
                Pending.Dequeue(); PendingSet.Remove(item);
                // UnloadIfInactive self-checks activeInHierarchy/enabled and
                // is a no-op if the item somehow became active again;
                // UnloadIfNotEnabled covers the component-disabled zombie.
                try
                {
                    item.UnloadIfInactive();
                    if (item.ready) item.UnloadIfNotEnabled();
                }
                catch { }
                if (!item.ready) unloaded++;
                // A done item must not re-queue on its stale timestamp —
                // that was the old infinite churn.
                InactiveSince.Remove(item);
                Done.Add(item);
                budget--;
            }
            if (unloaded > 0)
            {
                Log("slow lane: unloaded " + unloaded +
                    ", " + Pending.Count + " queued");
                KickUnusedAssets();
            }
        }

        // Batched Unity asset cleanup is distinct from explicit Destroy/Release.
        // Trigger only after this janitor actually unloaded instances (or an
        // explicit caller released ownership). An unconditional preset sweep
        // measured 12 seconds with no VRAM benefit. Never clear live caches here.
        private static bool _uuaPending, _uuaRequested;
        private static float _nextUuaKick, _uuaAfter;
        private static int _runtimeGeneration;
        private static readonly FieldInfo RealQueuedImages = typeof(ImageLoaderThreaded)
            .GetField("numRealQueuedImages", BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic);

        internal static void KickUnusedAssets()
        {
            PresetCleanupCoalescer.NoteReleased();
            _uuaRequested = true;
            _uuaAfter = Time.unscaledTime + 15f;
            if (_uuaPending) return;
            var p = Quest3TriggerUIPlugin.Instance;
            if (p == null) return;
            _uuaPending = true;
            try { p.StartCoroutine(UnloadUnusedSoon()); }
            catch { _uuaPending = false; }
        }

        internal static bool ImagesBusy()
        {
            var loader = ImageLoaderThreaded.singleton;
            if (loader == null) return false;
            // Missing queue metadata is not proof that loading has finished.
            return RealQueuedImages == null || (int)RealQueuedImages.GetValue(loader) > 0;
        }

        private static System.Collections.IEnumerator UnloadUnusedSoon()
        {
            int generation = _runtimeGeneration;
            try
            {
                while (generation == _runtimeGeneration)
                {
                    var sc = SuperController.singleton;
                    if (sc != null && !sc.isLoading && !SceneLoadAccelerator.SceneLoadActive &&
                        !ImagesBusy() && _purgeAt.Count == 0 && _purgeJobs.Count == 0 &&
                        Time.unscaledTime >= _uuaAfter && Time.unscaledTime >= _nextUuaKick)
                        break;
                    yield return new WaitForSecondsRealtime(1f);
                }
                if (generation != _runtimeGeneration) yield break;
                MemoryProbe.Snapshot("preset-cleanup-before");
                float started = Time.realtimeSinceStartup;
                _uuaRequested = false;
                AsyncOperation operation = PresetCleanupCoalescer.UnloadForJanitor();
                // Keep pending true until Unity has actually completed the job.
                yield return operation;
                _nextUuaKick = Time.unscaledTime + 60f;
                if (generation != _runtimeGeneration) yield break;
                Log("asset cleanup completed in " +
                    (Time.realtimeSinceStartup - started).ToString("F2") + "s");
                MemoryProbe.Snapshot("preset-cleanup-after");
            }
            finally
            {
                if (generation == _runtimeGeneration)
                {
                    _uuaPending = false;
                    // A preset changed while the AsyncOperation was in flight.
                    // Preserve its request, subject to the same quiet/cooldown.
                    if (_uuaRequested) KickUnusedAssets();
                }
            }
        }

        private static DAZCharacterSelector OwnerSelector(
            JSONStorableDynamic item)
        {
            try
            {
                Atom a = item.containingAtom;
                if (a == null) return null;
                return a.GetStorableByID("geometry") as DAZCharacterSelector;
            }
            catch { return null; }
        }

        // ---------- event-scoped preset transactions ----------
        // Latest successful Appearance/Clothing/Hair restore schedules inactive
        // instance cleanup after the native character/image tail. This is not
        // a rollback of the preset itself: native restore and failures remain
        // native. Only full appearance transactions also purge morph scratch.

        private sealed class PurgeJob
        {
            internal Atom atom;
            internal DAZCharacterSelector sel;
            internal bool morph;
            internal long transaction;
            internal int ci, hi;
            internal int unloaded, guarded, loading, notReady, active, gone, failed;
        }
        private sealed class Transaction
        {
            internal Atom atom;
            internal float due;
            internal bool morph;
            internal long id;
            internal bool superseded;
        }
        private static long _transactionId;
        private static readonly Dictionary<Atom, Transaction> ActiveTransactions = new Dictionary<Atom, Transaction>();
        private static readonly Dictionary<Atom, Transaction> _purgeAt =
            new Dictionary<Atom, Transaction>();
        // Retry count survives the job object: rescheduling through
        // _purgeAt spawns a fresh PurgeJob, so attempts live per-atom and
        // reset when a new person load lands.
        private static readonly Dictionary<Atom, int> _purgeRetries =
            new Dictionary<Atom, int>();
        private static readonly List<PurgeJob> _purgeJobs =
            new List<PurgeJob>();
        private static readonly List<Atom> _dueAtoms = new List<Atom>();
        private static readonly List<KeyValuePair<Atom, float>> _defer =
            new List<KeyValuePair<Atom, float>>();

        internal static void NoteFullPersonLoad(Atom atom)
        {
            if (atom == null || (PurgeOnLoad != null && !PurgeOnLoad.Value)) return;
            CancelTransaction(atom);
            Schedule(new Transaction { atom = atom, morph = true, id = ++_transactionId });
        }

        private static void CancelTransaction(Atom atom)
        {
            _purgeAt.Remove(atom);
            _purgeRetries.Remove(atom);
            for (int i = _purgeJobs.Count - 1; i >= 0; i--)
                if (_purgeJobs[i].atom == atom) _purgeJobs.RemoveAt(i);
        }

        private static void Schedule(Transaction transaction)
        {
            transaction.due = Time.unscaledTime +
                (PurgeDelaySeconds != null ? Mathf.Max(0f, PurgeDelaySeconds.Value) : 5f);
            _purgeAt[transaction.atom] = transaction;
            Log("transaction " + transaction.id + " queued; waits for image/character restore tail");
        }

        private static bool Busy(DAZCharacterSelector sel)
        {
            // insideRestore stays true for the whole synchronous restore —
            // draining mid-restore could destroy an item the preset is
            // about to reactivate.
            try
            {
                if (InsideRestoreField == null || CharacterLoadFlag == null) return true;
                var flag = CharacterLoadFlag.GetValue(sel) as AsyncFlag;
                return (bool)InsideRestoreField.GetValue(sel) || (flag != null && !flag.Raised);
            }
            catch { return true; }
        }

        private static void DrainPurge(ref int budget)
        {
            if (PurgeOnLoad != null && !PurgeOnLoad.Value)
            {
                if (_purgeAt.Count > 0) _purgeAt.Clear();
                if (_purgeJobs.Count > 0) _purgeJobs.Clear();
                _purgeRetries.Clear();
                return;
            }
            // Global image completion is conservative: other atoms may delay
            // reclamation, but no preset's asynchronous tail is cut short.
            if (ImagesBusy()) return;
            float now = Time.unscaledTime;
            if (_purgeAt.Count > 0)
            {
                foreach (var kv in _purgeAt)
                {
                    Atom a = kv.Key;
                    if (a == null || !a.gameObject.activeInHierarchy)
                    { _dueAtoms.Add(a); continue; }
                    if (now < kv.Value.due) continue;
                    var sel = a.GetStorableByID("geometry")
                        as DAZCharacterSelector;
                    if (sel == null) { _dueAtoms.Add(a); continue; }
                    if (Busy(sel))
                    { _defer.Add(new KeyValuePair<Atom, float>(a, now + 1f)); continue; }
                    _purgeJobs.Add(new PurgeJob { atom = a, sel = sel,
                        morph = kv.Value.morph, transaction = kv.Value.id });
                    Log("purge " + a.uid + ": load settled, draining inactive items");
                    _dueAtoms.Add(a);
                }
                foreach (var a in _dueAtoms) { _purgeAt.Remove(a); if (a == null || !a.gameObject.activeInHierarchy) _purgeRetries.Remove(a); }
                foreach (var kv in _defer) _purgeAt[kv.Key].due = kv.Value;
                _dueAtoms.Clear();
                _defer.Clear();
            }
            for (int j = _purgeJobs.Count - 1; j >= 0 && budget > 0; j--)
            {
                PurgeJob job = _purgeJobs[j];
                Atom a = job.atom;
                DAZCharacterSelector sel = job.sel;
                if (a == null || sel == null || !a.gameObject.activeInHierarchy)
                { _purgeRetries.Remove(a); _purgeJobs.RemoveAt(j); continue; }
                if (Busy(sel)) continue;   // a new restore started — wait it out
                // Walk budget is separate from unload budget: catalog
                // entries with no instance cost nothing, so checking 4000
                // per frame still only calls UnloadIfInactive on real
                // zombies — 2 per frame shared with the slow lane.
                int checkBudget = CheckPerFrame;
                DAZDynamicItem[] clothes = sel.clothingItems;
                while (clothes != null && job.ci < clothes.Length &&
                    checkBudget > 0 && budget > 0)
                {
                    var it = clothes[job.ci++]; checkBudget--;
                    if (it == null) { job.gone++; continue; }
                    if (!it.ready)
                    {
                        var flag = DynamicLoadFlag != null
                            ? DynamicLoadFlag.GetValue(it) as AsyncFlag : null;
                        if (flag != null && !flag.Raised) job.loading++;
                        else job.notReady++;
                        continue;
                    }
                    if (it.active) { job.active++; continue; }
                    try
                    {
                        it.UnloadIfInactive();
                        if (it.ready) it.UnloadIfNotEnabled();
                        if (it.ready) job.guarded++; else job.unloaded++;
                    }
                    catch { job.failed++; }
                    budget--;
                }
                DAZDynamicItem[] hairs = sel.hairItems;
                while (hairs != null && job.hi < hairs.Length &&
                    checkBudget > 0 && budget > 0)
                {
                    var it = hairs[job.hi++]; checkBudget--;
                    if (it == null) { job.gone++; continue; }
                    if (!it.ready)
                    {
                        var flag = DynamicLoadFlag != null
                            ? DynamicLoadFlag.GetValue(it) as AsyncFlag : null;
                        if (flag != null && !flag.Raised) job.loading++;
                        else job.notReady++;
                        continue;
                    }
                    if (it.active) { job.active++; continue; }
                    try
                    {
                        it.UnloadIfInactive();
                        if (it.ready) it.UnloadIfNotEnabled();
                        if (it.ready) job.guarded++; else job.unloaded++;
                    }
                    catch { job.failed++; }
                    budget--;
                }
                if ((clothes == null || job.ci >= clothes.Length) &&
                    (hairs == null || job.hi >= hairs.Length))
                {
                    // Items deactivated while their async load was still in
                    // flight are not unloadable yet — extra passes 10s later
                    // catch them once their instance lands (max 2 retries).
                    int tries;
                    if (job.loading > 0 &&
                        (!_purgeRetries.TryGetValue(a, out tries) ||
                            tries < 2))
                    {
                        if (job.unloaded > 0) KickUnusedAssets();
                        _purgeRetries[a] = tries + 1;
                        _purgeAt[a] = new Transaction { atom = a, due = now + 10f, morph = job.morph, id = job.transaction };
                        _purgeJobs.RemoveAt(j);
                        continue;
                    }
                    if (job.loading == 0 && job.morph && (PurgeMorphDeltas == null || PurgeMorphDeltas.Value))
                    {
                        try { sel.UnloadRuntimeMorphDeltas(); } catch { }
                        try { sel.UnloadDemandActivatedMorphs(); } catch { }
                    }
                    Log(string.Format(
                        "purge {0}: done (unloaded {1}, guarded {2}, loading {3}, active {4}, gone {5}, failed {6}, notReady {7})",
                        a.uid, job.unloaded, job.guarded, job.loading,
                        job.active, job.gone, job.failed, job.notReady));
                    if (job.unloaded > 0) KickUnusedAssets();
                    Log("transaction " + job.transaction + (job.loading == 0 ? " settled" : " deferred resources retained; async load still pending"));
                    _purgeRetries.Remove(a);
                    _purgeJobs.RemoveAt(j);
                }
            }
        }

        // ---------- detection: Harmony postfix on PresetManager ----------

        private const BindingFlags InstAll = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticAll = BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
        private static Harmony _harmony;
        private static bool _patchTried;
        private static readonly FieldInfo PmField =
            typeof(MeshVR.PresetManagerControl).GetField("pm",
                BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo DynamicLoadFlag =
            typeof(JSONStorableDynamic).GetField("loadFlag", InstAll);
        private static readonly FieldInfo InsideRestoreField =
            typeof(JSONStorable).GetField("insideRestore", InstAll);
        private static readonly FieldInfo CharacterLoadFlag =
            typeof(DAZCharacterSelector).GetField("onCharacterLoadedFlag", InstAll);

        private static void TryPatch()
        {
            if (_patchTried) return;
            _patchTried = true;
            try
            {
                MethodInfo post = typeof(MeshVR.PresetManager).GetMethod(
                    "LoadPresetPost", InstAll);
                MethodInfo hook = typeof(WardrobeJanitor).GetMethod(
                    "AfterPresetTransaction", StaticAll);
                if (post == null || hook == null || InsideRestoreField == null || CharacterLoadFlag == null)
                    throw new MissingMemberException("preset transaction native anchors");
                _harmony = new Harmony("Quest3TriggerUI.wardrobe");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(post,
                    prefix: new HarmonyMethod(typeof(WardrobeJanitor).GetMethod("BeforePresetTransaction", StaticAll)),
                    finalizer: new HarmonyMethod(hook));
                Log("transactions installed: appearance/clothing/hair; native restore/callbacks retained");
            }
            catch (Exception e) { Log("transactions not installed: " + e.Message); }
        }

        // Scope is synchronous RestoreStorables; cleanup is a separate settling
        // phase. Failed restores never commit a cleanup transaction. No JSON,
        // callback delegate or PresetManager is held after this call returns.
        private static void BeforePresetTransaction(MeshVR.PresetManager __instance, out Transaction __state)
        {
            __state = null;
            if (PurgeOnLoad != null && !PurgeOnLoad.Value) return;
            try
            {
                Atom atom = OwnerOf(__instance);
                if (atom == null || atom.type != "Person" || atom.isPreparingToPutBackInPool) return;
                MeshVR.PresetManagerControl self = null, clothing = null;
                foreach (var c in atom.presetManagerControls)
                {
                    if (c == null) continue;
                    if (ReferenceEquals(PmField.GetValue(c), __instance)) self = c;
                    if (c.name == "ClothingPresets") clothing = c;
                }
                if (self == null || (self.name != "AppearancePresets" &&
                    self.name != "ClothingPresets" && self.name != "HairPresets")) return;
                Transaction previous;
                if (ActiveTransactions.TryGetValue(atom, out previous)) previous.superseded = true;
                CancelTransaction(atom);
                __state = new Transaction { atom = atom, id = ++_transactionId,
                    morph = self.name == "AppearancePresets" && (clothing == null || !clothing.lockParams) };
                ActiveTransactions[atom] = __state;
            }
            catch (Exception e) { Log("transaction begin retained native load: " + e.Message); }
        }

        private static Exception AfterPresetTransaction(Transaction __state, bool __result, Exception __exception)
        {
            if (__state != null && __state.atom != null)
            {
                Transaction active;
                if (ActiveTransactions.TryGetValue(__state.atom, out active) && ReferenceEquals(active, __state))
                    ActiveTransactions.Remove(__state.atom);
                if (!__state.superseded && __exception == null && __result) Schedule(__state);
                else Log("transaction " + __state.id + " aborted/superseded; no purge scheduled");
            }
            return __exception;
        }

        internal static Atom OwnerOf(MeshVR.PresetManager pm)
        {
            if (pm == null || PmField == null) return null;
            // Only executed on a preset event; a handful of managers, not the
            // clothing catalogue. No permanent pm -> atom -> JSON strong root.
            var sc = SuperController.singleton;
            var atoms = sc != null ? sc.GetAtoms() : null;
            if (atoms == null) return null;
            foreach (var atom in atoms)
            {
                if (atom == null || atom.presetManagerControls == null) continue;
                foreach (var c in atom.presetManagerControls)
                    if (c != null && ReferenceEquals(PmField.GetValue(c), pm)) return atom;
            }
            return null;
        }

        // Drop every reference to scene objects so a scene change or a
        // payload unload never keeps dead graphs pinned through our statics.
        private static void ClearRuntime()
        {
            _runtimeGeneration++;
            _uuaPending = false;
            _uuaRequested = false;
            InactiveSince.Clear();
            Pending.Clear();
            PendingSet.Clear();
            Done.Clear();
            Scratch.Clear();
            _scanAtoms = null;
            _scanSel = null;
            _scanActive = false;
            _purgeAt.Clear();
            ActiveTransactions.Clear();
            _purgeJobs.Clear();
            _purgeRetries.Clear();
            _dueAtoms.Clear();
            _defer.Clear();
            _dropKeys.Clear(); _dropDone.Clear();
            TextureOrphanSweeper.ClearRuntime();
        }

        internal static void Shutdown()
        {
            TextureOrphanSweeper.Shutdown();
            StaleTextureRequestGuard.Shutdown();
            PresetCleanupCoalescer.Shutdown();
            PresetInstanceReuse.Shutdown();
            BumpNormalRowConverter.Shutdown();
            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(_harmony.Id);
            }
            catch { }
            _harmony = null;
            _patchTried = false;
            ClearRuntime();
        }

        internal static void Log(string msg)
        {
            try
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo("[wardrobe] " + msg);
            }
            catch { }
        }
    }
}
