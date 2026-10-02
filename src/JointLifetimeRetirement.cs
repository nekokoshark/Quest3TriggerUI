using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using GPUTools.Common.Scripts.PL.Tools;
using GPUTools.Physics.Scripts.Types.Joints;
using GPUTools.Cloth.Scripts;
using GPUTools.Hair.Scripts;

namespace Quest3TriggerUI
{
    // Scoped to the audited GPDistanceJoint pipeline, not every GPU buffer.
    internal static class JointLifetimeRetirement
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ClothRuntime = typeof(ClothSettings).GetField("<Runtime>k__BackingField", Instance);
        private static readonly FieldInfo ClothBuilder = typeof(ClothSettings).GetField("<builder>k__BackingField", Instance);
        private static readonly FieldInfo HairRuntime = typeof(HairSettings).GetField("<RuntimeData>k__BackingField", Instance);
        private static readonly FieldInfo HairBuilder = typeof(HairSettings).GetField("<HairBuidCommand>k__BackingField", Instance);
        private static Harmony _harmony;
        private static long _groupsReleased, _jointSlotsReleased, _clothOwners, _hairOwners, _metadataCompacted;

        private static void BeforeData(GroupedData<GPDistanceJoint> __instance)
        {
            // Native get_Data appends one GroupData per group on every read.
            // Rebuild this derived table rather than accumulating stale entries.
            var metadata = __instance.GroupsData;
            int groups = __instance.Groups.Count;
            bool oversized = metadata.Capacity > 4 && metadata.Capacity > (long)groups * 2;
            metadata.Clear();
            if (oversized) { metadata.TrimExcess(); _metadataCompacted++; }
        }

        private static void ReleaseGroups(GroupedData<GPDistanceJoint> grouped)
        {
            // Keep separately borrowed child lists/data intact. Remove only the
            // disposed owner's references to the large child payloads.
            // Kernels borrow the small GroupsData table until RebindData; keep
            // that table intact even during Hair.CreateDistanceJoints/restart.
            if (grouped.Groups != null)
            {
                _groupsReleased += grouped.Groups.Count;
                foreach (var child in grouped.Groups)
                    if (child != null) _jointSlotsReleased += child.Count;
                grouped.Groups = new List<List<GPDistanceJoint>>();
            }
        }

        private static IEnumerable<CodeInstruction> DisposeCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int changed = 0;
            var release = typeof(JointLifetimeRetirement).GetMethod("ReleaseGroups", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method != null &&
                    method.DeclaringType == typeof(GroupedData<GPDistanceJoint>) && method.Name == "Dispose")
                {
                    // Native Dispose is empty and may already be inlined. Patch
                    // the seven real caller bodies, which Harmony recompiles.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = release;
                    changed++;
                }
                yield return instruction;
            }
            if (changed != 1)
                throw new InvalidOperationException("Expected one grouped-dispose call in " + __originalMethod + "; got " + changed);
        }

        private static void ClothDestroyed(ClothSettings __instance)
        {
            // Postfix runs only after the original provider/builder cleanup.
            // OnDisable/restart/live runtime data are deliberately not hooked.
            if (ClothRuntime.GetValue(__instance) != null || ClothBuilder.GetValue(__instance) != null) _clothOwners++;
            ClothRuntime.SetValue(__instance, null);
            ClothBuilder.SetValue(__instance, null);
        }

        private static void HairDestroyed(HairSettings __instance)
        {
            if (HairRuntime.GetValue(__instance) != null || HairBuilder.GetValue(__instance) != null) _hairOwners++;
            HairRuntime.SetValue(__instance, null);
            HairBuilder.SetValue(__instance, null);
        }

        private static void VerifyGetter(MethodInfo getter)
        {
            // Small isolated input also verifies the closed-generic detour on
            // the running Mono, without changing scene objects or requesting GC.
            var sample = new GroupedData<GPDistanceJoint>();
            sample.AddGroup(new List<GPDistanceJoint> { new GPDistanceJoint(1, 2, 3f, 4f) });
            sample.AddGroup(new List<GPDistanceJoint>());
            getter.Invoke(sample, null);
            var data = (GPDistanceJoint[])getter.Invoke(sample, null);
            if (data.Length != 1 || data[0].Body1Id != 1 || data[0].Body2Id != 2 ||
                data[0].Distance != 3f || data[0].Elasticity != 4f || sample.GroupsData.Count != 2 ||
                sample.GroupsData[0].Start != 0 || sample.GroupsData[0].Num != 1 ||
                sample.GroupsData[1].Start != 1 || sample.GroupsData[1].Num != 0)
                throw new InvalidOperationException("Joint metadata detour self-check failed");
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            if (ClothRuntime == null || ClothBuilder == null || HairRuntime == null || HairBuilder == null)
                throw new MissingFieldException("Cloth/Hair runtime lifetime fields changed");
            if (ClothRuntime.FieldType != typeof(GPUTools.Cloth.Scripts.Runtime.Data.RuntimeData) ||
                ClothBuilder.FieldType != typeof(GPUTools.Cloth.Scripts.Runtime.BuildRuntimeCloth) ||
                HairRuntime.FieldType != typeof(GPUTools.Hair.Scripts.Runtime.Data.RuntimeData) ||
                HairBuilder.FieldType != typeof(GPUTools.Hair.Scripts.Runtime.Commands.BuildRuntimeHair))
                throw new InvalidOperationException("Cloth/Hair runtime lifetime field types changed");
            _harmony = new Harmony("quest3triggerui.jointlifetime." + typeof(JointLifetimeRetirement).Namespace);
            try
            {
                var getter = typeof(GroupedData<GPDistanceJoint>).GetProperty("Data").GetGetMethod();
                _harmony.Patch(getter, prefix: new HarmonyMethod(typeof(JointLifetimeRetirement), "BeforeData"));
                string[] types = {
                    "GPUTools.Cloth.Scripts.Runtime.Commands.BuildDistanceJoints",
                    "GPUTools.Cloth.Scripts.Runtime.Commands.BuildNearbyJoints",
                    "GPUTools.Cloth.Scripts.Runtime.Commands.BuildStiffnessJoints",
                    "GPUTools.Hair.Scripts.Runtime.Commands.Physics.BuildCompressionJoints",
                    "GPUTools.Hair.Scripts.Runtime.Commands.Physics.BuildDistanceJoints",
                    "GPUTools.Hair.Scripts.Runtime.Commands.Physics.BuildNearbyDistanceJoints"
                };
                var assembly = typeof(ClothSettings).Assembly;
                var transpiler = new HarmonyMethod(typeof(JointLifetimeRetirement), "DisposeCalls");
                foreach (string name in types)
                {
                    var type = assembly.GetType(name, true);
                    _harmony.Patch(type.GetMethod("OnDispose", Instance | BindingFlags.DeclaredOnly), transpiler: transpiler);
                }
                _harmony.Patch(assembly.GetType(types[4]).GetMethod("CreateDistanceJoints", Instance | BindingFlags.DeclaredOnly), transpiler: transpiler);
                _harmony.Patch(typeof(ClothSettings).GetMethod("OnDestroy", Instance | BindingFlags.DeclaredOnly), postfix: new HarmonyMethod(typeof(JointLifetimeRetirement), "ClothDestroyed"));
                _harmony.Patch(typeof(HairSettings).GetMethod("OnDestroy", Instance | BindingFlags.DeclaredOnly), postfix: new HarmonyMethod(typeof(JointLifetimeRetirement), "HairDestroyed"));
                VerifyGetter(getter);
                Log("installed getter=1 disposeCallers=7 ownerExits=2 getterSelfCheck=passed liveData=preserved gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony == null) return;
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Log("shutdown clothOwners=" + _clothOwners + " hairOwners=" + _hairOwners + " groupsReleased=" + _groupsReleased +
                " jointSlotsReleased=" + _jointSlotsReleased + " metadataCompacted=" + _metadataCompacted + " countsNotUniqueBytes=True");
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[joint-lifetime] " + message);
        }
    }
}
