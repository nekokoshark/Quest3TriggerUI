using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static UIDynamicButton _addPreset, _replacePreset, _savePreset;
        private static GameObject _presetList;
        private static object _presetEditor;
        private static float _nextPresetCheck;
        private static bool _presetBrowsing;

        private static void ClearPresetButtons()
        {
            if (_addPreset != null) UnityEngine.Object.Destroy(_addPreset.gameObject);
            if (_replacePreset != null) UnityEngine.Object.Destroy(_replacePreset.gameObject);
            if (_savePreset != null) UnityEngine.Object.Destroy(_savePreset.gameObject);
            _addPreset = _replacePreset = _savePreset = null;
            _presetList = null;
            _presetEditor = null;
        }

        private static void UpdatePresetButtons(SuperController sc)
        {
            if (Time.unscaledTime < _nextPresetCheck) return;
            _nextPresetCheck = Time.unscaledTime + 0.5f;
            if (sc.isLoading || !sc.MainHUDVisible) { ClearAlternateDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
            try
            {
                Snapshot state = FindEditor(sc);
                if (state == null) { ClearAlternateDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
                GameObject list = Read(state.Editor.GetType(), state.Editor, "aceScrollListGO") as GameObject;
                if (list == null) { ClearAlternateDock(); ClearFavoritesBar(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); return; }
                UpdateAlternateDock(state, list);
                UpdateFavoritesBar(state, list);
                UpdateBanBar(state, list);
                UpdateLockBar(state, list);
                if (_presetList == list && ReferenceEquals(_presetEditor, state.Editor) &&
                    _addPreset != null && _replacePreset != null &&
                    _savePreset != null) return;
                ClearPresetButtons();
                object canvas = Read(state.Editor.GetType(), state.Editor, "_uiButtonCanvas");
                _presetList = list;
                _presetEditor = state.Editor;
                _addPreset = CreatePresetButton(canvas, list, "新增", -112f,
                    delegate { BrowseClothingPreset(true); });
                _replacePreset = CreatePresetButton(canvas, list, "替换", -38f,
                    delegate { BrowseClothingPreset(false); });
                _savePreset = CreatePresetButton(canvas, list, "保存", 36f,
                    BrowseClothingPresetSave);
            }
            catch (Exception e) { ClearPresetButtons(); _nextPresetCheck = Time.unscaledTime + 5f; Error(e); }
        }

        private static UIDynamicButton CreatePresetButton(object canvas, GameObject list,
            string label, float offset, Action onClick)
        {
            MethodInfo create = canvas.GetType().GetMethod("CreateUIButtonInCanvas", Flags);
            ParameterInfo[] parameters = create.GetParameters();
            object[] args = new object[parameters.Length];
            object[] values = { label, 0f, 0f, 0f, 68f, 30f, 16 };
            for (int i = 0; i < args.Length; i++)
                args[i] = i < values.Length ? Convert.ChangeType(values[i], parameters[i].ParameterType) : parameters[i].DefaultValue;
            UIDynamicButton button = (UIDynamicButton)create.Invoke(canvas, args);
            RectTransform rect = (RectTransform)button.transform;
            RectTransform listRect = (RectTransform)list.transform;
            Vector3[] corners = new Vector3[4];
            listRect.GetWorldCorners(corners);
            // Dedicated header row above existing clear/scroll controls; same
            // native canvas/raycast path as the editor, not an independent overlay.
            rect.position = corners[2] + listRect.TransformVector(new Vector3(offset, 64f, 0f));
            rect.SetAsLastSibling();
            button.button.onClick.AddListener(delegate { onClick(); });
            return button;
        }

        private static void BrowseClothingPreset(bool merge)
        {
            if (_presetBrowsing) return;
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            _presetBrowsing = true;
            try
            {
                VrPresetBrowser.ShowDialogFull("加载服装预设",
                    PluginPaths.ClothingPresetDir, "vap", false, null,
                    delegate(string path, bool closed) {
                    try
                    {
                        if (sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null ||
                            state.Target == null || sc.GetAtomByUid(state.Target.uid) != state.Target) return;
                        if (!string.IsNullOrEmpty(path)) LoadEditorClothingPreset(state.Target, path, merge);
                    }
                    catch (Exception e) { Error(e); }
                    finally
                    {
                        if (closed)
                            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetEditor(state, scene));
                    }
                });
            }
            catch (Exception e) { _presetBrowsing = false; Error(e); }
        }

        private static void BrowseClothingPresetSave()
        {
            if (_presetBrowsing) return;
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if (state == null || state.Target == null) { Log("请先在服装编辑器中选择角色。"); return; }
            string scene = sc.LoadedSceneName;
            _presetBrowsing = true;
            try
            {
                string startDir = PresetSaveDirs.Get("ClothingPresets", PluginPaths.ClothingPresetDir);
                string defaultName =
                    ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString() + ".vap";
                VrPresetBrowser.ShowDialogFull("保存服装预设", startDir, "vap",
                    true, defaultName,
                    delegate(string path, bool closed) {
                    try
                    {
                        if (sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null ||
                            state.Target == null || sc.GetAtomByUid(state.Target.uid) != state.Target) return;
                        Log("服装保存对话框返回：" + (path ?? "<null>"));
                        if (!string.IsNullOrEmpty(path)) SaveEditorClothingPreset(state.Target, path);
                    }
                    catch (Exception e) { Error(e); }
                    finally
                    {
                        if (closed)
                            Quest3TriggerUIPlugin.Instance.StartCoroutine(RestorePresetEditor(state, scene));
                    }
                });
            }
            catch (Exception e) { _presetBrowsing = false; Error(e); }
        }

        // StorePreset resolves the output file purely from pm.presetName
        // (storeFolder + presetSubPath + storeName + "_" + presetSubName +
        // ".vap"), so this writes presetName directly and never touches
        // presetBrowsePath — the sync that could trigger a preset *load*
        // structurally cannot run.
        private static void SaveEditorClothingPreset(Atom target, string path)
        {
            MeshVR.PresetManagerControl presets = target.GetStorableByID("ClothingPresets") as MeshVR.PresetManagerControl;
            if (presets == null) throw new InvalidOperationException("ClothingPresets is unavailable.");
            JSONStorableString nameParam = presets.GetStringJSONParam("presetName");
            MeshVR.PresetManager pm = PresetManagerOf(presets);
            if (nameParam == null || pm == null) throw new InvalidOperationException("Clothing presetName is unavailable.");
            // VaM preset files are "Preset_<name>.vap"; a name typed without
            // the prefix is rejected by GetPresetNameFromFilePath.
            int slash = path.LastIndexOf('/');
            string dir = slash >= 0 ? path.Substring(0, slash) : path;
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            if (file.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                file = file.Substring(0, file.Length - 4);
            if (!file.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                file = "Preset_" + file;
            path = SuperController.singleton.NormalizePath(dir + "/" + file + ".vap");
            string name = pm.GetPresetNameFromFilePath(path);
            if (string.IsNullOrEmpty(name))
                name = file.Substring("Preset_".Length); // outside store folder: store root
            if (string.IsNullOrEmpty(pm.storeName))
                pm.storeName = "Preset";
            nameParam.val = name;
            presets.CallAction("StorePresetWithScreenshot");
            int storeSlash = path.LastIndexOfAny(new char[] { '/', '\\' });
            if (storeSlash > 0)
                PresetSaveDirs.Set("ClothingPresets", path.Substring(0, storeSlash));
            Log("保存服装预设：" + path + " (presetName=" + name + ")");
        }

        private static readonly FieldInfo PresetManagerField =
            typeof(MeshVR.PresetManagerControl).GetField("pm",
                BindingFlags.NonPublic | BindingFlags.Instance);

        internal static MeshVR.PresetManager PresetManagerOf(
            MeshVR.PresetManagerControl presets)
        {
            try
            {
                return PresetManagerField == null
                    ? null
                    : PresetManagerField.GetValue(presets) as MeshVR.PresetManager;
            }
            catch { return null; }
        }

        private static void LoadEditorClothingPreset(Atom target, string path, bool merge)
        {
            MeshVR.PresetManagerControl presets = target.GetStorableByID("ClothingPresets") as MeshVR.PresetManagerControl;
            if (presets == null) throw new InvalidOperationException("ClothingPresets is unavailable.");
            JSONStorableUrl url = presets.GetUrlJSONParam("presetBrowsePath");
            JSONStorableBool auto = presets.GetBoolJSONParam("loadPresetOnSelect");
            if (url == null || auto == null) throw new InvalidOperationException("Clothing preset parameters are unavailable.");
            bool oldAuto = auto.val, oldLock = presets.lockParams;
            string oldPath = url.val;
            try
            {
                auto.val = false;
                presets.lockParams = false;
                url.val = SuperController.singleton.NormalizePath(path);
                presets.CallAction(merge ? "MergeLoadPreset" : "LoadPreset");
                Log((merge ? "新增服装预设：" : "替换服装预设：") + target.uid + " / " + path);
            }
            finally { url.val = oldPath; auto.val = oldAuto; presets.lockParams = oldLock; }
        }

        private static IEnumerator RestorePresetEditor(Snapshot state, string scene)
        {
            yield return null; // Let the native browser finish its own close/hide callbacks.
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.isLoading || sc.LoadedSceneName != scene || state.Owner == null || state.Target == null) yield break;
                if (!ReferenceEquals(Read(state.Globals, null, "mvrScript"), state.Owner)) yield break;
                CancelPending();
                Write(state.Globals, null, "hideGameControlUI", false);
                Write(state.Control, null, "gameControlDisplayMode", state.Ace);
                Write(state.Control, null, "gridsActivated", true);
                sc.activeUI = SuperController.ActiveUI.None;
                sc.ShowMainHUD(true, false);
                Call(state.Display.GetType(), state.Display, "DestroyUIButtons");
                Call(state.Display.GetType(), state.Display, "CreateUIButtons");
                state.Editor.GetType().GetMethod("RefreshClothing", Flags).Invoke(state.Editor, new object[] { state.Target.uid });
                Call(state.Control, null, "OnEnable");
                _nextPresetCheck = 0f;
            }
            finally { _presetBrowsing = false; }
        }
    }
}

