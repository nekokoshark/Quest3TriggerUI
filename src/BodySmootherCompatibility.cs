using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // ABS Redux forces chooser.defaultVal="" to force serialization. Missing
    // scene plugin JSON then resets Skip to "", which ApplyMaterial tessellates.
    internal static class BodySmootherCompatibility
    {
        private const string TypeName = "HuntingSuccubus.AutomaticBodySmoother";
        private const string TessShader = "Custom/Subsurface/GlossNMTessMappedFixedComputeBuff";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static bool _installed;
        private static readonly HashSet<Type> KnownTypes = new HashSet<Type>();
        private static volatile bool _scan;
        private static float _nextCheck;

        internal static bool ValidPolicy(string value)
        {
            return value == "Skip" || value == "Tessellated" ||
                value == "Tessellated (Masked)" || value == "Tessellated (Not Masked)";
        }
        internal static bool EyeMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string stem in new[] { "EyeReflection", "Pupils", "Tear", "Irises", "Cornea", "Sclera", "Eyelashes" })
            {
                if (name == stem || name.StartsWith(stem + "-", StringComparison.Ordinal) ||
                    name.StartsWith(stem + " (", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        internal static bool ShouldRestore(string name, string policy, string shader, bool hasOriginal)
        {
            return EyeMaterial(name) && !ValidPolicy(policy) && shader == TessShader && hasOriginal;
        }
        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
            _scan = true;
            Log("parameter compatibility enabled; no dynamic-script method hooks");
        }
        private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args) { _scan = true; }
        internal static void Tick()
        {
            if (!_installed || (!_scan && Time.unscaledTime < _nextCheck) || SuperController.singleton == null ||
                SuperController.singleton.isLoading || SceneLoadAccelerator.SceneLoadActive) return;
            _nextCheck = Time.unscaledTime + 2f;
            if (_scan)
            {
                _scan = false;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = asm.GetType(TypeName, false);
                    if (type != null && KnownTypes.Add(type)) Log("located " + asm.GetName().Name);
                }
            }
            // Only the cached optional plugin's small chooser list is checked.
            // No repeated scene/material scans; snapshots are visited only when
            // an invalid eye policy has actually been found.
            foreach (Type type in KnownTypes)
            {
                try
                {
                    RepairExisting(type);
                }
                catch (Exception e) { Log("parameter repair failed: " + e.Message); }
            }
        }
        private static object Field(object target, string name)
        {
            var f = target.GetType().GetField(name, All);
            return f == null ? null : f.GetValue(target);
        }
        private static void RepairExisting(Type type)
        {
            var instanceField = type.GetField("instance", All);
            object instance = instanceField == null ? null : instanceField.GetValue(null);
            if (instance == null || instance as UnityEngine.Object == null) return;
            var property = type.GetProperty("materialPolicies", All);
            var policies = property == null ? null : property.GetValue(instance, null) as IEnumerable;
            if (policies == null) return;
            var invalidEyes = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in policies)
            {
                var chooser = value as JSONStorableStringChooser;
                if (chooser == null || !EyeMaterial(chooser.name)) continue;
                // All seven eye entries in Redux initialize to Skip. Keep a
                // valid user selection (including explicit Sclera tessellation).
                if (!ValidPolicy(chooser.defaultVal)) chooser.defaultVal = "Skip";
                if (ValidPolicy(chooser.val)) continue;
                Log("invalid eye policy " + chooser.name + " value='" + chooser.val + "' default='" + chooser.defaultVal + "'");
                invalidEyes.Add(chooser.name);
                chooser.valNoCallback = "Skip";
                chooser.defaultVal = "Skip";
            }
            if (invalidEyes.Count == 0) return;
            var watchers = Field(instance, "_watchers") as IEnumerable;
            if (watchers == null) return;
            int restored = 0;
            foreach (object watcher in watchers)
            {
                if (watcher == null) continue;
                var snapshots = Field(watcher, "_materialSnapshots") as IList;
                if (snapshots == null) continue;
                int beforeWatcher = restored;
                for (int i = snapshots.Count - 1; i >= 0; i--)
                {
                    object snapshot = snapshots[i];
                    var material = Field(snapshot, "material") as Material;
                    var original = Field(snapshot, "shader") as Shader;
                    if (material == null) continue;
                    bool invalid = false;
                    foreach (string name in invalidEyes)
                        if (material.name == name || material.name.StartsWith(name + "-", StringComparison.Ordinal) ||
                            material.name.StartsWith(name + " (", StringComparison.Ordinal)) { invalid = true; break; }
                    if (!invalid || !ShouldRestore(material.name, "", material.shader == null ? null : material.shader.name, original != null)) continue;
                    string before = material.shader.name;
                    material.shader = original;
                    if (material.HasProperty("_TessTex")) material.SetTexture("_TessTex", Field(snapshot, "tessTex") as Texture);
                    if (material.HasProperty("_Tess")) material.SetFloat("_Tess", (float)Field(snapshot, "tess"));
                    if (material.HasProperty("_TessPhong")) material.SetFloat("_TessPhong", (float)Field(snapshot, "tessPhong"));
                    if ((bool)Field(snapshot, "colorized") && material.HasProperty("_Color"))
                        material.SetColor("_Color", (Color)Field(snapshot, "color"));
                    // This exact snapshot was restored, so the plugin must not
                    // restore it again over subsequent user material changes.
                    snapshots.RemoveAt(i);
                    restored++;
                    Log("restored " + material.name + " " + before + " -> " + original.name);
                }
                if (restored != beforeWatcher)
                {
                    // Native PersonWatcher.Restore broadcasts OnApplicationFocus;
                    // DAZSkinV2 implements that as FlushBuffers. Call that exact
                    // native operation directly, without invoking dynamic script.
                    var skin = Field(watcher, "_currentSkin") as DAZSkinV2;
                    if (skin != null) skin.FlushBuffers();
                }
            }
            Log("existing eye materials restored=" + restored + "; body and valid explicit policies retained");
        }
        internal static void Shutdown()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
            _installed = false; KnownTypes.Clear(); _scan = false; _nextCheck = 0f;
        }
        private static void Log(string message) { Quest3TriggerUIPlugin.Log.LogInfo("[abs-compat] " + message); }
    }
}
