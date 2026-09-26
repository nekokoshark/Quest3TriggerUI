using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Person body + instantiated clothing only. No hair/catalog thumbnails,
    // no FindObjectsOfTypeAll, no asset destruction, no GC/UUA policy change.
    internal static class ResourceLedger
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> DumpRequested;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", All);
        private static readonly FieldInfo[] Catalogs = {
            typeof(DAZCharacterSelector).GetField("_femaleClothingItems", All),
            typeof(DAZCharacterSelector).GetField("_maleClothingItems", All)
        };
        private static readonly string[] TextureSlots = { "_MainTex", "_BumpMap", "_SpecTex", "_GlossTex", "_AlphaTex", "_DecalTex", "_DetailMap", "_DetailNormalMap", "_TessTex" };
        private sealed class Owner
        {
            internal WeakReference Item, Atom;
            internal int Root;
            internal string Label, Kind;
            internal bool Dirty;
            internal ResourceHistoryStore.Record History;
            internal long Generation;
        }
        private sealed class Seed
        {
            internal WeakReference Selector;
            internal int Phase, Offset;
        }
        private sealed class Walk
        {
            internal int Id, Root;
            internal long Generation;
            internal readonly Stack<WeakReference> Nodes = new Stack<WeakReference>();
            internal readonly Dictionary<int, ResourceLedgerIndex.Asset> Assets = new Dictionary<int, ResourceLedgerIndex.Asset>();
        }
        internal sealed class ReleaseSnapshot
        {
            internal readonly HashSet<int> Assets = new HashSet<int>();
            internal readonly HashSet<int> Owners = new HashSet<int>();
        }
        private static readonly ResourceLedgerIndex Index = new ResourceLedgerIndex();
        private static readonly Dictionary<int, Owner> Owners = new Dictionary<int, Owner>();
        private static readonly Dictionary<int, WeakReference> People = new Dictionary<int, WeakReference>();
        private static readonly Queue<Seed> Seeds = new Queue<Seed>();
        private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();
        private static readonly List<int> Removed = new List<int>();
        private static Harmony _harmony;
        private static Walk _walk;
        private static float _nextPeople, _nextLog, _nextDump;
        private static long _dumpedRevision = -1, _dumpedRetired = -1;
        private static int _coveredUnloads, _unknownUnloads, _emptyUnloadCalls, _untrackedLiveUnloads, _unsettledUnloads;
        private static bool _loading, _enabled;
        private static long _logged = -1;
        private static int _failures;
        private static long _nativeLogged = -1, _nativeDumped = -1;
        internal static bool NativeObservationEnabled { get { return On; } }
        internal static ReleaseSnapshot BeginReleaseSnapshot(DAZCharacterSelector selector)
        {
            Atom selectorAtom = selector == null ? null : selector.GetComponentInParent<Atom>();
            if (!On || selectorAtom == null) return null;
            var snapshot = new ReleaseSnapshot();
            foreach (var pair in Owners)
                if (ReferenceEquals(pair.Value.Atom.Target, selectorAtom))
                    snapshot.Owners.Add(pair.Key);
            foreach (var pair in Index.Assets)
            {
                foreach (int owner in pair.Value.Owners)
                    if (snapshot.Owners.Contains(owner)) { snapshot.Assets.Add(pair.Key); break; }
            }
            return snapshot;
        }
        internal static bool TryProveRelease(ReleaseSnapshot snapshot, out string reason, out int uncovered)
        {
            uncovered = 0;
            reason = "no release snapshot";
            if (!On || snapshot == null || snapshot.Assets.Count == 0) return false;
            if (Index.Overflow != 0 || _untrackedLiveUnloads != 0 || _unsettledUnloads != 0)
            { reason = "ledger has uncovered or overflowed release"; return false; }
            // Only pending work on the SNAPSHOT's owners can still change
            // this proof's evidence — the catalogue seeds and walks over
            // other (new-person) owners are unrelated and must not block it.
            foreach (int ownerId in snapshot.Owners)
            {
                Owner pending;
                if (Owners.TryGetValue(ownerId, out pending) && pending.Dirty)
                {
                    var pItem = pending.Item.Target as JSONStorableDynamic;
                    bool walkable = pItem != null && pItem.ready &&
                        Instance.GetValue(pItem) as Transform != null;
                    reason = "ledger still walking: owner " + ownerId + " dirty" +
                        (walkable ? "" : " (unwalkable)"); return false;
                }
                if (_walk != null && _walk.Id == ownerId)
                { reason = "ledger still walking: owner " + ownerId + " in walk"; return false; }
            }
            // A wrong call never breaks anything — UUA only collects what has
            // no references, and any tolerated stragglers are bounded leak that
            // the next real sweep recovers. So a handful of unknown/weak-lost
            // verdicts is acceptable debt, not a veto.
            const int ToleratedUncovered = 4;
            string firstUncovered = null;
            foreach (int id in snapshot.Assets)
            {
                ResourceLedgerIndex.Asset current;
                if (Index.Assets.TryGetValue(id, out current)) continue;
                ResourceLedgerIndex.Asset retired;
                if (Index.Retired.TryGetValue(id, out retired))
                {
                    string allocation = NativeAllocationLedger.Describe(retired.Target.Target as UnityEngine.Object);
                    string state = retired.ReleaseState;
                    // Confirmed destroyed is the strongest coverage of all —
                    // there is nothing left for UUA to collect.
                    if (state == "native-destroyed" ||
                        allocation.StartsWith("native-destroy-requested:", StringComparison.Ordinal) ||
                        allocation == "native-destroy-confirmed") continue;
                    // Provably still referenced → UUA could never collect it,
                    // so it contributes nothing to the sweep's real debt.
                    // "sweep-survivor" is the same guarantee witnessed by a
                    // completed mark-sweep (eye textures live on skin tables
                    // we cannot enumerate — they survive every real UUA).
                    if (state == "native-still-used" || state == "known-current-material-owner" ||
                        state == "image-loader-cache-retained" || state == "sweep-survivor") continue;
                    // Unaudited or provisional verdicts (audit ran while the
                    // ledger was still churning) are evidence in flight —
                    // settling, not a rejection.
                    string who = retired.Kind + "/" + retired.Name + " lastOwner=" + retired.LastOwner;
                    if (state == "pending-reconciliation" || state == "unexamined" ||
                        !retired.ReleaseSettled)
                    { reason = "asset " + id + " " + who + " still settling; " + state + "; " + allocation; return false; }
                    uncovered++;
                    if (firstUncovered == null) firstUncovered = "asset " + id + " " + who + " remains " + state + "; " + allocation;
                    continue;
                }
                uncovered++;
                if (firstUncovered == null) firstUncovered = "asset " + id + " left ledger without native receipt";
            }
            if (uncovered == 0)
            { reason = "all snapshot assets remain shared or have native destroy receipts"; return true; }
            if (uncovered <= ToleratedUncovered)
            { reason = "covered; " + uncovered + " stragglers tolerated; first=" + firstUncovered; return true; }
            reason = uncovered + " assets uncovered; first=" + firstUncovered; return false;
        }

        private static void NoteSweepCompleted(UuaSweepTelemetry.Sample sample)
        {
            if (!On || sample == null) return;
            try
            {
                int survivors, destroyed;
                ResourceReleaseLedger.SweepAudit(Index, sample.retiredWatermark, out survivors, out destroyed);
                if (survivors > 0 || destroyed > 0)
                    Log("sweep audit: survivors=" + survivors + " destroyed=" + destroyed +
                        " watermark=" + sample.retiredWatermark);
            }
            catch (Exception e) { Fault(e); }
        }

        private static HarmonyMethod Hook(string name)
        { return new HarmonyMethod(typeof(ResourceLedger).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
        private static void Patch(Type type, string name, string hook)
        {
            var method = type.GetMethod(name, All);
            if (method == null) throw new MissingMethodException(type.Name, name);
            _harmony.Patch(method, postfix: Hook(hook));
        }
        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                if (Instance == null || Catalogs[0] == null || Catalogs[1] == null)
                    throw new MissingFieldException("ledger lifecycle fields");
                _harmony = new Harmony("Quest3TriggerUI.person-resource-ledger");
                _harmony.UnpatchAll(_harmony.Id);
                Patch(typeof(JSONStorableDynamic), "OnLoadComplete", "Loaded");
                _harmony.Patch(typeof(JSONStorableDynamic).GetMethod("UnloadInstance", All),
                    prefix: Hook("BeforeUnload"), postfix: Hook("Unloaded"));
                Patch(typeof(DAZCharacterSelector), "set_selectedCharacter", "Selected");
                Patch(typeof(DAZCharacterSelector), "OnCharacterLoaded", "Selected");
                Patch(typeof(DAZCharacterTextureControl), "OnImageLoaded", "TextureChanged");
                Patch(typeof(DAZCharacterTextureControl), "DeregisterAllTextures", "TextureChanged");
                for (int n = 1; n <= 6; n++) Patch(typeof(MaterialOptions), "OnTexture" + n + "Loaded", "TextureChanged");
                Patch(typeof(MaterialOptions), "DeregisterAllTextures", "TextureChanged");
                ResourceHistoryRuntime.Install();
                NativeAllocationLedger.Install();
                UuaSweepTelemetry.SweepCompleted = NoteSweepCompleted;
                Log("installed; weak person/clothing observed-edge ledger; incremental events; no unload authority");
            }
            catch (Exception e) { Shutdown(); Log("install failed: " + e.Message); }
        }
        private static bool On { get { return _harmony != null && (Enabled == null || Enabled.Value); } }
        private static void Loaded(JSONStorableDynamic __instance)
        { if (On) try { Track(__instance); } catch (Exception e) { Fault(e); } }
        private static void BeforeUnload(JSONStorableDynamic __instance)
        {
            if (!On || __instance == null || (!(__instance is DAZCharacter) && !(__instance is DAZClothingItem))) return;
            try
            {
                // The pre-existing weak graph is the pre-unload candidate source.
                // VaM calls UnloadInstance on catalogue items without instances.
                // Those are callback noise, not omitted live-resource edges.
                Transform root = Instance.GetValue(__instance) as Transform;
                if (root == null) { _emptyUnloadCalls++; return; }
                Owner owner;
                int id = __instance.GetInstanceID();
                if (!Owners.TryGetValue(id, out owner))
                { _untrackedLiveUnloads++; _unknownUnloads++; }
                else if (owner.Dirty || owner.Root != root.GetInstanceID() || (_walk != null && _walk.Id == id))
                { _unsettledUnloads++; _unknownUnloads++; }
                else _coveredUnloads++;
            }
            catch (Exception e) { Fault(e); }
        }
        private static void Unloaded(JSONStorableDynamic __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                int id = __instance.GetInstanceID();
                if (Instance.GetValue(__instance) as Transform == null)
                { if (Owners.ContainsKey(id)) Remove(id); }
                else Track(__instance);
            }
            catch (Exception e) { Fault(e); }
        }
        private static void Selected(DAZCharacterSelector __instance)
        { if (On && __instance != null) Loaded(__instance.selectedCharacter); }
        private static void TextureChanged(Component __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                Atom atom = __instance.GetComponentInParent<Atom>();
                if (atom == null || atom.type != "Person") return;
                // Some native material controls are outside the instantiated root.
                // Coalesce to this person's known owners; never scan catalogues here.
                foreach (var pair in Owners)
                    if (ReferenceEquals(pair.Value.Atom.Target, atom)) Dirty(pair.Value);
            }
            catch (Exception e) { Fault(e); }
        }
        private static void Dirty(Owner owner) { owner.Dirty = true; owner.Generation++; }
        private static void Track(JSONStorableDynamic item)
        {
            if (item == null || (!(item is DAZCharacter) && !(item is DAZClothingItem))) return;
            Atom atom = item.containingAtom;
            if (atom == null) atom = item.GetComponentInParent<Atom>();
            if (atom == null || atom.type != "Person" || atom.isPreparingToPutBackInPool) return;
            Transform root = Instance.GetValue(item) as Transform;
            int id = item.GetInstanceID();
            if (root == null) { Remove(id); return; }
            Owner owner;
            if (!Owners.TryGetValue(id, out owner))
            {
                owner = new Owner { Item = new WeakReference(item), Atom = new WeakReference(atom),
                    Label = Clean(atom.uid + "/" + item.name), Kind = item is DAZClothingItem ? "clothing" : "person" };
                Owners.Add(id, owner);
            }
            else if (!ReferenceEquals(owner.Item.Target, item))
            { Remove(id); Track(item); return; }
            if (owner.Root != root.GetInstanceID()) Index.Remove(id);
            owner.Root = root.GetInstanceID(); Dirty(owner);
            if (owner.History == null) owner.History = ResourceHistoryRuntime.Clothing(item);
        }
        private static void Remove(int id)
        { Owners.Remove(id); Index.Remove(id); if (_walk != null && _walk.Id == id) _walk = null; }

        internal static void Tick()
        {
            if (!On) { if (_enabled) { Clear(); ResourceHistoryRuntime.Suspend(); } _enabled = false; return; }
            _enabled = true;
            var sc = SuperController.singleton;
            bool loading = sc == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive;
            if (loading)
            { if (!_loading) Clear(); _loading = true; return; }
            if (_loading) { _loading = false; _nextPeople = 0; }
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _nextPeople) { Discover(sc); Prune(); _nextPeople = now + 2f; }
                // Existing native busy state: the ledger never stalls decoding.
                // Catalogue seeding (hundreds of items per selector) shares
                // that courtesy, but WalkSlice only reads component fields —
                // keeping it running while images decode is exactly what lets
                // the ledger settle inside the post-load window.
                // Seed and walk both stay ungated — neither decodes, and
                // gating them on the image tail starves the release audit:
                // pending never drains while images are busy, "settled" stays
                // false, and deferred proofs time out on pending-reconciliation.
                SeedSlice();
                WalkSlice();
                NativeAllocationLedger.Tick();
                bool settled = PendingCount() == 0;
                ResourceReleaseLedger.Tick(Index, settled);
                if (settled && ResourceHistoryRuntime.HasPending) ResourceHistoryRuntime.FinishPending(HistorySnapshot);
                if (now >= _nextLog && (_logged != Index.Revision || _nativeLogged != NativeAllocationLedger.Revision))
                {
                    _nextLog = now + 10f; _logged = Index.Revision;
                    _nativeLogged = NativeAllocationLedger.Revision;
                    Log("revision=" + Index.Revision + " owners=" + Index.OwnerCount + " assets=" + Index.Assets.Count +
                        " pending=" + PendingCount() + " failures=" + _failures + " retired=" + Index.Retired.Count +
                        " nativeGone=" + Index.NativeGone + " weakUnknown=" + Index.UncertainCollected + " reused=" + Index.Reused +
                        " overflow=" + Index.Overflow + " " + ResourceHistoryRuntime.Summary() + " " + NativeAllocationLedger.Summary() + " partialCoverage=true");
                }
                if (now >= _nextDump && (_dumpedRevision != Index.Revision || _dumpedRetired != Index.RetiredRevision || _nativeDumped != NativeAllocationLedger.Revision) && settled)
                { _nextDump = now + 10f; Dump(); _dumpedRevision = Index.Revision; _dumpedRetired = Index.RetiredRevision; _nativeDumped = NativeAllocationLedger.Revision; }
                if (DumpRequested != null && DumpRequested.Value)
                { DumpRequested.Value = false; Dump(); }
            }
            catch (Exception e) { _walk = null; Fault(e); }
        }
        private static int PendingCount()
        { int n = Seeds.Count; foreach (var pair in Owners) if (pair.Value.Dirty) n++; return n + (_walk == null ? 0 : 1); }
        // Monotonic work signature — any pipeline movement (seeded item,
        // walked node, audited asset, graph edit) increments it. Revisions
        // alone could not see a walk that finds no changes.
        internal static long ProgressSignature
        { get { return Index.Work + Index.Revision + Index.RetiredRevision; } }
        private static void Discover(SuperController sc)
        {
            // Small atom list only. Clothing catalogues are seeded once per new
            // Person, using backing fields and 1024 slots/frame, never getter Init().
            foreach (var atom in sc.GetAtoms())
            {
                if (atom == null || atom.type != "Person" || atom.isPreparingToPutBackInPool) continue;
                int id = atom.GetInstanceID(); WeakReference known;
                if (People.TryGetValue(id, out known) && ReferenceEquals(known.Target, atom)) continue;
                People[id] = new WeakReference(atom);
                var selector = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                if (selector == null) { People.Remove(id); continue; }
                Track(selector.selectedCharacter);
                Seeds.Enqueue(new Seed { Selector = new WeakReference(selector) });
            }
        }
        private static void SeedSlice()
        {
            int budget = 1024;
            while (budget > 0 && Seeds.Count > 0)
            {
                Seed seed = Seeds.Peek();
                var selector = seed.Selector.Target as DAZCharacterSelector;
                if (selector == null || seed.Phase >= Catalogs.Length) { Seeds.Dequeue(); continue; }
                var items = Catalogs[seed.Phase].GetValue(selector) as DAZDynamicItem[];
                if (items == null || seed.Offset >= items.Length) { seed.Phase++; seed.Offset = 0; continue; }
                var item = items[seed.Offset++]; budget--; Index.Work++;
                if (item != null && Instance.GetValue(item) as Transform != null) Track(item);
            }
        }
        private static void Prune()
        {
            Removed.Clear();
            foreach (var pair in People)
            {
                var atom = pair.Value.Target as Atom;
                if (atom == null || atom.isPreparingToPutBackInPool) Removed.Add(pair.Key);
            }
            foreach (int id in Removed) People.Remove(id);
            Removed.Clear();
            foreach (var pair in Owners)
            {
                var item = pair.Value.Item.Target as JSONStorableDynamic;
                var atom = pair.Value.Atom.Target as Atom;
                var root = item == null ? null : Instance.GetValue(item) as Transform;
                if (atom == null || atom.isPreparingToPutBackInPool || root == null) Removed.Add(pair.Key);
                else if (pair.Value.Root != root.GetInstanceID())
                { Index.Remove(pair.Key); pair.Value.Root = root.GetInstanceID(); Dirty(pair.Value); }
            }
            foreach (int id in Removed) Remove(id);
            Removed.Clear();
        }
        private static void WalkSlice()
        {
            if (_walk == null)
            {
                foreach (var pair in Owners)
                {
                    if (!pair.Value.Dirty) continue;
                    var item = pair.Value.Item.Target as JSONStorableDynamic;
                    var root = item == null ? null : Instance.GetValue(item) as Transform;
                    var atom = pair.Value.Atom.Target as Atom;
                    // A dirty owner that can never be walked again (instance or
                    // atom gone, or the atom is being pooled) wedges pending>0
                    // forever — remove it instead of skipping it every tick.
                    if (root == null || atom == null || atom.isPreparingToPutBackInPool)
                    { Remove(pair.Key); break; }
                    if (!item.ready) continue;
                    _walk = new Walk { Id = pair.Key, Root = root.GetInstanceID(), Generation = pair.Value.Generation };
                    _walk.Nodes.Push(new WeakReference(root)); pair.Value.Dirty = false;
                    break;
                }
            }
            if (_walk == null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int budget = PresetSweepGate.ProofPending ? 64 : 16;
            for (int n = 0; n < budget && _walk.Nodes.Count > 0; n++)
            {
                var node = _walk.Nodes.Pop().Target as Transform;
                Index.Work++;
                if (node == null)
                {
                    Owner interrupted;
                    if (Owners.TryGetValue(_walk.Id, out interrupted)) Dirty(interrupted);
                    _walk = null; return;
                }
                // Traverse only this instance; don't attribute nested foreign atoms,
                // separate clothing or hair instances to the body owner.
                if (node.GetInstanceID() != _walk.Root &&
                    (node.GetComponent<Atom>() != null || node.GetComponent<DAZDynamicItem>() != null)) continue;
                foreach (Component component in node.GetComponents<Component>()) Capture(component, _walk.Assets);
                for (int i = 0; i < node.childCount; i++) _walk.Nodes.Push(new WeakReference(node.GetChild(i)));
                if (clock.Elapsed.TotalMilliseconds >= 1.0) break;
            }
            if (_walk.Nodes.Count != 0) return;
            Owner owner;
            if (Owners.TryGetValue(_walk.Id, out owner))
            {
                var item = owner.Item.Target as JSONStorableDynamic;
                var root = item == null ? null : Instance.GetValue(item) as Transform;
                if (root != null && root.GetInstanceID() == _walk.Root && owner.Generation == _walk.Generation)
                    {
                    Index.Replace(_walk.Id, _walk.Assets);
                    ResourceHistoryRuntime.Observed(owner.History, _walk.Assets.Values);
                    }
                else Dirty(owner); // A callback invalidated this multi-frame snapshot.
            }
            _walk = null;
        }
        private static void Capture(Component component, Dictionary<int, ResourceLedgerIndex.Asset> assets)
        {
            if (component == null) return;
            NativeAllocationLedger.Capture(component, assets);
            var renderer = component as Renderer;
            if (renderer != null) foreach (var material in renderer.sharedMaterials) Add(assets, material);
            var meshFilter = component as MeshFilter;
            if (meshFilter != null) Add(assets, meshFilter.sharedMesh);
            var skinned = component as SkinnedMeshRenderer;
            if (skinned != null) Add(assets, skinned.sharedMesh);
            var collider = component as MeshCollider;
            if (collider != null) Add(assets, collider.sharedMesh);
            Type type = component.GetType();
            if (type.Assembly != typeof(DAZCharacter).Assembly) return;
            FieldInfo[] fields;
            if (!Fields.TryGetValue(type, out fields))
            {
                var found = new List<FieldInfo>();
                for (Type t = type; t != null && t.Assembly == type.Assembly; t = t.BaseType)
                    foreach (var f in t.GetFields(All | BindingFlags.DeclaredOnly))
                        if (AssetType(f.FieldType) || (f.FieldType.IsArray && AssetType(f.FieldType.GetElementType())) ||
                            f.FieldType == typeof(Dictionary<Texture2D, int>) || f.FieldType == typeof(Dictionary<Material, Texture2D>)) found.Add(f);
                fields = found.ToArray(); Fields.Add(type, fields);
            }
            foreach (var field in fields)
            {
                object value = field.GetValue(component);
                var asset = value as UnityEngine.Object;
                if (asset != null) { Add(assets, asset); continue; }
                var array = value as Array;
                if (array != null) { foreach (object entry in array) Add(assets, entry as UnityEngine.Object); continue; }
                var dict = value as IDictionary;
                if (dict != null) foreach (DictionaryEntry entry in dict)
                {
                    Add(assets, entry.Key as UnityEngine.Object); Add(assets, entry.Value as UnityEngine.Object);
                    var texture = entry.Key as Texture2D;
                    if (texture != null && value is Dictionary<Texture2D, int> && (int)entry.Value > 0)
                        assets[texture.GetInstanceID()].LocalUses += (int)entry.Value;
                }
            }
        }
        private static bool AssetType(Type type)
        { return typeof(Texture).IsAssignableFrom(type) || typeof(Material).IsAssignableFrom(type) || typeof(Mesh).IsAssignableFrom(type); }
        internal static void Add(Dictionary<int, ResourceLedgerIndex.Asset> assets, UnityEngine.Object asset)
        {
            if (asset == null || !AssetType(asset.GetType())) return;
            int id = asset.GetInstanceID();
            if (assets.ContainsKey(id)) return;
            assets.Add(id, new ResourceLedgerIndex.Asset(id, asset, asset.GetType().Name, Clean(asset.name)));
            var material = asset as Material;
            if (material != null) foreach (string slot in TextureSlots)
                if (material.HasProperty(slot)) Add(assets, material.GetTexture(slot));
        }
        private static string[][] HistorySnapshot(Atom atom, bool clothingOnly)
        {
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var descriptions = new HashSet<string>(StringComparer.Ordinal);
            var owners = new HashSet<int>();
            foreach (var pair in Owners)
            {
                if (!ReferenceEquals(pair.Value.Atom.Target, atom) || (clothingOnly && pair.Value.Kind != "clothing")) continue;
                owners.Add(pair.Key);
                var identity = pair.Value.History;
                if (identity != null) dependencies.Add(ResourceHistoryStore.Key(identity.Kind, identity.Source));
            }
            foreach (var asset in Index.Assets.Values) foreach (int owner in asset.Owners)
                if (owners.Contains(owner)) { descriptions.Add(asset.Kind + "\t" + asset.Name); break; }
            var deps = new List<string>(dependencies).ToArray(); var desc = new List<string>(descriptions).ToArray();
            Array.Sort(deps, StringComparer.Ordinal); Array.Sort(desc, StringComparer.Ordinal);
            return new[] { deps, desc };
        }
        private static string Clean(string value)
        { return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
        private static void Dump()
        {
            string path = Path.Combine(BepInEx.Paths.GameRootPath, "工作区/person_resource_ledger_20260926/LIVE_LEDGER.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("# observed edges only; partial native field/texture-slot coverage; external plugin owners unknown; no unload authority");
                writer.WriteLine("# revision=" + Index.Revision + " pending=" + PendingCount() + " failures=" + _failures);
                writer.WriteLine("# " + ResourceHistoryRuntime.Summary());
                writer.WriteLine("# " + NativeAllocationLedger.Summary());
                writer.WriteLine("ownerId\tkind\towner\tresourceId\ttype\tresource\tobservedOwnerCount\tobservedNativeUses\tnativeRegisteredUses\tnativeAllocation");
                foreach (var asset in Index.Assets.Values) foreach (int ownerId in asset.Owners)
                {
                    Owner owner;
                    if (Owners.TryGetValue(ownerId, out owner)) writer.WriteLine(ownerId + "\t" + owner.Kind + "\t" + owner.Label + "\t" + asset.Id + "\t" + asset.Kind + "\t" + asset.Name + "\t" + asset.Owners.Count + "\t" + asset.ObservedUses + "\t" + asset.NativeRefs + "\t" + NativeAllocationLedger.Describe(asset.Target.Target as UnityEngine.Object));
                }
            }
            string retired = Path.Combine(Path.GetDirectoryName(path), "RELEASE_CANDIDATES.tsv");
            using (var writer = new StreamWriter(retired, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("# observation only; no automatic release permission; nativeGone=" + Index.NativeGone + " weakUnknown=" + Index.UncertainCollected +
                    " reused=" + Index.Reused + " overflow=" + Index.Overflow + " observedUnloads=" + _coveredUnloads + " unknownUnloads=" + _unknownUnloads +
                    " emptyUnloadCalls=" + _emptyUnloadCalls + " untrackedLiveUnloads=" + _untrackedLiveUnloads + " unsettledUnloads=" + _unsettledUnloads);
                writer.WriteLine("# " + NativeAllocationLedger.Summary());
                writer.WriteLine("resourceId\ttype\tresource\tlastOwner\tnativeRegisteredUses\tstate\tnativeAllocation");
                foreach (var asset in Index.Retired.Values) writer.WriteLine(asset.Id + "\t" + asset.Kind + "\t" + asset.Name + "\t" + asset.LastOwner + "\t" + asset.NativeRefs + "\t" + asset.ReleaseState + "\t" + NativeAllocationLedger.Describe(asset.Target.Target as UnityEngine.Object));
            }
            Log("snapshot=" + path);
        }
        private static void Fault(Exception e)
        { _failures++; if (_failures <= 3) Log("observation failed (native behavior unchanged): " + e.Message); }
        private static void Clear()
        { Owners.Clear(); People.Clear(); Seeds.Clear(); Index.Clear(); ResourceReleaseLedger.Reset(); NativeAllocationLedger.Clear(); ResourceHistoryRuntime.ClearSession(); _walk = null; _nextPeople = 0; }
        internal static void Shutdown()
        {
            ResourceHistoryRuntime.Shutdown();
            NativeAllocationLedger.Shutdown();
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; Clear(); Fields.Clear(); _enabled = false;
        }
        private static void Log(string message)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[resource-ledger] " + message); }
    }
}
