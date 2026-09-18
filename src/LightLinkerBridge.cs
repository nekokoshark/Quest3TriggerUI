using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SimpleJSON;
using MVR.FileManagement;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class LightLinkerBridge
    {
        private MVRScript _script;
        private UIDynamicButton _restore;
        private bool _busy;
        private bool _disposed;

        internal void Open(MonoBehaviour host)
        {
            if (_busy) return;
            MVRScript script = FindLoadedScript();
            if (script != null)
            {
                if (_script != script) DisposeButton();
                _script = script;
                _disposed = false;
                host.StartCoroutine(ShowPanel());
                return;
            }
            host.StartCoroutine(LazyLoadAndShow(host));
        }

        private static MVRScript FindLoadedScript()
        {
            foreach (MVRScript script in Resources.FindObjectsOfTypeAll<MVRScript>())
            {
                if (script != null && script.GetType().FullName == "LzswwxPlugins.LightLinker" &&
                    script.containingAtom != null &&
                    script.containingAtom.type == "SessionPluginManager")
                    return script;
            }
            return null;
        }

        // The button finds nothing on machines where LightLinker was never
        // added to Session Plugins.  Load it on demand via the same
        // CreatePlugin + pluginURLJSON path the expression Timelines use;
        // the script appears once the cslist compiles.
        private IEnumerator LazyLoadAndShow(MonoBehaviour host)
        {
            string url = PluginPaths.LightLinkerUrl;
            if (!FileManager.FileExists(url, false, false))
            {
                Report("Light Linker: " + url + " not found — install the plugin " +
                    "or set [Paths] LightLinkerUrl to its actual location.");
                yield break;
            }
            MVRPluginManager manager = null;
            SuperController sc = SuperController.singleton;
            if (sc != null)
                foreach (Atom atom in sc.GetAtoms())
                    if (atom != null && atom.type == "SessionPluginManager")
                    {
                        manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;
                        break;
                    }
            if (manager == null)
            {
                Report("Light Linker: session plugin manager unavailable.");
                yield break;
            }
            MVRPlugin plugin = manager.CreatePlugin();
            if (plugin == null)
            {
                Report("Light Linker: could not create a session plugin slot.");
                yield break;
            }
            plugin.pluginURLJSON.val = url;
            Report("Light Linker: loading " + url + " on demand…");
            float deadline = Time.unscaledTime + 10f;
            while (Time.unscaledTime < deadline)
            {
                yield return null;
                MVRScript script = FindLoadedScript();
                if (script != null)
                {
                    _script = script;
                    _disposed = false;
                    host.StartCoroutine(ShowPanel());
                    yield break;
                }
            }
            Report("Light Linker: plugin slot added but the script never appeared.");
        }

        private IEnumerator ShowPanel()
        {
            _busy = true;
            try
            {
                SuperController sc = SuperController.singleton;
                UiAssistHudLink.CancelPending();
                sc.gameMode = SuperController.GameMode.Edit;
                sc.SelectModeOff();
                sc.ShowMainHUD(true, false);
                sc.activeUI = SuperController.ActiveUI.MainMenu;
                yield return null;
                if (_disposed || _script == null) yield break;
                if (sc.mainMenuTabSelector != null)
                    sc.mainMenuTabSelector.SetActiveTab("TabSessionPlugins");
                // Session scripts are not reliably enumerated by Atom.GetStorableIDs.
                // Resolve the actual controller by script reference, never label/slot text.
                object plugins = typeof(MVRPluginManager).GetField("plugins",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(_script.manager);
                foreach (MVRPlugin plugin in (IEnumerable)plugins)
                    foreach (MVRScriptController control in plugin.scriptControllers)
                        if (control.script == _script)
                        {
                            foreach (MVRPlugin otherPlugin in (IEnumerable)plugins)
                                foreach (MVRScriptController other in otherPlugin.scriptControllers)
                                    if (other != control) other.CloseUI();
                            control.OpenUI();
                            // OpenUI only enables customUI itself; a hidden parent
                            // still hides it, and later sibling panels can cover it.
                            for (Transform t = control.customUI; t != null; t = t.parent)
                                t.gameObject.SetActive(true);
                            if (control.customUI != null) control.customUI.SetAsLastSibling();
                            if (_restore == null)
                            {
                                _restore = _script.CreateButton("恢复场景原始灯光", false);
                                _restore.button.onClick.AddListener(RestoreOriginal);
                                _restore.transform.SetAsFirstSibling();
                            }
                            Report("Light Linker: native controller.OpenUI invoked; restore button attached.");
                            yield break;
                        }
                Report("Light Linker: no controller matched the loaded script.");
            }
            finally { _busy = false; }
        }

        private void RestoreOriginal()
        {
            if (_busy || _script == null) return;
            SuperController sc = SuperController.singleton;
            if (sc.isLoading) { Report("Light restore: wait until scene loading finishes."); return; }
            string path = sc.LoadedSceneName;
            JSONClass scene;
            try
            {
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Save/load a scene before restoring its original lights.");
                scene = sc.LoadJSON(path).AsObject;
                if (scene == null || !scene.HasKey("atoms")) throw new InvalidOperationException("Scene has no atoms array.");
            }
            catch (Exception e) { Report("Light restore: " + e.Message); return; }
            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestoreLights(scene, path));
        }

        private IEnumerator RestoreLights(JSONClass scene, string path)
        {
            _busy = true;
            try
            {
                SuperController sc = SuperController.singleton;
                JSONArray atoms = scene["atoms"].AsArray;
                Dictionary<string, JSONClass> originals = new Dictionary<string, JSONClass>();
                foreach (JSONNode node in atoms)
                {
                    JSONClass item = node.AsObject;
                    if (item != null && item["type"].Value == "InvisibleLight")
                        originals.Add(item["id"].Value, item);
                }
                // Validate UID collisions before changing any existing lights.
                foreach (string id in originals.Keys)
                {
                    Atom existing = sc.GetAtomByUid(id);
                    if (existing != null && existing.type != "InvisibleLight")
                    { Report("Light restore: UID now belongs to a non-light atom: " + id); yield break; }
                }
                _script.StopAllCoroutines(); // Cancel pending Light Linker config/link operations.
                foreach (string id in originals.Keys)
                {
                    if (sc.GetAtomByUid(id) == null) yield return sc.AddAtomByType("InvisibleLight", id);
                    if (_disposed || sc.LoadedSceneName != path || sc.isLoading) yield break;
                    if (sc.GetAtomByUid(id) == null) { Report("Light restore: light creation failed: " + id); yield break; }
                }
                foreach (Atom atom in new List<Atom>(sc.GetAtoms()))
                    if (atom.type == "InvisibleLight" && !originals.ContainsKey(atom.uid)) sc.RemoveAtom(atom);
                foreach (KeyValuePair<string, JSONClass> entry in originals)
                {
                    Atom atom = sc.GetAtomByUid(entry.Key);
                    atom.PreRestore(true, true);
                    atom.RestoreTransform(entry.Value);
                    atom.Restore(entry.Value, true, true, false, atoms);
                }
                foreach (KeyValuePair<string, JSONClass> entry in originals)
                {
                    Atom atom = sc.GetAtomByUid(entry.Key);
                    atom.LateRestore(entry.Value, true, true, false);
                    atom.PostRestore(true, true);
                }
                Report("Light restore: restored " + originals.Count + " scene-file lights; characters and scene were not reloaded.");
            }
            finally { _busy = false; }
        }

        private static void Report(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(message);
        }
        private void DisposeButton()
        {
            if (_restore != null && _script != null) _script.RemoveButton(_restore);
            _restore = null;
        }
        internal void Dispose() { _disposed = true; DisposeButton(); _script = null; }
    }
}
