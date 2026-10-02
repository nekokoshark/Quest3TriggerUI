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
    // Read-only CPU buffer ownership sample. No getters on GPUTools objects:
    // GroupedData.Data allocates and changes GroupsData when read.
    internal static class GpuPhysicsRetentionProbe
    {
        private const int OwnerCap = 256, NodeCap = 16384, ArrayCap = 4096, ContainerCap = 4096;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Hair = "GPUTools.Hair.Scripts.HairSettings";
        private const string Cloth = "GPUTools.Cloth.Scripts.ClothSettings";
        private static readonly List<Record> Records = new List<Record>();
        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Identity Comparer = new Identity();
        private static Harmony _harmony;
        private static bool _installed, _reporting;
        private static int _nextId, _dropped;
        private static string _request;
        private static float _after, _started;
        private static IEnumerator _walk;
        private static Sample _sample;
        internal static bool Running { get { return _walk != null; } }
        internal static int Tracked { get { return Records.Count; } }

        private sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object x) { return RuntimeHelpers.GetHashCode(x); }
        }
        private sealed class Record
        {
            internal WeakReference target;
            internal int id;
            internal bool exited;
        }
        private sealed class Owner
        {
            internal Record record;
            internal bool rooted, dead;
            internal string path;
            internal long bytes;
            internal int buffersLive, buffersDisposed, buffersUnknown;
            internal readonly HashSet<object> seen = new HashSet<object>(Comparer);
            internal readonly HashSet<object> arrays = new HashSet<object>(Comparer);
        }
        private sealed class Data
        {
            internal long bytes, length;
            internal int token, hash, stride, flags, owners;
            internal string type, path;
        }
        private sealed class Node
        {
            internal object value;
            internal string path;
            internal Owner owner;
            internal int depth;
            internal bool weak, disposed;
        }
        private sealed class Sample
        {
            internal string tag;
            internal int nodes, missing, unknownArrays, clipped, persons;
            internal string consumerManagerState = "unread";
            internal int consumersBefore = -1, consumersAfter = -1;
            internal int consumersScanned, consumerDuplicates, consumerNulls, consumerDead, consumerOther;
            internal int clothAttachments, attachmentReadFailures, clothSwitcherVisits, clothCreatorVisits, clothReloaderVisits;
            internal readonly HashSet<object> consumersSeen = new HashSet<object>(Comparer);
            internal readonly HashSet<object> rootsSeen = new HashSet<object>(Comparer);
            internal readonly Dictionary<object, Owner> owners = new Dictionary<object, Owner>(Comparer);
            internal readonly Dictionary<object, Data> arrays = new Dictionary<object, Data>(Comparer);
            internal readonly Stack<Node> stack = new Stack<Node>();
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Fields | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }
        private static object Read(object value, string name, Sample s)
        {
            FieldInfo f = FindField(value.GetType(), name);
            if (f == null) { s.missing++; return null; }
            return f.GetValue(value);
        }
        private static bool IsSettings(Type t) { return t.FullName == Hair || t.FullName == Cloth; }
        private static bool Generic(Type t, string name)
        { return t.IsGenericType && t.GetGenericTypeDefinition().FullName == name; }
        private static bool Buffer(Type t)
        { return Generic(t, "GPUTools.Common.Scripts.PL.Tools.GpuBuffer`1"); }
        private static bool Group(Type t)
        { return Generic(t, "GPUTools.Common.Scripts.PL.Tools.GroupedData`1"); }
        private static bool Command(Type t)
        {
            for (; t != null; t = t.BaseType)
                if (t.FullName == "GPUTools.Common.Scripts.Tools.Commands.BuildChainCommand") return true;
            return false;
        }
        // Strides verified against this game's value fields and Mono array metadata.
        internal static int Stride(Type t)
        {
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(bool)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(float) || t == typeof(int) || t == typeof(uint)) return 4;
            if (t == typeof(double) || t == typeof(long) || t == typeof(ulong)) return 8;
            switch (t.FullName)
            {
                case "UnityEngine.Vector2": return 8;
                case "UnityEngine.Vector3": return 12;
                case "UnityEngine.Vector4": case "UnityEngine.Quaternion": return 16;
                case "UnityEngine.Matrix4x4": return 64;
                case "GPUTools.Physics.Scripts.Types.Dynamic.GPParticle": return 116;
                case "GPUTools.Physics.Scripts.Types.Joints.GPDistanceJoint": return 16;
                case "GPUTools.Physics.Scripts.Types.Joints.GPPointJoint": return 24;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPSphere": return 20;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPSphereWithDelta": return 32;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPLineSphere": return 36;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPLineSphereWithDelta": return 72;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPLineSphereWithMatrixDelta": return 100;
                case "GPUTools.Physics.Scripts.Types.Shapes.GPGrabSphere": return 24;
                case "GPUTools.Hair.Scripts.Runtime.Render.RenderParticle": return 28;
                case "GPUTools.Hair.Scripts.Runtime.Render.TessRenderParticle": return 56;
                case "GPUTools.Cloth.Scripts.Types.ClothVertex": return 36;
                case "GPUTools.Cloth.Scripts.Types.Int2":
                case "GPUTools.Common.Scripts.PL.Tools.GroupData": return 8;
            }
            return 0;
        }
        private static FieldInfo[] References(Type type, Sample s)
        {
            FieldInfo[] result;
            if (FieldCache.TryGetValue(type, out result)) return result;
            int missingBefore = s.missing;
            string[] names = null;
            for (Type t = type; t != null && names == null; t = t.BaseType)
            {
                switch (t.FullName)
                {
                    case "Atom": names = new[] { "_storables", "_storableById", "<presetManagerControls>k__BackingField" }; break;
                    case "HairSimControl": names = new[] { "hairSettings" }; break;
                    case "ClothSimControl": names = new[] { "clothSettings" }; break;
                    case "DAZSkinWrapSwitcher": names = new[] { "clothSettings" }; break;
                    case "MeshVR.DAZRuntimeCreator": names = new[] { "pm", "clothSettingsForThread" }; break;
                    case "MeshVR.DAZClothSettingsSimTextureReloader": names = new[] { "clothSettings" }; break;
                    case "MeshVR.PresetManagerControl": names = new[] { "pm" }; break;
                    case "MeshVR.PresetManager": names = new[] { "storables", "optionalStorables", "optionalStorables2", "optionalStorables3", "dynamicStorables", "regularStorables" }; break;
                    case "MeshVR.PresetManager/Storable":
                    case "MeshVR.PresetManager+Storable": names = new[] { "storable" }; break;
                    case Hair: names = new[] { "<RuntimeData>k__BackingField", "<HairBuidCommand>k__BackingField" }; break;
                    case Cloth: names = new[] { "<Runtime>k__BackingField", "<builder>k__BackingField" }; break;
                }
            }
            var fields = new List<FieldInfo>();
            if (names != null)
            {
                foreach (string name in names)
                {
                    FieldInfo f = FindField(type, name);
                    if (f == null) s.missing++; else fields.Add(f);
                }
            }
            else if (Group(type))
            {
                foreach (string name in new[] { "Groups", "GroupsData" })
                {
                    FieldInfo f = FindField(type, name);
                    if (f == null) s.missing++; else fields.Add(f);
                }
            }
            else
            {
                bool runtime = type.FullName == "GPUTools.Hair.Scripts.Runtime.Data.RuntimeData" ||
                    type.FullName == "GPUTools.Cloth.Scripts.Runtime.Data.RuntimeData";
                bool command = Command(type);
                if (runtime || command)
                    for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                        foreach (FieldInfo f in t.GetFields(Fields | BindingFlags.DeclaredOnly))
                            if (f.FieldType.IsArray || Buffer(f.FieldType) || Group(f.FieldType) ||
                                (command && (f.Name == "commands" || f.Name == "settings"))) fields.Add(f);
            }
            result = fields.ToArray();
            if (s.missing == missingBefore && FieldCache.Count < 128) FieldCache[type] = result;
            return result;
        }
        private static Record Remember(object target)
        {
            for (int i = Records.Count - 1; i >= 0; i--)
            {
                object old = Records[i].target.Target;
                if (old == null) Records.RemoveAt(i);
                else if (ReferenceEquals(old, target)) return Records[i];
            }
            if (Records.Count >= OwnerCap) { _dropped++; return null; }
            var r = new Record { target = new WeakReference(target), id = ++_nextId };
            Records.Add(r); return r;
        }
        private static void AfterExit(object __instance)
        {
            try
            {
                if (ReferenceEquals(__instance, null)) return;
                Record r = Remember(__instance);
                if (r != null) r.exited = true;
                Request("settings-exit");
            }
            catch (Exception e) { Log("exit-observation failed=" + e.GetType().Name); }
        }
        private static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new Harmony("local.vam.gpu-owner-ret." + typeof(GpuPhysicsRetentionProbe).Assembly.GetName().Name);
                var postfix = new HarmonyMethod(typeof(GpuPhysicsRetentionProbe).GetMethod("AfterExit", BindingFlags.NonPublic | BindingFlags.Static));
                foreach (Type t in new[] { typeof(GPUTools.Hair.Scripts.HairSettings), typeof(GPUTools.Cloth.Scripts.ClothSettings) })
                    _harmony.Patch(t.GetMethod("OnDestroy", Fields), postfix: postfix);
                Log("installed readOnly=True exitHooks=2 scope=GPUColliders-consumers+Person-registry+active-settings+live-cloth-attachments+weak-exits");
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Log("exit-hooks failed=" + e.GetType().Name + " (fixed-root sampling remains available)");
            }
            Request("startup");
        }
        private static void Push(Sample s, object value, string path, Owner owner, int depth, bool weak, bool disposed)
        {
            if (ReferenceEquals(value, null)) return;
            if (depth > 24 || s.stack.Count >= NodeCap) { s.clipped++; return; }
            s.stack.Push(new Node { value = value, path = path, owner = owner, depth = depth, weak = weak, disposed = disposed });
        }
        private static void AddArray(Sample s, Node n, Array array)
        {
            int width = Stride(array.GetType().GetElementType());
            if (width == 0) { s.unknownArrays++; return; }
            Data data;
            if (!s.arrays.TryGetValue(array, out data))
            {
                if (s.arrays.Count >= ArrayCap) { s.clipped++; return; }
                data = new Data { bytes = array.LongLength * width, length = array.LongLength,
                    stride = width, hash = RuntimeHelpers.GetHashCode(array), token = s.arrays.Count + 1,
                    type = array.GetType().GetElementType().FullName, path = n.path };
                s.arrays.Add(array, data);
            }
            int flag = !n.owner.rooted ? 4 : n.owner.dead ? 2 : 1;
            if (flag == 2 && (data.flags & 2) == 0) data.path = n.path;
            data.flags |= flag | (n.disposed && n.owner.rooted ? 8 : 0);
            if (n.owner.arrays.Add(array)) { n.owner.bytes += data.bytes; data.owners++; }
        }
        private static IEnumerable<int> Drain(Sample s)
        {
            while (s.stack.Count != 0)
            {
                if (++s.nodes > NodeCap) { s.clipped++; s.stack.Clear(); yield break; }
                Node n = s.stack.Pop(); object value = n.value; Type type = value.GetType();
                if (IsSettings(type))
                {
                    Owner owner;
                    if (!s.owners.TryGetValue(value, out owner))
                    {
                        if (s.owners.Count >= OwnerCap) { s.clipped++; yield return 0; continue; }
                        var unity = value as UnityEngine.Object;
                        owner = new Owner { record = Remember(value), rooted = !n.weak,
                            dead = !ReferenceEquals(unity, null) && unity == null, path = n.path };
                        s.owners.Add(value, owner);
                        // Only query the same GameObject of a known live, rooted cloth.
                        // No scene-wide component enumeration; never call native APIs on dead/weak owners.
                        var cloth = value as GPUTools.Cloth.Scripts.ClothSettings;
                        if (!ReferenceEquals(cloth, null) && owner.rooted && !owner.dead)
                        {
                            try
                            {
                                var attached = cloth.GetComponents<MeshVR.DAZClothSettingsSimTextureReloader>();
                                int count = Math.Min(attached.Length, ContainerCap);
                                if (count < attached.Length) s.clipped++;
                                s.clothAttachments += count;
                                for (int i = 0; i < count; i++)
                                    Push(s, attached[i], n.path + ".GetComponents<DAZClothSettingsSimTextureReloader>[" + i + "]",
                                        null, n.depth + 1, false, false);
                            }
                            catch (Exception) { s.attachmentReadFailures++; }
                        }
                    }
                    n.owner = owner;
                }
                var array = value as Array;
                if (array != null && array.Rank == 1 && array.GetType().GetElementType().IsValueType)
                {
                    if (n.owner != null && array.Length != 0) AddArray(s, n, array);
                    yield return 0; continue;
                }
                var seen = n.owner == null ? s.rootsSeen : n.owner.seen;
                if (!seen.Add(value)) { yield return 0; continue; }
                if (type.FullName == "DAZSkinWrapSwitcher") s.clothSwitcherVisits++;
                else if (type.FullName == "MeshVR.DAZRuntimeCreator") s.clothCreatorVisits++;
                else if (type.FullName == "MeshVR.DAZClothSettingsSimTextureReloader") s.clothReloaderVisits++;
                if (array != null)
                {
                    if (array.Rank != 1) { s.clipped++; yield return 0; continue; }
                    int count = Math.Min(array.Length, ContainerCap);
                    if (count < array.Length) s.clipped++;
                    for (int i = 0; i < count; i++)
                    {
                        Push(s, array.GetValue(i), n.path + "[" + i + "]", n.owner, n.depth + 1, n.weak, false);
                        yield return 0;
                    }
                }
                else if (Generic(type, "System.Collections.Generic.List`1"))
                    Push(s, Read(value, "_items", s), n.path + "._items", n.owner, n.depth + 1, n.weak, false);
                else if (Generic(type, "System.Collections.Generic.Dictionary`2"))
                {
                    int i = 0;
                    foreach (DictionaryEntry pair in (IDictionary)value)
                    {
                        if (i >= ContainerCap) { s.clipped++; break; }
                        Push(s, pair.Value, n.path + ".value[" + i++ + "]", n.owner, n.depth + 1, n.weak, false);
                        yield return 0;
                    }
                }
                else if (Buffer(type) && n.owner != null)
                {
                    object compute = Read(value, "<ComputeBuffer>k__BackingField", s);
                    object ptr = compute == null ? null : Read(compute, "m_Ptr", s);
                    bool disposed = ptr is IntPtr && (IntPtr)ptr == IntPtr.Zero;
                    if (disposed) n.owner.buffersDisposed++;
                    else if (ptr is IntPtr) n.owner.buffersLive++;
                    else n.owner.buffersUnknown++;
                    Push(s, Read(value, "<Data>k__BackingField", s), n.path + ".<Data>", n.owner, n.depth + 1, n.weak, disposed);
                }
                else foreach (FieldInfo f in References(type, s))
                {
                    Push(s, f.GetValue(value), n.path + "." + f.Name, n.owner, n.depth + 1, n.weak, false);
                    yield return 0;
                }
                yield return 0;
            }
        }
        private static IEnumerable<int> Scan(Sample s)
        {
            // This static field and consumers field were verified against Assembly-CSharp.
            // CLR-non-null/native-dead managers still constitute a real static holding chain.
            FieldInfo singleton = typeof(GPUCollidersManager).GetField("singleton",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (singleton == null) s.missing++;
            else
            {
                object manager = singleton.GetValue(null);
                if (ReferenceEquals(manager, null)) s.consumerManagerState = "null";
                else
                {
                    var unity = manager as UnityEngine.Object;
                    s.consumerManagerState = !ReferenceEquals(unity, null) && unity == null ? "native-dead" : "live";
                    object value = Read(manager, "consumers", s);
                    var list = value as IList;
                    if (list == null) s.consumerManagerState += ReferenceEquals(value, null) ? "/list-null" : "/list-unsupported";
                    else
                    {
                        s.consumersBefore = list.Count;
                        int count = Math.Min(s.consumersBefore, ContainerCap);
                        if (count < s.consumersBefore) s.clipped++;
                        for (int i = 0; i < count && i < list.Count; i++)
                        {
                            object consumer = list[i]; s.consumersScanned++;
                            if (ReferenceEquals(consumer, null)) s.consumerNulls++;
                            else if (!s.consumersSeen.Add(consumer)) s.consumerDuplicates++;
                            else
                            {
                                var native = consumer as UnityEngine.Object;
                                if (!ReferenceEquals(native, null) && native == null) s.consumerDead++;
                                if (!IsSettings(consumer.GetType())) s.consumerOther++;
                                else
                                {
                                    Push(s, consumer, "GPUCollidersManager.singleton.consumers[" + i + "]", null, 0, false, false);
                                    foreach (int step in Drain(s)) yield return step;
                                }
                            }
                            yield return 0;
                        }
                        s.consumersAfter = list.Count;
                    }
                }
            }
            int index = 0;
            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                if (index++ >= 128) { s.clipped++; break; }
                if (atom == null || atom.type != "Person") continue;
                s.persons++;
                Push(s, atom, "Person[" + (index - 1) + "]", null, 0, false, false);
                foreach (int step in Drain(s)) yield return step;
            }
            foreach (Type type in new[] { typeof(GPUTools.Hair.Scripts.HairSettings), typeof(GPUTools.Cloth.Scripts.ClothSettings) })
            {
                // These two settings types are already in use in the loaded scene.
                FieldInfo active = type.GetField("s_activeFemales", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (active == null) { s.missing++; continue; }
                Push(s, active.GetValue(null), type.Name + ".s_activeFemales", null, 0, false, false);
                foreach (int step in Drain(s)) yield return step;
            }
            // Observations obtained through WeakReference are never called strong roots.
            for (int i = 0; i < Records.Count; i++)
            {
                object value = Records[i].target.Target;
                if (value == null || s.owners.ContainsKey(value)) continue;
                Push(s, value, "weak-only(record=" + Records[i].id + ")", null, 0, true, false);
                foreach (int step in Drain(s)) yield return step;
            }
        }
        private static IEnumerable<int> Emit(Sample s, string status)
        {
            long total = 0, live = 0, dead = 0, weak = 0, disposed = 0, shared = 0;
            int liveOwners = 0, deadOwners = 0, weakOwners = 0, shown = 0;
            foreach (Owner owner in s.owners.Values)
            {
                if (!owner.rooted) weakOwners++; else if (owner.dead) deadOwners++; else liveOwners++;
                if (shown++ < 48)
                    Log(s.tag + " owner record=" + (owner.record == null ? 0 : owner.record.id) +
                        " state=" + (!owner.rooted ? "weak-only" : owner.dead ? "native-dead-rooted" : "native-live-rooted") +
                        " nativeDead=" + owner.dead + " exitObserved=" + (owner.record != null && owner.record.exited) +
                        " arrays=" + owner.arrays.Count + " aliasPayloadBytes=" + owner.bytes +
                        " computeLive=" + owner.buffersLive + " computeDisposed=" + owner.buffersDisposed +
                        " computeUnknown=" + owner.buffersUnknown + " path=" + owner.path);
                yield return 0;
            }
            var rows = new List<Data>(s.arrays.Values);
            rows.Sort(delegate(Data a, Data b) {
                int rankA = (a.flags & 2) != 0 ? 2 : (a.flags & 8) != 0 ? 1 : 0;
                int rankB = (b.flags & 2) != 0 ? 2 : (b.flags & 8) != 0 ? 1 : 0;
                int c = rankB.CompareTo(rankA); return c != 0 ? c : b.bytes.CompareTo(a.bytes);
            });
            foreach (Data data in rows)
            {
                total += data.bytes;
                if ((data.flags & 1) != 0) live += data.bytes;
                if ((data.flags & 2) != 0) dead += data.bytes;
                if ((data.flags & 3) == 0 && (data.flags & 4) != 0) weak += data.bytes;
                if ((data.flags & 8) != 0) disposed += data.bytes;
                if (data.owners > 1) shared += data.bytes;
                yield return 0;
            }
            for (int i = 0; i < Math.Min(32, rows.Count); i++)
            {
                Data d = rows[i];
                Log(s.tag + " data token=" + d.token + " identityHash=" + d.hash + " type=" + d.type +
                    " length=" + d.length + " stride=" + d.stride + " payloadBytes=" + d.bytes +
                    " ownerCount=" + d.owners + " flags=" + d.flags + " path=" + d.path);
                yield return 0;
            }
            Log(s.tag + " status=" + status + " persons=" + s.persons + " rootLiveOwners=" + liveOwners +
                " rootDeadOwners=" + deadOwners + " weakOnlyOwners=" + weakOwners + " arrays=" + s.arrays.Count +
                " unionPayloadBytes=" + total + " rootLiveBytes=" + live + " rootDeadBytes=" + dead +
                " weakOnlyBytes=" + weak + " disposedStrongBytes=" + disposed + " sharedAcrossOwnersBytes=" + shared +
                " nodes=" + s.nodes + " missingFields=" + s.missing + " unknownArrays=" + s.unknownArrays +
                " clipped=" + s.clipped + " weakDropped=" + _dropped +
                " consumerManager=" + s.consumerManagerState + " consumersBefore=" + s.consumersBefore +
                " consumersAfter=" + s.consumersAfter + " consumersScanned=" + s.consumersScanned +
                " consumerDuplicates=" + s.consumerDuplicates + " consumerNulls=" + s.consumerNulls +
                " consumerNativeDead=" + s.consumerDead + " consumerOtherTypes=" + s.consumerOther +
                " clothAttachments=" + s.clothAttachments + " attachmentReadFailures=" + s.attachmentReadFailures +
                " clothSwitcherVisits=" + s.clothSwitcherVisits + " clothCreatorVisits=" + s.clothCreatorVisits +
                " clothReloaderVisits=" + s.clothReloaderVisits + " scopeLimited=True" +
                " (CPU array capacity, not native/GPU/exclusive bytes; categories overlap; flags=live1/dead2/weak4/disposedStrong8)");
            yield return 0;
        }
        internal static void Request(string reason)
        { _request = reason; _after = Time.realtimeSinceStartup + 2f; }
        private static bool Ready()
        {
            var sc = SuperController.singleton;
            return sc != null && !sc.isLoading && !SceneLoadAccelerator.SceneLoadActive &&
                !LoadWindow.PresetBusy && !WardrobeJanitor.ImagesBusy();
        }
        internal static void Tick()
        {
            Install();
            if (!Ready())
            {
                if (_walk != null) { Cancel(); Request("interrupted-by-load"); }
                return;
            }
            if (_walk == null && _request != null && Time.realtimeSinceStartup >= _after)
            {
                _sample = new Sample { tag = _request }; _request = null;
                _started = Time.realtimeSinceStartup; _walk = Scan(_sample).GetEnumerator();
            }
            if (_walk == null) return;
            try
            {
                if (!_reporting && Time.realtimeSinceStartup - _started > 20f)
                { BeginReport("timeout-partial"); return; }
                long start = Stopwatch.GetTimestamp();
                for (int n = 0; n < 1024; n++)
                {
                    if (!_walk.MoveNext())
                    {
                        if (_reporting) Cancel(); else BeginReport("done");
                        break;
                    }
                    if (Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency / 500) break;
                }
            }
            catch (Exception e) { Log("walk failed=" + e.GetType().Name + ":" + e.Message); Cancel(); }
        }
        private static void BeginReport(string status)
        {
            var disposable = _walk as IDisposable;
            if (disposable != null) disposable.Dispose();
            _reporting = true; _walk = Emit(_sample, status).GetEnumerator();
        }
        private static void Cancel()
        {
            var disposable = _walk as IDisposable;
            if (disposable != null) disposable.Dispose();
            _walk = null; _reporting = false;
            if (_sample != null)
            {
                foreach (Owner owner in _sample.owners.Values) { owner.seen.Clear(); owner.arrays.Clear(); }
                _sample.owners.Clear(); _sample.arrays.Clear(); _sample.stack.Clear(); _sample.rootsSeen.Clear(); _sample.consumersSeen.Clear();
            }
            _sample = null;
        }
        internal static void Shutdown()
        {
            Cancel(); _request = null;
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; _installed = false; Records.Clear(); FieldCache.Clear();
            _nextId = _dropped = 0; _after = _started = 0;
        }
        private static void Log(string text)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[gpu-owner-ret] " + text); }
    }
}
