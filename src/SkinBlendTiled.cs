using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class SkinBlendTiled
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int PixelsPerTile = 131072;
        private static readonly MethodInfo Target = typeof(DAZCharacterTextureControl).GetMethod("BlendGenitalTexture", All);
        private static readonly FieldInfo Lighten = typeof(DAZCharacterTextureControl).GetField("autoBlendGenitalLightenDarkenJSON", All);
        private static readonly FieldInfo Hue = typeof(DAZCharacterTextureControl).GetField("autoBlendGenitalHueOffsetJSON", All);
        private static readonly FieldInfo Saturation = typeof(DAZCharacterTextureControl).GetField("autoBlendGenitalSaturationOffsetJSON", All);
        private static Harmony harmony;
        private static long completed;

        private static bool BeforeBlend(DAZCharacterTextureControl __instance, Texture2D inTorsoTex, Texture2D inGenTex,
            bool linear, bool colorAdjust, bool isNormalMap, ref Texture2D __result)
        {
            // Harmony 2.0 executes every bool prefix even after an earlier false.
            // During overlapping hot generations, only the last installed copy
            // performs the blend. Removing it lets the remaining copy take over.
            if (!IsSelectedImplementation()) return true;
            var mask = __instance.genitalBlendMaskTexture;
            if (inTorsoTex == null || inGenTex == null || mask == null) return true;
            int width = inTorsoTex.width, height = inTorsoTex.height;
            // The original indexes all three flattened arrays by the torso index.
            // Keep its behavior for unequal shapes rather than inventing a resample.
            if (width <= 0 || width > PixelsPerTile || height <= 0 || inGenTex.width != width || inGenTex.height != height
                || mask.width != width || mask.height != height) return true;
            // The original allocates its output before this probe and abandons it
            // on failure. Preserve the null result without creating that orphan.
            try { inTorsoTex.GetPixel(0, 0); }
            catch { __result = null; return false; }

            var output = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
            bool wrote = false, completePixels = false;
            try
            {
                bool torsoPacked = isNormalMap && (inTorsoTex.format == TextureFormat.DXT5 || inTorsoTex.format == TextureFormat.BC7);
                bool genPacked = isNormalMap && (inGenTex.format == TextureFormat.DXT5 || inGenTex.format == TextureFormat.BC7);
                var lighten = colorAdjust ? (JSONStorableFloat)Lighten.GetValue(__instance) : null;
                var hue = colorAdjust ? (JSONStorableFloat)Hue.GetValue(__instance) : null;
                var saturation = colorAdjust ? (JSONStorableFloat)Saturation.GetValue(__instance) : null;
                int rows = Math.Max(1, PixelsPerTile / width);
                var byteInputs = SkinBlendByteInputs.TryRead(inTorsoTex, inGenTex, mask, width, height);
                Color[] outputTile = null;
                for (int y = 0; y < height; y += rows)
                {
                    int take = Math.Min(rows, height - y);
                    bool byteTorso = byteInputs != null && byteInputs.HasTorso;
                    bool byteGen = byteInputs != null && byteInputs.HasGen;
                    bool byteMask = byteInputs != null && byteInputs.HasMask;
                    if (byteTorso && (outputTile == null || outputTile.Length != width * take)) outputTile = new Color[width * take];
                    Color[] torso = byteTorso ? outputTile : inTorsoTex.GetPixels(0, y, width, take, 0);
                    Color[] gen = byteGen ? null : inGenTex.GetPixels(0, y, width, take, 0);
                    Color[] weights = byteMask ? null : mask.GetPixels(0, y, width, take, 0);
                    int offset = y * width;
                    Color color = default(Color);
                    for (int i = 0; i < torso.Length; i++)
                    {
                        Color g = byteGen ? byteInputs.Gen(offset + i) : gen[i];
                        if (colorAdjust && (lighten.val != 0f || hue.val != 0f || saturation.val != 0f))
                        {
                            HSVColor hsv = HSVColorPicker.RGBToHSV(g.r, g.g, g.b);
                            hsv.V = Mathf.Clamp01(hsv.V + lighten.val);
                            hsv.H = Mathf.Clamp01(hsv.H + hue.val);
                            hsv.S = Mathf.Clamp01(hsv.S + saturation.val);
                            g = HSVColorPicker.HSVToRGB(hsv);
                        }
                        float r = byteMask ? byteInputs.Weight(offset + i) : weights[i].r;
                        float inverse = 1f - r;
                        Color t = byteTorso ? byteInputs.Torso(offset + i) : torso[i];
                        if (isNormalMap)
                        {
                            if (torsoPacked) { color.r = t.a * inverse; color.g = t.g * inverse; }
                            else { color.r = t.r * inverse; color.g = t.g * inverse; }
                            if (genPacked) { color.r += g.a * r; color.g += g.g * r; }
                            else { color.r += g.r * r; color.g += g.g * r; }
                            color.b = 1f; color.a = 1f;
                        }
                        else { color = t * inverse + g * r; color.a = 1f; }
                        torso[i] = color;
                    }
                    // Reuse the torso tile for output; no fourth Color[] exists.
                    output.SetPixels(0, y, width, take, torso, 0);
                    wrote = true;
                }
                completePixels = true;
                output.Apply(); // Original mip generation/readability/linear policy.
                completed++;
                if (completed <= 3 || completed % 16 == 0)
                    Log("completed size=" + width + "x" + height + " normal=" + isNormalMap + " colorAdjust=" + colorAdjust
                        + " maxTileColorBytes=" + ((long)Math.Min(rows, height) * width * 16)
                        + " byteSources=" + (byteInputs == null ? 0 : byteInputs.Sources) + " outputArray=reused readable=preserved measuredGameSavings=unknown");
            }
            catch (UnityException error)
            {
                if (wrote && !completePixels)
                {
                    // Whole-array input failure returns the original blank output.
                    // Do not publish a partial CPU texture after a later tile fails.
                    UnityEngine.Object.Destroy(output);
                    output = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
                }
                SuperController.LogError("Exception while blending texture " + error);
            }
            __result = output;
            return false;
        }

        private static bool IsSelectedImplementation()
        {
            var patches = Harmony.GetPatchInfo(Target);
            if (patches == null) return true;
            Type selected = null;
            foreach (var patch in patches.Prefixes)
            {
                var method = patch.PatchMethod;
                var type = method.DeclaringType;
                if (method.Name != "BeforeBlend" || type == null || type.Name != "SkinBlendTiled" || type.Namespace == null
                    || (type.Namespace != "Quest3TriggerUI" && !type.Namespace.StartsWith("Quest3TriggerUI.", StringComparison.Ordinal))) continue;
                // Identical copies use the same default ordering. Prefixes are
                // appended on install; their numeric indices can be reused.
                selected = type;
            }
            return selected == null || selected == typeof(SkinBlendTiled);
        }

        internal static void Install()
        {
            if (harmony != null) return;
            if (Target == null || Target.ReturnType != typeof(Texture2D)) throw new MissingMemberException("skin blend method changed");
            var parameters = Target.GetParameters();
            var types = new[] { typeof(Texture2D), typeof(Texture2D), typeof(bool), typeof(bool), typeof(bool) };
            if (parameters.Length != types.Length) throw new MissingMemberException("skin blend parameters changed");
            for (int i = 0; i < types.Length; i++) if (parameters[i].ParameterType != types[i]) throw new MissingMemberException("skin blend parameter type changed");
            foreach (var field in new[] { Lighten, Hue, Saturation })
                if (field == null || field.FieldType != typeof(JSONStorableFloat)) throw new MissingMemberException("skin blend adjustment fields changed");
            SkinBlendByteInputs.Initialize();
            harmony = new Harmony("quest3triggerui.skinblendtiled." + typeof(SkinBlendTiled).Namespace);
            try
            {
                harmony.Patch(Target, prefix: new HarmonyMethod(typeof(SkinBlendTiled), "BeforeBlend"));
                Log("installed equalShape=boundedFloatTiles pixelsPerTile=" + PixelsPerTile + " rgba32Mips=original hsv=original normals=original readability=preserved unreadableTorso=noOutputAllocation");
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; throw; }
        }

        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null; completed = 0;
            SkinBlendByteInputs.Shutdown();
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[skin-blend-tiled] " + message);
        }
    }
}
