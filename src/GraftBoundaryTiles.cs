using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class GraftBoundaryTiles
    {
        internal static ConfigEntry<bool> Enabled;
        private const int Tile = 64;
        private static Harmony harmony;
        private static Func<DAZMergedMesh, Vector3[]> movement, alternate;
        private static Func<DAZMergedMesh, float[]> weights;
        private static Func<DAZMergedMesh, bool[]> free;
        private static int reported;

        private static Func<DAZMergedMesh, T> Getter<T>(string name)
        {
            FieldInfo field = AccessTools.Field(typeof(DAZMergedMesh), name);
            if (field == null || field.FieldType != typeof(T)) throw new InvalidOperationException("Field changed: " + name);
            var method = new DynamicMethod("graft_get_" + name, typeof(T), new[] { typeof(DAZMergedMesh) }, typeof(GraftBoundaryTiles), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<DAZMergedMesh, T>)method.CreateDelegate(typeof(Func<DAZMergedMesh, T>));
        }

        internal static void Install()
        {
            if (harmony != null) return;
            try
            {
                movement = Getter<Vector3[]>("_graftMovements");
                alternate = Getter<Vector3[]>("_graftMovements2");
                weights = Getter<float[]>("_graftWeights");
                free = Getter<bool[]>("_graftIsFreeVert");
                harmony = new Harmony("Quest3TriggerUI.graft-boundary-tiles");
                harmony.UnpatchAll(harmony.Id);
                harmony.Patch(AccessTools.Method(typeof(DAZMergedMesh), "UpdateVerticesThreadedFast"),
                    transpiler: new HarmonyMethod(typeof(GraftBoundaryTiles), "RouteBoundary"));
                Log("installed; Boundary only; tile=64; stackScratch=1792B; scalar accumulation order unchanged");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e); }
        }

        private static IEnumerable<CodeInstruction> RouteBoundary(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var instructions = new List<CodeInstruction>(input);
            Label? boundary = null;
            int switches = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Switch)
                {
                    var labels = instruction.operand as Label[];
                    if (labels == null || labels.Length != 4) continue;
                    boundary = labels[(int)DAZMergedMesh.GraftMethod.Boundary];
                    switches++;
                }
            }
            if (switches != 1 || !boundary.HasValue) throw new InvalidOperationException("Graft switch count=" + switches);
            // Preserve every original instruction and its original exits.
            Label fallback = generator.DefineLabel();
            MethodInfo helper = AccessTools.Method(typeof(GraftBoundaryTiles), "TryBoundary");
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.labels.Contains(boundary.Value))
                {
                    var owner = new CodeInstruction(OpCodes.Ldarg_0);
                    owner.labels.AddRange(instruction.labels);
                    owner.blocks.AddRange(instruction.blocks);
                    instruction.labels.Clear();
                    instruction.labels.Add(fallback);
                    instruction.blocks.Clear();
                    yield return owner;
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Ldarg, 4);
                    yield return new CodeInstruction(OpCodes.Ldarg, 5);
                    yield return new CodeInstruction(OpCodes.Ldarg, 6);
                    yield return new CodeInstruction(OpCodes.Ldarg, 7);
                    yield return new CodeInstruction(OpCodes.Call, helper);
                    yield return new CodeInstruction(OpCodes.Brfalse, fallback);
                    yield return new CodeInstruction(OpCodes.Ret);
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Boundary entry count=" + replaced);
        }

        private static unsafe bool TryBoundary(DAZMergedMesh owner, Vector3[] source, Vector3[] output,
            int minimum, int maximum, bool useAlternate)
        {
            if (Enabled == null || !Enabled.Value) return false;
            Vector3[] moves = useAlternate ? alternate(owner) : movement(owner);
            float[] w = weights(owner);
            bool[] isFree = free(owner);
            var pairs = owner.graftMesh.meshGraft.vertexPairs;
            int n = owner.numGraftBaseVertices, count = pairs.Length;
            if (minimum < 0 || maximum > n || maximum < minimum || moves == null || w == null || isFree == null
                || moves.Length < n || isFree.Length < n || w.LongLength < (long)n * count
                || source == null || output == null || source.Length < maximum || owner.startGraftVertIndex < 0
                || output.LongLength < (long)owner.startGraftVertIndex + maximum
                || ReferenceEquals(source, output) || ReferenceEquals(moves, output) || ReferenceEquals(moves, source)) return false;
            // Boundary anchors must not be written by the free-vertex loop.
            // Custom/inconsistent topology keeps the original algorithm.
            for (int j = 0; j < count; j++)
            {
                int anchor = pairs[j].vertexNum;
                if (anchor < 0 || anchor >= n || isFree[anchor]) return false;
            }
            // Bounded stack-only scratch. No cached topology, transposed weights,
            // retained mesh/array references or per-frame managed allocations.
            float* sums = stackalloc float[Tile * 3];
            float* factors = stackalloc float[Tile * 3];
            int* indices = stackalloc int[Tile];
            for (int start = minimum; start < maximum; start += Tile)
            {
                int used = 0, end = Math.Min(start + Tile, maximum);
                for (int i = start; i < end; i++)
                {
                    if (!isFree[i]) continue;
                    indices[used] = i;
                    sums[used] = sums[Tile + used] = sums[Tile * 2 + used] = 0f;
                    float fx = owner.graftXFactor, fy = owner.graftYFactor, fz = owner.graftZFactor;
                    if (owner.useGraftSymmetry)
                    {
                        switch (owner.graftSymmetryAxis)
                        {
                            case DAZMergedMesh.GraftSymmetryAxis.X: fx *= Mathf.Clamp01(Mathf.Abs(source[i].x) / owner.graftSymmetryDistance); break;
                            case DAZMergedMesh.GraftSymmetryAxis.Y: fy *= Mathf.Clamp01(Mathf.Abs(source[i].y) / owner.graftSymmetryDistance); break;
                            case DAZMergedMesh.GraftSymmetryAxis.Z: fz *= Mathf.Clamp01(Mathf.Abs(source[i].z) / owner.graftSymmetryDistance); break;
                        }
                    }
                    factors[used] = fx; factors[Tile + used] = fy; factors[Tile * 2 + used] = fz;
                    used++;
                }
                if (used != 0 && reported == 0) ReportFirst(n, count, minimum, maximum, useAlternate);
                // j order and (anchor * weight) * factor grouping are unchanged
                // for every vertex. Weight accesses now traverse nearby indices.
                for (int j = 0; used != 0 && j < count; j++)
                {
                    Vector3 a = moves[pairs[j].vertexNum];
                    int row = j * n;
                    for (int k = 0; k < used; k++)
                    {
                        float weight = w[row + indices[k]];
                        sums[k] += a.x * weight * factors[k];
                        sums[Tile + k] += a.y * weight * factors[Tile + k];
                        sums[Tile * 2 + k] += a.z * weight * factors[Tile * 2 + k];
                    }
                }
                for (int k = 0; k < used; k++)
                {
                    int i = indices[k], to = owner.startGraftVertIndex + i;
                    moves[i].x = sums[k]; moves[i].y = sums[Tile + k]; moves[i].z = sums[Tile * 2 + k];
                    output[to].x = source[i].x + moves[i].x;
                    output[to].y = source[i].y + moves[i].y;
                    output[to].z = source[i].z + moves[i].z;
                }
            }
            return true;
        }

        private static void ReportFirst(int vertices, int pairs, int minimum, int maximum, bool useAlternate)
        {
            if (Interlocked.CompareExchange(ref reported, 1, 0) == 0)
                Log("first-used vertices=" + vertices + " boundaryPairs=" + pairs + " range=" + minimum + ".." + maximum + " alternate=" + useAlternate);
        }

        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null;
            Enabled = null;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[graft-tiles] " + message);
        }
    }
}
