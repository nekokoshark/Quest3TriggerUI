using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class SceneQuickActions
    {
        private const string EmbodyUrl =
            "AcidBubbles.Embody.latest:/Custom/Scripts/AcidBubbles/Embody/Embody.cslist";
        private const string EmbodyPreset = "Passenger (Free Look)";
        private readonly MonoBehaviour _host;
        private readonly FastStandbyController _standby;
        private readonly SoftRestartController _softRestart;
        private Atom _embodyTarget;
        private JSONStorable _embody;
        private bool _embodyBusy;
        private Coroutine _embodyActivationCoroutine;
        private float _nextEmbodyResolve;
        private readonly IncrementalVarRefresh _varRefresh;
        private readonly PhysicsPluginApplicator _physicsPlugins;
        private bool _appearanceLoadBusy;
        private Atom _expressionTarget;
        private readonly Dictionary<int, JSONStorable> _expressionTimelines =
            new Dictionary<int, JSONStorable>();
        private readonly HashSet<int> _activeExpressionIndices =
            new HashSet<int>();
        private Atom _tongueBaselineTarget;
        private readonly Dictionary<string, float> _tongueBaselineValues =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private bool _expressionBusy;
        private bool _neutralExpressionSelected;
        private int _lastExpressionIndex;
        private Atom _eyeGapTarget;
        private DAZMeshEyelidControl _eyeGapControl;
        private float _eyeGapSliderValue = 0.5f;
        private float _eyeGapBaseline = 0.5f;
        private DAZMorph _eyeTopDownLeft;
        private DAZMorph _eyeTopDownRight;
        private DAZMorph _eyeBottomUpLeft;
        private DAZMorph _eyeBottomUpRight;
        private DAZMorph _eyeTopUpLeft;
        private DAZMorph _eyeTopUpRight;
        private DAZMorph _eyeBottomDownLeft;
        private DAZMorph _eyeBottomDownRight;
        private JSONStorableBool _eyeLookMorphsEnabled;
        private bool _eyeLookMorphsOriginalValue;
        private bool _eyeLookMorphsSuppressed;
        private static readonly MethodInfo UpdateEyelidWeightsMethod =
            typeof(DAZMeshEyelidControl).GetMethod(
                "UpdateWeights",
                BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo EyelidLookMorphsEnabledField =
            typeof(DAZMeshEyelidControl).GetField(
                "eyelidLookMorphsEnabledJSON",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private enum PersonGenderFilter
        {
            Any,
            Male,
            Female
        }

        internal SceneQuickActions(MonoBehaviour host)
        {
            _host = host;
            _standby = new FastStandbyController();
            _softRestart = new SoftRestartController(host, _standby);
            _lastExpressionIndex = -1;
            _varRefresh = new IncrementalVarRefresh(host, NotifyPackageRefreshHandlers);
            _physicsPlugins = new PhysicsPluginApplicator(host);
        }

        internal void ApplyPhysicsPackage(Action<string> completed)
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                const string message = "物理：视线前方没有女性角色。";
                LogError(message);
                if (completed != null) completed(message);
                return;
            }
            _physicsPlugins.Apply(target, delegate(string message) {
                LogInfo(message);
                if (completed != null) completed(message);
            });
        }

        internal bool IsExpressionActive(int index)
        {
            return index == 0
                ? _neutralExpressionSelected
                : _activeExpressionIndices.Contains(index);
        }

        internal bool ExpressionActive
        {
            get
            {
                return _neutralExpressionSelected ||
                    _activeExpressionIndices.Count > 0;
            }
        }

        internal bool ExpressionTimelinesInitialized
        {
            get
            {
                ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
                if (_expressionTarget == null ||
                    _expressionTimelines.Count < expressions.Length - 1)
                    return false;
                for (int i = 1; i < expressions.Length; i++)
                {
                    JSONStorable timeline;
                    if (!_expressionTimelines.TryGetValue(i, out timeline) ||
                        timeline == null)
                        return false;
                }
                return true;
            }
        }

        internal bool StandbyActive
        {
            get { return _standby.Active; }
        }

        internal bool EmbodyActive
        {
            get
            {
                if (_embody == null && Time.unscaledTime >= _nextEmbodyResolve)
                {
                    _nextEmbodyResolve = Time.unscaledTime + 0.5f;
                    Atom nearest = FindClosestPerson(true);
                    JSONStorable found = nearest == null ? null : FindEmbody(nearest);
                    JSONStorableBool foundActive = found == null
                        ? null : found.GetBoolJSONParam("Active");
                    if (foundActive != null && foundActive.val)
                    {
                        _embodyTarget = nearest;
                        _embody = found;
                    }
                }
                JSONStorableBool active = _embody == null
                    ? null
                    : _embody.GetBoolJSONParam("Active");
                return active != null && active.val;
            }
        }

        internal bool EmbodyBusy
        {
            get { return _embodyBusy; }
        }

        internal void ToggleEmbody(Action<bool> completed)
        {
            if (_embodyBusy)
                return;

            if (_embody != null && EmbodyActive)
            {
                ConfigureEmbody(_embody, false);
                LogInfo("Embody: active instance disabled.");
                if (completed != null) completed(false);
                return;
            }

            Atom target = FindClosestPerson(true);
            if (target == null)
            {
                LogError("Embody: no male Person atom is in front of the VR view.");
                if (completed != null) completed(false);
                return;
            }

            _embodyBusy = true;
            JSONStorable embody = FindEmbody(target);
            LogInfo("Embody: activation requested for " + target.uid +
                (embody == null ? "; creating plugin slot." : "; reusing existing instance."));
            _embodyActivationCoroutine =
                _host.StartCoroutine(EnsureEmbodyActive(target, embody, completed));
        }

        internal bool DeactivateEmbodyForNavigationReset()
        {
            if (_embodyActivationCoroutine != null)
            {
                _host.StopCoroutine(_embodyActivationCoroutine);
                _embodyActivationCoroutine = null;
                _embodyBusy = false;
            }

            JSONStorable embody = _embody;
            if (embody == null && _embodyTarget != null)
                embody = FindEmbody(_embodyTarget);
            if (embody == null)
            {
                Atom nearest = FindClosestPerson(true);
                embody = nearest == null ? null : FindEmbody(nearest);
            }
            JSONStorableBool active = embody == null
                ? null : embody.GetBoolJSONParam("Active");
            if (active == null || !active.val)
                return false;

            _embody = embody;
            ConfigureEmbody(embody, false);
            LogInfo("Embody: active instance disabled for navigation reset.");
            return true;
        }

        internal void OpenClothing()
        {
            OpenPersonTab("Clothing");
        }

        internal void OpenUiAssistClothingEditor()
        {
            UiAssistHudLink.CancelPending();
            Atom target = FindClosestPerson(false);
            if (target == null)
            {
                LogError("UIAssist clothing editor: no Person atom is in front of the VR view.");
                return;
            }

            try
            {
                Type gameControlUi = FindLoadedType("JayJayWon.GameControlUI");
                Type gridsDisplay = FindLoadedType("JayJayWon.GridsDisplay");
                if (gameControlUi == null || gridsDisplay == null)
                    throw new InvalidOperationException(
                        "UIAssist 93 Game Control UI is not loaded");

                Type uiAssistGlobals = FindLoadedType("JayJayWon.UIAGlobals");
                if (uiAssistGlobals != null)
                    SetMemberValue(uiAssistGlobals, null, "hideGameControlUI", false);

                object editor = GetMemberValue(
                    gridsDisplay, null, "_uiActiveClothingEditor");
                if (editor == null)
                    throw new InvalidOperationException(
                        "UIAssist Active Clothing Editor is not initialized");

                ConfigureUiAssistClothingEditor(editor, target);
                ForcePersonEditorOpen(target);
                SetMemberValue(gameControlUi, null, "gameControlDisplayMode", 2);
                SetMemberValue(gameControlUi, null, "gridsActivated", true);

                // UIAssist's vrVAMUI canvas exists only while VaM's main HUD is
                // visible and activeUI is None. Force that exact state instead of
                // requiring the user to reopen the control panel manually.
                SuperController.singleton.activeUI = SuperController.ActiveUI.None;
                SuperController.singleton.ShowMainHUD(true, false);

                // RefreshWristUIButtonGrid refuses to build when _currentGrid is
                // null. ACE does not need a backing button grid, so rebuild the
                // GridsDisplay directly and make both UIAssist canvases visible.
                object display = GetMemberValue(gameControlUi, null, "gridsDisplay");
                if (display == null)
                    throw new InvalidOperationException(
                        "UIAssist GridsDisplay instance is not initialized");
                Type displayType = display.GetType();
                InvokeMethod(displayType, display, "DestroyUIButtons");
                InvokeMethod(displayType, display, "CreateUIButtons");
                InvokeMethod(gameControlUi, null, "OnEnable");
            }
            catch (Exception exception)
            {
                LogError("UIAssist clothing editor open failed: " + exception);
            }
        }

internal void OpenPersonPreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Person preset: no female Person atom is in front of the VR view.");
                return;
            }

            SelectTargetAndShowHUD(target);
            ShowAppearancePresetDialog(target, false);
        }

        internal void OpenAppearancePresetWithoutClothing()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Appearance preset: no female Person atom is in front of the VR view.");
                return;
            }

            SelectTargetAndShowHUD(target);
            ShowAppearancePresetDialog(target, true);
        }

        internal void OpenSkinPreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Skin preset: no female Person atom is in front of the VR view.");
                return;
            }

            SelectTargetAndShowHUD(target);
            JSONStorable skinPresets = target.GetStorableByID("SkinPresets");
            JSONStorableActionPresetFilePath loadAction = skinPresets == null
                ? null
                : skinPresets.GetPresetFilePathAction("LoadPresetWithPath");
            if (loadAction == null)
            {
                LogError("Skin preset: SkinPresets.LoadPresetWithPath is unavailable.");
                return;
            }

            loadAction.Browse(delegate(string path)
            {
                if (!string.IsNullOrEmpty(path))
                    skinPresets.CallPresetFileAction("LoadPresetWithPath", path);
            });
        }

        internal void OpenHairPreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Hair preset: no female Person atom is in front of the VR view.");
                return;
            }
            SelectTargetAndShowHUD(target);
            VrPresetBrowser.ShowDialogFull("加载头发预设", PluginPaths.HairPresetDir,
                "vap", false, null,
                delegate(string path, bool didClose) {
                    if (target != null && !string.IsNullOrEmpty(path))
                        LoadHairPreset(target, path);
                });
        }

        private void LoadHairPreset(Atom target, string path)
        {
            MeshVR.PresetManagerControl hair =
                target.GetStorableByID("HairPresets") as MeshVR.PresetManagerControl;
            JSONStorableUrl selected = hair == null ? null : hair.GetUrlJSONParam("presetBrowsePath");
            if (selected == null)
            {
                LogError("Hair preset: HairPresets.presetBrowsePath is unavailable.");
                return;
            }
            bool wasLocked = hair.lockParams;
            string previousPath = selected.val;
            JSONStorableBool loadOnSelect = hair.GetBoolJSONParam("loadPresetOnSelect");
            if (loadOnSelect == null)
            {
                LogError("Hair preset: loadPresetOnSelect is unavailable.");
                return;
            }
            bool previousLoadOnSelect = loadOnSelect.val;
            try
            {
                hair.lockParams = false;
                loadOnSelect.val = false;
                // UIAssist uses the callback setter: it initializes PresetManager's
                // directory/name parameters, not just the visible URL value.
                selected.val = SuperController.singleton.NormalizePath(path);
                // Explicit LoadPreset replaces hair; never MergeLoadPreset. Repeated
                // selection of the same file still executes the load exactly once.
                hair.CallAction("LoadPreset");
                LogInfo("Hair preset replaced: " + path);
            }
            catch (Exception exception)
            {
                LogError("Hair preset load failed: " + exception);
            }
            finally
            {
                selected.val = previousPath;
                loadOnSelect.val = previousLoadOnSelect;
                hair.lockParams = wasLocked;
            }
        }

        internal void SaveAppearancePreset()
        {
            SavePersonPresetDialog("AppearancePresets",
                PluginPaths.AppearancePresetDir, "外观");
        }

        internal void SaveSkinPreset()
        {
            SavePersonPresetDialog("SkinPresets",
                PluginPaths.SkinPresetDir, "皮肤");
        }

        internal void SaveHairPreset()
        {
            SavePersonPresetDialog("HairPresets",
                PluginPaths.HairPresetDir, "头发");
        }

        // Mirrors UIAssist's save-mode file pick: the SAME media browser is
        // opened, then switched to text-entry save mode — fileEntryField is
        // shown and pre-filled with a unique default name. Picking an
        // existing file = overwrite; the typed name = new file.
        // Deliberately NOT SelectTargetAndShowHUD: the atom control panel's
        // own preset list is bound to loadPresetOnSelect, and clicking a
        // file there performs a load while our save dialog is open.
        private void SavePersonPresetDialog(
            string storableId, string suggestedDir, string label)
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError(label + " preset save: no female Person atom is in front of the VR view.");
                return;
            }
            MeshVR.PresetManagerControl presets =
                target.GetStorableByID(storableId) as MeshVR.PresetManagerControl;
            if (presets == null)
            {
                LogError(label + " preset save: " + storableId + " is unavailable.");
                return;
            }
            SuperController sc = SuperController.singleton;
            sc.ShowMainHUDAuto();
            string startDir = PresetSaveDirs.Get(storableId, suggestedDir);
            string defaultName =
                ((int)(DateTime.UtcNow -
                    new DateTime(1970, 1, 1)).TotalSeconds).ToString() + ".vap";
            VrPresetBrowser.ShowDialogFull("保存" + label + "预设", startDir, "vap",
                true, defaultName,
                delegate(string path, bool didClose) {
                    if (target == null)
                        return;
                    LogInfo(label + " save dialog returned path=" +
                        (path ?? "<null>"));
                    if (string.IsNullOrEmpty(path))
                        return;
                    StorePresetToPath(presets, path, storableId, label);
                });
        }

        // StorePreset resolves the output file purely from pm.presetName
        // (storeFolder + presetSubPath + storeName + "_" + presetSubName +
        // ".vap"), so this writes presetName directly and never touches
        // presetBrowsePath — the sync that could trigger a preset *load*
        // structurally cannot run.
        private void StorePresetToPath(
            MeshVR.PresetManagerControl presets, string path,
            string storableId, string label)
        {
            JSONStorableString nameParam =
                presets.GetStringJSONParam("presetName");
            MeshVR.PresetManager pm = PresetManagerOf(presets);
            if (nameParam == null || pm == null)
            {
                LogError(label + " preset save: presetName is unavailable.");
                return;
            }
            // VaM preset files are "<storeName>_<name>.vap" (storeName="Preset").
            // A name typed without the prefix is rejected by
            // GetPresetNameFromFilePath, so normalise the chosen path.
            int slash = path.LastIndexOf('/');
            string dir = slash >= 0 ? path.Substring(0, slash) : path;
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            if (file.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                file = file.Substring(0, file.Length - 4);
            if (!file.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                file = "Preset_" + file;
            path = SuperController.singleton.NormalizePath(
                dir + "/" + file + ".vap");
            try
            {
                string name = pm.GetPresetNameFromFilePath(path);
                if (string.IsNullOrEmpty(name))
                {
                    // Picked path is outside this manager's store folder:
                    // store in the store root under the chosen filename.
                    name = file.Substring("Preset_".Length);
                }
                if (string.IsNullOrEmpty(pm.storeName))
                    pm.storeName = "Preset";
                nameParam.val = name;
                presets.CallAction("StorePresetWithScreenshot");
                int storeSlash = path.LastIndexOfAny(new char[] { '/', '\\' });
                if (storeSlash > 0)
                    PresetSaveDirs.Set(storableId, path.Substring(0, storeSlash));
                LogInfo(label + " preset saved: " + path +
                    " (presetName=" + name + ")");
            }
            catch (Exception exception)
            {
                LogError(label + " preset save failed: " + exception);
            }
        }

        private static readonly FieldInfo PresetManagerField =
            typeof(MeshVR.PresetManagerControl).GetField("pm",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static MeshVR.PresetManager PresetManagerOf(
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

        private readonly LightLinkerBridge _lightLinker = new LightLinkerBridge();
        internal void OpenLightLinker()
        {
            _lightLinker.Open(_host);
        }
        internal void OptimizeMemory()
        {
            if (MemoryOptimizer.singleton == null)
            {
                LogError("Optimize Memory: VaM MemoryOptimizer is not available.");
                return;
            }

            MemoryOptimizer.singleton.TriggerOptimize();
        }

        internal void ToggleStandby(Action<string> completed)
        {
            _standby.Toggle(completed);
        }

        internal void ThrottleStandbyFrame()
        {
            _standby.ThrottleFrame();
        }

        internal float EyeGapSliderValue
        {
            get { return _eyeGapSliderValue; }
        }

        internal bool SetNearestFemaleEyeGap(float sliderValue, out string message)
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                message = "视线前方没有可用的女性角色。";
                return false;
            }

            DAZMeshEyelidControl eyelids =
                target.GetStorableByID("EyelidControl") as DAZMeshEyelidControl;
            if (eyelids == null || UpdateEyelidWeightsMethod == null)
            {
                message = "当前女性角色没有可用的眼皮控制器。";
                return false;
            }

            if (_eyeGapTarget != target)
            {
                RestoreEyeLookMorphs(true);
                _eyeGapBaseline = 0.5f;
            }
            _eyeGapTarget = target;
            _eyeGapControl = eyelids;
            CacheEyelidMorphs(eyelids);
            _eyeGapSliderValue = Mathf.Clamp01(sliderValue);
            ApplyEyeGap();
            message = "已调节 " + target.uid + " 的眼缝宽度：" +
                Mathf.RoundToInt(_eyeGapSliderValue * 100f) + "%";
            return true;
        }

        internal bool CalibrateNearestFemaleEyeGap(out string message)
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                message = "视线前方没有可用的女性角色。";
                return false;
            }

            DAZMeshEyelidControl eyelids =
                target.GetStorableByID("EyelidControl") as DAZMeshEyelidControl;
            if (eyelids == null || UpdateEyelidWeightsMethod == null)
            {
                message = "当前女性角色没有可用的眼皮控制器。";
                return false;
            }

            float currentGap = _eyeGapTarget == target
                ? EffectiveEyeGap()
                : 0.5f;
            if (_eyeGapTarget != target)
                RestoreEyeLookMorphs(true);
            _eyeGapTarget = target;
            _eyeGapControl = eyelids;
            CacheEyelidMorphs(eyelids);
            _eyeGapBaseline = currentGap;
            _eyeGapSliderValue = 0.5f;
            ApplyEyeGap();
            message = "已将 " + target.uid +
                " 的当前眼缝设为中点；滑条已归位到 50%。";
            return true;
        }

        internal void LateTickEyeGap()
        {
            if (_eyeGapTarget == null || !_eyeGapTarget.on ||
                _eyeGapControl == null)
            {
                RestoreEyeLookMorphs(false);
                return;
            }
            if (!_eyeLookMorphsSuppressed &&
                Mathf.Abs(EffectiveEyeGap() - 0.5f) < 0.001f)
                return;
            ApplyEyeGap();
        }

        private float EffectiveEyeGap()
        {
            if (_eyeGapSliderValue <= 0.5f)
                return _eyeGapBaseline * (_eyeGapSliderValue * 2f);
            return _eyeGapBaseline + (1f - _eyeGapBaseline) *
                ((_eyeGapSliderValue - 0.5f) * 2f);
        }

        private void ApplyEyeGap()
        {
            if (_eyeGapControl == null || UpdateEyelidWeightsMethod == null)
                return;

            float adjustment = (EffectiveEyeGap() - 0.5f) * 2f;
            if (Mathf.Abs(adjustment) < 0.001f)
            {
                RestoreEyeLookMorphs(true);
                return;
            }

            float nativeWeight = _eyeGapControl.currentWeight;
            if (adjustment < 0f)
            {
                RestoreEyeLookMorphs(false);
                _eyeGapControl.currentWeight = Mathf.Max(nativeWeight, -adjustment);
                try
                {
                    UpdateEyelidWeightsMethod.Invoke(_eyeGapControl, null);
                }
                finally
                {
                    // Keep VaM's blink state untouched. The enforced closure is
                    // applied only to this rendered frame.
                    _eyeGapControl.currentWeight = nativeWeight;
                }
                return;
            }

            // VaM writes the eye-look morphs in Update while this adjustment is
            // written in LateUpdate. Leaving both writers enabled made the
            // upper eyelid alternate between two values every frame. Suspend
            // only that writer while custom opening is active, then blend the
            // absolute opening value down during a native blink.
            SuppressEyeLookMorphs();
            float blinkBlend = 1f - Mathf.Clamp01(nativeWeight);
            float openValue = 2f * Mathf.Clamp01(adjustment) * blinkBlend;
            SetOpenMorph(_eyeTopUpLeft, openValue);
            SetOpenMorph(_eyeTopUpRight, openValue);
            SetOpenMorph(_eyeBottomDownLeft, openValue);
            SetOpenMorph(_eyeBottomDownRight, openValue);
            SetOpenMorph(_eyeBottomUpLeft, 0f);
            SetOpenMorph(_eyeBottomUpRight, 0f);
        }

        private void CacheEyelidMorphs(DAZMeshEyelidControl eyelids)
        {
            _eyeTopDownLeft = GetEyelidMorph(eyelids, "LeftTopEyelidDownMorph");
            _eyeTopDownRight = GetEyelidMorph(eyelids, "RightTopEyelidDownMorph");
            _eyeBottomUpLeft = GetEyelidMorph(eyelids, "LeftBottomEyelidUpMorph");
            _eyeBottomUpRight = GetEyelidMorph(eyelids, "RightBottomEyelidUpMorph");
            _eyeTopUpLeft = GetEyelidMorph(eyelids, "LeftTopEyelidUpMorph");
            _eyeTopUpRight = GetEyelidMorph(eyelids, "RightTopEyelidUpMorph");
            _eyeBottomDownLeft = GetEyelidMorph(eyelids, "LeftBottomEyelidDownMorph");
            _eyeBottomDownRight = GetEyelidMorph(eyelids, "RightBottomEyelidDownMorph");
            _eyeLookMorphsEnabled = EyelidLookMorphsEnabledField == null
                ? null
                : EyelidLookMorphsEnabledField.GetValue(eyelids) as JSONStorableBool;
        }

        private static DAZMorph GetEyelidMorph(
            DAZMeshEyelidControl eyelids, string fieldName)
        {
            FieldInfo field = typeof(DAZMeshEyelidControl).GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : field.GetValue(eyelids) as DAZMorph;
        }

        private void SuppressEyeLookMorphs()
        {
            if (_eyeLookMorphsSuppressed || _eyeLookMorphsEnabled == null)
                return;
            _eyeLookMorphsOriginalValue = _eyeLookMorphsEnabled.val;
            _eyeLookMorphsEnabled.val = false;
            _eyeLookMorphsSuppressed = true;
        }

        private void RestoreEyeLookMorphs(bool refreshWeights)
        {
            if (!_eyeLookMorphsSuppressed)
                return;
            if (_eyeLookMorphsEnabled != null)
                _eyeLookMorphsEnabled.val = _eyeLookMorphsOriginalValue;
            _eyeLookMorphsSuppressed = false;
            if (refreshWeights && _eyeGapControl != null &&
                UpdateEyelidWeightsMethod != null)
            {
                UpdateEyelidWeightsMethod.Invoke(_eyeGapControl, null);
            }
        }

        private static void SetOpenMorph(DAZMorph morph, float value)
        {
            if (morph == null)
                return;
            if (value > morph.max)
                morph.morphValueAdjustLimits = value;
            else
                morph.morphValue = value;
        }

        internal void RefreshVars(Action<string> completed) { _varRefresh.Begin(false, completed); }
        internal void FullRefreshVars(Action<string> completed) { _varRefresh.Begin(true, completed); }
        internal void SoftRestart(Action<string> completed)
        {
            _softRestart.Begin(completed);
        }

        internal void ReplayActiveExpression(Action<string> completed)
        {
            PlayExpression(_lastExpressionIndex >= 0 ? _lastExpressionIndex : 0,
                completed);
        }

        internal void PlayExpression(int index, Action<string> completed)
        {
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            if (index < 0 || index >= expressions.Length)
            {
                if (completed != null) completed("表情索引无效。");
                return;
            }
            if (_expressionBusy)
            {
                if (completed != null) completed("表情正在切换，请稍候。");
                return;
            }

            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Expression: no female Person atom is in front of the VR view.");
                if (completed != null) completed("视线前方没有可用的女性角色。");
                return;
            }

            _expressionBusy = true;
            _host.StartCoroutine(PlayExpressionRoutine(target, index, completed));
        }

        internal void InitializeExpressions(Action<string> completed)
        {
            if (_expressionBusy)
            {
                if (completed != null) completed("表情正在处理，请稍候。");
                return;
            }
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                if (completed != null) completed("视线前方没有可用的女性角色。");
                return;
            }
            _expressionBusy = true;
            _host.StartCoroutine(InitializeExpressionsRoutine(target, completed));
        }

        internal void Dispose()
        {
            _lightLinker.Dispose();
            _varRefresh.Dispose();
            RestoreEyeLookMorphs(true);
            _eyeGapSliderValue = 0.5f;
            _eyeGapBaseline = 0.5f;
            _eyeGapTarget = null;
            _eyeGapControl = null;
            StopAllExpressionTimelines(_expressionTimelines.Values);
            ResetAllExpressionsToNeutral(_expressionTarget);
            RestoreInitialTongueState(_expressionTarget);
            SetAllExpressionTimelinesEnabled(false);
            _standby.RestoreImmediately();
        }

        private IEnumerator InitializeExpressionsRoutine(
            Atom target, Action<string> completed)
        {
            if (_expressionTarget != target)
            {
                if (_expressionTarget != null)
                {
                    StopAllExpressionTimelines(_expressionTimelines.Values);
                    yield return null;
                    ResetAllExpressionsToNeutral(_expressionTarget);
                    RestoreInitialTongueState(_expressionTarget);
                    SetAllExpressionTimelinesEnabled(false);
                }
                _expressionTimelines.Clear();
                _activeExpressionIndices.Clear();
                _neutralExpressionSelected = false;
                _lastExpressionIndex = -1;
                _expressionTarget = target;
            }

            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            Dictionary<int, string> pendingIds = new Dictionary<int, string>();
            try
            {
                for (int i = 1; i < expressions.Length; i++)
                {
                    JSONStorable timeline;
                    if (!_expressionTimelines.TryGetValue(i, out timeline))
                        timeline = FindExpressionTimeline(target, i);
                    if (timeline != null)
                        _expressionTimelines[i] = timeline;
                }

                if (_expressionTimelines.Count < expressions.Length - 1)
                {
                    MVRPluginManager manager =
                        target.GetStorableByID("PluginManager") as MVRPluginManager;
                    if (manager == null)
                        throw new InvalidOperationException(
                            "目标角色没有 PluginManager");
                    for (int i = 1; i < expressions.Length; i++)
                    {
                        if (_expressionTimelines.ContainsKey(i))
                            continue;
                        pendingIds.Add(i, CreateExpressionTimeline(manager));
                    }
                }
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
                yield break;
            }

            if (pendingIds.Count > 0)
            {
                float deadline = Time.unscaledTime + 18f;
                while (Time.unscaledTime < deadline && pendingIds.Count > 0)
                {
                    List<int> loaded = new List<int>();
                    foreach (KeyValuePair<int, string> pair in pendingIds)
                    {
                        JSONStorable timeline = target.GetStorableByID(pair.Value);
                        if (timeline == null)
                            continue;
                        _expressionTimelines[pair.Key] = timeline;
                        loaded.Add(pair.Key);
                    }
                    for (int i = 0; i < loaded.Count; i++)
                        pendingIds.Remove(loaded[i]);
                    if (pendingIds.Count > 0)
                        yield return null;
                }
                if (pendingIds.Count > 0)
                {
                    CompleteExpressionFailure(completed,
                        new InvalidOperationException(
                            "批量表情 Timeline 加载超时，尚缺 " +
                            pendingIds.Count + " 个"));
                    yield break;
                }
            }

            StopAllExpressionTimelines(_expressionTimelines.Values);
            yield return null;
            ResetAllExpressionsToNeutral(target);
            RestoreInitialTongueState(target);
            for (int i = 1; i < expressions.Length; i++)
            {
                try
                {
                    JSONStorable timeline = _expressionTimelines[i];
                    JSONClass config = JSON.Parse(
                        expressions[i].TimelineJson).AsObject;
                    config["id"] = timeline.storeId;
                    config["pluginLabel"] =
                        ExpressionTimelineLabel(i, expressions[i]);
                    timeline.RestoreFromJSON(config, true, true, null, true);
                    LoadExpressionTimeline(timeline, config);
                    SetExpressionTimelineEnabled(timeline, false);
                    string actionName = "Play Segment " + expressions[i].Segment;
                    if (timeline.GetAction(actionName) == null)
                        throw new InvalidOperationException(
                            "Timeline 未注册动作：" + actionName);
                }
                catch (Exception exception)
                {
                    CompleteExpressionFailure(completed, exception);
                    yield break;
                }
                // Each expression embeds a large Timeline JSON; spread the
                // parse+restore across frames instead of one heavy frame.
                yield return null;
            }
            try
            {
                _activeExpressionIndices.Clear();
                _neutralExpressionSelected = false;
                _lastExpressionIndex = -1;
                _expressionBusy = false;
                if (completed != null)
                    completed("表情初始化完成：已一次加载全部 " +
                        (expressions.Length - 1) + " 个表情 Timeline。");
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
            }
        }

        private IEnumerator PlayExpressionRoutine(
            Atom target, int index, Action<string> completed)
        {
            ExpressionDefinition definition = SevenSeasonExpressionLibrary.All[index];
            if (_expressionTarget != target)
            {
                if (_expressionTarget != null)
                {
                    StopAllExpressionTimelines(_expressionTimelines.Values);
                    yield return null;
                    ResetAllExpressionsToNeutral(_expressionTarget);
                    RestoreInitialTongueState(_expressionTarget);
                    SetAllExpressionTimelinesEnabled(false);
                }
                _expressionTimelines.Clear();
                _activeExpressionIndices.Clear();
                _neutralExpressionSelected = false;
                _lastExpressionIndex = -1;
                _expressionTarget = target;

                List<JSONStorable> persisted =
                    FindAllExpressionTimelines(target);
                if (persisted.Count > 0)
                {
                    StopAllExpressionTimelines(persisted);
                    yield return null;
                    ResetAllExpressionsToNeutral(target);
                    for (int i = 0; i < persisted.Count; i++)
                        SetExpressionTimelineEnabled(persisted[i], false);
                }
            }

            if (definition.IsNeutral)
            {
                StopAllExpressionTimelines(_expressionTimelines.Values);
                yield return null;
                ResetAllExpressionsToNeutral(target);
                RestoreInitialTongueState(target);
                SetAllExpressionTimelinesEnabled(false);
                _activeExpressionIndices.Clear();
                _neutralExpressionSelected = true;
                _lastExpressionIndex = 0;
                _expressionBusy = false;
                if (completed != null)
                    completed("已让 " + target.uid + " 恢复中性/待机表情。");
                yield break;
            }

            JSONStorable expressionTimeline;
            if (_activeExpressionIndices.Contains(index) &&
                _expressionTimelines.TryGetValue(index, out expressionTimeline))
            {
                StopExpressionTimeline(expressionTimeline);
                yield return null;
                ResetExpressionToNeutral(target, index);
                SetExpressionTimelineEnabled(expressionTimeline, false);
                _activeExpressionIndices.Remove(index);
                if (ExpressionUsesTongue(definition) &&
                    !HasActiveTongueExpression())
                    RestoreInitialTongueState(target);
                _neutralExpressionSelected = false;
                _lastExpressionIndex = LastActiveExpressionIndex();
                _expressionBusy = false;
                if (completed != null)
                    completed("已取消 " + target.uid + " 的表情：" +
                        definition.Label + "。");
                yield break;
            }

            string newTimelineId = null;
            try
            {
                if (!_expressionTimelines.TryGetValue(index, out expressionTimeline))
                    expressionTimeline = FindExpressionTimeline(target, index);
                if (expressionTimeline == null)
                {
                    MVRPluginManager manager =
                        target.GetStorableByID("PluginManager") as MVRPluginManager;
                    if (manager == null)
                        throw new InvalidOperationException(
                            "目标角色没有 PluginManager");

                    newTimelineId = CreateExpressionTimeline(manager);
                }
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
                yield break;
            }

            if (expressionTimeline == null)
            {
                float deadline = Time.unscaledTime + 12f;
                while (Time.unscaledTime < deadline)
                {
                    expressionTimeline = target.GetStorableByID(newTimelineId);
                    if (expressionTimeline != null)
                        break;
                    yield return null;
                }
                if (expressionTimeline == null)
                {
                    CompleteExpressionFailure(completed,
                        new InvalidOperationException(
                            "专用 Timeline 表情通道加载超时"));
                    yield break;
                }
            }
            _expressionTimelines[index] = expressionTimeline;
            CaptureInitialTongueState(target, definition);

            try
            {
                // Timeline.RestoreFromJSON deliberately ignores animation data
                // once its animation object exists.  Stop the current clip,
                // clear every SevenSeason expression morph, then call Timeline's
                // public Load method so the old expression cannot keep driving
                // morphs underneath the newly selected one.
                StopExpressionTimeline(expressionTimeline);
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
                yield break;
            }
            yield return null;
            ResetExpressionToNeutral(target, index);

            try
            {
                JSONClass config = JSON.Parse(definition.TimelineJson).AsObject;
                config["id"] = expressionTimeline.storeId;
                config["pluginLabel"] = ExpressionTimelineLabel(index, definition);
                expressionTimeline.RestoreFromJSON(config, true, true, null, true);
                LoadExpressionTimeline(expressionTimeline, config);
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
                yield break;
            }
            yield return null;

            try
            {
                SetExpressionTimelineEnabled(expressionTimeline, true);
                string actionName = "Play Segment " + definition.Segment;
                if (expressionTimeline.GetAction(actionName) == null)
                    throw new InvalidOperationException(
                        "Timeline 未注册动作：" + actionName);
                expressionTimeline.CallAction(actionName);
                _activeExpressionIndices.Add(index);
                _neutralExpressionSelected = false;
                _lastExpressionIndex = index;
                if (completed != null)
                    completed("已对 " + target.uid + " 启用表情：" +
                        definition.Label + "。再次选择可取消；可同时启用多个表情。" );
                _expressionBusy = false;
            }
            catch (Exception exception)
            {
                CompleteExpressionFailure(completed, exception);
            }
        }

        private void CompleteExpressionFailure(
            Action<string> completed, Exception exception)
        {
            _expressionBusy = false;
            if (!HasActiveTongueExpression())
                RestoreInitialTongueState(_expressionTarget);
            LogError("Expression activation failed: " + exception);
            if (completed != null)
                completed("表情启用失败：" + exception.Message);
        }

        private static bool StopExpressionTimeline(JSONStorable timeline)
        {
            if (timeline == null)
                return false;
            try
            {
                JSONStorableAction stop = timeline.GetAction("Stop");
                if (stop == null)
                    return false;
                timeline.CallAction("Stop");
                return true;
            }
            catch (NullReferenceException)
            {
                // A newly added Timeline exposes its actions before its
                // animation object exists.  LoadExpressionTimeline initializes
                // that object; a pre-load Stop is unnecessary in this state.
                return false;
            }
        }

        private static void StopAllExpressionTimelines(
            IEnumerable<JSONStorable> timelines)
        {
            foreach (JSONStorable timeline in timelines)
                StopExpressionTimeline(timeline);
        }

        private static void ResetExpressionToNeutral(Atom target, int index)
        {
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            if (target == null || index <= 0 || index >= expressions.Length)
                return;
            ApplyExpressionResetValues(target, expressions[index].ResetValues);
        }

        private static void ResetAllExpressionsToNeutral(Atom target)
        {
            if (target == null)
                return;
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            // This is the same order as SevenSeason's Neutral action turning
            // every expression act off; later entries intentionally win where
            // the source scene gives the same morph more than one reset value.
            for (int expressionIndex = 1;
                 expressionIndex < expressions.Length; expressionIndex++)
            {
                ExpressionResetValue[] values =
                    expressions[expressionIndex].ResetValues;
                ApplyExpressionResetValues(target, values);
            }
        }

        private static void ApplyExpressionResetValues(
            Atom target, ExpressionResetValue[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                JSONStorable storable =
                    target.GetStorableByID(values[i].Storable);
                JSONStorableFloat parameter = storable == null
                    ? null
                    : storable.GetFloatJSONParam(values[i].Parameter);
                if (parameter != null)
                    parameter.val = values[i].Value;
            }
        }

        private void CaptureInitialTongueState(
            Atom target, ExpressionDefinition definition)
        {
            if (target == null || !ExpressionUsesTongue(definition))
                return;
            if (_tongueBaselineTarget != target)
            {
                _tongueBaselineTarget = target;
                _tongueBaselineValues.Clear();
            }
            if (_tongueBaselineValues.Count > 0)
                return;

            JSONStorable geometry = target.GetStorableByID("geometry");
            if (geometry == null)
                return;
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            for (int expressionIndex = 1;
                 expressionIndex < expressions.Length; expressionIndex++)
            {
                ExpressionResetValue[] values =
                    expressions[expressionIndex].ResetValues;
                for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
                {
                    ExpressionResetValue value = values[valueIndex];
                    if (!IsTongueParameter(value) ||
                        _tongueBaselineValues.ContainsKey(value.Parameter))
                        continue;
                    JSONStorableFloat parameter =
                        geometry.GetFloatJSONParam(value.Parameter);
                    if (parameter != null)
                        _tongueBaselineValues.Add(value.Parameter, parameter.val);
                }
            }
        }

        private void RestoreInitialTongueState(Atom target)
        {
            if (target == null || target != _tongueBaselineTarget)
                return;
            JSONStorable geometry = target.GetStorableByID("geometry");
            if (geometry != null)
            {
                foreach (KeyValuePair<string, float> pair in
                         _tongueBaselineValues)
                {
                    JSONStorableFloat parameter =
                        geometry.GetFloatJSONParam(pair.Key);
                    if (parameter != null)
                        parameter.val = pair.Value;
                }
            }
            _tongueBaselineValues.Clear();
            _tongueBaselineTarget = null;
        }

        private bool HasActiveTongueExpression()
        {
            ExpressionDefinition[] expressions = SevenSeasonExpressionLibrary.All;
            foreach (int index in _activeExpressionIndices)
            {
                if (index > 0 && index < expressions.Length &&
                    ExpressionUsesTongue(expressions[index]))
                    return true;
            }
            return false;
        }

        private static bool ExpressionUsesTongue(ExpressionDefinition definition)
        {
            if (definition == null)
                return false;
            ExpressionResetValue[] values = definition.ResetValues;
            for (int i = 0; i < values.Length; i++)
            {
                if (IsTongueParameter(values[i]))
                    return true;
            }
            return false;
        }

        private static bool IsTongueParameter(ExpressionResetValue value)
        {
            if (value == null ||
                !string.Equals(value.Storable, "geometry",
                    StringComparison.Ordinal) ||
                string.IsNullOrEmpty(value.Parameter))
                return false;
            return value.Parameter.IndexOf(
                       "tongue", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.Parameter.IndexOf(
                       "mouth lick", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LoadExpressionTimeline(
            JSONStorable timeline, JSONClass config)
        {
            MethodInfo load = timeline.GetType().GetMethod(
                "Load", BindingFlags.Public | BindingFlags.Instance, null,
                new Type[] { typeof(JSONNode), typeof(JSONNode) }, null);
            if (load == null)
                throw new MissingMethodException(
                    timeline.GetType().FullName, "Load");
            load.Invoke(timeline,
                new object[] { config["Animation"], config["Options"] });
        }

        private static string CreateExpressionTimeline(MVRPluginManager manager)
        {
            // CreatePlugin only adds the new slot. Restoring the complete
            // PluginManager JSON reloads every existing plugin on the Person and
            // can stop the Timeline or AnimationPattern currently driving the
            // body, which made applying a facial expression freeze the main act.
            MVRPlugin plugin = manager.CreatePlugin();
            if (plugin == null)
                throw new InvalidOperationException("无法创建表情 Timeline 插件槽");
            plugin.pluginURLJSON.val = SevenSeasonExpressionLibrary.TimelineUrl;
            return plugin.uid + "_VamTimeline.AtomPlugin";
        }

        private static void SetExpressionTimelineEnabled(
            JSONStorable timeline, bool enabled)
        {
            if (timeline == null)
                return;
            timeline.enabled = enabled;
            JSONStorableBool state = timeline.GetBoolJSONParam("enabled");
            if (state != null)
                state.val = enabled;
        }

        private void SetAllExpressionTimelinesEnabled(bool enabled)
        {
            foreach (JSONStorable timeline in _expressionTimelines.Values)
                SetExpressionTimelineEnabled(timeline, enabled);
        }

        private int LastActiveExpressionIndex()
        {
            int result = -1;
            foreach (int index in _activeExpressionIndices)
                result = index;
            return result;
        }

        private static string ExpressionTimelineLabel(
            int index, ExpressionDefinition definition)
        {
            return "Quest3 表情 #" + index + " - " + definition.Label;
        }

        private static JSONStorable FindExpressionTimeline(Atom target, int index)
        {
            ExpressionDefinition[] definitions = SevenSeasonExpressionLibrary.All;
            string expected = ExpressionTimelineLabel(index, definitions[index]);
            string legacy = "Quest3 表情 - " + definitions[index].Label;
            List<string> ids = target.GetStorableIDs();
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i].IndexOf("_VamTimeline.AtomPlugin",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                JSONStorable storable = target.GetStorableByID(ids[i]);
                JSONStorableString label = storable == null
                    ? null
                    : storable.GetStringJSONParam("pluginLabel");
                string labelValue = label == null ? null : label.val;
                if (string.IsNullOrEmpty(labelValue) && storable != null)
                {
                    JSONClass state = storable.GetJSON(true, true, true);
                    if (state != null)
                        labelValue = state["pluginLabel"].Value;
                }
                if (string.Equals(labelValue, expected, StringComparison.Ordinal) ||
                    string.Equals(labelValue, legacy, StringComparison.Ordinal))
                    return storable;
            }
            return null;
        }

        private static List<JSONStorable> FindAllExpressionTimelines(Atom target)
        {
            List<JSONStorable> result = new List<JSONStorable>();
            ExpressionDefinition[] definitions = SevenSeasonExpressionLibrary.All;
            for (int i = 1; i < definitions.Length; i++)
            {
                JSONStorable timeline = FindExpressionTimeline(target, i);
                if (timeline != null && !result.Contains(timeline))
                    result.Add(timeline);
            }
            return result;
        }

        private static void NotifyPackageRefreshHandlers()
        {
            FieldInfo handlersField = typeof(FileManager).GetField(
                "onRefreshHandlers", BindingFlags.NonPublic | BindingFlags.Static);
            Delegate handlers = handlersField == null
                ? null
                : handlersField.GetValue(null) as Delegate;
            if (handlers == null)
                return;

            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    invocationList[i].DynamicInvoke();
                }
                catch (Exception exception)
                {
                    LogError("VAR refresh handler failed: " +
                        (exception.InnerException == null
                            ? exception.Message
                            : exception.InnerException.Message));
                }
            }
        }

        private void OpenPersonTab(string tab)
        {
            Atom target = FindClosestPerson(false);
            if (target == null)
            {
                LogError(tab + ": no Person atom is in front of the VR view.");
                return;
            }

            _host.StartCoroutine(OpenPersonPanel(target, tab));
        }

        private IEnumerator EnsureEmbodyActive(
            Atom target, JSONStorable embody, Action<bool> completed)
        {
            float deadline = Time.unscaledTime + 12f;
            if (embody == null)
            {
                string creationError;
                if (!TryCreateEmbodyPlugin(target, out creationError))
                {
                    FinishEmbodyRequest(target, null, false, completed, creationError);
                    yield break;
                }
                LogInfo("Embody: plugin slot created; waiting for initialization.");
            }

            JSONStorableBool active = null;
            JSONStorableStringChooser presets = null;
            while (Time.unscaledTime < deadline)
            {
                if (target == null)
                {
                    FinishEmbodyRequest(null, null, false, completed,
                        "Embody: target Person disappeared during initialization.");
                    yield break;
                }
                embody = FindEmbody(target);
                active = embody == null ? null : embody.GetBoolJSONParam("Active");
                presets = embody == null ? null : embody.GetStringChooserJSONParam("Presets");
                if (active != null && presets != null && presets.choices != null &&
                    presets.choices.Contains(EmbodyPreset))
                {
                    LogInfo("Embody: Active and Presets parameters are ready.");
                    break;
                }
                yield return null;
            }

            if (active == null || presets == null || presets.choices == null ||
                !presets.choices.Contains(EmbodyPreset))
            {
                FinishEmbodyRequest(target, embody, false, completed,
                    "Embody: latest plugin did not finish loading within 12 seconds.");
                yield break;
            }

            _embodyTarget = target;
            _embody = embody;
            string presetError;
            if (!TrySetEmbodyPreset(embody, out presetError))
            {
                FinishEmbodyRequest(target, embody, false, completed, presetError);
                yield break;
            }
            LogInfo("Embody: Passenger (Free Look) preset applied; waiting one frame.");
            yield return null;

            int attempts = 0;
            int stableFrames = 0;
            bool lastObserved = false;
            bool hasObservation = false;
            float nextAttempt = 0f;
            while (Time.unscaledTime < deadline)
            {
                if (target == null)
                {
                    FinishEmbodyRequest(null, null, false, completed,
                        "Embody: target Person disappeared during activation.");
                    yield break;
                }

                embody = FindEmbody(target);
                active = embody == null ? null : embody.GetBoolJSONParam("Active");
                if (active == null)
                {
                    stableFrames = 0;
                    yield return null;
                    continue;
                }

                bool observed = active.val;
                if (!hasObservation || observed != lastObserved)
                {
                    LogInfo("Embody: Active observed=" + observed + ".");
                    lastObserved = observed;
                    hasObservation = true;
                }
                if (observed)
                {
                    stableFrames++;
                    if (stableFrames >= 2)
                    {
                        _embodyTarget = target;
                        _embody = embody;
                        FinishEmbodyRequest(target, embody, true, completed, null);
                        yield break;
                    }
                }
                else
                {
                    stableFrames = 0;
                    if (Time.unscaledTime >= nextAttempt)
                    {
                        attempts++;
                        string activeError;
                        LogInfo("Embody: Active request #" + attempts + ".");
                        if (!TrySetEmbodyActive(embody, true, out activeError))
                        {
                            FinishEmbodyRequest(target, embody, false, completed, activeError);
                            yield break;
                        }
                        nextAttempt = Time.unscaledTime + 0.2f;
                    }
                }
                yield return null;
            }

            FinishEmbodyRequest(target, embody, false, completed,
                "Embody: Active did not remain enabled within 12 seconds after " +
                attempts + " request(s).");
        }

        private static bool TryCreateEmbodyPlugin(Atom target, out string error)
        {
            try
            {
                MVRPluginManager manager =
                    target.GetStorableByID("PluginManager") as MVRPluginManager;
                if (manager == null)
                    throw new InvalidOperationException("target Person has no PluginManager.");

                // Direct URL assignment does not run JSON restore's latest/min
                // resolution. Resolve through VaM before creating a plugin slot.
                string embodyPath = FileManager.NormalizeLoadPath(EmbodyUrl);
                if (string.IsNullOrEmpty(embodyPath) ||
                    !FileManager.FileExists(embodyPath, false, false))
                    throw new FileNotFoundException(
                        "Embody plugin file is not registered: " + embodyPath);

                // Add only Embody. Rebuilding PluginManager destroys live Timeline state.
                MVRPlugin plugin = manager.CreatePlugin();
                if (plugin == null)
                    throw new InvalidOperationException("Embody plugin slot creation failed.");
                plugin.pluginURLJSON.val = embodyPath;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Embody: plugin load request failed: " + exception;
                return false;
            }
        }

        private static bool TrySetEmbodyPreset(JSONStorable embody, out string error)
        {
            try
            {
                embody.SetStringChooserParamValue("Presets", EmbodyPreset);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Embody: preset selection failed: " + exception;
                return false;
            }
        }

        private static bool TrySetEmbodyActive(
            JSONStorable embody, bool active, out string error)
        {
            try
            {
                embody.SetBoolParamValue("Active", active);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Embody: Active request failed: " + exception;
                return false;
            }
        }

        private void FinishEmbodyRequest(
            Atom target, JSONStorable embody, bool success,
            Action<bool> completed, string error)
        {
            if (success)
            {
                _embodyTarget = target;
                _embody = embody;
                LogInfo("Embody: activation completed and remained active for two frames.");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                LogError(error);
            }
            _embodyBusy = false;
            _embodyActivationCoroutine = null;
            if (completed != null)
                completed(success);
        }

        private static void ConfigureEmbody(JSONStorable embody, bool active)
        {
            if (active)
                embody.SetStringChooserParamValue("Presets", EmbodyPreset);
            embody.SetBoolParamValue("Active", active);
        }

        private IEnumerator OpenPersonPanel(Atom target, string tab)
        {
            ForcePersonEditorOpen(target);

            float deadline = Time.unscaledTime + 2f;
            while (Time.unscaledTime < deadline)
            {
                if (!SuperController.singleton.MainHUDVisible)
                    SuperController.singleton.ShowMainHUD(true, false);

                UITabSelector[] selectors =
                    target.gameObject.GetComponentsInChildren<UITabSelector>(true);
                for (int i = 0; i < selectors.Length; i++)
                {
                    if (!selectors[i].HasTab(tab))
                        continue;
                    selectors[i].SetActiveTab(tab);
                    yield break;
                }
                yield return null;
            }

            LogError(tab + ": the selected Person has no " + tab + " tab.");
        }

        private void ShowAppearancePresetDialog(Atom target, bool keepCurrentClothing)
        {
            JSONStorable appearancePresets = target.GetStorableByID("AppearancePresets");
            if (appearancePresets == null)
            {
                LogError("Appearance preset: target Person has no AppearancePresets manager.");
                return;
            }

            JSONStorableActionPresetFilePath loadAction =
                appearancePresets.GetPresetFilePathAction("LoadPresetWithPath");
            if (loadAction == null)
            {
                LogError("Appearance preset: native .vap browser action is unavailable.");
                return;
            }

            // Browse uses a separate JSONStorableUrl which suppresses callbacks
            // when the same path is selected again. Reset only its selected value,
            // not the remembered directory, before presenting the native browser.
            JSONStorableUrl browserSelection = GetMemberValue(
                loadAction.GetType(), loadAction, "url") as JSONStorableUrl;
            if (browserSelection == null)
            {
                LogError("Appearance preset: native browser selection URL is unavailable.");
                return;
            }
            browserSelection.valNoCallback = string.Empty;

            loadAction.Browse(
                delegate(string path)
                {
                    LoadAppearancePreset(target, path, keepCurrentClothing);
                });
        }

        private void LoadAppearancePreset(Atom target, string path, bool keepCurrentClothing)
        {
            if (target == null || string.IsNullOrEmpty(path))
                return;

            JSONStorable appearancePresets = target.GetStorableByID("AppearancePresets");
            if (appearancePresets == null)
            {
                LogError("Appearance preset: target Person has no AppearancePresets manager.");
                return;
            }

            if (!keepCurrentClothing)
            {
                LoadFullAppearancePreset(target, appearancePresets, path);
                return;
            }

            if (_appearanceLoadBusy)
            {
                LogError("Appearance preset: an appearance-only load is already running.");
                return;
            }

            LoadAppearanceWithoutClothing(target, appearancePresets, path);
        }

        private void LoadFullAppearancePreset(
            Atom target, JSONStorable appearancePresets, string path)
        {
            if (_appearanceLoadBusy)
            {
                LogError("Person preset: an appearance load is already running.");
                return;
            }

            _appearanceLoadBusy = true;
            JSONStorableBool loadOnSelect = null;
            JSONStorableUrl presetPath = null;
            string previousPath = null;
            bool previousLoadOnSelect = false;
            List<MeshVR.PresetManagerControl> controls =
                target.presetManagerControls;
            bool[] previousLocks = controls == null
                ? new bool[0]
                : new bool[controls.Count];

            try
            {
                // A Person load is an exact appearance replay. Temporarily clear
                // every preset lock (especially ClothingPresets), then invoke the
                // explicit LoadPreset action so choosing the same .vap path again
                // still reapplies its clothing instead of being treated as an
                // unchanged presetBrowsePath value.
                for (int i = 0; controls != null && i < controls.Count; i++)
                {
                    if (controls[i] == null)
                        continue;
                    previousLocks[i] = controls[i].lockParams;
                    controls[i].lockParams = false;
                }

                loadOnSelect = appearancePresets.GetBoolJSONParam(
                    "loadPresetOnSelect");
                presetPath = appearancePresets.GetUrlJSONParam(
                    "presetBrowsePath");
                if (presetPath == null)
                    throw new InvalidOperationException(
                        "AppearancePresets.presetBrowsePath is unavailable");

                previousPath = presetPath.val;
                if (loadOnSelect != null)
                {
                    previousLoadOnSelect = loadOnSelect.val;
                    loadOnSelect.val = false;
                }

                presetPath.val = SuperController.singleton.NormalizePath(path);
                appearancePresets.CallAction("LoadPreset");
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Person preset LoadPreset invoked with locks cleared: " + path);
            }
            catch (Exception exception)
            {
                LogError("Person preset replay failed: " + exception);
            }
            finally
            {
                if (presetPath != null && previousPath != null)
                    presetPath.val = previousPath;
                if (loadOnSelect != null)
                    loadOnSelect.val = previousLoadOnSelect;
                for (int i = 0; controls != null && i < controls.Count; i++)
                {
                    if (controls[i] != null)
                        controls[i].lockParams = previousLocks[i];
                }
                _appearanceLoadBusy = false;
            }
        }

        private void LoadAppearanceWithoutClothing(
            Atom target, JSONStorable appearancePresets, string path)
        {
            _appearanceLoadBusy = true;
            MeshVR.PresetManagerControl clothingControl = null;
            bool clothingWasLocked = false;
            try
            {
                List<MeshVR.PresetManagerControl> controls = target.presetManagerControls;
                for (int i = 0; controls != null && i < controls.Count; i++)
                {
                    if (controls[i] != null && controls[i].name == "ClothingPresets")
                    {
                        clothingControl = controls[i];
                        break;
                    }
                }
                if (clothingControl == null)
                    throw new InvalidOperationException(
                        "target Person has no ClothingPresets lock control");

                // UIAssist's “Load Appearance Preset” follows this native path:
                // lock ClothingPresets for one AppearancePresets load pass.
                clothingWasLocked = clothingControl.lockParams;
                clothingControl.lockParams = true;
                appearancePresets.CallPresetFileAction(
                    "LoadPresetWithPath", path);
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Appearance loaded in one native pass with ClothingPresets locked.");
            }
            catch (Exception exception)
            {
                LogError("Appearance-only preset load failed: " + exception);
            }
            finally
            {
                if (clothingControl != null)
                    clothingControl.lockParams = clothingWasLocked;
                _appearanceLoadBusy = false;
            }
        }

        private static void SelectTargetAndShowHUD(Atom target)
        {
            UiAssistHudLink.CancelPending();
            SuperController.singleton.SelectController(
                target.mainController, false, false, true, false);
            SuperController.singleton.ShowMainHUDAuto();
        }

        internal void OpenPersonControlPanel()
        {
            Atom target = FindClosestPerson(false);
            if (target == null)
            {
                LogError("Person panel: no Person atom is in front of the VR view.");
                return;
            }
            ForcePersonEditorOpen(target);
        }

        private static void ForcePersonEditorOpen(Atom target)
        {
            UiAssistHudLink.CancelPending();
            SuperController controller = SuperController.singleton;
            controller.gameMode = SuperController.GameMode.Edit;
            controller.SelectModeOff();
            controller.SelectController(
                target.mainController, false, false, true, true);
            controller.ShowMainHUD(true, false);
        }

        private static void ConfigureUiAssistClothingEditor(
            object editor, Atom target)
        {
            int aceMode = 2;
            bool realClothingFilter = true;
            TryReadUiAssistAceButtonSettings(
                ref aceMode, ref realClothingFilter);

            Type editorType = editor.GetType();
            SetMemberValue(editorType, editor, "aceMode", aceMode);
            SetMemberValue(
                editorType, editor, "aceRealClothingFilterActive",
                realClothingFilter);

            if (aceMode == 1)
            {
                SetMemberValue(editorType, editor, "isGazeSelectedMode", true);
                SetMemberValue(editorType, editor, "gazeSelectedTargetType", 1);
            }
            else
            {
                SetMemberValue(editorType, editor, "isGazeSelectedMode", false);
                InvokeMethod(editorType, editor, "RefreshPersonAtomNames");
                object chooser = GetMemberValue(
                    editorType, editor, "personAtomNamesJSSC");
                if (chooser != null)
                    SetMemberValue(chooser.GetType(), chooser, "val", target.name);
            }

            InvokeMethod(
                editorType, editor, "RefreshClothing", target.name);
        }

        private static bool TryReadUiAssistAceButtonSettings(
            ref int aceMode, ref bool realClothingFilter)
        {
            Type storablesType = FindLoadedType("JayJayWon.UIAStorables");
            if (storablesType == null)
                return false;

            ArrayList grids = new ArrayList();
            IEnumerable configuredGrids = GetMemberValue(
                storablesType, null, "buttonGridsList") as IEnumerable;
            if (configuredGrids != null)
            {
                foreach (object grid in configuredGrids)
                    grids.Add(grid);
            }

            object quickLaunch = GetMemberValue(
                storablesType, null, "quickLaunchButtonGrid");
            if (quickLaunch != null)
                grids.Insert(0, quickLaunch);

            foreach (object grid in grids)
            {
                if (grid == null)
                    continue;
                IEnumerable buttons = GetMemberValue(
                    grid.GetType(), grid, "buttonList") as IEnumerable;
                if (buttons == null)
                    continue;

                foreach (object button in buttons)
                {
                    if (button == null)
                        continue;
                    IEnumerable operations = GetMemberValue(
                        button.GetType(), button, "buttonOperations") as IEnumerable;
                    if (operations == null)
                        continue;

                    foreach (object operation in operations)
                    {
                        if (operation == null)
                            continue;
                        object operationChooser = GetMemberValue(
                            operation.GetType(), operation, "buttonOpTypeJSEnum");
                        if (ReadIntValue(operationChooser, "val", -1) != 23)
                            continue;

                        object targetComponent = GetMemberValue(
                            operation.GetType(), operation, "targetComponent");
                        if (targetComponent == null)
                            return false;
                        object modeChooser = GetMemberValue(
                            targetComponent.GetType(), targetComponent, "aceModeJSEnum");
                        object filter = GetMemberValue(
                            targetComponent.GetType(), targetComponent,
                            "aceRealClothingFilterJSB");
                        aceMode = ReadIntValue(modeChooser, "val", aceMode);
                        realClothingFilter = ReadBoolValue(
                            filter, "val", realClothingFilter);
                        return true;
                    }
                }
            }
            return false;
        }

        private static int ReadIntValue(
            object instance, string name, int fallback)
        {
            if (instance == null)
                return fallback;
            object value = GetMemberValue(instance.GetType(), instance, name);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        private static bool ReadBoolValue(
            object instance, string name, bool fallback)
        {
            if (instance == null)
                return fallback;
            object value = GetMemberValue(instance.GetType(), instance, name);
            return value == null ? fallback : Convert.ToBoolean(value);
        }

        private static Type FindLoadedType(string fullName)
        {
            return AssemblyCatalog.FindType(fullName);
        }

        private static object GetMemberValue(
            Type type, object instance, string name)
        {
            const BindingFlags flags = BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null)
                return property.GetValue(instance, null);

            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(instance);
            }
            return null;
        }

        private static void SetMemberValue(
            Type type, object instance, string name, object value)
        {
            const BindingFlags flags = BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null)
            {
                property.SetValue(instance, value, null);
                return;
            }

            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    name, flags | BindingFlags.DeclaredOnly);
                if (field == null)
                    continue;
                field.SetValue(instance, value);
                return;
            }
            throw new MissingMemberException(type.FullName, name);
        }

        private static object InvokeMethod(
            Type type, object instance, string name, params object[] arguments)
        {
            const BindingFlags flags = BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static;
            MethodInfo method = type.GetMethod(
                name, flags, null,
                Array.ConvertAll(arguments, delegate(object argument) {
                    return argument.GetType();
                }), null);
            if (method == null)
                throw new MissingMethodException(type.FullName, name);
            return method.Invoke(instance, arguments);
        }

        private static JSONStorable FindEmbody(Atom atom)
        {
            List<string> ids = atom.GetStorableIDs();
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i].IndexOf("_Embody", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                JSONStorable storable = atom.GetStorableByID(ids[i]);
                if (storable != null && storable.GetBoolJSONParam("Active") != null)
                    return storable;
            }
            return null;
        }

        private static string FindFreePluginSlot(JSONClass plugins)
        {
            HashSet<string> used = new HashSet<string>();
            foreach (string key in plugins.Keys)
                used.Add(key);

            for (int i = 0; i < 1000; i++)
            {
                string candidate = "plugin#" + i;
                if (!used.Contains(candidate))
                    return candidate;
            }
            return "plugin#1000";
        }

        internal static Atom FindClosestFemale() { return FindClosestPerson(PersonGenderFilter.Female); }

        internal static Atom FindClosestPerson(bool maleOnly)
        {
            return FindClosestPerson(maleOnly
                ? PersonGenderFilter.Male
                : PersonGenderFilter.Any);
        }

        private static Atom FindClosestPerson(PersonGenderFilter genderFilter)
        {
            if (SuperController.singleton == null || SuperController.singleton.lookCamera == null)
                return null;

            Transform view = SuperController.singleton.lookCamera.transform;
            Atom best = null;
            float bestScore = float.MaxValue;
            List<Atom> atoms = SuperController.singleton.GetAtoms();
            for (int i = 0; i < atoms.Count; i++)
            {
                Atom atom = atoms[i];
                if (atom == null || !atom.on || atom.type != "Person")
                    continue;
                if (genderFilter == PersonGenderFilter.Male && !IsGender(atom, "Male"))
                    continue;
                if (genderFilter == PersonGenderFilter.Female && !IsGender(atom, "Female"))
                    continue;

                Transform point = TargetPoint(atom);
                Vector3 offset = point.position - view.position;
                float distance = offset.magnitude;
                if (distance <= 0.001f)
                    continue;
                float forward = Vector3.Dot(view.forward, offset / distance);
                if (forward <= 0f)
                    continue;

                float score = (1f - forward) + distance * 0.0001f;
                if (score >= bestScore)
                    continue;
                bestScore = score;
                best = atom;
            }
            return best;
        }

        private static Transform TargetPoint(Atom atom)
        {
            FreeControllerV3 head = atom.GetStorableByID("headControl") as FreeControllerV3;
            if (head != null)
                return head.transform;
            return atom.mainController.transform;
        }

        private static bool IsGender(Atom atom, string gender)
        {
            DAZCharacterSelector geometry =
                atom.GetStorableByID("geometry") as DAZCharacterSelector;
            return geometry != null &&
                   string.Equals(geometry.gender.ToString(), gender,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void LogError(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError(message);
        }

        private static void LogInfo(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(message);
        }
    }

    // VaM-relative preset folder roots, bound to the [Paths] config section
    // so players whose content lives elsewhere can redirect the browsers.
    internal static class PluginPaths
    {
        internal static string HairPresetDir = "Custom/Atom/Person/Hair";
        internal static string ClothingPresetDir = "Custom/Atom/Person/Clothing";
        internal static string AppearancePresetDir = "Custom/Atom/Person/Appearance";
        internal static string SkinPresetDir = "Custom/Atom/Person/Skin";
        internal static string LightLinkerUrl = "Custom/Scripts/LightLinker/LightLinker.cslist";
    }

    // Last-used save folder per preset type, persisted under BepInEx/config so
    // the next save dialog opens where the user last saved. Keys are storable
    // ids (AppearancePresets/SkinPresets/HairPresets/ClothingPresets).
    internal static class PresetSaveDirs
    {
        private static Dictionary<string, string> _dirs;
        private static string FilePath
        {
            get
            {
                return Path.Combine(BepInEx.Paths.ConfigPath,
                    "Quest3TriggerUI.preset-save-dirs.txt");
            }
        }
        internal static string Get(string key, string fallback)
        {
            EnsureLoaded();
            string dir;
            return _dirs.TryGetValue(key, out dir) &&
                   !string.IsNullOrEmpty(dir) ? dir : fallback;
        }
        internal static void Set(string key, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            EnsureLoaded();
            _dirs[key] = dir;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in _dirs)
                    lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }
        private static void EnsureLoaded()
        {
            if (_dirs != null) return;
            _dirs = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) _dirs[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
        }
    }
}





