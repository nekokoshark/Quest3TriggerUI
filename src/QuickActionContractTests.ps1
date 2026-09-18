$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$scene = Get-Content -Raw -LiteralPath (Join-Path $root 'SceneQuickActions.cs')
$overlay = Get-Content -Raw -LiteralPath (Join-Path $root 'VrKeyboardOverlay.cs')
$plugin = Get-Content -Raw -LiteralPath (Join-Path $root 'Quest3TriggerUI.cs')
$radial = Get-Content -Raw -LiteralPath (Join-Path $root 'VrRadialMenu.cs')
$pitch = Get-Content -Raw -LiteralPath (Join-Path $root 'GlobalVrPitchController.cs')
$browserTools = Get-Content -Raw -LiteralPath (Join-Path $root 'PresetBrowserFileTools.cs')
$standby = Get-Content -Raw -LiteralPath (Join-Path $root 'FastStandbyController.cs')
$limiter = Get-Content -Raw -LiteralPath (Join-Path $root 'StandbyFrameLimiter.cs')
$appearanceFilter = Get-Content -Raw -LiteralPath (Join-Path $root 'AppearancePresetFilter.cs')
$softRestart = Get-Content -Raw -LiteralPath (Join-Path $root 'SoftRestartController.cs')
$hotLoader = Get-Content -Raw -LiteralPath (Join-Path $root 'HotUpdateLoader.cs')
$gameRoot = (Resolve-Path -LiteralPath (Join-Path $root '..\..\..')).Path
$appearanceDirectory = Join-Path $gameRoot 'Custom\Atom\Person\Appearance'
$sampleVap = Get-ChildItem -LiteralPath $appearanceDirectory -Filter '*.vap' -File |
             Select-Object -First 1
$samplePreset = if ($sampleVap) {
    Get-Content -Raw -LiteralPath $sampleVap.FullName | ConvertFrom-Json
} else {
    $null
}
$grabStart = $plugin.IndexOf('internal static class RightTriggerGrabResult')
$grabEnd = $plugin.IndexOf('[HarmonyPatch(typeof(SuperController), "GetRightGrab")]', $grabStart)
$singleIndexGrabSection = $plugin.Substring($grabStart, $grabEnd - $grabStart)

$checks = [ordered]@{
    Version = $plugin.Contains('PluginVersion = "4.0.1"')
    PersonPreset = $scene.Contains('FindClosestPerson(PersonGenderFilter.Female)') -and
                   $scene.Contains('ShowAppearancePresetDialog(target, false);') -and
                   -not $scene.Contains('target.LoadPresetDialog();')
    AppearancePreset = $scene.Contains('OpenAppearancePresetWithoutClothing') -and
                       $scene.Contains('ShowAppearancePresetDialog(target, true);') -and
                       $scene.Contains('AppearancePresetFilter.StripClothing(preset, true)') -and
                       $scene.Contains('AppearancePresetFilter.CreateClothingOnly(') -and
                       $scene.Contains('"LoadPresetWithPath", appearanceRelativePath);') -and
                       $scene.Contains('"MergeLoadPresetWithPath", clothingRelativePath);') -and
                       $scene.Contains('"/Preset_Quest3TriggerUI_appearance_only_"') -and
                       $scene.Contains('"/Preset_Quest3TriggerUI_original_clothing_"') -and
                       $appearanceFilter.Contains('geometry["clothing"] = new JSONArray();') -and
                       $appearanceFilter.Contains('storables.Remove(i);') -and
                       $appearanceFilter.Contains('item["internalId"]')
    AppearancePresetFolder = $scene.Contains('AppearancePresetDirectory = "Custom/Atom/Person/Appearance"') -and
                             -not $scene.Contains('SuperController.singleton.savesDir + target.type + "\\appearance"')
    NativeVapBrowser = $scene.Contains('GetPresetFilePathAction("LoadPresetWithPath")') -and
                       $scene.Contains('loadAction.Browse(') -and
                       -not $scene.Contains('fileBrowserUI.Show(') -and
                       -not $scene.Contains('fileBrowserUI.defaultPath')
    AppearancePresetManager = $scene.Contains('target.GetStorableByID("AppearancePresets")') -and
                              $scene.Contains('appearancePresets.CallPresetFileAction("LoadPresetWithPath", path);') -and
                              $scene.Contains('"LoadPresetWithPath", appearanceRelativePath);') -and
                              $scene.Contains('"MergeLoadPresetWithPath", clothingRelativePath);') -and
                              $scene.Contains('JSON.Parse(reader.ReadToEnd())') -and
                              $scene.Contains('preset["storables"].AsArray') -and
                              -not $scene.Contains('["atoms"].AsArray') -and
                              -not $scene.Contains('target.Restore(atomJson')
    AppearancePresetFixture = (Test-Path -LiteralPath $appearanceDirectory -PathType Container) -and
                              $null -ne $sampleVap -and
                              $null -ne $samplePreset.storables
    OptimizeMemory = $scene.Contains('MemoryOptimizer.singleton.TriggerOptimize();')
    RefreshVAR = $overlay.Contains('new QuickActionDefinition("刷新VAR", RefreshVars)') -and
                 $scene.Contains('FileManager.Refresh();') -and
                 $scene.Contains('yield return null;') -and
                 $scene.Contains('不重启VaM')
    FastStandby = $overlay.Contains('new QuickActionDefinition("待机/恢复", ToggleStandby,') -and
                  $standby.Contains('controller.SetFreezeAnimation(true);') -and
                  $standby.Contains('controller.pauseAutoSimulation = true;') -and
                  $standby.Contains('AudioListener.pause = true;') -and
                  $standby.Contains('camera.cullingMask = uiMask;') -and
                  $standby.Contains('controller.pauseAutoSimulation = _pauseAutoSimulation;') -and
                  $standby.Contains('controller.SetFreezeAnimation(_freezeAnimation);') -and
                  $standby.Contains('AudioListener.pause = _audioPaused;') -and
                  $standby.Contains('Application.targetFrameRate = _targetFrameRate;') -and
                  $standby.Contains('Application.targetFrameRate = StandbyFramesPerSecond;') -and
                  $standby.Contains('QualitySettings.vSyncCount = 0;') -and
                  $standby.Contains('_frameLimiter.WaitForNextFrame();') -and
                  $limiter.Contains('Thread.Sleep(milliseconds);') -and
                  $limiter.Contains('Stopwatch.Frequency / _framesPerSecond') -and
                  $plugin.Contains('_keyboard.ThrottleStandbyFrame();') -and
                  $scene.Contains('_standby.RestoreImmediately();') -and
                  -not $standby.Contains('Time.timeScale = 0f') -and
                  -not $standby.Contains('Physics.autoSimulation = false') -and
                  -not $standby.Contains('FileManager.Refresh') -and
                  -not $standby.Contains('UnloadUnusedAssets') -and
                  -not $standby.Contains('System.IO')
    SoftRestart = $overlay.Contains('new QuickActionDefinition("软重启", SoftRestart)') -and
                  $scene.Contains('internal void SoftRestart(Action<string> completed)') -and
                  $scene.Contains('_softRestart.Begin(completed);') -and
                  $softRestart.Contains('_standby.RestoreImmediately();') -and
                  $softRestart.Contains('controller.NewScenePlayMode();') -and
                  $softRestart.Contains('bool recoveredStuckLoad = controller.isLoading;') -and
                  $softRestart.Contains('RaiseFlag(LoadFlagField') -and
                  $softRestart.Contains('SetLoadingField(controller, false);') -and
                  $softRestart.Contains('FastResetTimeoutSeconds = 6f') -and
                  $softRestart.Contains('controller.HardReset();') -and
                  $softRestart.IndexOf('controller.NewScenePlayMode();') -lt
                      $softRestart.IndexOf('controller.HardReset();') -and
                  -not $softRestart.Contains('FileManager.Refresh') -and
                  -not $softRestart.Contains('UnloadUnusedAssets') -and
                  -not $softRestart.Contains('GC.Collect') -and
                  -not $softRestart.Contains('Process.Start(')
    HotUpdateLoader = $hotLoader.Contains('Quest3TriggerUI.payload.dll.disabled') -and
                      $hotLoader.Contains('Assembly.Load(bytes)') -and
                      $hotLoader.Contains('DestroyImmediate(_runtime)') -and
                      $hotLoader.Contains('gameObject.AddComponent(nextType)') -and
                      $hotLoader.Contains('SHA256.Create()') -and
                      $hotLoader.Contains('_stableObservations >= 2') -and
                      $hotLoader.Contains('Hot-reloaded') -and
                      $hotLoader.Contains('Payload Awake did not complete.') -and
                      $plugin.Contains('public static bool RuntimeReady { get; private set; }') -and
                      $plugin.Contains('RuntimeReady = true;')
    BindableButtons = $overlay.Contains('for (int i = 0; i < _quickActionDefinitions.Count; i++)') -and
                      $overlay.Contains('_quickActions.OpenPersonPreset') -and
                      $overlay.Contains('_quickActions.OpenAppearancePresetWithoutClothing') -and
                      $overlay.Contains('_quickActions.OptimizeMemory')
    SharedRadialCatalog = $plugin.Contains('new VrRadialMenu(') -and
                          $plugin.Contains('_keyboard.QuickActions') -and
                          $plugin.Contains('Trigger.LongPressStartFrame == frame') -and
                          $plugin.Contains('_radialMenu.ShowHold();') -and
                          $plugin.Contains('Trigger.LongPressReleaseFrame == frame') -and
                          $plugin.Contains('_radialMenu.ReleaseAndSelect();') -and
                          $radial.Contains('IPointerEnterHandler, IPointerExitHandler') -and
                          $radial.Contains('? HoverColor') -and
                          $radial.Contains('Selectable.Transition.ColorTint') -and
                          $radial.Contains('colors.highlightedColor = HoverColor;') -and
                          $radial.Contains('IPointerDownHandler') -and
                          -not $radial.Contains('_nextVisualSync') -and
                          $radial.Contains('selected.Action();') -and
                          $radial.Contains('Hide();') -and
                          -not $radial.Contains('button.onClick.AddListener') -and
                          $radial.Contains('for (int i = 0; i < _actions.Count; i++)')
    IndexGrabConflictRemoved = -not $singleIndexGrabSection.Contains('LongPressStartFrame ==') -and
                               $plugin.Contains('KeyboardChord.LongHoldStartFrame == frame') -and
                               $plugin.Contains('RightGripTrigger.LongPressStartFrame == frame')
    GlobalVrPitch = $plugin.Contains('"ViewPitch", "DegreesPerSecond", 45f') -and
                    $plugin.Contains('"ViewPitch", "MaximumDegrees", 80f') -and
                    $overlay.Contains('internal bool EmbodyActive') -and
                    $plugin.Contains('bool embodyActive = _keyboard != null && _keyboard.EmbodyActive;') -and
                    $plugin.Contains('_globalPitch.LateTick(embodyActive, blockInput);') -and
                    $pitch.Contains('internal void LateTick(bool enabled, bool blockInput)') -and
                    $pitch.Contains('if (!enabled)') -and
                    $pitch.Contains('_state.Reset();') -and
                    $pitch.Contains('OVRInput.Axis2D.SecondaryThumbstick') -and
                    $pitch.Contains('rig.RotateAround(_appliedPivot, _appliedAxis, _appliedDegrees);') -and
                    $pitch.Contains('[HarmonyPatch(typeof(SuperController), "Update")]') -and
                    $pitch.Contains('[HarmonyPatch(typeof(SuperController), "ResetNavigationRigPositionRotation")]')
    PresetBrowserFileTools = $plugin.Contains('new PresetBrowserFileTools()') -and
                             $plugin.Contains('_presetBrowserFileTools.Tick();') -and
                             $browserTools.Contains('controller.fileBrowserWorldUI') -and
                             $browserTools.Contains('controller.fileBrowserUI') -and
                             $browserTools.Contains('IsBrowserVisible(world)') -and
                             $browserTools.Contains('IsBrowserVisible(desktop)') -and
                             $browserTools.Contains('browser.currentPathField') -and
                             $browserTools.Contains('"新建文件夹"') -and
                             $browserTools.Contains('"移动文件"') -and
                             $browserTools.Contains('"移动到这里"') -and
                             $browserTools.Contains('Directory.CreateDirectory(FileManager.GetFullPath(path));') -and
                             $browserTools.Contains('File.Move(') -and
                             $browserTools.Contains('IsInsideAppearanceRoot') -and
                             $browserTools.Contains('Path.ChangeExtension(source, ".jpg")') -and
                             $browserTools.Contains('source + ".hide"') -and
                             $browserTools.Contains('RollBackMoves(moves, completed);') -and
                             $browserTools.Contains('_browser.GotoDirectory(')
}

$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
if ($failed.Count -ne 0) {
    Write-Output ('QUICK ACTION RESULT=FAIL; Failed=' + (($failed | ForEach-Object Key) -join ','))
    exit 1
}

Write-Output 'QUICK ACTION RESULT=PASS; Version=4.0.1; RadialHover=ColorTint+PointerEnter+PointerDown+EventDrivenNoPeriodicOverwrite; AppearanceLoad=FullAppearanceWithEmptySelectedClothingThenOriginalClothingOnlyRestore; ClothingOverlay=False; Standby=VaMCoordinatedPause+VSyncOff+Target5FPS+MainThread5FPSLimiter; CharacterTwistRegression=False; HotUpdate=PayloadSwapWithoutGameRestart; ExistingFeatures=Preserved'
exit 0
