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

        // Called by the scene-load accelerator: a load under the standby frame
        // limiter and paused simulation would stall, so standby is dropped
        // first. Returns false when it was not active.
        internal bool RestoreStandbyForSceneLoad()
        {
            if (!_standby.Active) return false;
            _standby.RestoreImmediately();
            return true;
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

        internal void OpenClothingOnlyPreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Clothing preset: no female Person atom is in front of the VR view.");
                return;
            }

            SelectTargetAndShowHUD(target);
            ShowAppearancePresetDialog(target, false, true);
        }

        internal void OpenStoreClothingIntoPreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Clothing preset: no female Person atom is in front of the VR view.");
                return;
            }

            SelectTargetAndShowHUD(target);
            ShowAppearancePresetDialog(target, false, false, true);
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
            VrPresetBrowser.ShowDialogFull("加载皮肤预设", PluginPaths.SkinPresetDir,
                "vap", false, null,
                delegate(string path, bool didClose) {
                    Atom t = VrPresetBrowser.LoadTarget ?? target;
                    if (t != null && !string.IsNullOrEmpty(path))
                        LoadSkinPreset(t, path);
                }, loadTarget: target);
        }

        // Same unified model as hair: a pure skin .vap IS a skin-only preset,
        // so the whitelist extraction passes it through unchanged while a
        // person preset is filtered down to its skin storables.
        internal void LoadSkinPreset(Atom target, string path)
        {
            LoadExtractedPreset(target, path, "SkinPresets", "Skin preset",
                ExtractSkinPreset);
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
            // Start in the hair preset library — the unified loader also
            // accepts a person preset's hair section if the user browses
            // over to Appearance, but the default view stays hair-focused.
            VrPresetBrowser.ShowDialogFull("加载头发预设", PluginPaths.HairPresetDir,
                "vap", false, null,
                delegate(string path, bool didClose) {
                    Atom t = VrPresetBrowser.LoadTarget ?? target;
                    if (t != null && !string.IsNullOrEmpty(path))
                        LoadHairPreset(t, path);
                }, loadTarget: target);
        }

        // Unified hair load: a pure hair .vap IS a hair-only section, so the
        // same extraction that pulls hair out of a person preset passes a
        // hair preset through unchanged. No file-type dispatch needed.
        private void LoadHairPreset(Atom target, string path)
        {
            LoadExtractedPreset(target, path, "HairPresets", "Hair preset",
                delegate(JSONClass source) {
                    return ExtractSectionPreset(source, "hair");
                }, true);
        }

        // Shared spine of the section loads: read the picked .vap, shrink it
        // to one section's storables, then feed that JSON straight into the
        // section's own PresetManager — presetBrowsePath points at the source
        // file first so relative/package refs inside the section resolve.
        internal void LoadExtractedPreset(Atom target, string path,
            string managerId, string label,
            Func<JSONClass, JSONClass> extract, bool nativeFile = false)
        {
            if (_appearanceLoadBusy)
            {
                LogError(label + ": a preset load is already running.");
                return;
            }
            _appearanceLoadBusy = true;
            MeshVR.PresetManagerControl ctl = null;
            JSONStorableUrl url = null;
            JSONStorableBool auto = null;
            bool oldAuto = false, oldLock = false;
            string oldPath = null;
            string tempPath = null;
            try
            {
                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException(
                        "preset file is unreadable: " + path);
                JSONClass extracted = extract(JSON.Parse(text).AsObject);
                if (extracted == null)
                    throw new InvalidOperationException(
                        "preset has no matching section: " + path);

                ctl = target.GetStorableByID(managerId)
                    as MeshVR.PresetManagerControl;
                if (ctl == null)
                    throw new InvalidOperationException(
                        "target Person has no " + managerId + " manager.");
                MeshVR.PresetManager pm = PresetManagerOf(ctl);
                url = ctl.GetUrlJSONParam("presetBrowsePath");
                auto = ctl.GetBoolJSONParam("loadPresetOnSelect");
                if (pm == null || url == null || auto == null)
                    throw new InvalidOperationException(
                        managerId + " preset parameters are unavailable.");

                oldAuto = auto.val;
                oldLock = ctl.lockParams;
                oldPath = url.val;
                auto.val = false;
                ctl.lockParams = false;
                if (nativeFile)
                {
                    // Sections that stream asset bundles (hair) misbehave
                    // when injected via LoadPresetFromJSON — the first apply
                    // lands before the bundle is ready. Routing the same
                    // JSON through a temp .vap and the manager's native
                    // path-load makes VaM's own async pipeline handle it,
                    // exactly like picking the file in the stock UI.
                    tempPath = TempSectionPresetPath(pm);
                    File.WriteAllText(tempPath, extracted.ToString(),
                        new System.Text.UTF8Encoding(false));
                    if (string.IsNullOrEmpty(pm.GetPresetNameFromFilePath(tempPath)))
                        throw new InvalidOperationException("native preset path rejected: " + tempPath);
                    if (ctl.GetPresetFilePathAction("LoadPresetWithPath")
                        != null)
                        ctl.CallPresetFileAction(
                            "LoadPresetWithPath", tempPath);
                    else
                    {
                        url.val = SuperController.singleton.NormalizePath(
                            tempPath);
                        ctl.CallAction("LoadPreset");
                    }
                }
                else
                {
                    url.val = SuperController.singleton.NormalizePath(path);
                    pm.LoadPresetFromJSON(extracted, false);
                }
                LogInfo(label + " load dispatched: " + path);
            }
            catch (Exception exception)
            {
                LogError(label + " load failed: " + exception);
            }
            finally
            {
                if (url != null) url.val = oldPath;
                if (auto != null) auto.val = oldAuto;
                if (ctl != null) ctl.lockParams = oldLock;
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                _appearanceLoadBusy = false;
            }
        }

        // Native managers validate both their store directory and filename
        // prefix. A person-preset directory is not valid for HairPresets.
        private static string TempSectionPresetPath(MeshVR.PresetManager pm)
        {
            string dir = pm.GetStoreFolderPath(false);
            dir = dir.Replace('\\', '/').TrimEnd('/');
            Directory.CreateDirectory(dir);
            return dir + "/" + pm.storeName + "_q3tmp_" +
                Guid.NewGuid().ToString("N") + ".vap";
        }

        internal void SaveAppearancePreset()
        {
            SavePersonPresetDialog("AppearancePresets",
                PluginPaths.AppearancePresetDir, "外观");
        }

        internal void SaveSkinPreset()
        {
            SaveSkinPresetFor(null, false, null);
        }

        // Dock-context save: explicit editor atom, compact browser, and an
        // onDone(didClose) so the caller can restore its panel afterwards.
        internal void SaveSkinPresetFor(Atom target, bool small,
            Action<bool> onDone)
        {
            SavePersonPresetDialog("SkinPresets",
                PluginPaths.SkinPresetDir, "皮肤",
                SkinStoreIntercept, target, small, onDone);
        }

        private bool SkinStoreIntercept(Atom target, string path)
        {
            if (FileManager.IsPackagePath(path))
            {
                LogError("皮肤 save: presets inside VAR packages cannot be rewritten.");
                return true;
            }
            if (!File.Exists(path))
                return false; // new name → native store
            int kind = ClassifyPresetFile(path, "character");
            if (kind == 2)
            {
                // Person preset: merge the live skin storables into it.
                StoreSkinIntoPreset(target, path);
                return true;
            }
            if (kind == 0)
            {
                LogError("皮肤 save: selected file is neither a person nor a skin preset: " + path);
                return true;
            }
            return false; // pure skin preset → native store (+thumbnail)
        }

        internal void OpenEyePreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Eye preset: no female Person atom is in front of the VR view.");
                return;
            }
            SelectTargetAndShowHUD(target);
            VrPresetBrowser.ShowDialogFull("加载眼睛预设", PluginPaths.EyePresetDir,
                "vap", false, null,
                delegate(string path, bool didClose) {
                    Atom t = VrPresetBrowser.LoadTarget ?? target;
                    if (t != null && !string.IsNullOrEmpty(path))
                        LoadEyePreset(t, path);
                }, loadTarget: target);
        }

        // Direct per-storable restore: AppearancePresets.LoadPresetFromJSON
        // would run the whole person-scale pipeline (defaults preload,
        // dynamic storable refresh, 4-phase sweep over ~110 storables) just
        // to change 3 material storables. Mirroring the manager's own
        // storable-level sequence keeps the restore semantics identical at
        // a fraction of the cost.
        private void LoadEyePreset(Atom target, string path)
        {
            if (_appearanceLoadBusy)
            {
                LogError("Eye preset: a preset load is already running.");
                return;
            }
            _appearanceLoadBusy = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException(
                        "preset file is unreadable: " + path);
                JSONClass eyeJson =
                    ExtractEyePreset(JSON.Parse(text).AsObject);
                if (eyeJson == null)
                    throw new InvalidOperationException(
                        "preset has no eye section: " + path);

                JSONArray storables = eyeJson["storables"].AsArray;
                int applied = 0;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass sub = storables[i].AsObject;
                    if (sub == null) continue;
                    string id = sub["id"].Value;
                    JSONStorable storable = target.GetStorableByID(id);
                    if (storable == null) continue;
                    try
                    {
                        RestoreEyeStorable(storable, sub);
                        applied++;
                    }
                    catch (Exception storableEx)
                    {
                        LogError("Exception during eye Restore of " + id +
                            ": " + storableEx);
                    }
                }
                if (applied == 0)
                    throw new InvalidOperationException(
                        "target Person exposes no eye storables.");
                LogInfo("Eye preset applied (" + applied + " storables, " +
                    sw.ElapsedMilliseconds + "ms): " + path);
            }
            catch (Exception exception)
            {
                LogError("Eye preset load failed: " + exception);
            }
            finally
            {
                _appearanceLoadBusy = false;
            }
        }

        // Mirrors PresetManager's per-storable Pre/Restore/Late/Post calls.
        // includePhysical+includeAppearance=true matches an appearance-scale
        // restore; setUnlisted=false since we only carry the stored params.
        private static void RestoreEyeStorable(
            JSONStorable storable, JSONClass json)
        {
            storable.isPresetRestore = true;
            storable.mergeRestore = false;
            try
            {
                storable.PreRestore();
                storable.PreRestore(true, true);
                storable.RestoreFromJSON(json, true, true, null, false);
                storable.LateRestoreFromJSON(json, true, true, false);
                storable.PostRestore();
                storable.PostRestore(true, true);
            }
            finally
            {
                storable.mergeRestore = false;
                storable.isPresetRestore = false;
            }
        }

        internal void SaveEyePreset()
        {
            Atom target = FindClosestPerson(PersonGenderFilter.Female);
            if (target == null)
            {
                LogError("Eye preset: no female Person atom is in front of the VR view.");
                return;
            }
            SelectTargetAndShowHUD(target);
            Directory.CreateDirectory(PluginPaths.EyePresetDir);
            string startDir = PresetSaveDirs.Get("EyePresets",
                PluginPaths.EyePresetDir);
            string defaultName = "Preset_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".vap";
            VrPresetBrowser.ShowDialogFull("保存眼睛预设", startDir,
                "vap", true, defaultName,
                delegate(string path, bool didClose) {
                    if (string.IsNullOrEmpty(path)) return;
                    if (FileManager.IsPackagePath(path))
                    {
                        LogError("Eye preset: presets inside VAR packages cannot be rewritten.");
                        return;
                    }
                    if (!File.Exists(path))
                    {
                        StoreEyePresetFile(target, path);
                        return;
                    }
                    int kind = ClassifyPresetFile(path, "character");
                    if (kind == 2)
                    {
                        // Person preset: merge the live eye storables into it.
                        StoreStorableSetIntoPreset(target, path,
                            EyeStorableIds, false, "eye");
                        return;
                    }
                    if (FileContainsStorable(path, "irises"))
                    {
                        // Existing eye preset: overwrite + fresh thumbnail.
                        StoreEyePresetFile(target, path);
                        return;
                    }
                    LogError("Eye preset: selected file is neither a person nor an eye preset: " + path);
                });
        }

        private static bool FileContainsStorable(string path, string id)
        {
            try
            {
                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text)) return false;
                JSONArray storables =
                    JSON.Parse(text).AsObject["storables"].AsArray;
                if (storables == null) return false;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable != null && storable["id"].Value == id)
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Write a standalone eye preset (.vap + .jpg). The thumbnail goes
        // through SuperController.DoSaveScreenshot — the same lo-res capture
        // native StorePreset uses.
        private void StoreEyePresetFile(Atom target, string path)
        {
            if (_appearanceLoadBusy)
            {
                LogError("Eye preset: a preset load is already running.");
                return;
            }
            _appearanceLoadBusy = true;
            string tempPath = null;
            try
            {
                string normalized = SuperController.singleton.NormalizePath(path);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(normalized));
                string fileName = Path.GetFileName(normalized);
                if (!fileName.StartsWith("Preset_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    normalized = Path.Combine(
                        Path.GetDirectoryName(normalized),
                        "Preset_" + fileName);
                }

                var root = new JSONClass();
                // See ExtractEyePreset: false keeps unlisted storables intact
                // when this file is fed to AppearancePresets.
                root["setUnlistedParamsToDefault"] = new JSONData(false);
                var outStorables = new JSONArray();
                for (int i = 0; i < EyeStorableIds.Length; i++)
                {
                    string id = EyeStorableIds[i];
                    JSONStorable storable = target.GetStorableByID(id);
                    JSONClass sub = storable == null
                        ? null
                        : storable.GetJSON(true, true, true);
                    if (sub == null) continue;
                    sub["id"] = new JSONData(id);
                    outStorables.Add(sub);
                }
                if (outStorables.Count == 0)
                    throw new InvalidOperationException(
                        "target Person exposes no eye storables.");
                root["storables"] = outStorables;

                tempPath = normalized + ".q3tmp";
                File.WriteAllText(tempPath, root.ToString(),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(normalized))
                    File.Replace(tempPath, normalized, null);
                else
                    File.Move(tempPath, normalized);
                tempPath = null;

                string jpg = normalized.Substring(0,
                    normalized.Length - ".vap".Length) + ".jpg";
                try
                {
                    SuperController.singleton.DoSaveScreenshot(jpg,
                        new SuperController.ScreenShotCallback(
                            delegate(string s) { }));
                }
                catch (Exception shotEx)
                {
                    LogError("Eye preset thumbnail failed (file saved): " +
                        shotEx.Message);
                }
                PresetSaveDirs.Set("EyePresets",
                    Path.GetDirectoryName(normalized));
                LogInfo("Eye preset saved: " + normalized);
            }
            catch (Exception exception)
            {
                LogError("Eye preset save failed: " + exception);
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                _appearanceLoadBusy = false;
            }
        }

        internal void SaveHairPreset()
        {
            SaveHairPresetFor(null, false, null);
        }

        internal void SaveHairPresetFor(Atom target, bool small,
            Action<bool> onDone)
        {
            SavePersonPresetDialog("HairPresets",
                PluginPaths.HairPresetDir, "头发",
                HairStoreIntercept, target, small, onDone);
        }

        private bool HairStoreIntercept(Atom target, string path)
        {
            if (FileManager.IsPackagePath(path))
            {
                LogError("头发 save: presets inside VAR packages cannot be rewritten.");
                return true;
            }
            if (!File.Exists(path))
                return false; // new name → native store
            int kind = ClassifyPresetFile(path, "hair");
            if (kind == 2)
            {
                // Person preset: merge the live hair section into it.
                StoreSectionIntoPreset(target, path, "hair");
                return true;
            }
            if (kind == 0)
            {
                LogError("头发 save: selected file is neither a person nor a hair preset: " + path);
                return true;
            }
            return false; // pure hair preset → native store (+thumbnail)
        }

        // Mirrors UIAssist's save-mode file pick: the SAME media browser is
        // opened, then switched to text-entry save mode — fileEntryField is
        // shown and pre-filled with a unique default name. Picking an
        // existing file = overwrite; the typed name = new file.
        // Deliberately NOT SelectTargetAndShowHUD: the atom control panel's
        // own preset list is bound to loadPresetOnSelect, and clicking a
        // file there performs a load while our save dialog is open.
        private void SavePersonPresetDialog(
            string storableId, string suggestedDir, string label,
            Func<Atom, string, bool> interceptPath = null,
            Atom targetOverride = null, bool small = false,
            Action<bool> onDone = null)
        {
            Atom target = targetOverride != null
                ? targetOverride
                : FindClosestPerson(PersonGenderFilter.Female);
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
                    try
                    {
                        // The header atom dropdown may have re-pointed the
                        // save at a different Person — resolve its manager.
                        Atom eff = VrPresetBrowser.LoadTarget ?? target;
                        if (eff == null)
                            return;
                        MeshVR.PresetManagerControl effPresets =
                            eff == target ? presets
                            : eff.GetStorableByID(storableId)
                                as MeshVR.PresetManagerControl;
                        if (effPresets == null)
                        {
                            LogError(label + " preset save: " + storableId +
                                " is unavailable on " + eff.uid + ".");
                            return;
                        }
                        LogInfo(label + " save dialog returned path=" +
                            (path ?? "<null>"));
                        if (string.IsNullOrEmpty(path))
                            return;
                        if (interceptPath != null &&
                            interceptPath(eff, path))
                            return;
                        StorePresetToPath(effPresets, path, storableId, label);
                    }
                    finally { if (onDone != null) onDone(didClose); }
                }, compact: small, loadTarget: target);
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
                // StorePreset (not *WithScreenshot): the aim-and-select
                // screenshot pass ate its first capture as a skip. We write
                // the sidecar jpg ourselves from the already-rendered eye
                // frame — instant, no mode flip, no lost first shot.
                presets.CallAction("StorePreset");
                PresetThumbCapture.Queue(path, null);
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

        // ---- preset-dock 保存 entries: the dock's editor atom is the
        // target (not the closest person), the browser is the compact
        // dock-adjacent variant, and onDone(didClose) lets the dock restore
        // the editor UI when the dialog closes. ----

        // Dock save-mode overwrite: store the editor atom straight onto the
        // .vap the clicked cell references. Same rules as the save dialog —
        // existing person .vap merges via the per-tab intercepts, pure-type
        // files go through the native store. VAR-embedded presets refuse.
        internal bool DockStorePreset(Atom target, int tab, string path)
        {
            if (target == null || string.IsNullOrEmpty(path)) return false;
            if (FileManager.IsPackagePath(path))
            {
                LogError("preset overwrite: VAR-embedded presets cannot be rewritten.");
                return false;
            }
            string storableId;
            string label;
            Func<Atom, string, bool> intercept = null;
            switch (tab)
            {
                case 1:
                    storableId = "HairPresets"; label = "发型";
                    intercept = HairStoreIntercept;
                    break;
                case 2:
                    storableId = "ClothingPresets"; label = "服装";
                    intercept = ClothingStoreIntercept;
                    break;
                case 3:
                    storableId = "SkinPresets"; label = "皮肤";
                    intercept = SkinStoreIntercept;
                    break;
                case 4:
                    StoreMakeupPresetFile(target, path);
                    return true;
                default:
                    storableId = "AppearancePresets"; label = "人物";
                    break;
            }
            if (intercept != null && intercept(target, path))
                return true;
            MeshVR.PresetManagerControl presets =
                target.GetStorableByID(storableId) as MeshVR.PresetManagerControl;
            if (presets == null)
            {
                LogError(label + " preset save: " + storableId +
                    " is unavailable on " + target.uid + ".");
                return false;
            }
            StorePresetToPath(presets, path, storableId, label);
            return true;
        }

        internal void SavePersonPresetFor(Atom target, string label,
            bool small, Action<bool> onDone)
        {
            SavePersonPresetDialog("AppearancePresets",
                PluginPaths.AppearancePresetDir, label,
                null, target, small, onDone);
        }

        // 服装 tab save — mirror of SaveHairPresetFor: a real save dialog in
        // the clothing preset dir. New name → native ClothingPresets store
        // (standalone clothing preset + thumbnail); existing person .vap →
        // merge the live clothing section into it.
        internal void SaveClothingPresetFor(Atom target, bool small,
            Action<bool> onDone)
        {
            SavePersonPresetDialog("ClothingPresets",
                PluginPaths.ClothingPresetDir, "服装",
                ClothingStoreIntercept, target, small, onDone);
        }

        private bool ClothingStoreIntercept(Atom target, string path)
        {
            if (FileManager.IsPackagePath(path))
            {
                LogError("服装 save: presets inside VAR packages cannot be rewritten.");
                return true;
            }
            if (!File.Exists(path))
                return false; // new name → native store
            int kind = ClassifyPresetFile(path, "clothing");
            if (kind == 2)
            {
                // Person preset: merge the live clothing section into it.
                StoreSectionIntoPreset(target, path, "clothing");
                return true;
            }
            if (kind == 0)
            {
                LogError("服装 save: selected file is neither a person nor a clothing preset: " + path);
                return true;
            }
            return false; // pure clothing preset → native store (+thumbnail)
        }

        // Person-menu 服装→保存: pick an existing person .vap and merge the
        // live clothing section into it (no standalone file is created).
        internal void SaveClothingIntoPresetFor(Atom target, bool small,
            Action<bool> onDone)
        {
            if (target == null) return;
            SuperController sc = SuperController.singleton;
            if (sc != null) sc.ShowMainHUDAuto();
            VrPresetBrowser.ShowDialogFull("选择写入服装的人物预设",
                PluginPaths.AppearancePresetDir, "vap", false, null,
                delegate(string path, bool didClose) {
                    try
                    {
                        Atom eff = VrPresetBrowser.LoadTarget ?? target;
                        if (eff == null || string.IsNullOrEmpty(path))
                            return;
                        StoreSectionIntoPreset(eff, path, "clothing");
                    }
                    finally { if (onDone != null) onDone(didClose); }
                }, compact: small, loadTarget: target);
        }

        // 化妆 tab save — a standalone clothing preset holding ONLY the
        // worn items the makeup classifier recognises (eye shadow /
        // highlight, blush, lips…). Same file shape as a native clothing
        // preset so the dock loads it back through the merge path.
        internal void SaveMakeupPresetFor(Atom target, bool small,
            Action<bool> onDone)
        {
            if (target == null) return;
            SuperController sc = SuperController.singleton;
            if (sc != null) sc.ShowMainHUDAuto();
            string makeupDir = PluginPaths.ClothingPresetDir + "/化妆";
            try { Directory.CreateDirectory(
                SuperController.singleton.NormalizePath(makeupDir)); }
            catch { }
            string startDir = PresetSaveDirs.Get("MakeupPresets", makeupDir);
            string defaultName = "Preset_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".vap";
            VrPresetBrowser.ShowDialogFull("保存化妆预设", startDir,
                "vap", true, defaultName,
                delegate(string path, bool didClose) {
                    try
                    {
                        Atom eff = VrPresetBrowser.LoadTarget ?? target;
                        if (eff == null || string.IsNullOrEmpty(path))
                            return;
                        if (FileManager.IsPackagePath(path))
                        {
                            LogError("化妆 save: presets inside VAR packages cannot be rewritten.");
                            return;
                        }
                        StoreMakeupPresetFile(eff, path);
                    }
                    finally { if (onDone != null) onDone(didClose); }
                }, compact: small, loadTarget: target);
        }

        private void StoreMakeupPresetFile(Atom target, string path)
        {
            if (_appearanceLoadBusy)
            {
                LogError("化妆 preset: a preset load is already running.");
                return;
            }
            _appearanceLoadBusy = true;
            string tempPath = null;
            try
            {
                JSONStorable geometryStorable =
                    target.GetStorableByID("geometry");
                DAZCharacterSelector selector =
                    geometryStorable as DAZCharacterSelector;
                JSONClass geometryJson = geometryStorable == null
                    ? null
                    : geometryStorable.GetJSON(true, true, true);
                JSONArray liveList = geometryJson == null
                    ? null
                    : geometryJson["clothing"].AsArray;
                if (selector == null || selector.clothingItems == null ||
                    liveList == null)
                    throw new InvalidOperationException(
                        "target Person exposes no clothing list.");

                var makeupKeys = new HashSet<string>();
                for (int i = 0; i < selector.clothingItems.Length; i++)
                {
                    DAZClothingItem ci = selector.clothingItems[i];
                    if (ci == null || !ci.active || !IsMakeupClothing(ci))
                        continue;
                    if (!string.IsNullOrEmpty(ci.uid))
                        makeupKeys.Add(ci.uid);
                    if (!string.IsNullOrEmpty(ci.internalUid))
                        makeupKeys.Add(ci.internalUid);
                    if (!string.IsNullOrEmpty(ci.packageUid))
                        makeupKeys.Add(ci.packageUid);
                }
                if (makeupKeys.Count == 0)
                    throw new InvalidOperationException(
                        "没有在穿的化妆件（眼影/高光/腮红等）可存。");

                var makeupList = new JSONArray();
                var itemIds = new List<string>();
                for (int i = 0; i < liveList.Count; i++)
                {
                    JSONClass item = liveList[i].AsObject;
                    if (item == null) continue;
                    string key = MakeupItemKey(item);
                    if (string.IsNullOrEmpty(key) ||
                        !makeupKeys.Contains(key))
                        continue;
                    makeupList.Add(item);
                    if (itemIds.IndexOf(key) < 0) itemIds.Add(key);
                }
                if (makeupList.Count == 0)
                    throw new InvalidOperationException(
                        "在穿清单里没有匹配到化妆件。");

                var root = new JSONClass();
                root["setUnlistedParamsToDefault"] = new JSONData(true);
                var geometry = new JSONClass();
                geometry["id"] = new JSONData("geometry");
                geometry["clothing"] = makeupList;
                var outStorables = new JSONArray();
                outStorables.Add(geometry);
                List<string> storableIds = target.GetStorableIDs();
                for (int i = 0; storableIds != null && i < storableIds.Count; i++)
                {
                    string id = storableIds[i];
                    if (!IsItemStorable(id, itemIds)) continue;
                    JSONStorable storable = target.GetStorableByID(id);
                    JSONClass sub = storable == null
                        ? null
                        : storable.GetJSON(true, true, true);
                    if (sub == null) continue;
                    sub["id"] = new JSONData(id);
                    outStorables.Add(sub);
                }
                root["storables"] = outStorables;

                string normalized =
                    SuperController.singleton.NormalizePath(path);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(normalized));
                string fileName = Path.GetFileName(normalized);
                if (!fileName.StartsWith("Preset_",
                        StringComparison.OrdinalIgnoreCase))
                    normalized = Path.Combine(
                        Path.GetDirectoryName(normalized),
                        "Preset_" + fileName);

                tempPath = normalized + ".q3tmp";
                File.WriteAllText(tempPath, root.ToString(),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(normalized))
                    File.Replace(tempPath, normalized, null);
                else
                    File.Move(tempPath, normalized);
                tempPath = null;

                string jpg = normalized.Substring(0,
                    normalized.Length - ".vap".Length) + ".jpg";
                try
                {
                    SuperController.singleton.DoSaveScreenshot(jpg,
                        new SuperController.ScreenShotCallback(
                            delegate(string s) { }));
                }
                catch (Exception shotEx)
                {
                    LogError("化妆 preset thumbnail failed (file saved): " +
                        shotEx.Message);
                }
                PresetSaveDirs.Set("MakeupPresets",
                    Path.GetDirectoryName(normalized));
                LogInfo("化妆 preset saved (" + makeupList.Count +
                    " items): " + normalized);
            }
            catch (Exception exception)
            {
                LogError("化妆 preset save failed: " + exception);
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                _appearanceLoadBusy = false;
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

        private void ShowAppearancePresetDialog(Atom target,
            bool keepCurrentClothing, bool clothingOnly = false,
            bool storeClothing = false)
        {
            JSONStorable appearancePresets = target.GetStorableByID("AppearancePresets");
            if (appearancePresets == null)
            {
                LogError("Appearance preset: target Person has no AppearancePresets manager.");
                return;
            }

            // Open our browser directly (instead of loadAction.Browse →
            // FileBrowser takeover) so the header target dropdown knows
            // this dialog's person and can retarget the load.
            string title = storeClothing ? "选择写入服装的人物预设"
                : clothingOnly ? "加载服装预设"
                : keepCurrentClothing ? "加载外观预设" : "加载人物预设";
            string dir = clothingOnly
                ? PluginPaths.ClothingPresetDir
                : PluginPaths.AppearancePresetDir;
            VrPresetBrowser.ShowDialogFull(title, dir, "vap", false, null,
                delegate(string path, bool didClose)
                {
                    if (string.IsNullOrEmpty(path)) return;
                    if (storeClothing)
                    {
                        StoreSectionIntoPreset(target, path, "clothing");
                        return;
                    }
                    LoadAppearancePreset(
                        VrPresetBrowser.LoadTarget ?? target, path,
                        keepCurrentClothing, clothingOnly);
                },
                loadTarget: storeClothing ? null : target);
        }

        private void LoadAppearancePreset(Atom target, string path,
            bool keepCurrentClothing, bool clothingOnly = false)
        {
            if (target == null || string.IsNullOrEmpty(path))
                return;

            JSONStorable appearancePresets = target.GetStorableByID("AppearancePresets");
            if (appearancePresets == null)
            {
                LogError("Appearance preset: target Person has no AppearancePresets manager.");
                return;
            }

            if (clothingOnly)
            {
                if (_appearanceLoadBusy)
                {
                    LogError("Clothing preset: a preset load is already running.");
                    return;
                }
                LoadClothingOnlyPreset(target, path);
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

        // Clothing presets live inside the person .vap itself: the
        // "geometry" storable's clothing list plus one storable set per worn
        // item. Extract exactly those into an in-memory clothing preset and
        // feed it to the native ClothingPresets manager — morphs, hair, skin
        // and physics are never part of the replay, so nothing else can move.
        private void LoadClothingOnlyPreset(Atom target, string path)
        {
            LoadExtractedPreset(target, path, "ClothingPresets",
                "Clothing preset",
                delegate(JSONClass source) {
                    return ExtractSectionPreset(source, "clothing");
                });
        }

        // Makeup-item detection for worn garments. Evidence: .vam tags are
        // unreliable (VAM_GS ships none, Toussaint/paledriver use
        // eye/eyes/reflection), isRealClothingItem is always true, and
        // exclusiveRegion is always None on this content. What reliably
        // holds: eye-area tags where present, and name tokens —
        // "StartsWith(eye)" alone covers eye/eyes/eyeball/eyeshadow/
        // eyelash/eyelid/eyebrow/eyeliner/EYE2.
        private static readonly string[] MakeupTagTerms = {
            "eye", "eyes", "iris", "pupil", "eyelash", "eyelashes",
            "eyebrow", "eyebrows", "eyelid", "lid", "lids", "eyeshadow",
            "eyeshade", "eyemakeup", "eyeliner", "eyeline", "reflection",
            "reflections", "blush", "blusher", "lip", "lips", "lipgloss",
            "lipstick", "makeup", "mascara", "facepaint", "眼影", "睫毛",
            "眉", "唇", "腮红", "高光", "妆", "眼线"
        };
        private static readonly string[] MakeupNameTokens = {
            "iris", "pupil", "lash", "lashes", "eyaball", "lid", "lids",
            "brow", "brows", "lip", "lips", "lipgloss", "lipstick",
            "blush", "blusher", "blushers", "makeup", "mascara",
            "facepaint", "facepainting"
        };
        private static readonly string[] MakeupNameCjk = {
            "眼影", "睫毛", "眼线", "眉", "唇", "腮红", "高光", "妆"
        };
        private static bool IsMakeupClothing(DAZClothingItem item)
        {
            if (item == null) return false;
            string[] tags = item.tagsArray;
            if (tags != null)
                for (int i = 0; i < tags.Length; i++)
                {
                    string tag = tags[i];
                    if (string.IsNullOrEmpty(tag)) continue;
                    tag = tag.Trim();
                    for (int t = 0; t < MakeupTagTerms.Length; t++)
                        if (string.Equals(tag, MakeupTagTerms[t],
                                StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            return NameLooksMakeup(item.displayName) ||
                NameLooksMakeup(item.uid) ||
                NameLooksMakeup(item.internalUid);
        }
        private static bool NameLooksMakeup(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < MakeupNameCjk.Length; i++)
                if (name.IndexOf(MakeupNameCjk[i],
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            string token = "";
            for (int i = 0; i <= name.Length; i++)
            {
                if (i < name.Length && char.IsLetterOrDigit(name[i]))
                { token += char.ToLowerInvariant(name[i]); continue; }
                if (token.Length > 0)
                {
                    if (token.StartsWith("eye", StringComparison.Ordinal) ||
                        token.StartsWith("face", StringComparison.Ordinal))
                        return true;
                    for (int t = 0; t < MakeupNameTokens.Length; t++)
                        if (token == MakeupNameTokens[t]) return true;
                    token = "";
                }
            }
            return false;
        }

        // Makeup presets are clothing presets but must not steamroll the
        // whole outfit: the synthesized preset the manager receives is
        //   current worn set  − removeIds  − preset item ids
        //                       + the preset's enabled entries
        // Kept items ride along with their LIVE storable JSON so tweaked
        // params (colors, materials) survive — sending the list alone would
        // reset them under setUnlistedParamsToDefault. The preset's own
        // item storables carry the makeup look.
        internal static JSONClass BuildMakeupClothingPreset(
            JSONClass personPreset, Atom target, List<string> removeIds,
            bool addItems, List<string> appliedIds)
        {
            if (personPreset == null || target == null) return null;
            JSONArray storables = personPreset["storables"].AsArray;
            if (storables == null) return null;

            // Preset side: enabled clothing entries + their item ids.
            var presetIds = new List<string>();
            var presetEntries = new JSONArray();
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable == null || storable["id"].Value != "geometry")
                    continue;
                JSONArray list = storable["clothing"].AsArray;
                if (list == null) break;
                for (int j = 0; j < list.Count; j++)
                {
                    JSONClass item = list[j].AsObject;
                    if (item == null || item["enabled"].Value != "true")
                        continue;
                    string key = MakeupItemKey(item);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (presetIds.IndexOf(key) < 0) presetIds.Add(key);
                    presetEntries.Add(item);
                }
                break;
            }
            if (addItems && presetIds.Count == 0) return null;
            if (appliedIds != null)
            {
                appliedIds.Clear();
                appliedIds.AddRange(presetIds);
            }

            // Live side: the target's current clothing list.
            JSONStorable geometryStorable =
                target.GetStorableByID("geometry");
            JSONClass geometryJson = geometryStorable == null
                ? null
                : geometryStorable.GetJSON(true, true, true);
            JSONArray liveList = geometryJson == null
                ? null
                : geometryJson["clothing"].AsArray;
            if (liveList == null) return null;

            var drop = new HashSet<string>();
            if (removeIds != null)
                for (int i = 0; i < removeIds.Count; i++)
                    if (!string.IsNullOrEmpty(removeIds[i]))
                        drop.Add(removeIds[i]);
            // The preset's own ids always drop — on apply the new copy
            // replaces them, on toggle-off they are what comes off (this
            // also self-heals a lost tracking record).
            for (int i = 0; i < presetIds.Count; i++)
                drop.Add(presetIds[i]);
            // Applying a new makeup preset also strips every worn item the
            // classifier recognises as makeup (eye shadow/highlight, blush,
            // lips…) — including makeup worn before our tracking existed.
            if (addItems)
            {
                DAZCharacterSelector selector =
                    geometryStorable as DAZCharacterSelector;
                if (selector != null && selector.clothingItems != null)
                    for (int i = 0; i < selector.clothingItems.Length; i++)
                    {
                        DAZClothingItem ci = selector.clothingItems[i];
                        if (ci == null || !ci.active ||
                            !IsMakeupClothing(ci)) continue;
                        if (!string.IsNullOrEmpty(ci.uid))
                            drop.Add(ci.uid);
                        if (!string.IsNullOrEmpty(ci.internalUid))
                            drop.Add(ci.internalUid);
                        if (!string.IsNullOrEmpty(ci.packageUid))
                            drop.Add(ci.packageUid);
                    }
            }

            var mergedList = new JSONArray();
            var keepIds = new List<string>();
            for (int i = 0; i < liveList.Count; i++)
            {
                JSONClass item = liveList[i].AsObject;
                if (item == null) continue;
                string key = MakeupItemKey(item);
                if (!string.IsNullOrEmpty(key) && drop.Contains(key))
                    continue;
                if (!string.IsNullOrEmpty(key)) keepIds.Add(key);
                mergedList.Add(item);
            }
            if (addItems)
                for (int i = 0; i < presetEntries.Count; i++)
                    mergedList.Add(presetEntries[i]);

            var output = new JSONClass();
            output["setUnlistedParamsToDefault"] = new JSONData(true);
            var geometry = new JSONClass();
            geometry["id"] = new JSONData("geometry");
            geometry["clothing"] = mergedList;
            var outStorables = new JSONArray();
            outStorables.Add(geometry);

            // Live storables for every kept item — preserves the params the
            // user already has on those garments.
            var dropList = new List<string>(drop);
            List<string> storableIds = target.GetStorableIDs();
            for (int i = 0; storableIds != null && i < storableIds.Count; i++)
            {
                string id = storableIds[i];
                if (IsItemStorable(id, dropList)) continue;
                if (!IsItemStorable(id, keepIds)) continue;
                JSONStorable storable = target.GetStorableByID(id);
                JSONClass sub = storable == null
                    ? null
                    : storable.GetJSON(true, true, true);
                if (sub == null) continue;
                sub["id"] = new JSONData(id);
                outStorables.Add(sub);
            }
            // Preset storables for the incoming makeup items.
            if (addItems)
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable == null) continue;
                    if (IsItemStorable(storable["id"].Value, presetIds))
                        outStorables.Add(storable);
                }
            output["storables"] = outStorables;
            return output;
        }

        private static string MakeupItemKey(JSONClass item)
        {
            string iid = item == null ? null : item["internalId"].Value;
            if (string.IsNullOrEmpty(iid))
                iid = item == null ? null : item["id"].Value;
            return iid;
        }

        // Item storable tails: clothing items use Sim/ItemControl/WrapControl/
        // Material*; hair adds a per-item "Preset" storable and vendor
        // material/wrap names like KrayonScalpMaterial/CustomScalpWrapControl,
        // so the fragment set covers anything containing WrapControl/Material.
        private static readonly string[] ItemStorablePrefixes =
            { "Sim", "ItemControl", "Preset" };
        private static readonly string[] ItemStorableFragments =
            { "WrapControl", "Material" };

        // Eyes: VaM has no EyePresets manager, but AppearancePresets' restore
        // domain covers these storables (they ride along in every person
        // .vap), so feeding it a 3-storable JSON restores only the eyes.
        private static readonly string[] EyeStorableIds =
            { "irises", "sclera", "lacrimals" };

        private static JSONClass ExtractEyePreset(JSONClass source)
        {
            JSONArray storables = source == null
                ? null
                : source["storables"].AsArray;
            if (storables == null) return null;

            var outStorables = new JSONArray();
            bool found = false;
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable == null) continue;
                if (Array.IndexOf(EyeStorableIds,
                        storable["id"].Value) < 0)
                    continue;
                outStorables.Add(storable);
                found = true;
            }
            if (!found) return null;

            var output = new JSONClass();
            // MUST be false here: AppearancePresets' restore domain is the
            // whole person, so "unlisted → default" would reset every other
            // storable (morphs/clothing/hair/skin) to a stock character.
            output["setUnlistedParamsToDefault"] = new JSONData(false);
            output["storables"] = outStorables;
            return output;
        }

        // Skin is whitelist-shaped, not list-shaped: every native skin preset
        // carries exactly these storables plus a geometry stub that only holds
        // "character". irises/sclera/lacrimals live in person presets but are
        // NOT part of the native skin-preset contract, so they stay out.
        private static readonly string[] SkinStorableIds =
            { "skin", "textures", "teeth", "tongue", "mouth" };

        internal static JSONClass ExtractSkinPreset(JSONClass source)
        {
            JSONArray storables = source == null
                ? null
                : source["storables"].AsArray;
            if (storables == null) return null;

            var outStorables = new JSONArray();
            bool foundSkin = false;
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable == null) continue;
                string id = storable["id"].Value;
                if (id == "geometry")
                {
                    // Only "character" may pass — morphs/clothing/hair in a
                    // person preset's geometry must not reach the skin load.
                    var stub = new JSONClass();
                    stub["id"] = new JSONData("geometry");
                    JSONNode character = storable["character"];
                    if (character != null &&
                        !string.IsNullOrEmpty(character.Value))
                        stub["character"] = character;
                    outStorables.Add(stub);
                }
                else if (Array.IndexOf(SkinStorableIds, id) >= 0)
                {
                    outStorables.Add(storable);
                    if (id == "skin") foundSkin = true;
                }
            }
            if (!foundSkin) return null;

            var output = new JSONClass();
            output["setUnlistedParamsToDefault"] = new JSONData(true);
            output["storables"] = outStorables;
            return output;
        }

        // Reverse of a whitelist load: replace the target .vap's whitelisted
        // storables with the live ones; skin additionally refreshes
        // geometry.character. Every other section stays byte-identical.
        private void StoreStorableSetIntoPreset(Atom target, string path,
            string[] storableIds, bool syncCharacter, string label)
        {
            if (target == null || string.IsNullOrEmpty(path)) return;
            if (_appearanceLoadBusy)
            {
                LogError(label + " preset: a preset load is already running.");
                return;
            }
            if (FileManager.IsPackagePath(path))
            {
                LogError(label + " preset: presets inside VAR packages cannot be rewritten.");
                return;
            }
            _appearanceLoadBusy = true;
            string tempPath = null;
            try
            {
                var newStorables = new JSONArray();
                for (int i = 0; i < storableIds.Length; i++)
                {
                    string id = storableIds[i];
                    JSONStorable storable = target.GetStorableByID(id);
                    JSONClass sub = storable == null
                        ? null
                        : storable.GetJSON(true, true, true);
                    if (sub == null) continue;
                    sub["id"] = new JSONData(id);
                    newStorables.Add(sub);
                }
                if (newStorables.Count == 0)
                    throw new InvalidOperationException(
                        "target Person exposes no " + label + " storables.");

                JSONNode character = null;
                if (syncCharacter)
                {
                    JSONStorable geometryStorable =
                        target.GetStorableByID("geometry");
                    JSONClass geometryJson = geometryStorable == null
                        ? null
                        : geometryStorable.GetJSON(true, true, true);
                    character = geometryJson == null
                        ? null
                        : geometryJson["character"];
                }

                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException(
                        "preset file is unreadable: " + path);
                JSONClass person = JSON.Parse(text).AsObject;
                JSONArray storables = person == null
                    ? null
                    : person["storables"].AsArray;
                if (storables == null)
                    throw new InvalidOperationException(
                        "not a person preset (no storables): " + path);

                var kept = new JSONArray();
                bool geometrySeen = false;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable == null) continue;
                    string id = storable["id"].Value;
                    if (Array.IndexOf(storableIds, id) >= 0)
                        continue; // dropped, live copies appended below
                    if (id == "geometry")
                    {
                        geometrySeen = true;
                        if (character != null &&
                            !string.IsNullOrEmpty(character.Value))
                            storable["character"] = character;
                    }
                    kept.Add(storable);
                }
                if (!geometrySeen)
                    throw new InvalidOperationException(
                        "preset has no geometry storable: " + path);
                for (int i = 0; i < newStorables.Count; i++)
                    kept.Add(newStorables[i]);
                person["storables"] = kept;

                tempPath = path + ".q3tmp";
                File.WriteAllText(tempPath, person.ToString(),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
                tempPath = null;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        label + " written into person preset: " + path);
            }
            catch (Exception exception)
            {
                LogError(label + " write into person preset failed: " +
                    exception);
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                _appearanceLoadBusy = false;
            }
        }

        private void StoreSkinIntoPreset(Atom target, string path)
        {
            StoreStorableSetIntoPreset(target, path, SkinStorableIds,
                true, "skin");
        }

        internal static JSONClass ExtractSectionPreset(JSONClass personPreset,
            string sectionKey)
        {
            JSONArray storables = personPreset == null
                ? null
                : personPreset["storables"].AsArray;
            if (storables == null) return null;

            JSONNode section = null;
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable != null && storable["id"].Value == "geometry")
                {
                    section = storable[sectionKey];
                    break;
                }
            }
            List<string> itemIds = CollectItemIds(section);
            if (itemIds == null) return null;

            var output = new JSONClass();
            output["setUnlistedParamsToDefault"] = new JSONData(true);
            var geometry = new JSONClass();
            geometry["id"] = new JSONData("geometry");
            geometry[sectionKey] = section;
            var outStorables = new JSONArray();
            outStorables.Add(geometry);
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable == null) continue;
                string id = storable["id"].Value;
                if (IsItemStorable(id, itemIds))
                    outStorables.Add(storable);
            }
            output["storables"] = outStorables;
            return output;
        }

        private static List<string> CollectItemIds(JSONNode clothing)
        {
            JSONArray clothingList = clothing == null ? null : clothing.AsArray;
            if (clothingList == null) return null;
            var itemIds = new List<string>();
            for (int i = 0; i < clothingList.Count; i++)
            {
                JSONClass item = clothingList[i].AsObject;
                // A missing key yields a lazy placeholder whose Value is "".
                string iid = item == null ? null : item["internalId"].Value;
                if (string.IsNullOrEmpty(iid))
                    iid = item == null ? null : item["id"].Value;
                if (!string.IsNullOrEmpty(iid))
                    itemIds.Add(iid);
            }
            return itemIds;
        }

        private static bool IsItemStorable(string id, List<string> itemIds)
        {
            if (string.IsNullOrEmpty(id) || itemIds == null) return false;
            for (int i = 0; i < itemIds.Count; i++)
            {
                string prefix = itemIds[i];
                if (id.Length <= prefix.Length ||
                    !id.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string tail = id.Substring(prefix.Length);
                for (int j = 0; j < ItemStorablePrefixes.Length; j++)
                    if (tail.StartsWith(ItemStorablePrefixes[j],
                            StringComparison.Ordinal))
                        return true;
                for (int j = 0; j < ItemStorableFragments.Length; j++)
                    if (tail.Contains(ItemStorableFragments[j]))
                        return true;
            }
            return false;
        }

        // Reverse of the section load: serialize the target's live section
        // (clothing/hair) exactly like a native section preset, then merge it
        // into the chosen person .vap — its geometry.<section> list is
        // replaced and its old item storables are swapped for the current
        // ones; every other section is left byte-identical.
        private void StoreSectionIntoPreset(Atom target, string path,
            string sectionKey)
        {
            if (target == null || string.IsNullOrEmpty(path)) return;
            if (_appearanceLoadBusy)
            {
                LogError(sectionKey + " preset: a preset load is already running.");
                return;
            }
            if (FileManager.IsPackagePath(path))
            {
                LogError(sectionKey + " preset: presets inside VAR packages cannot be rewritten.");
                return;
            }
            _appearanceLoadBusy = true;
            string tempPath = null;
            try
            {
                JSONStorable geometryStorable =
                    target.GetStorableByID("geometry");
                JSONClass geometryJson = geometryStorable == null
                    ? null
                    : geometryStorable.GetJSON(true, true, true);
                JSONNode section = geometryJson == null
                    ? null
                    : geometryJson[sectionKey];
                List<string> itemIds = CollectItemIds(section);
                if (section == null || itemIds == null)
                    throw new InvalidOperationException(
                        "target Person exposes no " + sectionKey + " list.");

                var newStorables = new JSONArray();
                List<string> storableIds = target.GetStorableIDs();
                for (int i = 0; storableIds != null && i < storableIds.Count; i++)
                {
                    string id = storableIds[i];
                    if (!IsItemStorable(id, itemIds)) continue;
                    JSONStorable storable = target.GetStorableByID(id);
                    JSONClass sub = storable == null
                        ? null
                        : storable.GetJSON(true, true, true);
                    if (sub == null) continue;
                    sub["id"] = new JSONData(id);
                    newStorables.Add(sub);
                }

                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException(
                        "preset file is unreadable: " + path);
                JSONClass person = JSON.Parse(text).AsObject;
                JSONArray storables = person == null
                    ? null
                    : person["storables"].AsArray;
                if (storables == null)
                    throw new InvalidOperationException(
                        "not a person preset (no storables): " + path);

                JSONClass fileGeometry = null;
                JSONNode oldSection = null;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable != null && storable["id"].Value == "geometry")
                    {
                        fileGeometry = storable;
                        oldSection = storable[sectionKey];
                        break;
                    }
                }
                if (fileGeometry == null)
                    throw new InvalidOperationException(
                        "preset has no geometry storable: " + path);
                List<string> oldItemIds = CollectItemIds(oldSection);
                fileGeometry[sectionKey] = section;

                var kept = new JSONArray();
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable == null) continue;
                    if (!IsItemStorable(storable["id"].Value, oldItemIds))
                        kept.Add(storable);
                }
                for (int i = 0; i < newStorables.Count; i++)
                    kept.Add(newStorables[i]);
                person["storables"] = kept;

                tempPath = path + ".q3tmp";
                File.WriteAllText(tempPath, person.ToString(),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
                tempPath = null;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        sectionKey + " written into person preset: " + path);
            }
            catch (Exception exception)
            {
                LogError(sectionKey + " write into person preset failed: " +
                    exception);
            }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                _appearanceLoadBusy = false;
            }
        }

        // 0 = not a usable target, 1 = section-only preset (e.g. a pure hair
        // preset whose geometry carries just its own list), 2 = person preset.
        internal static int ClassifyPresetFile(string path, string sectionKey)
        {
            try
            {
                string text = FileManager.ReadAllText(path, false);
                if (string.IsNullOrEmpty(text)) return 0;
                JSONClass root = JSON.Parse(text).AsObject;
                JSONArray storables = root == null
                    ? null
                    : root["storables"].AsArray;
                if (storables == null) return 0;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable == null ||
                        storable["id"].Value != "geometry")
                        continue;
                    int sectionCount = 0;
                    bool hasSection = false;
                    foreach (KeyValuePair<string, JSONNode> pair in storable)
                    {
                        if (pair.Key == "id") continue;
                        sectionCount++;
                        if (pair.Key == sectionKey) hasSection = true;
                    }
                    if (sectionCount > 1) return 2;
                    return hasSection ? 1 : 0;
                }
            }
            catch { }
            return 0;
        }

        internal void LoadFullAppearancePreset(
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

                if (PresetPathCompatibility.TryLoad(target, appearancePresets, path))
                {
                    LogInfo("Person preset loaded from exact path: " + path);
                    return;
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
                MemoryProbe.Snapshot("preset-pre");
                appearancePresets.CallAction("LoadPreset");
                if (Quest3TriggerUIPlugin.Instance != null)
                    Quest3TriggerUIPlugin.Instance.StartCoroutine(
                        MemoryProbe.SnapshotDelayed(
                            Quest3TriggerUIPlugin.Instance,
                            "preset-post", 8f));
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

        internal void LoadAppearanceWithoutClothing(
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
                if (!PresetPathCompatibility.TryLoad(target, appearancePresets, path))
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

        internal static bool IsGender(Atom atom, string gender)
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
        internal static string EyePresetDir = "Custom/Atom/Person/Eye";
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


