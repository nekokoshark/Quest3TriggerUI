using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // VaM's Finish only excludes DXT1/DXT5 from Texture2D.Compress.
    // Preserve an already compressed BC7 texture rather than recompress it.
    internal static class Bc7CacheLoadCompatibility
    {
        private static Harmony _harmony;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void Install()
        {
            if (_harmony != null) return;
            var harmony = new Harmony("Quest3TriggerUI.bc7-cache-load");
            try
            {
                harmony.UnpatchAll(harmony.Id);
                harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Finish"),
                    transpiler: new HarmonyMethod(typeof(Bc7CacheLoadCompatibility).GetMethod("Rewrite", Static)),
                    postfix: new HarmonyMethod(typeof(Bc7CacheLoadCompatibility).GetMethod("AfterFinish", Static)));
                _harmony = harmony;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo("[bc7-load] installed; three Compress sites preserve BC7; other formats unchanged");
            }
            catch { harmony.UnpatchAll(harmony.Id); throw; }
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }

        private static void CompressUnlessBc7(Texture2D texture, bool highQuality)
        {
            if (texture.format != TextureFormat.BC7) texture.Compress(highQuality);
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var original = typeof(Texture2D).GetMethod("Compress", new[] { typeof(bool) });
            var replacement = typeof(Bc7CacheLoadCompatibility).GetMethod("CompressUnlessBc7", Static);
            int count = 0;
            foreach (var instruction in code)
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) &&
                    Equals(instruction.operand, original))
                {
                    // Same consumed stack (Texture2D, bool), same void result;
                    // keep the instruction's labels and exception boundaries.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    count++;
                }
            if (count != 3) throw new InvalidOperationException("QueuedImage.Finish Compress anchors changed: " + count);
            return code;
        }

        private static void AfterFinish(ImageLoaderThreaded.QueuedImage __instance)
        {
            var q = __instance;
            if (q == null || !q.preprocessed || q.textureFormat != TextureFormat.BC7 || q.tex == null || !q.finished) return;
            int expected = 1, w = q.width, h = q.height;
            while (q.createMipMaps && (w > 1 || h > 1))
            {
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); expected++;
            }
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[bc7-load] format=" + q.tex.format + " size=" +
                    q.tex.width + "x" + q.tex.height + " mips=" + q.tex.mipmapCount + "/" + expected +
                    " linear=" + q.linear + " createMipMaps=" + q.createMipMaps +
                    " recompress=skipped; GPU appearance not verified");
        }
    }
}
