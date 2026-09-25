using System;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Explicit one-shot diagnostics only; no scene writes, texture readback,
    // Renderer.materials instantiation or per-frame scene scans.
    internal static class CharacterMaterialProbe
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Field(object target, string name)
        {
            if (target == null) return null;
            var f = target.GetType().GetField(name, All);
            return f == null ? null : f.GetValue(target);
        }
        private static string TextureInfo(Texture t)
        {
            return ReferenceEquals(t, null) ? "null" : t == null ? "destroyed" :
                t.name + "#" + t.GetInstanceID() + " " + t.width + "x" + t.height;
        }
        private static void Textures(string prefix, Texture[] textures)
        {
            if (textures == null) { Log(prefix + " null-array"); return; }
            for (int i = 0; i < textures.Length; i++) Log(prefix + "[" + i + "]=" + TextureInfo(textures[i]));
        }
        private static void MaterialInfo(string prefix, Material m)
        {
            if (m == null) { Log(prefix + " null-material"); return; }
            Log(prefix + " " + m.name + " shader=" + (m.shader == null ? "null" : m.shader.name));
            foreach (string p in new[] { "_MainTex", "_AlphaTex", "_BumpMap", "_SpecTex", "_GlossTex" })
                if (m.HasProperty(p)) Log(prefix + " " + p + "=" + TextureInfo(m.GetTexture(p)));
        }
        internal static void Dump()
        {
            try
            {
                var sc = SuperController.singleton;
                if (sc == null) return;
                if (sc.isLoading || SceneLoadAccelerator.SceneLoadActive)
                { Log("skipped: scene is loading; request another snapshot after completion"); return; }
                foreach (Atom atom in sc.GetAtoms())
                {
                    if (atom == null || atom.type != "Person") continue;
                    foreach (string id in new[] { "irises", "sclera", "FemaleEyelashes", "MaleEyelashes" })
                    {
                        var st = atom.GetStorableByID(id);
                        if (st == null) continue;
                        string prefix = atom.uid + "/" + id;
                        Log(prefix + " json=" + st.GetJSON(true, true, true).ToString());
                        MaterialInfo(prefix + "/defaults", Field(st, "materialForDefaults") as Material);
                        for (int g = 1; g <= 5; g++)
                        {
                            object group = Field(st, "textureGroup" + g);
                            if (group == null) continue;
                            string selected = Field(st, "currentTextureGroup" + g + "Set") as string;
                            var sets = Field(group, "sets") as Array;
                            Log(prefix + "/group" + g + " selected=" + selected + " sets=" + (sets == null ? 0 : sets.Length));
                            if (sets != null) foreach (object set in sets)
                                if ((Field(set, "name") as string) == selected)
                                    Textures(prefix + "/selected", Field(set, "textures") as Texture[]);
                        }
                        var container = Field(st, "materialContainer") as Transform;
                        var slots = Field(st, "paramMaterialSlots") as int[];
                        object skin = Field(st, "_skin");
                        var gpu = Field(skin, "GPUmaterials") as Material[];
                        Log(prefix + " binding type=" + st.GetType().Name + " skin=" + (skin == null ? "null" : skin.ToString()) + " slots=" + (slots == null ? "null" : string.Join(",", Array.ConvertAll(slots, x => x.ToString()))));
                        if (gpu != null && slots != null)
                            foreach (int slot in slots) if (slot >= 0 && slot < gpu.Length)
                                MaterialInfo(prefix + "/GPU/" + slot, gpu[slot]);
                        if (container == null || slots == null) continue;
                        foreach (Component component in container.GetComponents<Component>())
                        {
                            if (component == null) continue;
                            foreach (string field in new[] { "GPUmaterials", "materials" })
                            {
                                var mats = Field(component, field) as Material[];
                                if (mats == null) continue;
                                foreach (int slot in slots) if (slot >= 0 && slot < mats.Length)
                                    MaterialInfo(prefix + "/" + component.GetType().Name + "/" + slot, mats[slot]);
                            }
                        }
                    }
                }
                Log("snapshot complete (read-only)");
            }
            catch (Exception e) { Log("snapshot failed: " + e); }
        }
        private static void Log(string message) { Quest3TriggerUIPlugin.Log.LogInfo("[eye-material] " + message); }
    }
}
