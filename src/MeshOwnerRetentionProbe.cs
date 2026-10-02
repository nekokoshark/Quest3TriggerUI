using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Only DAZMesh owners observed at existing allocation/derivation hooks.
    // Weak records and fixed-field diagnostics; retire only native-dead, noncurrent character skin caches. No resource destruction or global census.
    internal static class MeshOwnerRetentionProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const int OwnerCap = 256, ItemCap = 4096, ArrayCap = 4096;
        private static readonly FieldInfo Allocated = typeof(MVR.ObjectAllocator).GetField("allocatedObjects", All);
        private static readonly FieldInfo[] Fields = typeof(DAZMesh).GetFields(All);
        private static readonly Dictionary<int, Entry> Owners = new Dictionary<int, Entry>();
        private static Harmony _harmony;
        private static bool _installTried;
        private static int _nextRecord;
        internal static bool RetireDeadSkinCaches = true;
        internal static bool RetireDeadControls = true;
        private static long _controlFieldsRetired;
        private static long _dynamicEntriesRetired;
        private static long _skinFieldsRetired;
        private static int _rootPage;
        private static float _rootPageAfter;
        private static readonly List<WeakReference> RetiredSkins = new List<WeakReference>();
        private sealed class CacheCandidate
        {
            internal WeakReference character, selector;
        }
        private static long _registers, _exits, _skipped, _retired, _exitDropped;
        private static readonly Queue<string> ExitRows = new Queue<string>();
        private static string _request;
        private static IEnumerator _walk;
        private static Stats _stats;
        private static float _started;
        internal static bool Running { get { return _walk != null; } }
        internal static int Tracked { get { return Owners.Count; } }
        internal sealed class Entry
        {
            internal WeakReference owner;
            internal int id, recordId, derives, before, after;
            internal bool lastNativeDead;
            internal long registrations, lastBytes;
            internal int lastItems = -1;
        }
        private sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object a) { return RuntimeHelpers.GetHashCode(a); }
        }
        private sealed class Stats
        {
            internal string tag;
            internal int live, nativeLive, nativeDead, rows, materials, meshes, gone, unknown, repeated, arraySkipped;
            internal long bytes, nativeDeadBytes;
            internal int rootSelectors, rootNodes, rootMissing, rootRows, rootRowsSkipped, rootUntrackedDead, rootSeeds;
            internal long rootDeadBytes, rootDeadSkinBytes, rootTotalDeadBytes;
            internal long skinWeightSlots, skinGeneralWeightSlots;
            internal readonly Dictionary<string,long> skinFieldAliasBytes = new Dictionary<string,long>();
            internal int rootDeadSkins, rootDeadWraps, retiredFields, cacheCandidatesSkipped, dynamicCandidatesSkipped, controlCandidatesSkipped;
            internal long rootDeadWrapBytes;
            internal readonly HashSet<object> rootWrapSeen = new HashSet<object>(new Identity());
            internal readonly HashSet<object> rootWrapArrays = new HashSet<object>(new Identity());
            internal readonly HashSet<object> rootSkinSeen = new HashSet<object>(new Identity());
            internal readonly HashSet<object> rootSkinArrays = new HashSet<object>(new Identity());
            internal readonly HashSet<object> rootMeshArrays = new HashSet<object>(new Identity());
            internal readonly List<CacheCandidate> cacheCandidates = new List<CacheCandidate>();
            internal readonly List<KeyValuePair<WeakReference,string>> dynamicCandidates = new List<KeyValuePair<WeakReference,string>>();
            internal readonly List<WeakReference> controlCandidates = new List<WeakReference>();
            internal readonly HashSet<object> rootDeadMeshes = new HashSet<object>(new Identity());
            internal readonly HashSet<object> rootArrays = new HashSet<object>(new Identity());
            internal bool rootPartial, rootMorePages, rootBudgetClipped;
            internal readonly HashSet<object> rootSeen = new HashSet<object>(new Identity());
            internal readonly HashSet<int> matchedDead = new HashSet<int>();
            internal readonly Dictionary<object, bool> arrays = new Dictionary<object, bool>(new Identity());
        }
        private static bool Ready()
        {
            var sc = SuperController.singleton;
            return sc != null && !sc.isLoading && !SceneLoadAccelerator.SceneLoadActive &&
                !LoadWindow.PresetBusy && !WardrobeJanitor.ImagesBusy();
        }
        internal static void Install()
        {
            if (_installTried) return;
            _installTried = true;
            try
            {
                if (Allocated == null) throw new MissingFieldException("ObjectAllocator.allocatedObjects");
                _harmony = new Harmony("Quest3TriggerUI.mesh-owner-retention");
                var own = typeof(MeshOwnerRetentionProbe);
                _harmony.Patch(typeof(MVR.ObjectAllocator).GetMethod("RegisterAllocatedObject", All),
                    postfix: new HarmonyMethod(own.GetMethod("AfterRegister", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(typeof(DAZMesh).GetMethod("DeriveMeshes", All),
                    prefix: new HarmonyMethod(own.GetMethod("BeforeDerive", BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(own.GetMethod("AfterDerive", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(typeof(MVR.ObjectAllocator).GetMethod("DestroyAllocatedObjects", All),
                    prefix: new HarmonyMethod(own.GetMethod("BeforeExit", BindingFlags.Static | BindingFlags.NonPublic)));
                Log("installed ownerCap=256 weak-only DAZMesh Register/Derive/DestroyAllocatedObjects; guarded stale-cache retirement active");
                if (_request == null) _request = "known-roots-baseline";
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null; Log("install failed=" + e.GetType().Name);
            }
        }
        private static void Prune()
        {
            var dead = new List<int>();
            foreach (var pair in Owners)
            {
                var owner = pair.Value.owner.Target as DAZMesh;
                if (ReferenceEquals(owner, null)) dead.Add(pair.Key);
            }
            foreach (int id in dead) { Owners.Remove(id); _retired++; }
        }
        private static Entry Find(DAZMesh owner)
        {
            foreach (Entry entry in Owners.Values)
                if (ReferenceEquals(entry.owner.Target, owner)) return entry;
            return null;
        }
        private static Entry Remember(DAZMesh owner)
        {
            if (owner == null) return null;
            Entry e = Find(owner);
            if (e != null) return e;
            if (Owners.Count >= OwnerCap) Prune();
            if (Owners.Count >= OwnerCap) { _skipped++; return null; }
            // Observer key is independent of Unity IDs, which can be recycled
            // while an old native-dead managed wrapper is still reachable.
            e = new Entry { id = owner.GetInstanceID(), recordId = ++_nextRecord, owner = new WeakReference(owner) };
            Owners[e.recordId] = e; return e;
        }
        private static int Count(DAZMesh owner)
        { var list = Allocated.GetValue(owner) as IList; return list == null ? 0 : list.Count; }
        private static void AfterRegister(MVR.ObjectAllocator __instance)
        {
            try { var mesh = __instance as DAZMesh; if (mesh == null) return; _registers++; var e = Remember(mesh); if (e != null) e.registrations++; }
            catch (Exception e) { Log("register observation failed=" + e.GetType().Name); }
        }
        private static void BeforeDerive(DAZMesh __instance)
        {
            try { var e = Remember(__instance); if (e != null) { e.derives++; e.before = Count(__instance); } }
            catch (Exception e) { Log("derive observation failed=" + e.GetType().Name); }
        }
        private static void AfterDerive(DAZMesh __instance)
        {
            try { Entry e = Find(__instance); if (e != null) e.after = Count(__instance); }
            catch (Exception e) { Log("derive end observation failed=" + e.GetType().Name); }
        }
        private static void BeforeExit(MVR.ObjectAllocator __instance)
        {
            if (!(__instance is DAZMesh)) return;
            _exits++;
            try
            {
                var owner = (DAZMesh)__instance; Entry e = Find(owner);
                if (e == null) return;
                int id = e.id;
                if (ExitRows.Count == 32) { ExitRows.Dequeue(); _exitDropped++; }
                ExitRows.Enqueue("exit ownerId=" + id + " recordId=" + e.recordId + " registryItems=" + Count(owner) + " derives=" + e.derives +
                    " lastDirectArrayAliasBytes=" + e.lastBytes + " (entry to native cleanup; not proof of completed release)");
            }
            catch (Exception e) { Log("exit observation failed=" + e.GetType().Name); }
        }
        // Direct array payload only, not native Mesh bytes or referenced object graphs.
        internal static int ElementBytes(Type t)
        {
            if (!t.IsValueType) return IntPtr.Size;
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(bool)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) return 4;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double) || t == typeof(Vector2)) return 8;
            if (t == typeof(Vector3)) return 12;
            if (t == typeof(Vector4) || t == typeof(Quaternion)) return 16;
            if (t == typeof(Matrix4x4)) return 64;
            return 0;
        }
        internal static void Request(string tag)
        {
            // A natural GC report during the same idle pass must not repeatedly
            // restart at page0. Loading interrupts the pass in Tick instead.
            if (tag.StartsWith("settled-") && Ready() && (_walk != null || _request != null)) return;
            Cancel(); _request = tag; _rootPage = 0; _rootPageAfter = 0f;
        }
        private static void Cancel()
        {
            var d = _walk as IDisposable; if (d != null) d.Dispose();
            _walk = null;
            if (_stats != null) { _stats.arrays.Clear(); _stats.rootSeen.Clear(); _stats.matchedDead.Clear(); _stats.rootDeadMeshes.Clear(); _stats.rootArrays.Clear(); _stats.rootSkinSeen.Clear(); _stats.rootWrapSeen.Clear(); _stats.rootWrapArrays.Clear(); _stats.rootSkinArrays.Clear(); _stats.rootMeshArrays.Clear(); _stats.cacheCandidates.Clear(); _stats.controlCandidates.Clear(); _stats.dynamicCandidates.Clear(); _stats.skinFieldAliasBytes.Clear(); }
            _stats = null;
        }
        private static IEnumerable<int> Scan(Stats s)
        {
            foreach (var pair in Owners)
            {
                Entry e = pair.Value;
                var owner = e.owner.Target as DAZMesh;
                if (ReferenceEquals(owner, null)) { yield return 0; continue; }
                bool nativeDead = owner == null;
                s.live++;
                if (nativeDead) s.nativeDead++; else s.nativeLive++;
                if (e.derives > 1) s.repeated++;
                var list = Allocated.GetValue(owner) as IList;
                int items = list == null ? 0 : list.Count;
                if (items > ItemCap) s.unknown++;
                for (int i = 0; i < Math.Min(items, ItemCap); i++)
                {
                    var obj = list[i] as UnityEngine.Object;
                    if (obj == null) s.gone++;
                    else if (obj is Mesh) s.meshes++;
                    else if (obj is Material) s.materials++;
                    yield return 0;
                }
                long ownerBytes = 0;
                foreach (var field in Fields)
                {
                    if (!field.FieldType.IsArray) continue;
                    var array = field.GetValue(owner) as Array;
                    if (array == null || array.Length == 0) continue;
                    int width = ElementBytes(field.FieldType.GetElementType());
                    if (width == 0) { s.unknown++; continue; }
                    // Per-owner sum may count aliases; global sum is identity-deduped.
                    ownerBytes += array.LongLength * width;
                    bool seenDead;
                    if (!s.arrays.TryGetValue(array, out seenDead))
                    {
                        if (s.arrays.Count >= ArrayCap) s.arraySkipped++;
                        else
                        {
                            s.arrays.Add(array, nativeDead); s.bytes += array.LongLength * width;
                            if (nativeDead) s.nativeDeadBytes += array.LongLength * width;
                        }
                    }
                    else if (nativeDead && !seenDead)
                    {
                        s.arrays[array] = true;
                        s.nativeDeadBytes += array.LongLength * width;
                    }
                    yield return 0;
                }
                if (s.rows < OwnerCap && ((_rootPage == 0 && nativeDead) || e.lastItems < 0 || items != e.lastItems || ownerBytes != e.lastBytes || nativeDead != e.lastNativeDead))
                {
                    Log(s.tag + " ownerId=" + e.id + " recordId=" + e.recordId + " state=" +
                        (nativeDead ? "native-dead-managed-alive" : "native-live") + " derives=" + e.derives + " deriveItems=" + e.before + "->" + e.after +
                        " registryItems=" + e.lastItems + "->" + items + " directArrayBytes=" + e.lastBytes + "->" + ownerBytes +
                        " registrations=" + e.registrations + " perOwnerAliasSum=True"); s.rows++;
                }
                e.lastItems = items; e.lastBytes = ownerBytes; e.lastNativeDead = nativeDead;
                yield return 0;
            }
            foreach (int step in ScanKnownRoots(s)) yield return step;
        }
        private struct RootNode
        {
            internal object value;
            internal string path;
            internal int depth;
            internal string context;
        }
        private static readonly string[] SelectorFields = {
            "femaleMorphBank1", "femaleMorphBank2", "femaleMorphBank3",
            "maleMorphBank1", "maleMorphBank2", "maleMorphBank3", "_characterRun",
            "_selectedCharacter", "_loadedCharacter", "_characters", "_characterByName",
            "_clothingItemById", "_hairItemById", "_clothingItemByBackupId", "_hairItemByBackupId", "_materialOptions", "femaleEyelashMaterialOptions", "maleEyelashMaterialOptions", "copyUIFrom"
        };
        // Fixed metadata names only; no Type keys or scene-object references.
        private static readonly string[] DAZMeshReferenceFields = { "copyMaterialsFrom", "graftTo" };
        private static readonly string[] DAZMergedMeshReferenceFields = { "targetMesh", "graftMesh", "graft2Mesh", "copyMaterialsFrom", "graftTo" };
        private static readonly string[] AtomReferenceFields = { "_storables", "_storableById", "<presetManagerControls>k__BackingField" };
        private static readonly string[] DAZSkinWrapControlReferenceFields = { "_wrap", "_wrap2" };
        private static readonly string[] ClothSimControlReferenceFields = { "skinWrap" };
        private static readonly string[] DAZSkinControlReferenceFields = { "_skin" };
        private static readonly string[] AutoColliderBatchUpdaterReferenceFields = { "skin" };
        private static readonly string[] DAZMorphBankReferenceFields = { "_connectedMesh" };
        private static readonly string[] DAZCharacterRunReferenceFields = { "mergedMesh", "mesh1", "mesh2", "mesh3", "skin", "skinForThread" };
        private static readonly string[] DAZCharacterReferenceFields = { "_skin", "_skinForClothes" };
        private static readonly string[] DAZClothingItemReferenceFields = { "_skin", "clothingItemControls" };
        private static readonly string[] DAZHairGroupReferenceFields = { "_skin", "hairGroupControls" };
        private static readonly string[] DAZClothingItemControlReferenceFields = { "presetManagerControl" };
        private static readonly string[] PresetManagerControlReferenceFields = { "pm" };
        private static readonly string[] PresetManagerReferenceFields = { "storables", "optionalStorables", "optionalStorables2", "optionalStorables3", "dynamicStorables", "regularStorables" };
        private static readonly string[] StorableReferenceFields = { "storable" };
        private static readonly string[] DAZCharacterMaterialOptionsReferenceFields = { "_skin", "copyUIFrom", "otherMaterialOptionsList" };
        private static readonly string[] DAZMeshMaterialOptionsReferenceFields = { "_mesh", "copyUIFrom", "otherMaterialOptionsList" };
        private static readonly string[] DAZSkinWrapMaterialOptionsReferenceFields = { "_skinWrap", "_skinWrap2", "copyUIFrom", "otherMaterialOptionsList" };
        private static readonly string[] MaterialOptionsReferenceFields = { "copyUIFrom", "otherMaterialOptionsList" };
        private static readonly string[] DAZSkinV2ReferenceFields = { "dazMesh" };
        private static readonly string[] DAZSkinWrapReferenceFields = { "dazMesh", "skin", "morphCopyFrom" };
        private static readonly string[] EmptyReferenceFields = new string[0];
        private static string[] ReferenceFields(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t == typeof(DAZMesh)) return DAZMeshReferenceFields;
                switch (t.Name)
                {
                    case "DAZMergedMesh": return DAZMergedMeshReferenceFields;
                    case "DAZCharacterSelector": return SelectorFields;
                    case "Atom": return AtomReferenceFields;
                    case "DAZSkinWrapControl": return DAZSkinWrapControlReferenceFields;
                    case "ClothSimControl": return ClothSimControlReferenceFields;
                    case "DAZSkinControl": return DAZSkinControlReferenceFields;
                    case "AutoCollider": return DAZSkinControlReferenceFields;
                    case "AutoColliderBatchUpdater": return AutoColliderBatchUpdaterReferenceFields;
                    case "DAZPhysicsMesh": return DAZSkinControlReferenceFields;
                    case "DAZMorphBank": return DAZMorphBankReferenceFields;
                    case "DAZCharacterRun": return DAZCharacterRunReferenceFields;
                    case "DAZCharacter": return DAZCharacterReferenceFields;
                    case "DAZClothingItem": return DAZClothingItemReferenceFields;
                    case "DAZHairGroup": return DAZHairGroupReferenceFields;
                    case "DAZDynamicItem": return DAZSkinControlReferenceFields;
                    case "DAZClothingItemControl": return DAZClothingItemControlReferenceFields;
                    case "DAZHairGroupControl": return DAZClothingItemControlReferenceFields;
                    case "PresetManagerControl": return PresetManagerControlReferenceFields;
                    case "PresetManager": return PresetManagerReferenceFields;
                    case "Storable": return StorableReferenceFields;
                    case "DAZCharacterMaterialOptions": return DAZCharacterMaterialOptionsReferenceFields;
                    case "DAZMeshMaterialOptions": return DAZMeshMaterialOptionsReferenceFields;
                    case "DAZSkinWrapMaterialOptions": return DAZSkinWrapMaterialOptionsReferenceFields;
                    case "MaterialOptions": return MaterialOptionsReferenceFields;
                    case "DAZSkinV2": return DAZSkinV2ReferenceFields;
                    case "DAZSkinWrap": return DAZSkinWrapReferenceFields;
                }
            }
            return EmptyReferenceFields;
        }
        private static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }
        // Detail output is bounded independently of graph traversal and byte totals.
        private static bool TakeRootRow(Stats s)
        {
            if (s.rootRows < OwnerCap) { s.rootRows++; return true; }
            s.rootRowsSkipped++; return false;
        }
        // Fixed Person selector roots and metadata-verified fields only.
        // This establishes an observed reference path, not every GC root.
        private static IEnumerable<int> ScanKnownRoots(Stats s)
        {
            const int NodeLimit = 16384, DepthLimit = 16, AtomLimit = 128, ContainerLimit = 256;
            int atoms = 0;
            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                if (++atoms > AtomLimit) { s.rootPartial = true; s.rootBudgetClipped = true; yield break; }
                if (atom == null || atom.type != "Person") continue;
                var selector = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                if (selector == null) continue;
                int index = s.rootSelectors++;
                object selected = FindField(selector.GetType(), "_selectedCharacter").GetValue(selector);
                object loaded = FindField(selector.GetType(), "_loadedCharacter").GetValue(selector);
                var stack = new Stack<RootNode>();
                stack.Push(new RootNode { value = selector, path = "selector[" + index + "]", depth = 0 });
                // Registry roots are not selector directories: inspect once per paging cycle.
                if (_rootPage == 0)
                    stack.Push(new RootNode { value = atom, path = "atom[" + index + "]", depth = 0 });
                try
                {
                    while (stack.Count != 0)
                    {
                        RootNode node = stack.Pop(); object value = node.value;
                        if (ReferenceEquals(value, null)) { yield return 0; continue; }
                        // The same geometry selector has its own paged root below. Do not
                        // expand it through Atom.storables at a different (unpaged) depth.
                        if (ReferenceEquals(value, selector) && node.path.StartsWith("atom[", StringComparison.Ordinal))
                        { yield return 0; continue; }
                        if (++s.rootNodes > NodeLimit) { s.rootPartial = true; s.rootBudgetClipped = true; yield break; }
                        string context = node.context;
                        var character = value as DAZCharacter;
                        if (!ReferenceEquals(character, null))
                        {
                            context = (character != null ? " descriptorNativeAlive=True" : " descriptorNativeAlive=False") +
                                (ReferenceEquals(character, selected) ? " selected=True" : " selected=False") +
                                (ReferenceEquals(character, loaded) ? " loaded=True" : " loaded=False");
                        }
                        var skin = value as DAZSkinV2;
                        if (!ReferenceEquals(skin, null))
                        {
                            context += (skin != null ? " skinNativeAlive=True" : " skinNativeAlive=False");
                            if (skin == null && s.rootSkinSeen.Add(skin))
                            {
                                s.rootDeadSkins++;
                                foreach (int step in MeasureDeadSkinArrays(skin, s)) yield return step;
                                if (TakeRootRow(s))
                                {
                                    Log(s.tag + " dead-skin path=" + node.path + context + " nativeDead=True (managed fields only; not exclusive bytes)");
                                }
                            }
                        }
                        var wrap = value as DAZSkinWrap;
                        if (!ReferenceEquals(wrap, null) && wrap == null && s.rootWrapSeen.Add(wrap))
                        {
                            s.rootDeadWraps++;
                            foreach (int step in MeasureDeadWrapArrays(wrap, s)) yield return step;
                            if (TakeRootRow(s))
                            {
                                Log(s.tag + " dead-wrap path=" + node.path + " nativeDead=True (managed fields only; not exclusive bytes)");
                            }
                        }
                        if (!ReferenceEquals(character, null) && character != null &&
                            !ReferenceEquals(character, selected) && !ReferenceEquals(character, loaded) &&
                            !s.rootSeen.Contains(character))
                        {
                            if (s.cacheCandidates.Count < OwnerCap)
                                s.cacheCandidates.Add(new CacheCandidate { character = new WeakReference(character), selector = new WeakReference(selector) });
                            else { s.cacheCandidatesSkipped++; s.rootPartial = true; s.rootBudgetClipped = true; }
                        }
                        var mesh = value as DAZMesh;
                        if (!ReferenceEquals(mesh, null))
                        {
                            Entry e = Find(mesh);
                            // Seed only native-live owners on these already-approved paths.
                            // No artificial derive event and no native API on a dead wrapper.
                            if (e == null && mesh != null)
                            { e = Remember(mesh); if (e != null) s.rootSeeds++; }
                            if (mesh == null && s.rootDeadMeshes.Add(mesh))
                            {
                                if (e != null) s.matchedDead.Add(e.recordId); else s.rootUntrackedDead++;
                                foreach (FieldInfo arrayField in Fields)
                                {
                                    if (!arrayField.FieldType.IsArray) continue;
                                    var array = arrayField.GetValue(mesh) as Array;
                                    if (array == null || array.Length == 0) continue;
                                    int width = ElementBytes(arrayField.FieldType.GetElementType());
                                    if (width == 0) { s.rootPartial = true; continue; }
                                    if (s.rootArrays.Count >= ArrayCap) { s.rootPartial = true; continue; }
                                    if (s.rootMeshArrays.Add(array)) s.rootDeadBytes += array.LongLength * width;
                                    if (s.rootArrays.Add(array)) s.rootTotalDeadBytes += array.LongLength * width;
                                    yield return 0;
                                }
                                if (TakeRootRow(s))
                                {
                                    Log(s.tag + " root-match recordId=" + (e == null ? "unobserved" : e.recordId.ToString()) +
                                        " path=" + node.path + context + " nativeDead=True (observed strong field path; not exclusive bytes)");
                                }
                            }
                        }
                        if (!s.rootSeen.Add(value)) { yield return 0; continue; }
                        var managerObject = value as UnityEngine.Object;
                        if (value.GetType().Name == "PresetManager" && managerObject != null)
                        {
                            if (s.dynamicCandidates.Count < OwnerCap)
                                s.dynamicCandidates.Add(new KeyValuePair<WeakReference,string>(new WeakReference(value), node.path));
                            else { s.dynamicCandidatesSkipped++; s.rootPartial = true; s.rootBudgetClipped = true; }
                        }
                        var dynamicItem = value as DAZDynamicItem;
                        if (dynamicItem != null && !dynamicItem.ready &&
                            (dynamicItem is DAZClothingItem || dynamicItem is DAZHairGroup))
                        {
                            string controlName = dynamicItem is DAZClothingItem ? "clothingItemControls" : "hairGroupControls";
                            FieldInfo controlField = FindField(dynamicItem.GetType(), controlName);
                            var controls = controlField == null ? null : controlField.GetValue(dynamicItem) as Array;
                            if (controls != null && controls.Length > 0)
                            {
                                if (s.controlCandidates.Count < OwnerCap) s.controlCandidates.Add(new WeakReference(dynamicItem));
                                else { s.controlCandidatesSkipped++; s.rootPartial = true; s.rootBudgetClipped = true; }
                            }
                        }
                        if (node.depth >= DepthLimit) { s.rootPartial = true; s.rootBudgetClipped = true; yield return 0; continue; }
                        var dict = value as IDictionary;
                        var list = value as IList;
                        if (dict != null || list != null)
                        {
                            int count = dict != null ? dict.Count : list.Count;
                            // Snapshot only this bounded, selector-owned container. Never foreign statics.
                            int room = Math.Max(0, NodeLimit - s.rootNodes - stack.Count);
                            // Page only selector directory roots. Applying the same offset to a nested
                            // container would silently lose its early entries on later root pages.
                            bool pagedDirectory = dict != null && node.depth == 1 && node.path.StartsWith("selector[", StringComparison.Ordinal);
                            int offset = pagedDirectory ? _rootPage * ContainerLimit : 0;
                            int limit = pagedDirectory ? ContainerLimit : 4096;
                            int remaining = Math.Max(0, count - offset);
                            int take = Math.Min(remaining, Math.Min(room / (dict != null ? 2 : 1), limit));
                            if (pagedDirectory && offset + take < count) s.rootMorePages = true;
                            if (offset > 0 || take < count) s.rootPartial = true;
                            if ((!pagedDirectory && take < count) || take < Math.Min(remaining, limit)) s.rootBudgetClipped = true;
                            if (dict != null)
                            {
                                int n = 0;
                                foreach (DictionaryEntry entry in dict)
                                {
                                    if (n < offset) { n++; yield return 0; continue; }
                                    if (n >= offset + take) break;
                                    stack.Push(new RootNode { value = entry.Value, path = node.path + ".values[" + n.ToString() + "]", depth = node.depth + 1, context = context });
                                    if (entry.Key is UnityEngine.Object)
                                        stack.Push(new RootNode { value = entry.Key, path = node.path + ".keys[" + n.ToString() + "]", depth = node.depth + 1, context = context });
                                    n++; yield return 0;
                                }
                            }
                            else
                                for (int n = take - 1; n >= 0; n--)
                                {
                                    stack.Push(new RootNode { value = list[n], path = node.path + "[" + n.ToString() + "]", depth = node.depth + 1, context = context });
                                    yield return 0;
                                }
                        }
                        else
                        {
                            string[] names = ReferenceFields(value.GetType());
                            for (int n = names.Length - 1; n >= 0; n--)
                            {
                                FieldInfo field = FindField(value.GetType(), names[n]);
                                if (field == null) { s.rootMissing++; continue; }
                                object nextValue = field.GetValue(value);
                                if (value is Atom)
                                {
                                    var registry = nextValue as ICollection;
                                    Log(s.tag + " atom-registry path=" + node.path + "." + names[n] + " count=" + (registry == null ? -1 : registry.Count));
                                }
                                stack.Push(new RootNode { value = nextValue, path = ReferenceEquals(nextValue, null) ? null : node.path + "." + names[n], depth = node.depth + 1, context = context });
                                yield return 0;
                            }
                        }
                        yield return 0;
                    }
                }
                finally { stack.Clear(); }
            }
        }
        // Read managed array fields only; never invoke getters/native APIs on a destroyed skin.
        private static IEnumerable<int> MeasureDeadSkinArrays(DAZSkinV2 skin, Stats s)
        {
            for (Type type = skin.GetType(); type != null && type != typeof(UnityEngine.Object); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(All | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsArray) continue;
                    var array = field.GetValue(skin) as Array;
                    if (array == null || array.Length == 0) continue;
                    int width = ElementBytes(field.FieldType.GetElementType());
                    if (width == 0) { s.rootPartial = true; continue; }
                    if (s.rootArrays.Count >= ArrayCap) { s.rootPartial = true; continue; }
                    if (s.rootSkinArrays.Add(array)) s.rootDeadSkinBytes += array.LongLength * width;
                    if (s.rootArrays.Add(array)) s.rootTotalDeadBytes += array.LongLength * width;
                    string key = type.Name + "." + field.Name;
                    long old;
                    if (s.skinFieldAliasBytes.TryGetValue(key, out old) || s.skinFieldAliasBytes.Count < 128)
                        s.skinFieldAliasBytes[key] = old + array.LongLength * width;
                    yield return 0;
                }
            // Nodes are plain managed objects. Measure only their known array fields,
            // not a general recursive graph. Reference slot counts are not object heap bytes.
            FieldInfo nodesField = FindField(skin.GetType(), "nodes");
            var nodes = nodesField == null ? null : nodesField.GetValue(skin) as IList;
            if (nodes != null)
            {
                if (nodes.Count > 512) s.rootPartial = true;
                for (int i = 0; i < Math.Min(nodes.Count, 512); i++)
                {
                    object node = nodes[i]; if (node == null) continue;
                    foreach (string name in new[] { "weights", "generalWeights", "fullyWeightedVertices" })
                    {
                        FieldInfo field = FindField(node.GetType(), name);
                        if (field == null) { s.rootMissing++; continue; }
                        var array = field.GetValue(node) as Array;
                        if (array == null || array.Length == 0) continue;
                        int width = ElementBytes(field.FieldType.GetElementType());
                        if (width == 0 || s.rootArrays.Count >= ArrayCap) { s.rootPartial = true; continue; }
                        if (s.rootSkinArrays.Add(array))
                        {
                            s.rootDeadSkinBytes += array.LongLength * width;
                            if (name == "weights") s.skinWeightSlots += array.LongLength;
                            if (name == "generalWeights") s.skinGeneralWeightSlots += array.LongLength;
                        }
                        if (s.rootArrays.Add(array)) s.rootTotalDeadBytes += array.LongLength * width;
                        yield return 0;
                    }
                }
            }
        }
        private static IEnumerable<int> MeasureDeadWrapArrays(DAZSkinWrap wrap, Stats s)
        {
            for (Type type = wrap.GetType(); type != null && type != typeof(UnityEngine.Object); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(All | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsArray) continue;
                    var array = field.GetValue(wrap) as Array;
                    if (array == null || array.Length == 0) continue;
                    int width = ElementBytes(field.FieldType.GetElementType());
                    if (width == 0 || s.rootArrays.Count >= ArrayCap) { s.rootPartial = true; continue; }
                    if (s.rootWrapArrays.Add(array)) s.rootDeadWrapBytes += array.LongLength * width;
                    if (s.rootArrays.Add(array)) s.rootTotalDeadBytes += array.LongLength * width;
                    yield return 0;
                }
        }
        private static void RetireStaleSkinFields(Stats s)
        {
            if (!RetireDeadSkinCaches || !Ready()) return;
            foreach (CacheCandidate candidate in s.cacheCandidates)
            {
                var selector = candidate.selector.Target as DAZCharacterSelector;
                var character = candidate.character.Target as DAZCharacter;
                if (selector == null || character == null) continue;
                object selected = FindField(selector.GetType(), "_selectedCharacter").GetValue(selector);
                object loaded = FindField(selector.GetType(), "_loadedCharacter").GetValue(selector);
                if (ReferenceEquals(character, selected) || ReferenceEquals(character, loaded)) continue;
                foreach (string name in new[] { "_skin", "_skinForClothes" })
                {
                    FieldInfo field = FindField(character.GetType(), name);
                    if (field == null) continue;
                    var skin = field.GetValue(character) as DAZSkinV2;
                    if (ReferenceEquals(skin, null) || skin != null) continue;
                    // CLR field write deliberately bypasses Unity fake-null equality.
                    // The native component is already dead; no shared asset is destroyed.
                    bool seen = false;
                    foreach (WeakReference weak in RetiredSkins)
                        if (ReferenceEquals(weak.Target, skin)) { seen = true; break; }
                    if (!seen && RetiredSkins.Count < 64) RetiredSkins.Add(new WeakReference(skin));
                    field.SetValue(character, null); s.retiredFields++; _skinFieldsRetired++;
                    Log(s.tag + " cache-retire field=DAZCharacter." + name +
                        " selected=False loaded=False skinNativeDead=True action=clear-managed-cache-only");
                }
            }
        }
        // InitInstance unconditionally rebuilds these arrays. SetLocked accepts CLR null.
        // Never clear live/partially live controls or arrays during an active load.
        private static void RetireStaleControls(DAZDynamicItem item, string path, string tag)
        {
            if (!RetireDeadControls || !Ready() || item == null || item.ready) return;
            string name = item is DAZClothingItem ? "clothingItemControls" :
                item is DAZHairGroup ? "hairGroupControls" : null;
            if (name == null) return;
            FieldInfo instanceField = FindField(item.GetType(), "instance");
            FieldInfo field = FindField(item.GetType(), name);
            if (instanceField == null || field == null) return;
            if ((instanceField.GetValue(item) as UnityEngine.Object) != null) return;
            Array controls = field.GetValue(item) as Array;
            if (controls == null || controls.Length == 0 || controls.Length > OwnerCap) return;
            foreach (object control in controls)
            {
                if (ReferenceEquals(control, null)) continue;
                var obj = control as UnityEngine.Object;
                if (ReferenceEquals(obj, null) || obj != null) return;
            }
            // Synchronous main-thread check/write: no yield, callbacks or native calls in between.
            field.SetValue(item, null); _controlFieldsRetired++;
            Log(tag + " control-cache-retire path=" + path + "." + name +
                " descriptorId=" + item.GetInstanceID() + " controls=" + controls.Length + " ready=False instanceNativeAlive=False" +
                " allControlsNativeDead=True action=clear-managed-cache-only total=" + _controlFieldsRetired);
        }
        private static int RetireDeadDynamicEntries(object manager, string path, string tag)
        {
            var nativeManager = manager as UnityEngine.Object;
            if (!Ready() || nativeManager == null || manager.GetType().Name != "PresetManager") return 0;
            FieldInfo field = FindField(manager.GetType(), "dynamicStorables");
            var list = field == null ? null : field.GetValue(manager) as IList;
            if (list == null || list.IsReadOnly || list.IsFixedSize || list.Count > ItemCap) return 0;
            int before = list.Count, removed = 0;
            // Main thread, no yield or refresh/lock callbacks. Preserve all live entries and order.
            for (int n = list.Count - 1; n >= 0; n--)
            {
                object entry = list[n];
                if (ReferenceEquals(entry, null)) continue;
                FieldInfo storableField = FindField(entry.GetType(), "storable");
                if (storableField == null) continue;
                var storable = storableField.GetValue(entry) as UnityEngine.Object;
                if (!ReferenceEquals(storable, null) && storable == null)
                { list.RemoveAt(n); removed++; }
            }
            if (removed != 0)
            {
                _dynamicEntriesRetired += removed;
                Log(tag + " dynamic-registry-retire path=" + path + ".dynamicStorables managerNativeAlive=True entries=" +
                    before + "->" + list.Count + " removed=" + removed + " total=" + _dynamicEntriesRetired +
                    " action=remove-native-dead-storable-only liveEntriesPreserved=True");
            }
            return removed;
        }
        private static void Finish(string status)
        {
            Stats s = _stats;
            if (status == "done")
                foreach (var candidate in s.dynamicCandidates)
                    RetireDeadDynamicEntries(candidate.Key.Target, candidate.Value, s.tag);
            RetireStaleSkinFields(s);
            // Measure the old graph first, then re-check state before removing its root.
            foreach (WeakReference weak in s.controlCandidates)
                RetireStaleControls(weak.Target as DAZDynamicItem, "catalog-page-" + _rootPage, s.tag);
            var skinFields = new List<KeyValuePair<string,long>>(s.skinFieldAliasBytes);
            skinFields.Sort((a,b) => b.Value.CompareTo(a.Value));
            for (int n=0;n<Math.Min(8,skinFields.Count);n++)
                Log(s.tag + " dead-skin-array field=" + skinFields[n].Key + " aliasPayloadBytes=" + skinFields[n].Value);
            Log(s.tag + " dead-skin-weights refSlots=" + s.skinWeightSlots + " generalRefSlots=" + s.skinGeneralWeightSlots +
                " scalarUpperBytesIfUnique=" + (s.skinWeightSlots*40 + s.skinGeneralWeightSlots*8) + " (known scalar fields only; no headers; shared objects may overcount)");
            int retiredStillAlive = 0;
            for (int i = RetiredSkins.Count - 1; i >= 0; i--)
                if (RetiredSkins[i].IsAlive) retiredStillAlive++; else RetiredSkins.RemoveAt(i);
            Log(s.tag + " stale-skin retiredFields=" + s.retiredFields + " retiredFieldsTotal=" + _skinFieldsRetired +
                " retiredSkinWeakAlive=" + retiredStillAlive + " (weak observations, not proof of released bytes)");
            Log(s.tag + " known-roots page=" + _rootPage + " morePages=" + s.rootMorePages + " selectors=" + s.rootSelectors + " nodes=" + s.rootNodes + " matchedDeadOwners=" + s.matchedDead.Count + " rootSeeds=" + s.rootSeeds + " rootUntrackedDead=" + s.rootUntrackedDead + " rootDeadDirectArrayBytes=" + s.rootDeadBytes + " rootDeadSkins=" + s.rootDeadSkins + " rootDeadSkinDirectArrayBytes=" + s.rootDeadSkinBytes + " rootDeadWraps=" + s.rootDeadWraps + " rootDeadWrapDirectArrayBytes=" + s.rootDeadWrapBytes + " rootTotalDeadDirectArrayBytes=" + s.rootTotalDeadBytes + " rootRows=" + s.rootRows + " rootRowsSkipped=" + s.rootRowsSkipped + " rootRowsTruncated=" + (s.rootRowsSkipped > 0) + " dynamicCandidates=" + s.dynamicCandidates.Count + " dynamicCandidatesSkipped=" + s.dynamicCandidatesSkipped + " controlCandidates=" + s.controlCandidates.Count + " controlCandidatesSkipped=" + s.controlCandidatesSkipped + " cacheCandidates=" + s.cacheCandidates.Count + " cacheCandidatesSkipped=" + s.cacheCandidatesSkipped + " partial=" + s.rootPartial + " budgetClipped=" + s.rootBudgetClipped + " missingFields=" + s.rootMissing + " rowCap=256 nodeCap=16384 directoryPageCap=256 nestedContainerCap=4096 (fixed selector and page-zero Atom registry paths; unmatched is not proof of no root)");
            for (int i = 0; i < 8 && ExitRows.Count != 0; i++) Log(s.tag + " " + ExitRows.Dequeue());
            Log(s.tag + " status=" + status + " scope=hook-observed-DAZMesh+fixed-root owners=" + s.live + " tracked=" + Owners.Count + " nativeLiveOwners=" + s.nativeLive + " nativeDeadManagedOwners=" + s.nativeDead +
                " repeatedDeriveOwners=" + s.repeated + " registeredMesh=" + s.meshes + " registeredMaterial=" + s.materials +
                " unityDead=" + s.gone + " directArrayPayloadBytes=" + s.bytes + " nativeDeadArrayPayloadBytes=" + s.nativeDeadBytes + " unknownFieldsOrLists=" + s.unknown +
                " arraySkipped=" + s.arraySkipped + " registrationsTotal=" + _registers + " exitCalls=" + _exits +
                " exitRowsDropped=" + _exitDropped + " ownerSkippedEvents=" + _skipped + " retiredWeak=" + _retired + " (direct arrays held by native-dead owners may be shared; not total heap/native/GPU or all preexisting owners)");
            bool nextPage = s.rootMorePages && _rootPage < 511 && status == "done";
            Cancel(); Prune();
            if (nextPage)
            {
                _rootPage++; _rootPageAfter = Time.realtimeSinceStartup + 0.25f;
                _request = "known-roots-page-" + _rootPage;
            }
        }
        internal static void Tick()
        {
            Install();
            if (!Ready()) { Cancel(); _request = null; _rootPage = 0; _rootPageAfter = 0f; return; }
            if (_walk == null && _request != null && Time.realtimeSinceStartup >= _rootPageAfter)
            {
                _stats = new Stats { tag = _request }; _request = null;
                _started = Time.realtimeSinceStartup; _walk = Scan(_stats).GetEnumerator();
            }
            if (_walk == null) return;
            try
            {
                if (Time.realtimeSinceStartup - _started > 30f) { Finish("timeout-partial"); return; }
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 4096; i++)
                {
                    if (!_walk.MoveNext()) { Finish("done"); return; }
                    if (Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency / 500) break;
                }
            }
            catch (Exception e) { Log("walk failed=" + e.GetType().Name); Cancel(); }
        }
        internal static void Shutdown()
        {
            Cancel(); _request = null;
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; _installTried = false; Owners.Clear(); ExitRows.Clear();
            _registers = _exits = _skipped = _retired = _exitDropped = 0; _nextRecord = 0; RetiredSkins.Clear(); _skinFieldsRetired = 0; _controlFieldsRetired = 0; _dynamicEntriesRetired = 0; _rootPage = 0; _rootPageAfter = 0f;
        }
        private static void Log(string text)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[mesh-owner-ret] " + text); }
    }
}
