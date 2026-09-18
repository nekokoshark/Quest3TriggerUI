using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Portable copy of AWAKENING MiniSkirt's user-visible physics settings.
    // The action intentionally contains only BetterBends and OrificeDynamics.
    internal sealed class PhysicsPluginApplicator
    {
        private sealed class ComponentTemplate
        {
            internal readonly string Suffix;
            internal readonly string ReadyParameter;
            internal readonly string Json;
            internal ComponentTemplate(string suffix, string readyParameter, string json)
            { Suffix = suffix; ReadyParameter = readyParameter; Json = json; }
        }

        private sealed class PluginTemplate
        {
            internal readonly string Label;
            internal readonly string Url;
            internal readonly ComponentTemplate[] Components;
            internal PluginTemplate(string label, string url, ComponentTemplate[] components)
            { Label = label; Url = url; Components = components; }
        }

        private static readonly PluginTemplate[] Templates = {
            new PluginTemplate(
                "BetterBends", "Skynet.BetterBends.15:/Custom/Scripts/Skynet/BetterBends/BetterBends.cs",
                new ComponentTemplate[] {
                    new ComponentTemplate("_Skynet.BetterBends", "Thigh Corrective Multiplier", @"{""id"":""plugin#3_Skynet.BetterBends"",""Hip Bend Multiplier"":""0"",""Thigh Corrective Multiplier"":""0.5"",""pluginLabel"":""BetterBends""}")
                }),
            new PluginTemplate(
                "OrificeDynamics", "Skynet.OrificeDynamics.30:/Custom/Scripts/Skynet/Orifice Dynamics/OrificeDynamics.cslist",
                new ComponentTemplate[] {
                    new ComponentTemplate("_Skynet.OrificeDynamics", "Enable Optimized Physics", @"{""id"":""plugin#4_Skynet.OrificeDynamics"",""Enable Anal Gape"":""False"",""Enable Vaginal Gape"":""False"",""Enable Mouth Gape"":""False"",""Section_Pre-Open_Morphs_Expanded"":""true"",""Section_Gape_Strength_Expanded"":""true"",""Section_Bulge_Dynamics_Expanded"":""true"",""Enable Throat Bulge"":""False"",""Enable Belly Bulge"":""False"",""Enable Lips Motion"":""False"",""Enable Vagina Motion"":""False"",""Enable Anal Motion"":""False"",""Enable Deep Penetration"":""False"",""Enable Anus Capsule"":""False"",""Enable Vagina Capsule"":""False"",""Enable Mouth Capsule"":""False"",""Section_In/Out_Motion_Expanded"":""true"",""Pre-Open Anal"":""0"",""Pre-Open Vaginal"":""0"",""Pre-Open Mouth"":""0"",""Mouth Gape Strength"":""0"",""Anal Gape Strength"":""0"",""Vaginal Gape Strength"":""0"",""pluginLabel"":""Orifice Dynamics"",""Enable Gape Module"":""True"",""Mouth Align Proximity"":""0.12"",""Anal Align Proximity"":""0.12"",""Vaginal Align Proximity"":""0.12"",""Anal Main Proximity"":""0.01"",""Vaginal Main Proximity"":""0.01"",""Mouth Main Proximity"":""0.02"",""Enable Optimized Physics"":""True"",""Enable Optimized Mass"":""False"",""Optimized Mass Lerp Speed"":""0.005"",""Enable Optimized Friction"":""False"",""Ignore Tongue Collision"":""True"",""Disable Base Auto Genitals"":""True"",""Anal Gape Smooth Factor"":""0.95"",""Vaginal Gape Smooth Factor"":""0.95"",""Mouth Gape Smooth Factor"":""0.95"",""Mouth Bulge Smoothing"":""0.6"",""Belly Bulge Smoothing"":""0.6"",""Anal Morph Multiplier (Dist)"":""10"",""Anal Morph Offset (Dist)"":""-0.24"",""Anal Gape Time"":""10"",""Enable Anal Twink Effect"":""True"",""Twink Time"":""5"",""Twink Time Variation"":""2"",""Twink Start Delay"":""1"",""Vaginal Morph Multiplier (Dist)"":""10"",""Vaginal Morph Offset (Dist)"":""-0.22"",""Vaginal Gape Time"":""0.5"",""Mouth Gape Time"":""0.6"",""Enable Bulge Module"":""True"",""Throat Bulge Multiplier"":""1"",""Abdomen Bulge Multiplier"":""1"",""Mouth Bulge Morph 1 Mult"":""1"",""Mouth Bulge Morph 2 Mult"":""1"",""Mouth Bulge Morph 3 Mult"":""1"",""Belly Bulge Morph 1 Mult"":""0.8"",""Belly Bulge Morph 2 Mult"":""1"",""Belly Bulge Morph 3 Mult"":""1"",""Belly Bulge Morph 4 Mult"":""1"",""Belly Bulge Morph 5 Mult"":""1"",""Bulge x Gape Effector"":""1"",""Belly Bulge Corrective 1 Mult"":""1.8"",""Lips In Strength"":""0.7"",""Lips Out Strength"":""1.2"",""Vagina Motion Strength"":""1.3"",""Anal Motion Strength"":""0.8"",""Use Alt Vagina Morphs"":""False"",""Adaptive Smooth Zero Velocity"":""0.98"",""Adaptive Smooth Max Velocity"":""0.25"",""Adaptive Smooth Velocity Reference"":""1"",""Motion Morph Idle Decay Smooth"":""0.95""}")
                })
        };

        private readonly MonoBehaviour _host;
        private bool _busy;

        internal PhysicsPluginApplicator(MonoBehaviour host) { _host = host; }
        internal bool Busy { get { return _busy; } }

        internal void Apply(Atom target, Action<string> completed)
        {
            if (_busy) { if (completed != null) completed("物理插件正在加载。"); return; }
            _busy = true;
            _host.StartCoroutine(ApplyRoutine(target, completed));
        }

        private IEnumerator ApplyRoutine(Atom target, Action<string> completed)
        {
            int added = 0;
            int skipped = 0;
            MVRPluginManager manager = null;
            try
            {
                if (target == null) throw new InvalidOperationException("目标角色不存在");
                manager = target.GetStorableByID("PluginManager") as MVRPluginManager;
                if (manager == null) throw new InvalidOperationException("目标角色没有 PluginManager");
            }
            catch (Exception exception)
            {
                CompleteFailure(completed, exception);
                yield break;
            }

            for (int i = 0; i < Templates.Length; i++)
            {
                PluginTemplate template = Templates[i];
                string slot = null;
                try
                {
                    if (FindExistingSlot(target, template) != null)
                    {
                        skipped++;
                        continue;
                    }

                    string path = FileManager.NormalizeLoadPath(template.Url);
                    if (string.IsNullOrEmpty(path) || !FileManager.FileExists(path, false, false))
                        throw new FileNotFoundException(template.Label + " 未注册：" + path);

                    MVRPlugin plugin = manager.CreatePlugin();
                    if (plugin == null) throw new InvalidOperationException(template.Label + " 无法创建插件槽");
                    slot = plugin.uid;
                    plugin.pluginURLJSON.val = path;
                }
                catch (Exception exception)
                {
                    CompleteFailure(completed, exception);
                    yield break;
                }

                float deadline = Time.unscaledTime + 18f;
                while (target != null && Time.unscaledTime < deadline &&
                       !AllComponentsReady(target, slot, template))
                    yield return null;
                if (target == null || !AllComponentsReady(target, slot, template))
                {
                    CompleteFailure(completed,
                        new TimeoutException(template.Label + " 初始化超时"));
                    yield break;
                }

                try
                {
                    for (int c = 0; c < template.Components.Length; c++)
                    {
                        ComponentTemplate component = template.Components[c];
                        JSONStorable storable = target.GetStorableByID(slot + component.Suffix);
                        JSONClass state = JSON.Parse(component.Json).AsObject;
                        state["id"] = storable.storeId;
                        // setMissingToDefault=false keeps plugin-owned calibration/default fields.
                        storable.RestoreFromJSON(state, true, true, null, false);
                    }
                    added++;
                }
                catch (Exception exception)
                {
                    CompleteFailure(completed, exception);
                    yield break;
                }
                yield return null;
            }

            _busy = false;
            if (completed != null)
                completed("物理：已为 " + target.uid + " 新增 " + added +
                    " 个插件槽，跳过已有 " + skipped + " 个；原插件和动画未改动。");
        }

        private void CompleteFailure(Action<string> completed, Exception exception)
        {
            _busy = false;
            if (completed != null) completed("物理应用失败：" + exception.Message);
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError("Physics package apply failed: " + exception);
        }

        private static string FindExistingSlot(Atom target, PluginTemplate template)
        {
            List<string> ids = target.GetStorableIDs();
            for (int i = 0; i < ids.Count; i++)
                for (int c = 0; c < template.Components.Length; c++)
                    if (ids[i].EndsWith(template.Components[c].Suffix,
                            StringComparison.OrdinalIgnoreCase))
                        return ids[i].Substring(0, ids[i].Length - template.Components[c].Suffix.Length);
            return null;
        }

        private static bool AllComponentsReady(Atom target, string slot, PluginTemplate template)
        {
            for (int i = 0; i < template.Components.Length; i++)
                {
                JSONStorable storable = target.GetStorableByID(slot + template.Components[i].Suffix);
                if (storable == null || (storable.GetBoolJSONParam(template.Components[i].ReadyParameter) == null &&
                    storable.GetFloatJSONParam(template.Components[i].ReadyParameter) == null &&
                    storable.GetStringJSONParam(template.Components[i].ReadyParameter) == null))
                    return false;
            }
            return true;
        }
    }
}
