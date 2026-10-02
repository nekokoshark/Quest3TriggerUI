using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace Quest3TriggerUI
{
    // One explicit existing-scene snapshot. No scene/material mutation or frame scanner.
    internal static class Bc7MaterialProbe
    {
        internal static void Dump()
        {
            try
            {
                var sc = SuperController.singleton;
                if (sc == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive)
                { Log("SKIP scene loading or missing"); return; }
                string dir = Path.Combine(@"F:\vam1.22.0.12\工作区\bc7_gpu_probe_20261002", "capture_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(dir);
                var lines = new List<string>(); var seen = new HashSet<int>(); int queued = 0;
                foreach (Atom atom in sc.GetAtoms())
                {
                    if (atom == null || atom.type != "Person") continue;
                    var selector = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                    if (selector == null || selector.clothingItems == null) continue;
                    foreach (var item in selector.clothingItems)
                    {
                        if (item == null || !item.active) continue;
                        foreach (var wrap in item.GetComponentsInChildren<DAZSkinWrap>(true))
                        {
                            if (wrap.GPUmaterials == null) continue;
                            for (int slot = 0; slot < wrap.GPUmaterials.Length; slot++)
                            {
                                var material = wrap.GPUmaterials[slot]; if (material == null) continue;
                                string prefix = atom.uid + "/" + item.name + "/" + slot;
                                lines.Add(prefix + " material=" + material.name + " shader=" + (material.shader == null ? "null" : material.shader.name) + " queue=" + material.renderQueue + " keywords=" + string.Join(",", material.shaderKeywords));
                                foreach (string property in new[] { "_Cutoff", "_AlphaAdjust", "_AlphaOffset", "_Alpha", "_Glossiness", "_SpecularIntensity" })
                                    if (material.HasProperty(property)) lines.Add(prefix + " " + property + "=" + material.GetFloat(property));
                                foreach (string property in new[] { "_MainTex", "_AlphaTex", "_BumpMap", "_SpecTex", "_GlossTex" })
                                {
                                    if (!material.HasProperty(property)) continue;
                                    var texture = material.GetTexture(property) as Texture2D;
                                    if (texture == null) { lines.Add(prefix + " " + property + "=null/non-Texture2D"); continue; }
                                    lines.Add(prefix + " " + property + "=" + texture.name + " id=" + texture.GetInstanceID() + " format=" + texture.format + " size=" + texture.width + "x" + texture.height + " mips=" + texture.mipmapCount + " filter=" + texture.filterMode + " wrap=" + texture.wrapMode + " aniso=" + texture.anisoLevel + " bias=" + texture.mipMapBias);
                                    if (texture.format != TextureFormat.BC7 || seen.Contains(texture.GetInstanceID())) continue;
                                    if (seen.Count >= 4) { lines.Add(prefix + " GPU_READBACK_LIMIT first four BC7 only"); continue; }
                                    seen.Add(texture.GetInstanceID());
                                    foreach (int level in Bc7MipReadbackPlan.Levels(texture.width, texture.height, texture.mipmapCount))
                                    {
                                        int mip = level, id = texture.GetInstanceID();
                                        int width = Math.Max(1, texture.width >> mip), height = Math.Max(1, texture.height >> mip);
                                        string name = texture.name;
                                        Capture(texture, Path.Combine(dir, "tex_" + id + "_mip_" + mip), mip, width, height);
                                        queued++;
                                    }
                                }
                            }
                        }
                    }
                }
                Control(dir, "BC7_POT_CONTROL", "POT_CONTROL.bc7", 4096, TextureFormat.BC7);
                Control(dir, "RGB_NPOT_CONTROL", "NATIVE_CONTROL.rgb", 4000, TextureFormat.RGB24);
                File.WriteAllLines(Path.Combine(dir, "MATERIALS.txt"), lines.ToArray());
                Log("SNAPSHOT bindings=" + lines.Count + " bc7Textures=" + seen.Count + " readbacksQueued=" + queued + " directory=" + dir + "; current scene only; no visual acceptance");
            }
            catch (Exception e) { Log("SNAPSHOT_EXCEPTION " + e); }
        }
        private static void Control(string directory, string name, string filename, int size, TextureFormat format)
        {
            string path = Path.Combine(@"F:\vam1.22.0.12\工作区\bc7_gpu_probe_20261002", filename);
            if (!File.Exists(path)) return;
            Texture2D control = null;
            try
            {
                control = new Texture2D(size, size, format, true, false);
                control.name = name; control.filterMode = FilterMode.Bilinear;
                control.LoadRawTextureData(File.ReadAllBytes(path)); control.Apply(false, false);
                foreach (int mip in Bc7MipReadbackPlan.Levels(size, size, control.mipmapCount))
                    Capture(control, Path.Combine(directory, name + "_mip_" + mip), mip, Math.Max(1, size >> mip), Math.Max(1, size >> mip));
            }
            catch (Exception e) { Log("CONTROL_EXCEPTION " + e.Message); }
            finally { if (control != null) UnityEngine.Object.DestroyImmediate(control); }
        }
        private static void Capture(Texture2D source, string file, int mip, int width, int height)
        {
            RenderTexture previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            RenderTexture target = null;
            Texture2D pixels = null;
            try
            {
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                GL.sRGBWrite = false;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                byte[] bytes = pixels.GetRawTextureData();
                if (bytes.Length != width * height * 4) throw new InvalidDataException("RGBA readback length mismatch");
                File.WriteAllBytes(file + ".rgba", bytes);
                File.WriteAllText(file + ".txt", "name=" + source.name + "\nwidth=" + width + "\nheight=" + height + "\nmip=" + mip + "\nbytes=" + bytes.Length + "\nmethod=Graphics.Blit implicit LOD from output size; linear render target; no sRGB write\ncolourSpace=" + QualitySettings.activeColorSpace + "\n");
                Log("READBACK id=" + source.GetInstanceID() + " mip=" + mip + " bytes=" + bytes.Length + " file=" + file + ".rgba");
            }
            catch (Exception error) { Log("GPU_READBACK_EXCEPTION " + error.Message); }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgb;
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                if (target != null) RenderTexture.ReleaseTemporary(target);
            }
        }
        private static void Log(string value) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[bc7-gpu] " + value); }
    }
}
