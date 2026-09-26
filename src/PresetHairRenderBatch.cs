using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using GPUTools.Hair.Scripts.Runtime.Commands.Render;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Coalesce only the 15 native render-parameter callback callsites.
    // Physics dispatch, hair construction, styling and interactive edits are
    // untouched. No work is delayed beyond the synchronous RestoreFromJSON.
    internal static class PresetHairRenderBatch
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static readonly Dictionary<BuildParticlesData, Scope> Scopes = new Dictionary<BuildParticlesData, Scope>();
        private static readonly string[] Callbacks = {
            "SyncRootColor", "SyncTipColor", "SyncColorRolloff", "SyncCurlScale", "SyncCurlFrequency",
            "SyncCurlRoot", "SyncCurlMid", "SyncCurlTip", "SyncCurlMidpoint", "SyncCurlCurvePower",
            "SyncSpreadRoot", "SyncSpreadMid", "SyncSpreadTip", "SyncSpreadMidpoint", "SyncSpreadCurvePower"
        };
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class Scope
        {
            internal HairSimControl owner;
            internal BuildParticlesData particles;
            internal Scope previous;
            internal int requests;
        }

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.preset-hair-render-batch");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(JSONStorable).GetMethod("RestoreFromJSON", All),
                    prefix: Hook("Begin"), finalizer: Hook("End"));
                foreach (string name in Callbacks)
                    _harmony.Patch(typeof(HairSimControl).GetMethod(name, All), transpiler: Hook("RouteUpdate"));
                Log("installed; 15 render callbacks batched only within hair parameter restore");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(PresetHairRenderBatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static BuildParticlesData Current(HairSimControl owner)
        {
            if (owner == null || owner.hairSettings == null || owner.hairSettings.HairBuidCommand == null) return null;
            return owner.hairSettings.HairBuidCommand.particlesData;
        }

        private static void Begin(JSONStorable __instance, out Scope __state)
        {
            __state = null;
            if (Enabled != null && !Enabled.Value) return;
            var owner = __instance as HairSimControl;
            var particles = Current(owner);
            if (particles == null) return;
            Scope previous;
            Scopes.TryGetValue(particles, out previous);
            __state = new Scope { owner = owner, particles = particles, previous = previous };
            Scopes[particles] = __state;
        }

        private static Exception End(Scope __state, Exception __exception)
        {
            if (__state == null) return __exception;
            if (__state.previous != null)
            {
                Scopes[__state.particles] = __state.previous;
                __state.previous.requests += __state.requests;
                return __exception;
            }
            Scopes.Remove(__state.particles);
            if (__state.requests == 0 || !ReferenceEquals(Current(__state.owner), __state.particles)) return __exception;
            try
            {
                __state.particles.UpdateSettings();
                if (__state.requests > 1) Log("requests=" + __state.requests + " uploads=1");
            }
            catch (Exception e)
            {
                Log("final render update failed: " + e.Message);
                return __exception ?? e;
            }
            return __exception;
        }

        private static IEnumerable<CodeInstruction> RouteUpdate(IEnumerable<CodeInstruction> instructions)
        {
            var original = typeof(BuildParticlesData).GetMethod("UpdateSettings", All);
            var routed = typeof(PresetHairRenderBatch).GetMethod("Request", BindingFlags.Static | BindingFlags.NonPublic);
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (Equals(instruction.operand, original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = routed;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Hair render callback anchor changed: " + replaced);
        }

        private static void Request(BuildParticlesData particles)
        {
            Scope scope;
            if (Scopes.TryGetValue(particles, out scope)) scope.requests++;
            else particles.UpdateSettings();
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            Scopes.Clear();
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[preset-hair-batch] " + message);
        }
    }
}
