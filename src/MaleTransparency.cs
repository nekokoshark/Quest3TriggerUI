using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Owns a single reversible visibility session, not a global material override.
    internal sealed class MaleTransparency : IDisposable
    {
        private const string Url = "HAL9001.GoingGoingGone.15:/Custom/Scripts/HAL9001/GoingGoingGone/GoingGoingGone.cslist";
        private readonly MonoBehaviour host;
        private Coroutine pending;
        private JSONStorable plugin;
        private Atom target;
        private bool busy, active, wasEnabled;
        private readonly Dictionary<string, string> saved = new Dictionary<string, string>();
        private readonly Dictionary<string, float> floats = new Dictionary<string, float>();
        internal static readonly string[] Parts = {
            "Back of Head", "Face", "Ears", "Eyelashes", "Irises", "Cornea", "Pupils", "Sclera",
            "Eye Reflection", "Lacrimals", "Tear", "Nostrils", "Lips", "Gums", "Teeth", "Tongue",
            "Inner Mouth", "Neck", "Shoulders", "Torso", "Nipples", "Forearms", "Hands", "Fingernails",
            "Hips", "Genitals Female", "Genitals Male", "Genitals", "Anus Male", "Legs", "Feet", "Toenails"
        };
        internal static bool Keep(string part) { return part == "Genitals Male" || part == "Genitals"; }
        internal MaleTransparency(MonoBehaviour owner) { host = owner; }
        internal bool Active { get { return active && target != null && plugin != null; } }
        internal void Toggle()
        {
            if (busy) return;
            if (active) { Restore(); return; }
            Atom chosen = SceneQuickActions.FindClosestPerson(true);
            if (chosen == null) { Log("男人透明：视线前方没有男性角色。"); return; }
            busy = true;
            pending = host.StartCoroutine(GuardedLoad(chosen));
        }
        private IEnumerator GuardedLoad(Atom chosen)
        {
            IEnumerator work = Load(chosen);
            try
            {
                while (true)
                {
                    bool more;
                    try { more = work.MoveNext(); }
                    catch (Exception e)
                    {
                        if (active) Restore();
                        Log("男人透明：" + e.Message);
                        yield break;
                    }
                    if (!more) yield break;
                    yield return work.Current;
                }
            }
            finally
            {
                IDisposable disposable = work as IDisposable;
                if (disposable != null) disposable.Dispose();
                busy = false; pending = null;
            }
        }
        private static JSONStorable Find(Atom atom)
        {
            foreach (string id in atom.GetStorableIDs())
            {
                JSONStorable st = atom.GetStorableByID(id);
                if (st != null && st.GetType().FullName == "HAL9001.GoingGoingGone") return st;
            }
            return null;
        }
        private IEnumerator Load(Atom chosen)
        {
            try
            {
                JSONStorable found = Find(chosen);
                if (found == null)
                {
                    string path = FileManager.NormalizeLoadPath(Url);
                    if (!FileManager.FileExists(path, false, false)) throw new InvalidOperationException("GoingGoingGone.15 尚未注册");
                    MVRPluginManager manager = chosen.GetStorableByID("PluginManager") as MVRPluginManager;
                    if (manager == null) throw new InvalidOperationException("角色缺少 PluginManager");
                    // Add one slot only; never restore or replace the full plugin list.
                    MVRPlugin slot = manager.CreatePlugin();
                    slot.pluginURLJSON.val = path;
                }
                float deadline = Time.unscaledTime + 12f;
                while (chosen != null && Time.unscaledTime < deadline)
                {
                    found = Find(chosen);
                    if (Ready(found)) break;
                    yield return null;
                }
                if (chosen == null || !Ready(found)) { Log("男人透明：插件初始化未完成，请稍后重试。"); yield break; }
                target = chosen; plugin = found;
                // Snapshot before changing any control; restoring a disabled authored
                // instance must leave it disabled, not turn all parts on globally.
                saved.Clear(); floats.Clear();
                foreach (string part in Parts)
                {
                    string name = part + " Visibility";
                    saved.Add(name, plugin.GetStringChooserJSONParam(name).val);
                    // The invisible replacement shaders can still participate in
                    // depth passes. Draw hidden skin after transparent clothing,
                    // as Westo #10 does, rather than before it at GGG's 2450 default.
                    if (!Keep(part))
                    {
                        string queue = part + " Render Queue";
                        var queueValue = plugin.GetFloatJSONParam(queue);
                        if (queueValue != null) floats.Add(queue, queueValue.val);
                    }
                    if (Keep(part))
                    {
                        string key = part + " Transparency";
                        var value = plugin.GetFloatJSONParam(key);
                        if (value != null) floats.Add(key, value.val);
                    }
                }
                var enabled = plugin.GetBoolJSONParam("enabled");
                wasEnabled = enabled == null || enabled.val;
                active = true; // partial application is still reversible in Dispose.
                foreach (string part in Parts)
                    plugin.SetStringChooserParamValue(part + " Visibility", Keep(part) ? "On" : "Off");
                foreach (string key in floats.Keys)
                    plugin.SetFloatParamValue(key, key.EndsWith(" Render Queue", StringComparison.Ordinal) ? 5000f : 0f);
                if (enabled != null) enabled.val = true;
                Log("男人透明：已启用 " + chosen.uid + "；再次点击恢复该角色。");
            }
            finally { busy = false; pending = null; }
        }
        private static bool Ready(JSONStorable st)
        {
            if (st == null) return false;
            foreach (string part in Parts)
            {
                var c = st.GetStringChooserJSONParam(part + " Visibility");
                if (c == null || c.choices == null || !c.choices.Contains("On") || !c.choices.Contains("Off")) return false;
            }
            return true;
        }
        private void Restore()
        {
            if (plugin != null)
            {
                foreach (var pair in saved) plugin.SetStringChooserParamValue(pair.Key, pair.Value);
                foreach (var pair in floats) plugin.SetFloatParamValue(pair.Key, pair.Value);
                var enabled = plugin.GetBoolJSONParam("enabled");
                if (enabled != null) enabled.val = wasEnabled;
            }
            saved.Clear(); floats.Clear(); plugin = null; target = null; active = false;
            Log("男人透明：已恢复开启前状态。");
        }
        public void Dispose()
        {
            if (pending != null) host.StopCoroutine(pending);
            pending = null; busy = false;
            if (active) Restore();
        }
        private static void Log(string text) { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(text); }
    }
}
