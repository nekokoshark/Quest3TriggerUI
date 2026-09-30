using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Quest3TriggerUIPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.vam.quest3-trigger-ui";
        public const string PluginName = "Quest 3 Trigger UI";
        public const string PluginVersion = "4.6.293";

        internal static Quest3TriggerUIPlugin Instance;
        internal static TriggerStateMachine Trigger;
		internal static TriggerStateMachine LeftTrigger;
		internal static TriggerStateMachine RightGripTrigger;
        internal static KeyboardChordStateMachine KeyboardChord;
        internal static KeyboardChordStateMachine RecenterChord;
        internal static ShortcutGestureBank ShortcutGestures;
        internal static ManualLogSource Log;
        public static bool RuntimeReady { get; private set; }

        private Harmony _harmony;
        private VrKeyboardOverlay _keyboard;
        internal void ShowTextKeyboard()
        {
            if (_keyboard != null && !_keyboard.Visible) _keyboard.Toggle();
            // While the preset browser owns the panel area, pin the keyboard
            // to it instead of letting it track the view centre.
            Transform dock = VrPresetBrowser.KeyboardDock;
            if (dock != null && _keyboard != null)
                _keyboard.DockAt(dock);
        }

        internal void UndockTextKeyboard(Transform host)
        {
            if (_keyboard != null && _keyboard.IsDockedUnder(host))
                _keyboard.Recenter();
        }
        private VrRadialMenu _radialMenu;
        private VrPinnedActionTiles _pinnedTiles;
        private GlobalVrPitchController _globalPitch;
        private VrAuxiliaryUiView _auxiliaryUiView;
        private EmbodyNavigationGuard _embodyNavigationGuard;
        private PresetBrowserFileTools _presetBrowserFileTools;
        private VrAimGuide _aimGuide;
        private VrGlobalCursor _globalCursor;
        private ConfigEntry<int> _physicsBudgetEntry;
        private ConfigEntry<int> _physicsSolverCapEntry;
        private ConfigEntry<float> _physicsHairScaleEntry;
        private ConfigEntry<int> _physicsHairCollEntry;
        private ConfigEntry<float> _physicsClothScaleEntry;
        private ConfigEntry<int> _physicsClothOffEntry;
        private ConfigEntry<bool> _memSnapshotEntry;
        private ConfigEntry<int> _memWatchEntry;
        private float _nextMemWatch;
        private float _cfgWatchT;
        private DateTime _cfgLastWrite;
        private int _handledToggleFrame = -1;
        private int _handledRecenterFrame = -1;
        private int _targetedInputSelfTestPhase;
        private int _targetedInputSelfTestDeadline;
        private bool _targetedInputSelfTestDown;
        private IntPtr _targetedInputSelfTestForeground;
        private int _duplicateSweepFrame;
        private bool _hudCleanupDone;
        private bool _updateLogged;
        private bool _warmupPending = true;
        private float _warmupDelay = 2.5f;
        private int _quickActionRevision = -1;
        private static int _inputSampleFrame = -1;
        private static Vector2 _sampledRightStick;
        // Read-only access for UI scroll plumbing (dock bars). The value is
        // refreshed once per frame by SampleInputs — no extra input call.
        internal static Vector2 SampledRightStick
        {
            get { return _sampledRightStick; }
        }
		internal static bool SliderDragActive { get; private set; }
        internal static AceFavDragSource ClothingDragCandidate;
        internal static bool ClothingDragActive;
        private static bool _radialDragBlocked;
        internal static bool RadialDragBlocked
        {
            get { return _radialDragBlocked || ClothingDragActive ||
                ClothingDragCandidate != null || VrPresetBrowser.CapturingGesture; }
        }

        private static void UpdateRadialDragBlock(bool held, bool releaseFrame, bool captured, bool freshPress)
        {
            // Retain gesture ownership across panel gaps, cancellation and
            // the release frame. Only a completed trigger cycle unlocks it.
            _radialDragBlocked = (held || releaseFrame) &&
                ((!freshPress && _radialDragBlocked) || captured);
        }

        internal static bool KeyboardVisible
        {
            get { return Instance != null && Instance._keyboard != null && Instance._keyboard.Visible; }
        }

        internal static bool RadialMenuVisible
        {
            get
            {
                return Instance != null && Instance._radialMenu != null &&
                       Instance._radialMenu.Visible;
            }
        }

        internal static bool PinnedTilesCapturingGrip { get { return Instance != null && Instance._pinnedTiles != null && Instance._pinnedTiles.CapturingGrip; } }
        internal static bool GripPitchCapturing { get { return Instance != null && Instance._globalPitch != null && Instance._globalPitch.CapturingGrip; } }

        internal static bool ShortcutBindingMode
        {
            get
            {
                return Instance != null && Instance._keyboard != null &&
                       Instance._keyboard.BindingMode;
            }
        }

        internal static bool InputRuntimeActive
        {
            get
            {
                return OVRManager.isHmdPresent || OpenVrInputBridge.IsActive;
            }
        }

        // A scene load must not run under the standby frame limiter or with
        // simulation paused, so the scene-load accelerator releases standby
        // before it hands the request to the native loader.
        internal bool ReleaseStandbyForSceneLoad()
        {
            return _keyboard != null && _keyboard.ReleaseStandby();
        }

        // The preheat pass must never load a scene while standby holds the
        // simulation paused and the frame limiter is active.
        internal bool StandbyActiveNow
        {
            get { return _keyboard != null && _keyboard.StandbyActive; }
        }

        internal static void AwakeDiag(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        BepInEx.Paths.ConfigPath, "q3haptics-diag.txt"),
                    msg + "\r\n");
            }
            catch { }
        }

        private void Awake()
        { DlssUiOverlay.Begin();
            try
            {
                RemoveDuplicateRuntimeInstances();
            }
            catch (Exception exception)
            {
                Logger.LogError("Duplicate runtime cleanup failed: " + exception);
            }
            try
            {
                RecoverOrphanedRecorders();
            }
            catch (Exception exception)
            {
                Logger.LogError("Orphaned recorder cleanup failed: " + exception);
            }
            RuntimeReady = false;
            ConfigEntry<float> longPress = Config.Bind(
                "Input", "LongPressSeconds", 0.35f,
				"Seconds before right index opens the radial menu or right grip starts Grab while the keyboard is hidden.");
            ConfigEntry<float> pressThreshold = Config.Bind(
                "Input", "PressThreshold", 0.55f,
                "Trigger pressure that begins a press.");
            ConfigEntry<float> releaseThreshold = Config.Bind(
                "Input", "ReleaseThreshold", 0.45f,
                "Trigger pressure below which a press is released.");
            ConfigEntry<float> chordTap = Config.Bind(
                "Keyboard", "ChordTapSeconds", 0.45f,
                "Maximum duration of a right index+grip tap that toggles the keyboard.");
            ConfigEntry<float> keyboardScale = Config.Bind(
                "Keyboard", "Scale", 0.00058f,
                "World-space keyboard scale.");
            ConfigEntry<float> keyboardDistance = Config.Bind(
                "Keyboard", "Distance", 0.90f,
                "Distance in metres used by Recenter.");
            ConfigEntry<float> pitchSpeed = Config.Bind(
                "ViewPitch", "DegreesPerSecond", 45f,
                "Global VR view pitch speed for the right thumbstick vertical axis.");
            ConfigEntry<float> pitchLimit = Config.Bind(
                "ViewPitch", "MaximumDegrees", 80f,
                "Maximum global VR view pitch above or below the neutral horizon.");
            ConfigEntry<float> pitchDeadzone = Config.Bind(
                "ViewPitch", "Deadzone", 0.18f,
                "Right thumbstick vertical deadzone for global VR view pitch.");
            ConfigEntry<bool> invertPitch = Config.Bind(
                "ViewPitch", "Invert", false,
                "Invert the global VR view pitch direction.");
            ConfigEntry<float> radialOpacity = Config.Bind(
                "RadialMenu", "Opacity", 0.9f,
                "Radial menu and pinned tile opacity, 0.2 to 1.0.");
            ConfigEntry<float> discOpacity = Config.Bind(
                "RadialMenu", "DiscOpacity", 0.30f,
                "Radial disc opacity only; pinned tiles keep Opacity. 0.2 to 1.0.");
            ConfigEntry<float> keyboardOpacity = Config.Bind(
                "Keyboard", "Opacity", 0.75f,
                "Virtual keyboard opacity, 0.2 to 1.0.");
            ConfigEntry<bool> globalCursor = Config.Bind(
                "GlobalCursor", "Enabled", true,
                "Glowing ring cursor on UI surfaces under the VR pointer ray.");
            ConfigEntry<bool> hapticsEnabled = Config.Bind(
                "Haptics", "Enabled", true,
                "Short controller vibration pulses on this plugin's VR UI interactions.");
            ConfigEntry<float> hapticsStrength = Config.Bind(
                "Haptics", "Strength", 1.0f,
                "Haptic pulse strength, 0.0 to 1.0 (scales pulse length).");
            VrHaptics.Enabled = hapticsEnabled.Value;
            VrHaptics.Strength = Mathf.Clamp01(hapticsStrength.Value);
            ConfigEntry<bool> takeover = Config.Bind(
                "Browser", "TakeoverNative", true,
                "Route every native file-browser call (loads, plugin dialogs) through the Quest3 browser. false = only the wired save/load entries use it.");
            FileBrowserTakeover.Enabled = takeover.Value;
            ConfigEntry<bool> pkgMgrBlock = Config.Bind(
                "Browser", "BlockPackageManager", true,
                "Disable VaM's built-in Package Manager entirely (it freezes for seconds scanning all .var files on open, and its queued thumbnail decodes keep hitching the game after close). true = every entry point is blocked and hidden; false = stock behavior.");
            PackageManagerGuard.BlockEntry = pkgMgrBlock.Value;
            ConfigEntry<bool> baPkgSync = Config.Bind(
                "BA", "SkipPackageListSync", true,
                "Incremental VA/VAR rescans skip VaM PackageBuilder.SyncPackages refresh handler (~13s on an 11k-var library). VaM own package registry is updated per package instead; only the Package Builder tool list waits for the next full rescan. false = always run it.");
            BrowserAssistScanAccelerator.SkipPackageListSync = baPkgSync.Value;
            ConfigEntry<bool> baDeadLinks = Config.Bind(
                "BA", "AutoPruneDeadLinks", true,
                "During scans, remove .var link entries whose target vanished (deleted from the linked library) so the package stops listing. Skips pruning when the target volume is offline. false = keep dead links untouched.");
            BrowserAssistScanAccelerator.AutoPruneDeadLinks = baDeadLinks.Value;
            // Drop thumbnail decodes/cache left over from any package
            // manager session that ran before this payload loaded.
            PackageManagerGuard.PurgeResiduals();
            ConfigEntry<bool> imeCloud = Config.Bind(
                "IME", "CloudCandidates", false,
                "Query Baidu's search-suggestion service for Chinese candidates (typed pinyin is sent over the network). false = local dictionary only. Off by default — the local rime-ice dictionary already covers IME-grade vocabulary.");
            PinyinEngine.CloudEnabled = imeCloud.Value;
            ConfigEntry<bool> imeLearning = Config.Bind(
                "IME", "Learning", true,
                "Remember committed words/phrases in a local user dictionary (全拼+简拼 both recall). Stored in Quest3TriggerUI.pinyin-user.txt next to the plugin.");
            PinyinEngine.UserLearning = imeLearning.Value;
            ConfigEntry<bool> imeBridge = Config.Bind(
                "IME", "DesktopImeBridge", false,
                "When the VaM window is foreground and the desktop Chinese IME (Doubao etc.) owns the layout, feed VR keys to it and mirror its candidate bar as an image in VR. false = always use the built-in engine.");
            DoubaoImeBridge.ConfigEnabled = imeBridge.Value;
            ConfigEntry<int> physicsBudget = Config.Bind(
                "PhysicsBudget", "Level", 0,
                "Physics cost cap: 0=off, 1=balanced, 2=aggressive. Runtime-only, fully revertible.");
            ConfigEntry<int> physicsSolverCap = Config.Bind(
                "PhysicsBudget", "SolverCap", -1,
                "solverIterations cap override; -1=off (caps measured harmful), 0=disable.");
            ConfigEntry<float> physicsHairScale = Config.Bind(
                "PhysicsBudget", "HairScale", -1.0f,
                "Hair density/detail scale; -1=level default (0.75/0.5), 0=disable.");
            ConfigEntry<int> physicsHairColl = Config.Bind(
                "PhysicsBudget", "HairCollision", -1,
                "Hair collision solve off: -1=level default (aggressive only), 0=never touch collision even at a level (头发碰撞 button off), 1=always off (works standalone at level 0).");
            ConfigEntry<float> physicsClothScale = Config.Bind(
                "PhysicsBudget", "ClothScale", -1.0f,
                "Cloth iteration scale for items with headroom; -1=level default (0.8/0.66), 0=disable.");
            ConfigEntry<int> physicsClothOff = Config.Bind(
                "PhysicsBudget", "ClothOffBelow", -1,
                "Cloth items with <=N physics particles get sim disabled entirely; -1=level default (250/500), 0=never.");
            ConfigEntry<int> physicsHairCollAbove = Config.Bind(
                "PhysicsBudget", "HairCollOffAbove", -1,
                "Hair collision solver off for items with >=N particles (density*detail); -1=level default (20000 at balanced, unused at aggressive where all collision is off), 0=never.");
            ConfigEntry<float> physicsHairCurl = Config.Bind(
                "PhysicsBudget", "HairCurlScale", -1f,
                "Hair curl frequency scale; -1=level default (0.75/0.6), 0=off, else overrides.");
            ConfigEntry<string> recordingDir = Config.Bind(
                "Recording", "OutputDirectory", "VR录制",
                "VR recording output folder. Relative paths resolve under the VaM install folder; absolute paths are used as-is.");
            ConfigEntry<string> ffmpegPath = Config.Bind(
                "Recording", "FfmpegPath", "ffmpeg.exe",
                "ffmpeg executable for VR recording. Bare name resolves through PATH; otherwise give a full path.");
            ConfigEntry<bool> pauseOnStall = Config.Bind(
                "Recording", "PauseOnStall", true,
                "Pause recording during game stalls: the dead span is skipped from the output instead of being filled with repeated frames, and capture resumes when the game does.");
            VrVideoRecorder.OutputDirectory = recordingDir.Value;
            VrVideoRecorder.FfmpegPath = ffmpegPath.Value;
            VrVideoRecorder.PauseOnStall = pauseOnStall.Value;
            ConfigEntry<string> hairPresetDir = Config.Bind(
                "Paths", "HairPresetDir", "Custom/Atom/Person/Hair",
                "VaM-relative folder where the hair preset browser opens. Change it if your hair presets live elsewhere.");
            ConfigEntry<string> clothingPresetDir = Config.Bind(
                "Paths", "ClothingPresetDir", "Custom/Atom/Person/Clothing",
                "VaM-relative folder where the clothing preset browser opens. Change it if your clothing presets live elsewhere.");
            ConfigEntry<string> appearancePresetDir = Config.Bind(
                "Paths", "AppearancePresetDir", "Custom/Atom/Person/Appearance",
                "VaM-relative appearance preset folder. Used by the preset browser file tools.");
            ConfigEntry<string> skinPresetDir = Config.Bind(
                "Paths", "SkinPresetDir", "Custom/Atom/Person/Skin",
                "VaM-relative skin preset folder. Used by the preset browser file tools.");
            ConfigEntry<string> lightLinkerUrl = Config.Bind(
                "Paths", "LightLinkerUrl", "Custom/Scripts/LightLinker/LightLinker.cslist",
                "LightLinker cslist URL used when the lights button lazy-loads it into Session Plugins. Point it at a .var path (e.g. Lzswwx.LightLinker.latest:/...) if yours lives in a package.");
            ConfigEntry<bool> standbyVramDiet = Config.Bind(
                "Standby", "VramDiet", true,
                "Compress textures during standby. Saves VRAM but hitches the world briefly on entry. Set false for near-instant standby.");
            ConfigEntry<bool> standbySweep = Config.Bind(
                "Standby", "SweepUnusedAssets", false,
                "Run Resources.UnloadUnusedAssets during standby. Reclaims managed memory but is the longest single stall — off by default.");
            FastStandbyController.VramDiet = standbyVramDiet.Value;
            FastStandbyController.SweepUnusedAssets = standbySweep.Value;
            ConfigEntry<bool> baFastRescan = Config.Bind(
                "BrowserAssist", "FastRescan", true,
                "Patch JayJayWon BrowserAssist's rescan button: skip the full scan when the file tree is unchanged, and only re-enumerate changed .var packages when it isn't. false = original always-full scan.");
            BrowserAssistScanAccelerator.Enabled = baFastRescan.Value;
            ConfigEntry<bool> baDeferHiddenResync = Config.Bind(
                "BrowserAssist", "DeferHiddenClothingUiResync", true,
                "While a rescan runs, skip the engine's clothing/hair selector UI rebuild for panels that are not currently visible in the hierarchy, and replay that rebuild once when the panel is opened. Closed panels cost ~7s per rescan on a 24k-item library. false = native behaviour (rebuild hidden panels too).");
            BrowserAssistScanAccelerator.DeferHiddenResync =
                baDeferHiddenResync.Value;
            ConfigEntry<bool> sceneResyncCoalesce = Config.Bind(
                "SceneLoad", "CoalesceSelectorResync", true,
                "While a scene load runs, allow the first rebuild of each clothing/hair selector UI per instance and skip the rest (every later rebuild reconstructs a catalogue that is still growing); when the load settles, the skipped panels are rebuilt the moment they become visible. Native cost on this library: 24 rebuilds inside one 190s load. false = native behaviour.");
            SceneResyncCoalesce.Enabled = sceneResyncCoalesce.Value;
            ConfigEntry<bool> sceneFastLoad = Config.Bind(
                "SceneLoad", "FastSwitch", true,
                "Accelerate scene loads: time every load, remember each scene's package dependencies, reuse that record while the VAR library is unchanged, and let the BrowserAssist package scan take its unchanged-library skip when a scene change triggers it. false = native behaviour only.");
            SceneLoadAccelerator.Enabled = sceneFastLoad.Value;
            SceneLoadAccelerator.FastSwitch = sceneFastLoad.Value;
            PluginPaths.HairPresetDir = hairPresetDir.Value;
            PluginPaths.ClothingPresetDir = clothingPresetDir.Value;
            PluginPaths.AppearancePresetDir = appearancePresetDir.Value;
            PluginPaths.SkinPresetDir = skinPresetDir.Value;
            PluginPaths.LightLinkerUrl = lightLinkerUrl.Value;
            PhysicsBudget.SetLevel(physicsBudget.Value);
            PhysicsBudget.SolverCapOverride = physicsSolverCap.Value;
            PhysicsBudget.HairScaleOverride = physicsHairScale.Value;
            PhysicsBudget.HairCollisionMode = physicsHairColl.Value;
            PhysicsBudget.ClothScaleOverride = physicsClothScale.Value;
            PhysicsBudget.ClothOffBelowOverride = physicsClothOff.Value;
            PhysicsBudget.HairCollOffAboveOverride = physicsHairCollAbove.Value;
            PhysicsBudget.HairCurlScaleOverride = physicsHairCurl.Value;
            PhysicsBudget.LevelEntry = physicsBudget;
            PhysicsBudget.CollEntry = physicsHairColl;
            _physicsBudgetEntry = physicsBudget;
            _physicsSolverCapEntry = physicsSolverCap;
            _physicsHairScaleEntry = physicsHairScale;
            _physicsHairCollEntry = physicsHairColl;
            _physicsClothScaleEntry = physicsClothScale;
            _physicsClothOffEntry = physicsClothOff;

            ConfigEntry<bool> preheatAuto = Config.Bind(
                "Preheat", "Enabled", true,
                "Shortly after startup, while still on the menu, realize the Person prefab and instantiate its morph-bank prefabs so the process-wide catalogue caches (_dirEntryCache/_morphInitCache — the whole-VAR morph scan shared by every later person) are paid up front. No person clone pool, no scene rehearsal. false = never preheat automatically.");
            ConfigEntry<float> preheatDelay = Config.Bind(
                "Preheat", "DelaySeconds", 30f,
                "Seconds after coming up before the startup preheat runs. Keep it long enough that the engine is fully initialized.");
            ScenePreheat.Auto = preheatAuto;
            ScenePreheat.DelaySeconds = preheatDelay;
            ScenePreheat.Source = Config;
            AudioDeviceFollower.Enabled = Config.Bind(
                "Audio", "FollowDefaultDevice", true,
                "Keeps VaM's audio on the Windows default output device: when the default changes, the Unity audio engine is reset once so it rebinds (all playing sounds restart). false = never follow.").Value;
            AudioCacheJanitor.Enabled = Config.Bind(
                "Audio", "CacheEviction", true,
                "Evict audio clips that no AudioSource has referenced for CacheGraceSeconds — VaM caches every decoded clip forever in URLAudioClipManager/EmbeddedAudioClipManager (~2GB observed). Evicted URL clips re-decode lazily if needed again.");
            AudioCacheJanitor.GraceSeconds = Config.Bind(
                "Audio", "CacheGraceSeconds", 180f,
                "Seconds a clip may stay unreferenced before the audio cache janitor evicts it.");
            AudioCacheJanitor.IncludeEmbedded = Config.Bind(
                "Audio", "CacheEvictEmbedded", false,
                "Also evict EmbeddedAudioClipManager clips (bigger pool; embedded clip data may not be re-loadable until VaM restarts — off by default).");
            AudioCacheJanitor.EvictNow = Config.Bind(
                "Diagnostics", "AudioCacheEvictNow", false,
                "One-shot: evict all currently-unreferenced audio clips immediately (ignores grace window), then resets to false.");
            PresetCleanupCoalescer.Enabled = Config.Bind(
                "TextureLoading", "CoalesceCoveredCleanup", true,
                "Reuse a recent full sweep for supplemental cleanup instead of paying a second full-heap mark.");
            PresetCleanupCoalescer.ReuseSeconds = Config.Bind(
                "TextureLoading", "CoalesceReuseSeconds", 80f,
                "Max age of a previous full sweep that may still cover the janitor's supplemental cleanup.");
            PresetCleanupCoalescer.DeferMax = Config.Bind(
                "TextureLoading", "CoalesceDeferMax", 4,
                "Max release events a covered sweep may defer before the janitor runs a backstop cleanup.");
            PresetInstanceReuse.Enabled = Config.Bind(
                "TextureLoading", "ReusePresetInstances", true,
                "Temporarily retain ready same-base clothing/hair instances across native preset reset; restore all parameters normally.");
            PresetDeltaApply.Enabled = Config.Bind("PresetLoading", "DeltaActiveItems", true,
                "Keep matching live same-base clothing/hair active during preset reset; restore all target parameters and reset physics normally.");
            PresetHairRenderBatch.Enabled = Config.Bind("PresetLoading", "BatchHairRenderUpdates", true,
                "Combine repeated hair render-particle updates within parameter restore; preserves density, physics and final parameter values.");
            PresetSweepGate.Enabled = Config.Bind("PresetLoading", "SkipUnchangedCharacterSweep", true,
                "Skip verified unchanged appearance/clothing UUA after a completed sweep; preserve actual release debt, pressure, manual cleanup and 120s request-time bound.");
            PresetSweepGate.SkipUnchangedGC = Config.Bind("PresetLoading", "SkipUnchangedPresetGC", true,
                "Skip verified unchanged preset GC at low growth; settle pending native preset GC after async loading (30s cap), or immediately at 75% RAM pressure. Keep 256MiB/120s GC limits and manual cleanup.");
            PresetSweepGate.DeferSweep = Config.Bind("PresetLoading", "DeferPresetSweep", true,
                "A sweep requested from inside a preset restore is submitted at the next quiet moment instead of mid-load (measured 8.68s wall / 6.3s freeze inside a 10.2s no-change reload). Same global mark, same debt, fewer waiting seconds; off restores the old inline submit.");
            PresetSweepGate.GcYieldGate = Config.Bind("PresetLoading", "DeferLowYieldPresetGC", true,
                "Also defer the post-swap GC when the previous one barely paid for itself (yield below max(64MiB, 5% of the pre-collect heap)). The tail-settled branch was the one path that collected without rechecking growth, and it fires 0.25s after a swap, i.e. once the character is already on screen: 20 measured swaps each cost 3.6-3.9s for 31MiB..1.5GiB. While deferred the request stays pending for the existing 30s bound, the 75% pressure line and the 1.5GB/60s idle watchdog, so retained garbage is capped at one 30s window (~0.2GB at the documented 0.28-0.34GB/min idle churn). Set false to collect at every settled tail again.");
            PresetSweepGate.SwapGcToIdle = Config.Bind("PresetLoading", "HandSwapGCToIdle", true,
                "Do not run the post-sweep preset GC inside the click window. Over 20 consecutive swaps each inline collect cost 3.9-4.1s and froze 4.8-6.9s of frame time to free 0.4-0.9GiB, while the 1.5GiB/60s idle watchdog returns 2.2-3.8GiB for the same 4s. With headroom the request is dropped and the watchdog owns the collection; memory pressure still collects inline and the 30s/120s bounds are unchanged. Set false to collect inline again.");
            PresetSweepGate.IdleGcAboveLine = Config.Bind("PresetLoading", "IdleGcWhenAboveLoadLine", true,
                "Above the 80% load line raise the idle watchdog's growth band to 3GiB instead of refusing the collect. Measured here: the session's steady state is load=82-88% with the external trimmer off, the 1.5GiB gate fired every cycle, and the load line refused every attempt until the heap sawtoothed 19.74 -> 26.6GB of 28.90GB and one 4.4-4.7s mark landed wherever Boehm picked it. The physical floor (2GB available RAM, 8GB page file) and the 60s minimum interval still apply. Set false to keep the 80% line absolute.");
            PresetSweepGate.IdleGcOnExhaust = Config.Bind("PresetLoading", "IdleGcOnHeapExhaust", true,
                "Also let the idle watchdog collect while RAM load sits above its 80% line, once the managed heap itself is nearly out of room (free below 8% of capacity, floor 256MiB). Measured twice in one idle session: the watchdog logged 'idle GC blocked ... load=84%' for the whole climb, monoFree fell to 0.24-0.28GB of 26.17GB, and Boehm then collected on its own from inside a UI frame at 4.2-4.5s of freeze. Same mark either way, cheaper frame. The hardware floor (2GB available RAM, 8GB page file) still applies. Set false to keep the load line absolute.");
            ColdTextureBufferLayout.Enabled = Config.Bind("TextureLoading", "CompactColdDecodeBuffers", true,
                "Use exact upload-sized cold buffers and convert bump maps in place after a local Unity byte-layout selftest; no new resident pool or cache format change.");
            BumpNormalRowConverter.Enabled = Config.Bind(
                "TextureLoading", "BumpNormalThreeRows", true,
                "Use three scratch rows for native-equivalent bump-to-normal conversion; preserve output format and resolution.");
            TextureUploadReuse.Enabled = Config.Bind("TextureLoading", "ReuseBeforeUpload", true,
                "Reuse a compatible completed native texture before duplicate upload; preserve callbacks and force reload.");
            TextureInFlight.Enabled = Config.Bind("TextureLoading", "ShareInFlightDecode", true,
                "Share identical in-flight worker decode results; native callbacks and byte reservations remain independent.");
            TextureCompletionBudget.Enabled = Config.Bind("TextureLoading", "BudgetCompletionQueue", true,
                "Keep native four completions; drain up to 16 cheap items within 2ms/32MiB soft budgets.");
            TextureMetadataReuse.Enabled = Config.Bind("TextureLoading", "ReuseRequestMetadata", true,
                "Reuse admission metadata in the same request after cache file stamp checks.");
            TextureScratchLifetime.Enabled = Config.Bind("TextureLoading", "ReleaseDecodeScratchEarly", true,
                "Release GDI source after drawing and destination after pixel copy; unchanged image processing.");
            TextureCacheWriteBudget.Enabled = Config.Bind("TextureLoading", "AccountCacheWrites", true,
                "Charge asynchronous cache-write arrays to the existing texture admission budget until completion.");
            TextureCacheBc7Convert.Enabled = Config.Bind("TextureLoading", "ConvertCacheToBc7", true,
                "Re-encode an uncompressed RGBA32/RGB24 image cache as BC7 in the background right after the game read it, so the next load reads a quarter of the bytes. Only runs while that texture is still cached and no scene is loading; the pair is swapped in one rename and journalled so an interrupted swap is repaired at the next start.");
            TextureCacheBc7Convert.ToolPath = Config.Bind("TextureLoading", "Bc7ToolPath", "",
                "Optional path to texconv.exe (DirectXTex). Empty tries the plugin folder, then XnView MP. Without a tool the automatic conversion stays off.");
            TextureCacheBc7Convert.DiagnoseLayout = Config.Bind("TextureLoading", "DiagnoseBc7CacheLayout", true,
                "One-off startup probe that asks Unity for the storage size of BC7 chains on non multiple-of-four dimensions and logs it. Set false once that layout question is settled.");
            TextureCacheBc7Convert.IdleSeconds = Config.Bind("TextureLoading", "Bc7IdleSeconds", 180,
                "Seconds without any input before a background re-encode may start when Bc7Immediate is off. 0 disables the in-session path so conversion only happens after the game exits.");
            TextureCacheBc7Convert.Immediate = Config.Bind("TextureLoading", "Bc7Immediate", true,
                "Re-encode each cache entry as soon as its load window closes instead of waiting for Bc7IdleSeconds without input. Encoding stays on the background worker and the swap still needs the window clear, so a first load never waits; immediate mode also swaps while the texture is still resident, which is when the swap can actually land.");
            TextureCacheBc7Convert.ConvertOnExit = Config.Bind("TextureLoading", "Bc7ConvertOnExit", true,
                "Hand the entries collected this session to a hidden helper that converts them one at a time after the game has exited.");
            TextureDecodeBudget.Enabled = Config.Bind(
                "TextureLoading", "DecodeBudgetEnabled", true,
                "Bound estimated pending/decoding/upload-wait texture bytes; preserves image quality.");
            TextureDecodeBudget.BudgetMiB = Config.Bind(
                "TextureLoading", "DecodeBudgetMiB", 2048,
                "Estimated in-flight budget (256..8192 MiB), reduced under physical RAM or system commit pressure. One oversize image may proceed.");
            TextureDecodeBudget.EarlyDiscard = Config.Bind(
                "TextureLoading", "DiscardStaleBeforeDecode", true,
                "Skip decoding only when all known native receivers are proven obsolete; retain native completion.");
            WardrobeJanitor.Enabled = Config.Bind(
                "Wardrobe", "AutoUnload", true,
                "Destroyed-removed clothing/hair reclamation: VaM keeps every swapped-out item's GameObject+textures until you press optimize memory. This unloads items that have been inactive for WardrobeIdleSeconds, a few per frame — reclaims their RAM+VRAM without the big optimize stall.");
            WardrobeJanitor.IdleSeconds = Config.Bind(
                "Wardrobe", "IdleSeconds", 60f,
                "Seconds a removed clothing/hair item may sit inactive before it is destroyed. Longer = safer when cycling the same outfit set (re-wearing a destroyed item reloads it from scratch).");
            WardrobeJanitor.MaxPerFrame = Config.Bind(
                "Wardrobe", "MaxPerFrame", 2,
                "Max inactive wardrobe items destroyed per frame — spreads the Destroy cost so reclamation is near-invisible.");
            WardrobeJanitor.PurgeOnLoad = Config.Bind(
                "Wardrobe", "PurgeOnLoad", true,
                "After successful appearance/clothing/hair restore, wait for native image/character load tail before reclaiming inactive instances. Full appearance only also purges morph scratch; independent of AutoUnload.");
            WardrobeJanitor.PurgeDelaySeconds = Config.Bind(
                "Wardrobe", "PurgeDelaySeconds", 5f,
                "Seconds after a full person preset load before the inactive-item purge runs — lets the async texture/morph tail settle first.");
            FaceDetailDistanceGuard.Enabled = Config.Bind(
                "Wardrobe", "FaceDetailDistanceGuard", true,
                "VAMSOY's template woman wears razor-thin facial overlays (CMA_EYESSHADOW, CMA_IRIS REFLEX, Lacrimal_gland_v2, Realistic Eyes, Eyes upper/side shadow, EYE2) whose alpha is ~0 in nearly every texel: a distant mip averages the transparent RGB back in and paints a grey film over the face, and her lashes are hair, so the hair distance LOD collapses them into long spikes. This switches those items' worn renderers off past FaceDetailDistanceM and back on inside it. item.active/item.enabled are never written, so the preset is unchanged; only renderers this guard switched off are switched back on.");
            FaceDetailDistanceGuard.HideDistance = Config.Bind(
                "Wardrobe", "FaceDetailDistanceM", 3f,
                "Metres from the camera at which facial overlays and lashes are switched off (measured to the item's own world bounds, so a reclining pose is measured against the face). Shown again below 85% of this to avoid flicker; tune to taste in a VAMSOY scene.");
            TextureOrphanSweeper.Enabled = Config.Bind(
                "Wardrobe", "TexOrphanSweep", true,
                "Release unused textureCache ownership only for observed native character/material callback results. Unknown/UI/preload consumers are retained; no direct texture destruction.");
            TextureOrphanSweeper.AgeSeconds = Config.Bind(
                "Wardrobe", "TexOrphanSeconds", 120f,
                "Seconds a cache entry may sit without any use count before its cache ownership is released — only observed native callbacks qualify; UI, preloads and unknown consumers are excluded.");
            TextureOrphanSweeper.UnusedBudgetMiB = Config.Bind(
                "Wardrobe", "UnusedNativeTextureBudgetMiB", 512,
                "Soft budget for observed native unused textures only; live/shared/UI/preload/unknown consumers retained; 15s grace and sweep batch apply.");
            TextureOrphanSweeper.MaxPerSweep = Config.Bind(
                "Wardrobe", "TexOrphanMaxPerSweep", 16,
                "Max orphan cache entries released per 10s sweep — spreads the work.");
            SceneOrphanSweep.SliceMs = Config.Bind(
                "Wardrobe", "OrphanSweepSliceMs", 3f,
                "Per-frame CPU budget (ms) for the post-scene-load orphan sweep. It used to run every leg inside one frame (measured 10.8s freeze); the same work is now spread across frames under this budget. Range 0.5 (lightest, slowest) to 16 (heaviest, fastest).");
            SceneOrphanSweep.SweepObjects = Config.Bind(
                "Wardrobe", "OrphanSweepObjects", false,
                "Include the GameObject census leg of the post-scene-load orphan sweep (groups inactive (Clone) trees, feeds the clone-kill pass). Measured 16.1s of a 17.7s sweep with zero kills, so it is off unless a clone leak is suspected.");
            SceneOrphanSweep.Census = Config.Bind(
                "Wardrobe", "OrphanSweepCensus", false,
                "Include the observation-only component census leg of the post-scene-load orphan sweep (stranded-component count plus a per-MonoBehaviour-type histogram). Nothing but the log line reads it, and it measured 6147ms of slice time in the last sweep, so it is off unless the census is what is being investigated.");
            SceneOrphanSweep.MeasureGC = Config.Bind(
                "Wardrobe", "OrphanSweepMeasureGC", false,
                "The sweep additionally forces a full GC just to log liveMiB vs retained heap. Off: the same numbers come from the scheduled PresetSweepGate GC without a multi-second collect inside the sweep.");
            DecodedBufferPool.Enabled = Config.Bind(
                "Wardrobe", "DecodedBufferPool", true,
                "Pool QueuedImage.raw decode buffers by exact size — reuses the arrays instead of allocating ~1.5GB of throwaway managed bytes per person preset load.");
            DecodedBufferPool.BudgetMiB = Config.Bind(
                "Wardrobe", "DecodedBufferPoolBudgetMiB", 1536,
                "Total byte cap for pooled decode buffers; excess arrays are left to GC.");
            TextureCacheByteReuse.Enabled = Config.Bind(
                "TextureLoading", "ReuseCachedTextureBytes", true,
                "Read a completed .vamcache through the exact-length decode pool instead of a fresh allocation. A preset switch served entirely from the texture cache still allocated 0.4-1.4GB of throwaway managed bytes per cycle (texture-budget managedDecodeMiB). Any deviation from the native read falls back to the native read.");
            TextureCacheByteReuse.MinBytesKB = Config.Bind(
                "TextureLoading", "ReuseCachedTextureBytesMinKB", 256,
                "Smallest cached texture (KB) worth pooling; thumbnails, meta files and other small reads stay on the native path.");
            NativeCacheBuffer.Enabled = Config.Bind(
                "TextureLoading", "NativeCacheReadBuffers", true,
                "Stage a completed .vamcache read in a native block (VirtualAlloc) and upload it with Texture2D.LoadRawTextureData(IntPtr,int); the block is released as soon as the upload consumed it, so its pages go back to the OS instead of joining Boehm's free lists for the rest of the session. Small files, oversize files, budget and every read failure fall back to the pooled managed read, so the request always uploads the same bytes.");
            NativeCacheBuffer.BudgetMiB = Config.Bind(
                "TextureLoading", "NativeCacheReadBudgetMiB", 1024,
                "Total bytes that may be staged natively at once; above it a request falls back to the pooled managed read.");
            NativeCacheBuffer.IdleMiB = Config.Bind(
                "TextureLoading", "NativeCacheReadReuseMiB", 192,
                "Committed native bytes kept for reuse after an upload. Above the cap blocks are released to the OS instead of being held; larger keeps more reuse, smaller holds less.");
            NativeCacheBuffer.MaxFileMiB = Config.Bind(
                "TextureLoading", "NativeCacheReadMaxFileMiB", 512,
                "Largest single cache file staged natively; anything bigger keeps the pooled managed read.");
            WardrobeJanitor.PurgeMorphDeltas = Config.Bind(
                "Wardrobe", "PurgeMorphDeltas", true,
                "The post-load purge also calls UnloadRuntimeMorphDeltas (the same call VaM's optimize-memory makes) to drop the previous preset's runtime morph deltas.");
            InstanceAssetLedger.Enabled = Config.Bind(
                "Wardrobe", "InstanceAssetLedger", true,
                "Record the materials/meshes a clothing or hair instance referenced before the janitor destroys it, so the objects it leaves behind can be released without a full-heap UUA mark. Registration only reads the shared* accessors; it never instantiates and never destroys.");
            InstanceAssetLedger.DestroyMaterials = Config.Bind(
                "Wardrobe", "InstanceAssetDestroyMaterials", false,
                "Release the ledger's material candidates that no live renderer, graphic or skinned mesh references. Only Unity-named \"(Clone)\" runtime instances qualify; package assets are counted, never touched. Off by default: read the asset-ledger log line for one swap first.");
            InstanceAssetLedger.DestroyMeshes = Config.Bind(
                "Wardrobe", "InstanceAssetDestroyMeshes", false,
                "Release unreferenced meshes from the ledger as well. Off by default and not recommended until the log shows what the mesh candidates are: Destroy() on a mesh that came from a package is how a character loses hair or turns pink.");
            InstanceAssetLedger.SettleSeconds = Config.Bind(
                "Wardrobe", "InstanceAssetSettleSeconds", 2f,
                "Seconds between an instance unload and the liveness census for its objects, so the native teardown and image tail have finished first.");
            _memSnapshotEntry = Config.Bind(
                "Diagnostics", "MemorySnapshot", false,
                "One-shot memory breakdown: set true (the cfg reloads live) and the plugin logs process/managed/texture/mesh/audio/atom numbers to the BepInEx log, then resets itself to false.");
            Config.Bind("Diagnostics", "EyeMaterialSnapshot", false,
                "Read-only one-shot eye/lash material bindings in the log; auto-resets, no scene changes.");
            Config.Bind("Diagnostics", "MemoryRetentionReport", false,
                "One-shot read-only retention report: set true (the cfg reloads live) and the plugin logs the collection-field inventory of this plugin and of Assembly-CSharp, then resets itself to false. Measurement only: it never releases anything.");
            Config.Bind("Diagnostics", "MemoryRetentionReportDeep", false,
                "One-shot retention report plus a GameObject/Transform/Component census; freezes the frame for seconds, auto-resets, no scene changes.");
            Config.Bind("Diagnostics", "MemoryRetentionReportGameScan", true,
                "Include the Assembly-CSharp half of the retention report. Reading a static field runs that type's initializer, so setting this to false and editing the cfg again stops the game half immediately; the plugin-assembly half keeps working.");
            _memWatchEntry = Config.Bind(
                "Diagnostics", "MemWatchSeconds", 15,
                "Periodic one-line memory/VRAM/texture-cache snapshot (snap[watch]); 0=off. Cheap — safe to leave on while hunting leaks.");

            Instance = this;
            Log = Logger;
            BodySmootherCompatibility.Install();
            ClothingScriptAssemblyReuse.Install();
            PresetDeltaApply.Install();
            PresetHairRenderBatch.Install();
            PresetSweepGate.Install();
            TextureUploadReuse.Install();
            TextureInFlight.Install();
            TextureCompletionBudget.Install();
            TextureMetadataReuse.Install();
            TextureScratchLifetime.Install();
            TextureCacheWriteBudget.Install();
            TextureCacheBc7Convert.Install();
            CharacterLoadTrace.Enabled = Config.Bind("Diagnostics", "TraceCharacterLoads", true,
                "Write one Quest3TriggerUI.charactertrace_<stamp>.tsv window per person/appearance preset load: every phase the plugin and the loader log, plus hitches and memory, keyed by request id.");
            CharacterLoadTrace.Install();
            ItemBuildProbe.Enabled = Config.Bind("Diagnostics", "TraceItemBuilds", true,
                "One [item-build] line per clothing/hair item whose DAZDynamic::Load passes 100ms: package:store + wall clock. Attribution only; no behaviour change.");
            ItemBuildProbe.Install();
            StreamReadAccel.Enabled = Config.Bind("FileLoading", "AccelerateFileStreamReads", true,
                "Wrap file entry streams in a 64 KiB buffer: zip entry streams out of .var packages and opened FileStreams. VaM's binary loaders (mesh, cloth geometry, hair, morph) ask for a few bytes per call straight into SharpZipLib's unbuffered PartialInputStream, which seeks the shared package FileStream on every call - 金瓶儿's six .vab (11.4 MiB) cost 8.4s, i.e. 1.43 MB/s. Byte content and order are unchanged; only the fetching is batched.");
            StreamReadAccel.ProbeOnce = Config.Bind("Diagnostics", "StreamReadProbeOnce", true,
                "One-shot: on the next DAZDynamic::Load, time 4-byte reads vs 64 KiB reads vs buffered 4-byte reads on that item's .vab and log one [stream-accel] probe line, then reset this to false. Measurement only.");
            StreamReadAccel.Install();
            UnloadCoverageProbe.Once = Config.Bind("Diagnostics", "UnloadCoverageProbeOnce", false,
                "One-shot coverage probe: on the next UnloadInstance, capture the dying subtree\'s materials/meshes and count how many are exclusively owned by it (safe to unload) vs shared with other live renderers (pink if unloaded). Logs one [unload-coverage] line then resets to false. Measurement only; no behaviour change.");
            UnloadCoverageProbe.Install();
            UuaTypeCensus.Enabled = Config.Bind("Diagnostics", "UuaTypeCensus", true,
                "Measure what each Resources.UnloadUnusedAssets sweep actually reclaims, by class: one census of Material/Mesh/Texture2D/RenderTexture counts and estimated bytes plus the Unity allocator, graphics-driver and mono totals immediately before the sweep, one right after its AsyncOperation reports done, then the delta. UUA's price is the full-heap mark (8-21s here, unloaded=0..5), so this is the number that decides whether the swap path can stop calling it. Measurement only, but the two censuses cost about 0.3-0.5s per sweep.");
            UuaTypeCensus.Heavy = Config.Bind("Diagnostics", "UuaTypeCensusHeavy", false,
                "Also enumerate GameObject and Component in the census. SceneOrphanSweep measured those legs at 4.7s/16.1s including per-object analysis; a bare count may be far cheaper, and every [uua-census] line reports censusMs so the real cost shows up. Off by default because it lands on the sweep path.");
            UuaGate.Enabled = Config.Bind("Diagnostics", "UuaGate", true,
                "Demote the native sweep the preset path submits after a swap settles. Measured over four real swaps (character x2, appearance, clothing): 8.57-9.12s of main-thread stall for 0-16 MiB of CPU allocation, 0 MiB of VRAM, 3-6 materials, 0-3 textures totalling <=1 MiB and no render targets, while the managed GC in the same swap frees 2.5-3.0GB in ~3.7s. Only a sweep whose caller chain names PresetSweepGate.SubmitSweep is skipped, and only after a real sweep has run once in this session (its completed operation is what gets handed back); standby, scene load, janitor cleanup and unrecognised callers keep their sweep. On by default (the enabled path): this changes behaviour on the swap path; set false to hand every sweep back to the engine.");
            UuaGate.BackstopSeconds = Config.Bind("Diagnostics", "UuaGateBackstopSeconds", 180,
                "Hard bound on how long the preset path may keep handing back a completed operation instead of running a real sweep. A swap-only session must not drift forever, so once this many seconds have passed since the last real sweep the next demotable call really sweeps (8-21s). Set 0 to keep the 180s default.");
            UuaGate.MaxSkips = Config.Bind("Diagnostics", "UuaGateMaxSkips", 8,
                "Hard bound on how many demotable sweeps may be handed a completed operation in a row. Reaching either this or UuaGateBackstopSeconds lets the next one run for real. Set 0 to keep the 8 default.");
            UuaGate.Report();
            FaceDetailDistanceGuard.Report();
            UuaTypeCensus.Install();
            LongFrameWatch.Install();
            TextureDecodeBudget.ColdEstimate = ColdTextureHeader.Estimate;
            GenBridge.AdoptPreviousGeneration();
            DecodedBufferPool.Adopt();
            DecodedBufferPool.Publish();
            PinyinEngine.Initialize();
            PinyinEngine.EnsureLoaded();   // background — 48MB dict parse
            _auxiliaryUiView = VrAuxiliaryUiView.Begin();
            Trigger = new TriggerStateMachine(longPress.Value, pressThreshold.Value, releaseThreshold.Value);
			LeftTrigger = new TriggerStateMachine(longPress.Value, pressThreshold.Value, releaseThreshold.Value);
			RightGripTrigger = new TriggerStateMachine(
				longPress.Value, pressThreshold.Value, releaseThreshold.Value);
            KeyboardChord = new KeyboardChordStateMachine(
                chordTap.Value, pressThreshold.Value, releaseThreshold.Value);
            RecenterChord = new KeyboardChordStateMachine(
                chordTap.Value, pressThreshold.Value, releaseThreshold.Value);
            ShortcutGestures = new ShortcutGestureBank(pressThreshold.Value, releaseThreshold.Value);
            _keyboard = new VrKeyboardOverlay(this, keyboardScale.Value, keyboardDistance.Value, keyboardOpacity.Value);
            _radialMenu = new VrRadialMenu(
                _keyboard.QuickActions, keyboardScale.Value, keyboardDistance.Value,
                discOpacity.Value);
            _quickActionRevision = _keyboard.QuickActionRevision;
            _pinnedTiles = new VrPinnedActionTiles(delegate(string id) { return _radialMenu.FindAction(id); }, radialOpacity.Value);
            _radialMenu.SetPinnedTiles(_pinnedTiles);
            _globalPitch = new GlobalVrPitchController(
                pitchSpeed.Value, pitchLimit.Value, pitchDeadzone.Value, invertPitch.Value);
            _embodyNavigationGuard = new EmbodyNavigationGuard();
            _presetBrowserFileTools = new PresetBrowserFileTools();
            _aimGuide = new VrAimGuide();
            _globalCursor = new VrGlobalCursor(globalCursor.Value);
            if (Environment.GetEnvironmentVariable("QUEST3_KEYBOARD_SELFTEST") == "1")
                _targetedInputSelfTestPhase = 1;

            AwakeDiag("awake entering patch region asm=" + GetType().Assembly.FullName);
            DoorstopIcallRepair.TryInstall();
            Logger.LogInfo("Doorstop GC icall repair: " + DoorstopIcallRepair.Status);
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Quest3TriggerUIPlugin).Assembly);
            AwakeDiag("PatchAll done");
            VrHaptics.ApplyExecutePatch(_harmony);
            // Keep the game loop and rendering active while the desktop window is in
            // the background; the OpenComposite/VDXR/OFXR path needs continuous frames.
            Application.runInBackground = true;
            Logger.LogInfo("Quest 3 Trigger UI: runInBackground forced enabled.");
            Logger.LogInfo(
				"Quest 3 full VR keyboard v" + PluginVersion + " ready: tap right index+grip to show/hide; " +
                "left index=UI click; right-index hold over slider=drag slider without radial; " +
                "both grips=recenter; toolbar=Embody/Recording; temporary shortcuts=ready; " +
                "hold right index=show radial; center=pin mode; pinned tiles=drag/delete; right-grip long press=Grab; " +
                "right-thumbstick vertical=Embody-only VR pitch; " +
                "right-grip+right-thumbstick vertical=non-Embody VR pitch; " +
                "preset browser=top toolbar + create folder + move preset bundle; person preset=exact replay including clothing; skin preset=nearest female; refresh VAR=toolbar action; " +
                "standby=resident scene + paused simulation + UI-only cameras + 15 FPS + immediate restore; " +
                "shared quick-action catalog=ready; bindable actions=ready; " +
                "SevenSeason expression submenu=non-destructive preload/playback + per-expression Timeline + toggle + optional mixing + tongue baseline restore; " +
                "keyboard eye-gap slider=50% calibrated midpoint/2x open upper/stable eyelid writer/blink blend/nearest female/non-radial; UIAssist clothing editor=forced direct display without reopening control panel; " +
                "title bar=hold-to-move; " +
                "scene load=staged timing + dependency cache + in-process switch; " +
                "keys=in-process Unity Input bridge + VaM window; VR Chinese IME=pinyin composition + clickable candidates; " +
                "desktop focus independent; preheat=headless startup (person prefab + morph-bank catalogue caches, no clone pool); no SteamVR dependency.");
            RuntimeReady = true;
            _duplicateSweepFrame = Time.frameCount + 2;
            Logger.LogInfo("payload awake asm=" + GetType().Assembly.GetHashCode() +
                " go='" + (gameObject != null ? gameObject.name : "?") + "'");
        }

        private void RemoveDuplicateRuntimeInstances()
        {
            int removed = 0, staleObjects = 0;
            var staleGos = new System.Collections.Generic.HashSet<GameObject>();
            MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour candidate = behaviours[i];
                if (candidate == null || ReferenceEquals(candidate, this))
                    continue;
                Type t = candidate.GetType();
                if (t.Namespace == null ||
                    !t.Namespace.StartsWith("Quest3TriggerUI") ||
                    t.Assembly == GetType().Assembly)
                    continue;
                try { if (!string.IsNullOrEmpty(t.Assembly.Location)) continue; }
                catch { }
                // Runtime instances live on the shared loader gameObject —
                // destroy the component only, never the host object.
                if (t.Name == "Quest3TriggerUIPlugin")
                {
                    UnityEngine.Object.DestroyImmediate(candidate);
                    removed++;
                    continue;
                }
                // GenAnchor carries the cross-generation handoff store — it
                // must survive this sweep or the shared dictionaries die
                // with its GameObject. GenBridge.EnsureAnchor destroys the
                // old component itself once the fields are harvested.
                if (t.Name == "GenAnchor") continue;
                // Leftover UI objects from older payload generations (dock
                // cells, browser rows, keyboard keys) pin their whole byte-
                // loaded assembly — statics, pooled buffers, thumbnails — in
                // memory forever, since Mono can never unload it. Destroying
                // the gameObject releases the entire subtree.
                //
                // But only when the host carries nobody else's scripts:
                // a stale tag component sitting on a VaM/plugin-owned
                // object (e.g. our drag markers on BrowserAssist buttons)
                // must come off alone — destroying the host would leave
                // the owner's managed collections pointing at dead
                // objects, which is exactly the BA row-refactor NRE.
                GameObject host = candidate.gameObject;
                if (host != null && SceneOrphanSweep.HostHasAlienScript(host))
                {
                    UnityEngine.Object.DestroyImmediate(candidate);
                    removed++;
                }
                else if (host != null) staleGos.Add(host);
            }
            foreach (GameObject go in staleGos)
            {
                UnityEngine.Object.Destroy(go);
                staleObjects++;
            }
            ClearForeignPools();
            SweepStaleGenerations();
            if (removed > 0)
                Logger.LogWarning("Removed " + removed +
                    " duplicate Quest3TriggerUI runtime instance(s) and their overlapping canvases.");
            if (staleObjects > 0)
                Logger.LogInfo("Destroyed " + staleObjects +
                    " stale UI objects from older payload generations (frees their assemblies' statics).");
        }

        // Older payload assemblies can never unload under Mono, but their
        // static pools still pin the buffers they hold. Ask each foreign
        // DecodedBufferPool to drop its arrays via reflection.
        private void ClearForeignPools()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == GetType().Assembly) continue;
                Type pool;
                try { pool = assembly.GetType("Quest3TriggerUI.DecodedBufferPool"); }
                catch { continue; }
                if (pool == null)
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch { continue; }
                    for (int i = 0; i < types.Length; i++)
                    {
                        Type t = types[i];
                        if (t != null && t.Name == "DecodedBufferPool" &&
                            t.Namespace != null && t.Namespace.StartsWith("Quest3TriggerUI"))
                        { pool = t; break; }
                    }
                }
                if (pool == null) continue;
                MethodInfo clear = pool.GetMethod("Clear",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                try { if (clear != null) clear.Invoke(null, null); } catch { }
            }
        }

        // Byte-loaded payload assemblies can never unload under Mono, so each
        // hot-load generation leaks whatever still references it. Two pins are
        // recoverable in-process:
        //   1. Harmony patches installed by dead generations — the global patch
        //      registry keeps their HarmonyMethod delegates, which pin the old
        //      assembly's code AND keep executing it (double handlers).
        //   2. Writable reference-type static fields — pools, thumbnail
        //      caches, listener lists, coroutine state — that survive even
        //      after every GameObject from that generation is destroyed.
        // Remove stale patches first so no dead code runs after its statics
        // are nulled. JIT code and metadata still leak (irreducible) — that is
        // why the HotLoader caps generations per session.
        private void SweepStaleGenerations()
        {
            Assembly current = GetType().Assembly;
            var stale = new System.Collections.Generic.HashSet<Assembly>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == current) continue;
                try { if (!string.IsNullOrEmpty(assembly.Location)) continue; }
                catch { continue; }
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                if (types == null) continue;
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t != null && t.Namespace != null &&
                        t.Namespace.StartsWith("Quest3TriggerUI"))
                    { stale.Add(assembly); break; }
                }
            }
            if (stale.Count == 0) return;

            int patches = 0;
            Harmony sweeper = null;
            try { sweeper = new Harmony(PluginGuid + ".stale-sweep"); }
            catch { }
            if (sweeper != null)
            {
                foreach (MethodBase original in Harmony.GetAllPatchedMethods())
                {
                    Patches info;
                    try { info = Harmony.GetPatchInfo(original); }
                    catch { continue; }
                    if (info == null) continue;
                    patches += UnpatchStale(sweeper, original, info.Prefixes, stale);
                    patches += UnpatchStale(sweeper, original, info.Postfixes, stale);
                    patches += UnpatchStale(sweeper, original, info.Transpilers, stale);
                    patches += UnpatchStale(sweeper, original, info.Finalizers, stale);
                }
            }

            int fields = 0;
            foreach (Assembly dead in stale)
            {
                Type[] types;
                try { types = dead.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                if (types == null) continue;
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null) continue;
                    FieldInfo[] statics;
                    try
                    {
                        statics = t.GetFields(BindingFlags.Static |
                            BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch { continue; }
                    for (int j = 0; j < statics.Length; j++)
                    {
                        FieldInfo f = statics[j];
                        if (f.IsLiteral || f.FieldType.IsValueType) continue;
                        try
                        {
                            if (f.GetValue(null) == null) continue;
                            f.SetValue(null, null);
                            fields++;
                        }
                        catch { }
                    }
                }
            }
            Logger.LogInfo("stale payload sweep: removed " + patches +
                " Harmony patch(es), nulled " + fields + " static field(s) on " +
                stale.Count + " dead generation(s).");
        }

        private static int UnpatchStale(Harmony sweeper, MethodBase original,
            System.Collections.IEnumerable list,
            System.Collections.Generic.HashSet<Assembly> stale)
        {
            int n = 0;
            if (list == null) return 0;
            foreach (object entry in list)
            {
                Patch patch = entry as Patch;
                if (patch == null) continue;
                MethodInfo pm;
                try { pm = patch.PatchMethod; } catch { continue; }
                if (pm == null || pm.DeclaringType == null) continue;
                if (!stale.Contains(pm.DeclaringType.Assembly)) continue;
                try { sweeper.Unpatch(original, pm); n++; } catch { }
            }
            return n;
        }

        // A crash or reload mid-recording destroys this runtime but leaves
        // the previous payload's VrAudioTap on the scene AudioListener: it
        // keeps feeding that payload's static recorder, so the .audio.wav
        // grows forever while the new UI reports nothing recording. Dispose
        // every foreign-assembly recorder (finalizes the WAV header, closes
        // the stream, stops its ffmpeg) and destroy tap components that
        // outlived their payload.
        private void RecoverOrphanedRecorders()
        {
            int disposed = 0, taps = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                if (types == null) continue;
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null || t == typeof(VrVideoRecorder)) continue;
                    if (t.Name != "VrVideoRecorder" || t.Namespace == null ||
                        !t.Namespace.StartsWith("Quest3TriggerUI"))
                        continue;
                    FieldInfo current = t.GetField("Current",
                        BindingFlags.Static | BindingFlags.NonPublic |
                        BindingFlags.Public);
                    if (current == null) continue;
                    object recorder;
                    try { recorder = current.GetValue(null); }
                    catch { continue; }
                    if (recorder == null) continue;
                    try
                    {
                        t.GetMethod("Dispose").Invoke(recorder, null);
                        disposed++;
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning("Orphaned recorder dispose failed: " + e);
                    }
                }
            }
            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null) continue;
                Type t = mb.GetType();
                if (t == typeof(VrAudioTap) || t == typeof(VrVideoTap)) continue;
                string name = t.Name;
                if ((name != "VrAudioTap" && name != "VrVideoTap") ||
                    t.Namespace == null ||
                    !t.Namespace.StartsWith("Quest3TriggerUI") ||
                    t.Assembly == GetType().Assembly)
                    continue;
                UnityEngine.Object.Destroy(mb);
                taps++;
            }
            if (disposed > 0 || taps > 0)
                Logger.LogInfo("Recovered orphaned recording: disposed=" +
                    disposed + " stale taps destroyed=" + taps);
        }

        private void Update()
        {
            LoadAttributionProbe.BeginFrame();
            LiveSetCensus.BeginFrame();
            BrowserAssistScanAccelerator.VarProbeFrame();
            long __frameBudgetT0 = FrameBudgetProbe.MarkUpdateStart();
            try
            {
                UpdateInner();
            }
            finally
            {
                LoadAttributionProbe.EndFrame();
                FrameBudgetProbe.MarkUpdateEnd(__frameBudgetT0);
            }
        }

        private void UpdateInner()
        {
            if (!_updateLogged) { _updateLogged = true; Logger.LogInfo("payload update asm=" + GetType().Assembly.GetHashCode()); }
            UiAssistHudLink.Observe();
            LoadAttributionProbe.Mark(1);
            BrowserAssistScanAccelerator.Tick();
            SceneLoadAccelerator.Tick();
            SceneResyncCoalesce.Tick();
            ScenePreheat.Tick();
            LoadAttributionProbe.Mark(2);
            if (_duplicateSweepFrame >= 0 && Time.frameCount >= _duplicateSweepFrame)
            {
                _duplicateSweepFrame = -1;
                RemoveDuplicateRuntimeInstances();
            }

            // Orphaned hide-groups injected by earlier payload generations
            // leave the control panel grey and unclickable for the whole
            // session. Clean once the HUD exists — Awake can precede it.
            if (!_hudCleanupDone && SuperController.singleton != null &&
                SuperController.singleton.mainHUD != null)
            {
                _hudCleanupDone = true;
                VrPresetBrowser.CleanupHudState();
                PackageManagerGuard.RemoveEntryButtons();
            }

            // First-open stall fix: a freshly (re)loaded payload has cold
            // JIT + empty dir/thumb caches, so the first browser open used
            // to freeze for seconds. Spread the warmup across frames.
            if (_warmupPending && SuperController.singleton != null)
            {
                if (_warmupDelay > 0f)
                    _warmupDelay -= Time.unscaledDeltaTime;
                else
                {
                    VrPresetBrowser.BeginWarmup();
                    if (!VrPresetBrowser.WarmupStep())
                        _warmupPending = false;
                }
            }

            if (_targetedInputSelfTestPhase != 0)
                RunTargetedInputSelfTest();

            // The native preset browser is also used by VDXR paths where
            // OVRManager.isHmdPresent can be false. Keep its toolbar outside
            // the Quest-controller input gate so it is always attached.
            if (SuperController.singleton != null)
                _presetBrowserFileTools.Tick();

            HairPerfProbe.Tick();
            LoadAttributionProbe.Mark(3);

            BodySmootherCompatibility.Tick();
            // Config file self-watch: the hot-loaded payload can't rely on
            // BepInEx's FileSystemWatcher (observed not firing for live
            // edits), so poll the cfg mtime and scan the flag directly —
            // no dependence on Config.Reload() working in this context.
            _cfgWatchT -= Time.unscaledDeltaTime;
            if (_cfgWatchT <= 0f)
            {
                _cfgWatchT = 0.5f;
                try
                {
                    DateTime mtime = System.IO.File.GetLastWriteTimeUtc(
                        Config.ConfigFilePath);
                    if (mtime != _cfgLastWrite)
                    {
                        bool first = _cfgLastWrite == DateTime.MinValue;
                        _cfgLastWrite = mtime;
                        if (!first)
                            Logger.LogInfo(
                                "[MemProbe] cfg mtime changed, scanning flag");
                        CheckMemorySnapshotFlag();
                    }
                }
                catch { }
            }
            if (_memSnapshotEntry != null && _memSnapshotEntry.Value)
            {
                _memSnapshotEntry.Value = false;
                MemoryProbe.Dump();
            }
            if (_memWatchEntry != null && _memWatchEntry.Value > 0 &&
                Time.unscaledTime >= _nextMemWatch)
            {
                _nextMemWatch = Time.unscaledTime + _memWatchEntry.Value;
                MemoryProbe.Snapshot("watch");
            }
            AudioDeviceFollower.Tick();
            AudioCacheJanitor.Tick();
            LoadAttributionProbe.Mark(4);
            WardrobeJanitor.Tick();
            FaceDetailDistanceGuard.Tick();
            GenBridge.Tick();
            PinyinEngine.IdleTick();
            LoadAttributionProbe.Mark(5);
            PresetSweepGate.Tick();
            MemoryRetentionReport.Tick();
            MeshOwnerRetentionProbe.Tick();
            UnloadCoverageProbe.Tick();
            UuaTypeCensus.Tick();
            SceneLoadAccelerator.RetryPendingBrackets();
            SceneOrphanSweep.Tick();
            TextureCacheBc7Convert.Tick();
            CharacterLoadTrace.Tick();
            LongFrameWatch.Tick();
            LoadAttributionProbe.Mark(6);

            if (!InputRuntimeActive || SuperController.singleton == null)
                return;

            SampleInputs();
            VrHaptics.Tick();
            // Level syncs on change only — per-frame override syncing would
            // stomp runtime lever changes made through the menu.
            if (_physicsBudgetEntry != null &&
                _physicsBudgetEntry.Value != PhysicsBudget.Level)
                PhysicsBudget.SetLevel(_physicsBudgetEntry.Value);
            if (_physicsHairCollEntry != null &&
                _physicsHairCollEntry.Value != PhysicsBudget.HairCollisionMode)
                PhysicsBudget.HairCollisionMode = _physicsHairCollEntry.Value;
            PhysicsBudget.Tick();
            LoadAttributionProbe.Mark(7);
            _keyboard.Tick();
            LoadAttributionProbe.Mark(8);
            if (_quickActionRevision != _keyboard.QuickActionRevision)
            {
                _quickActionRevision = _keyboard.QuickActionRevision;

                _radialMenu.RefreshActions(_keyboard.QuickActions);
            }
            _radialMenu.Tick();
            _pinnedTiles.Tick(Trigger, RightGripTrigger);
            HairDebugMode.Tick();
            ClothingRegionMode.Tick();
            PluginListMode.Tick();
            LoadAttributionProbe.Mark(9);
            int frame = Time.frameCount;
            // The drag answers to whichever hand's trigger armed it —
            // candidate.RightPointer picks the state machine so a left-hand
            // grab releases on the left trigger, not the right.
            TriggerStateMachine dragTrigger = ClothingDragCandidate != null &&
                !ClothingDragCandidate.RightPointer ? LeftTrigger : Trigger;
            if (ClothingDragCandidate != null && dragTrigger != null &&
                dragTrigger.LongPressStartFrame == frame)
            {
                ClothingDragActive = UiAssistHudLink.BeginFavoriteDrag(ClothingDragCandidate);
            }
            if (ClothingDragActive && dragTrigger != null)
            {
                if (dragTrigger.ReleasedFrame == frame)
                {
                    UiAssistHudLink.EndFavoriteDrag(ClothingDragCandidate);
                    ClothingDragActive = false;
                    ClothingDragCandidate = null;
                }
                else
                {
                    UiAssistHudLink.TickFavoriteDrag();
                }
            }
            if (Trigger != null && Trigger.LongPressStartFrame == frame)
            {
                if (_keyboard.Visible)
                    Logger.LogInfo("Q3 radial blocked: keyboard visible");
                else if (SliderDragActive)
                    Logger.LogInfo("Q3 radial blocked: slider drag active");
                else if (RadialDragBlocked)
                    Logger.LogInfo("Q3 radial blocked: captured drag gesture");
                else if (ClothingRegionMode.PointerInside)
                    Logger.LogInfo("Q3 radial blocked: regional clothing drag");
                else if (PluginListMode.PointerInside)
                    Logger.LogInfo("Q3 radial blocked: pointer inside plugin panel");
                else if (VrPresetBrowser.PointerInside)
                    Logger.LogInfo("Q3 radial blocked: pointer inside browser");
                else if (_pinnedTiles != null && _pinnedTiles.PointerOverTile)
                    Logger.LogInfo("Q3 radial blocked: pointer over pinned tile");
                else if (VrShotCameras.PointerInside)
                    Logger.LogInfo("Q3 radial blocked: pointer inside shot panel");
                else if (KeyboardChord != null && KeyboardChord.Active)
                    Logger.LogInfo("Q3 radial blocked: chord active (grip latch?)");
                else
                {
                    _radialMenu.ShowHold();
                    Logger.LogInfo("VR radial quick menu shown for hold selection. asm=" +
                        GetType().Assembly.GetHashCode());
                }
            }
            if (_radialMenu.Visible && Trigger != null &&
                Trigger.LongPressReleaseFrame == frame)
            {
                string selectedAction = _radialMenu.ReleaseAndSelect();
                Logger.LogInfo(selectedAction == null
                    ? "VR radial quick menu closed without selection."
                    : "VR radial quick action selected: " + selectedAction);
            }
            if (_radialMenu.Visible && _radialMenu.PinMode && Trigger != null &&
                Trigger.TapFrame == frame)
            {
                string pinnedAction = _radialMenu.FixedTapSelect();
                if (pinnedAction != null) Logger.LogInfo("VR radial " + pinnedAction);
            }
            if (!_radialMenu.Visible)
            {
                _keyboard.HandleShortcutGestures(ShortcutGestures, frame);
            }
            if (KeyboardChord != null && KeyboardChord.ToggleFrame == frame && _handledToggleFrame != frame)
            {
                _handledToggleFrame = frame;
                _radialMenu.Hide();
                _keyboard.Toggle();
                Logger.LogInfo(_keyboard.Visible ? "VR keyboard shown." : "VR keyboard hidden.");
            }
            if (_keyboard.Visible && RecenterChord != null &&
                RecenterChord.ToggleFrame == frame && _handledRecenterFrame != frame)
            {
                _handledRecenterFrame = frame;
                _keyboard.Recenter();
                Logger.LogInfo("VR keyboard recentered by left+right grip tap.");
            }
        }

        // Reads the trigger flag straight from the cfg text: if a live
        // edit set MemorySnapshot=true, dump and write the flag back to
        // false so it is one-shot. Encoding/BOM preserved.
        private void CheckMemorySnapshotFlag()
        {
            try
            {
                string path = Config.ConfigFilePath;
                byte[] raw = System.IO.File.ReadAllBytes(path);
                string text = System.Text.Encoding.UTF8.GetString(raw);
                bool snap = text.Contains("MemorySnapshot = true");
                bool eye = text.Contains("EyeMaterialSnapshot = true");
                bool evict = text.Contains("AudioCacheEvictNow = true");
                bool ucov = text.Contains("UnloadCoverageProbeOnce = true");
                bool uip = text.Contains("UiRaycastCensusOnce = true");
                bool mrr = text.Contains("MemoryRetentionReport = true");
                bool mrd = text.Contains("MemoryRetentionReportDeep = true");
                MemoryRetentionReport.GameScan =
                    !text.Contains("MemoryRetentionReportGameScan = false");
                if (!snap && !evict && !eye && !ucov && !uip && !mrr && !mrd)
                {
                    Logger.LogInfo("[MemProbe] flag not set in cfg");
                    return;
                }
                text = text.Replace(
                    "MemorySnapshot = true", "MemorySnapshot = false");
                text = text.Replace(
                    "AudioCacheEvictNow = true", "AudioCacheEvictNow = false");
                text = text.Replace("EyeMaterialSnapshot = true", "EyeMaterialSnapshot = false");
                text = text.Replace(
                    "UnloadCoverageProbeOnce = true",
                    "UnloadCoverageProbeOnce = false");
                text = text.Replace(
                    "UiRaycastCensusOnce = true",
                    "UiRaycastCensusOnce = false");
                text = text.Replace(
                    "MemoryRetentionReport = true",
                    "MemoryRetentionReport = false");
                text = text.Replace(
                    "MemoryRetentionReportDeep = true",
                    "MemoryRetentionReportDeep = false");
                // GetBytes re-emits the BOM because the decoded string
                // still carries the \uFEFF character.
                System.IO.File.WriteAllBytes(
                    path, System.Text.Encoding.UTF8.GetBytes(text));
                _cfgLastWrite =
                    System.IO.File.GetLastWriteTimeUtc(path);
                if (snap) MemoryProbe.Dump();
                if (eye) CharacterMaterialProbe.Dump();
                if (evict) AudioCacheJanitor.SweepNow();
                if (ucov) UnloadCoverageProbe.Arm();
                if (uip) UiRaycastProbe.Dump();
                if (mrr) MemoryRetentionReport.Report("once");
                if (mrd) MemoryRetentionReport.DeepReport("once-deep");
            }
            catch (Exception ex)
            {
                Logger.LogError("MemorySnapshot flag check failed: " +
                    ex.Message);
            }
        }

        private void LateUpdate()
        {
            UiAssistHudLink.ApplyPanelPresentation();
            UiAssistHudLink.FlushAceRefresh();
            if (_keyboard != null)
            {
                _keyboard.LateTick();
                _keyboard.ThrottleStandbyFrame();
            }

            SuperController controller = SuperController.singleton;
            bool radialVisible = _radialMenu != null && _radialMenu.Visible;
            bool showAimGuide = InputRuntimeActive && controller != null &&


                (_keyboard == null || !_keyboard.StandbyActive);
            if (_aimGuide != null)
                _aimGuide.Tick(showAimGuide,
                    controller == null ? null : VrPointerPresentation.MotionController(controller, false),
                    controller == null ? null : VrPointerPresentation.MotionController(controller, true));
            if (_globalCursor != null && InputRuntimeActive)
                _globalCursor.Tick();
        }

        private void RefreshPitchInputMode()
        {
            if (_globalPitch == null)
                return;
            SuperController controller = SuperController.singleton;
            bool embodyActive = _keyboard != null && _keyboard.EmbodyActive;
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.Observe(
                    embodyActive, SuperController.singleton, _globalPitch);
            bool blockInput = _keyboard == null || _keyboard.Visible ||
                              _radialMenu == null || _radialMenu.Visible ||
                              (controller != null && controller.worldUIActivated) ||
                              (_pinnedTiles != null && _pinnedTiles.CapturingGrip) ||
                              (KeyboardChord != null && KeyboardChord.Active) ||
                              (Trigger != null && Trigger.LongPressActive) || UiAssistHudLink.PanelOrbitCapturing;
            bool gripPressed = RightGripTrigger != null && RightGripTrigger.Pressed;
            // Reuse this frame's sampled stick: SampleInputs already paid for
            // the OpenVR/Oculus read. Resampling here doubles reflection calls.
            Vector2 thumbstick;
            if (_inputSampleFrame == Time.frameCount)
            {
                thumbstick = _sampledRightStick;
            }
            else
            {
                float openVrIndex;
                float openVrGrip;
                float openVrLeftGrip;
                float openVrA;
                if (!OpenVrInputBridge.TryGetInput(
                    out openVrIndex, out openVrGrip, out openVrLeftGrip,
                    out openVrA, out thumbstick))
                {
                    thumbstick = OVRInput.Get(
                        OVRInput.Axis2D.SecondaryThumbstick,
                        OVRInput.Controller.Touch);
                }
            }
            float thumbstickY = thumbstick.y;
            _globalPitch.UpdateInputMode(
                embodyActive, gripPressed, blockInput, thumbstickY);
        }

        internal void BeforeSuperControllerUpdate(SuperController controller)
        {
            if (_globalPitch != null)
                _globalPitch.BeforeSuperControllerUpdate(controller);
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.BeforeFrame(controller, _globalPitch);
        }

        internal void BeforeControllerInteraction(SuperController controller)
        {
            if (_globalPitch == null) return;
            SampleInputs();
            RefreshPitchInputMode();
            VrShotCameras.ApplyPendingShot(controller, _globalPitch);
            _globalPitch.BeforeControllerInteraction(controller);
            UiAssistHudLink.ApplyPanelPresentation();
        }
        internal void BeforeNativeNavigation(SuperController controller)
        {
            if (_globalPitch != null)
                _globalPitch.BeforeNativeNavigation(controller);
        }
        internal void BeforePointerFinalization(SuperController controller)
        {
            if (_globalPitch != null)
                _globalPitch.BeforePointerFinalization(controller);
        }
        internal void BeforeEmbodyToggle(bool wasActive)
        {
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.BeforeToggle(
                    wasActive, SuperController.singleton, _globalPitch);
        }
        internal void AfterEmbodyToggle(bool wasActive, bool active)
        {
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.AfterToggle(wasActive, active);
        }
        internal void ForceEmbodyNavigationReset()
        {
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.ForceNavigationReset(
                    SuperController.singleton, _globalPitch);
        }
        internal void ResetGlobalPitch(SuperController controller)
        {
            if (_globalPitch != null)
                _globalPitch.Reset(controller);
        }

        private void RunTargetedInputSelfTest()
        {
            const ushort virtualKeyF12 = 0x7B;
            if (_targetedInputSelfTestPhase == 1)
            {
                if (Time.frameCount < 120)
                    return;

                _targetedInputSelfTestForeground = WindowsKeyboard.GetForegroundWindowHandle();
                bool gameForeground =
                    _targetedInputSelfTestForeground == WindowsKeyboard.GetGameWindowHandle();
                VirtualKeyboardState.KeyDown(virtualKeyF12, false);
                _targetedInputSelfTestDeadline = Time.frameCount + 30;
                _targetedInputSelfTestPhase = 2;
                Logger.LogInfo(
                    "INPROCESS INPUT SELFTEST START; Key=F12; WindowPost=False; " +
                    "GameForeground=" + gameForeground +
                    "; ApplicationFocused=" + Application.isFocused);
                return;
            }

            if (_targetedInputSelfTestPhase == 2)
            {
                if (Input.GetKeyDown(KeyCode.F12))
                {
                    _targetedInputSelfTestDown = true;
                    VirtualKeyboardState.KeyUp(virtualKeyF12, false);
                    _targetedInputSelfTestDeadline = Time.frameCount + 30;
                    _targetedInputSelfTestPhase = 3;
                }
                else if (Time.frameCount > _targetedInputSelfTestDeadline)
                {
                    VirtualKeyboardState.KeyUp(virtualKeyF12, false);
                    Logger.LogError(
                        "INPROCESS INPUT SELFTEST RESULT=FAIL; UnityKeyDown=False; ForegroundChanged=" +
                        (WindowsKeyboard.GetForegroundWindowHandle() != _targetedInputSelfTestForeground));
                    _targetedInputSelfTestPhase = 0;
                }
                return;
            }

            if (_targetedInputSelfTestPhase == 3)
            {
                if (Input.GetKeyUp(KeyCode.F12))
                {
                    bool foregroundChanged =
                        WindowsKeyboard.GetForegroundWindowHandle() != _targetedInputSelfTestForeground;
                    Logger.LogInfo(
                        "INPROCESS INPUT SELFTEST RESULT=PASS; UnityKeyDown=" +
                        _targetedInputSelfTestDown + "; UnityKeyUp=True; ForegroundChanged=" +
                        foregroundChanged);
                    _targetedInputSelfTestPhase = 0;
                }
                else if (Time.frameCount > _targetedInputSelfTestDeadline)
                {
                    Logger.LogError(
                        "INPROCESS INPUT SELFTEST RESULT=FAIL; UnityKeyDown=" +
                        _targetedInputSelfTestDown + "; UnityKeyUp=False");
                    _targetedInputSelfTestPhase = 0;
                }
            }
        }

        private void OnApplicationQuit()
        {
            TextureCacheBc7Convert.OnExit();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused && _keyboard != null)
                _keyboard.ReleaseAllKeys();
        }

        private void OnDestroy()
        {
            PinyinEngine.Shutdown();
            UiAssistHudLink.Reset(); DlssUiOverlay.Stop();
            UiNavSuppress.ReleaseAll();
            if (_auxiliaryUiView != null) DestroyImmediate(_auxiliaryUiView.gameObject);
            HairDebugMode.Shutdown();
            VrShotCameras.Shutdown();
            ClothingRegionMode.Shutdown();
            PluginListMode.Shutdown();
            MeshOwnerRetentionProbe.Shutdown();
            WardrobeJanitor.Shutdown();
            FaceDetailDistanceGuard.Shutdown();
            BodySmootherCompatibility.Shutdown();
            ClothingScriptAssemblyReuse.Shutdown();
            PresetDeltaApply.Shutdown();
            PresetHairRenderBatch.Shutdown();
            PresetSweepGate.Shutdown();
            TextureUploadReuse.Shutdown();
            TextureInFlight.Shutdown();
            TextureCompletionBudget.Shutdown();
            TextureMetadataReuse.Shutdown();
            TextureScratchLifetime.Shutdown();
            TextureCacheWriteBudget.Shutdown();
            TextureCacheBc7Convert.Shutdown();
            CharacterLoadTrace.Shutdown();
            LongFrameWatch.Shutdown();
            LoadAttributionProbe.Shutdown();
            TextureDecodeBudget.Shutdown();
            TextureOrphanSweeper.Shutdown();
            GpuResourceProbe.Shutdown();
            BumpNormalRowConverter.Shutdown();
            PresetCleanupCoalescer.Shutdown();
            UuaTypeCensus.Shutdown();
            PresetInstanceReuse.Shutdown();
            StaleTextureRequestGuard.Shutdown();
            VrPresetBrowser.Shutdown();
            VrTextInputBridge.Shutdown();
            // An undestroyed recorder outlives this runtime through the
            // AudioListener tap and writes to its .audio.wav forever.
            if (VrVideoRecorder.Current != null)
                VrVideoRecorder.Current.Dispose();
            PhysicsBudget.Restore();
            RuntimeReady = false;
            if (_globalPitch != null)
                _globalPitch.Reset(SuperController.singleton);
            if (_presetBrowserFileTools != null)
                _presetBrowserFileTools.Dispose();
            if (_keyboard != null)
                _keyboard.Dispose();
            if (_radialMenu != null)
                _radialMenu.Dispose();
            if (_pinnedTiles != null) _pinnedTiles.Dispose();
            if (_aimGuide != null) _aimGuide.Dispose();
            if (_globalCursor != null) _globalCursor.Dispose();
            VirtualKeyboardState.Clear();
            if (_harmony != null)
                _harmony.UnpatchAll(PluginGuid);

            Instance = null;
            Trigger = null;
			LeftTrigger = null;
			RightGripTrigger = null;
            KeyboardChord = null;
            RecenterChord = null;
            ShortcutGestures = null;
            _inputSampleFrame = -1;
			SliderDragActive = false;
            ClothingDragActive = false;
            ClothingDragCandidate = null;
            _radialDragBlocked = false;
            _globalPitch = null;
            if (_embodyNavigationGuard != null)
                _embodyNavigationGuard.Reset();
            _embodyNavigationGuard = null;
            _presetBrowserFileTools = null;
            _aimGuide = null;
            _globalCursor = null;
        }

        private static float _lastSampleTime = -1f;
        private static float _rightGripHeldSeconds;
        private static float _leftGripHeldSeconds;
        private static bool _rightGripStaleLogged;
        private static bool _leftGripStaleLogged;
        private static bool _gripReResolveTried;
        private const float StaleGripSeconds = 20f;
        private const float StaleGripThreshold = 0.5f;

        // A VR-runtime reconnect can freeze a SteamVR digital action at its
        // last state (e.g. grip held at disconnect stays "held"). A real grip
        // hold this long is a Grab handled natively by VaM, so zeroing our
        // sampled value past the window only costs gestures we would never
        // trigger mid-Grab anyway.
        private static float FilterStaleHold(float raw, bool right, float dt)
        {
            if (raw >= StaleGripThreshold)
            {
                if (right) _rightGripHeldSeconds += dt;
                else _leftGripHeldSeconds += dt;
            }
            else
            {
                if (right) { _rightGripHeldSeconds = 0f; _rightGripStaleLogged = false; }
                else { _leftGripHeldSeconds = 0f; _leftGripStaleLogged = false; }
                if (!_rightGripStaleLogged && !_leftGripStaleLogged)
                    _gripReResolveTried = false;
                return raw;
            }

            float held = right ? _rightGripHeldSeconds : _leftGripHeldSeconds;
            if (held <= StaleGripSeconds)
                return raw;
            if (right ? _rightGripStaleLogged : _leftGripStaleLogged)
                return 0f;
            if (right) _rightGripStaleLogged = true; else _leftGripStaleLogged = true;
            if (Log != null)
                Log.LogInfo("Q3: " + (right ? "right" : "left") +
                    " grip held " + StaleGripSeconds + "s — treating input as stale; " +
                    "re-resolving OpenVR actions and clamping grip.");
            if (!_gripReResolveTried)
            {
                _gripReResolveTried = true;
                OpenVrInputBridge.ForceReResolve();
            }
            return 0f;
        }

        internal static void SampleInputs()
        {
			if (Trigger == null || LeftTrigger == null || RightGripTrigger == null ||
				KeyboardChord == null || RecenterChord == null ||
                ShortcutGestures == null)
                return;

            int frame = Time.frameCount;
            if (_inputSampleFrame == frame)
                return;
            _inputSampleFrame = frame;
            float indexValue;
			float leftIndexValue;
            float gripValue;
            float leftGripValue;
            float aValue;
            float leftAValue = 0f;
            Vector2 rightStick;
            if (!OpenVrInputBridge.TryGetInput(
                out indexValue, out gripValue, out leftGripValue, out aValue,
                out rightStick))
            {
                indexValue = OVRInput.Get(
                    OVRInput.Axis1D.SecondaryIndexTrigger, OVRInput.Controller.Touch);
                gripValue = OVRInput.Get(
                    OVRInput.Axis1D.SecondaryHandTrigger, OVRInput.Controller.Touch);
                leftGripValue = OVRInput.Get(
                    OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.Touch);
                // Button.One is A on the right touch and X on the left —
                // split them so each hand can carry its own chords.
                aValue = OVRInput.Get(
                    OVRInput.Button.One, OVRInput.Controller.RTouch) ? 1f : 0f;
                leftAValue = OVRInput.Get(
                    OVRInput.Button.One, OVRInput.Controller.LTouch) ? 1f : 0f;
				leftIndexValue = OVRInput.Get(
					OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.Touch);
                rightStick = OVRInput.Get(
                    OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Controller.Touch);
            }
			else
            {
                if (!OpenVrInputBridge.TryGetLeftIndexTrigger(out leftIndexValue))
				    leftIndexValue = 0f;
                OpenVrInputBridge.TryGetAButtons(out aValue, out leftAValue);
            }
            _sampledRightStick = rightStick;
            float sampleDt = _lastSampleTime > 0f ?
                Time.unscaledTime - _lastSampleTime : 0f;
            _lastSampleTime = Time.unscaledTime;
            gripValue = FilterStaleHold(gripValue, true, sampleDt);
            leftGripValue = FilterStaleHold(leftGripValue, false, sampleDt);
            Trigger.Advance(frame, Time.unscaledTime, indexValue);
			LeftTrigger.Advance(frame, Time.unscaledTime, leftIndexValue);
			if (Trigger.PressedDownFrame == frame)
            {
                if (Log != null &&
                    ((KeyboardChord != null && KeyboardChord.Active) ||
                     KeyboardVisible || RadialMenuVisible ||
                     VrPresetBrowser.PointerInside))
                    Log.LogInfo("Q3 input idx=" + indexValue.ToString("F2") +
                        " grip=" + gripValue.ToString("F2") + " lGrip=" +
                        leftGripValue.ToString("F2") + " a=" + aValue.ToString("F2") +
                        " chordAct=" + (KeyboardChord != null && KeyboardChord.Active) +
                        " kbVis=" + KeyboardVisible + " radVis=" + RadialMenuVisible +
                        " ptrIn=" + VrPresetBrowser.PointerInside);
				SliderDragActive = VrSliderPointer.IsRightPointerOverSlider();
                // Each trigger only ever binds its own hand's laser —
                // falling back to the left look target let a right press
                // grab whatever the left cursor rested on.
                if (ClothingDragCandidate == null && !ClothingDragActive)
                {
                    GameObject pressLookRight =
                        VrPointerPresentation.CurrentLookTarget(true);
                    ClothingDragCandidate =
                        UiAssistHudLink.BeginFavoriteCandidate(pressLookRight, true);
                    if (ClothingDragCandidate == null)
                        UiAssistHudLink.LogPressMiss(pressLookRight,
                            VrPointerPresentation.CurrentLookTarget(false));
                }
            }
			else if (!Trigger.Pressed && Trigger.ReleasedFrame != frame)
            {
				SliderDragActive = false;
                if (ClothingDragCandidate == null ||
                    ClothingDragCandidate.RightPointer)
                    ClothingDragCandidate = null;
            }
            // Left index gets the same press pipeline for the left laser:
            // press on a slot arms a candidate, long-press drags it, and
            // the left submit patches withhold the native click meanwhile.
            if (LeftTrigger.PressedDownFrame == frame)
            {
                if (ClothingDragCandidate == null && !ClothingDragActive)
                    ClothingDragCandidate =
                        UiAssistHudLink.BeginFavoriteCandidate(
                            VrPointerPresentation.CurrentLookTarget(false), false);
            }
            else if (!LeftTrigger.Pressed && LeftTrigger.ReleasedFrame != frame &&
                     ClothingDragCandidate != null &&
                     !ClothingDragCandidate.RightPointer)
            {
                ClothingDragCandidate = null;
            }
            UpdateRadialDragBlock(Trigger.Pressed, Trigger.ReleasedFrame == frame,
                ClothingDragActive || ClothingDragCandidate != null ||
                VrPresetBrowser.CapturingGesture ||
                (Trigger.PressedDownFrame == frame && VrPresetBrowser.PointerInside),
                Trigger.PressedDownFrame == frame);
			RightGripTrigger.Advance(frame, Time.unscaledTime, gripValue);
            if (RightGripTrigger.PressedDownFrame == frame && Log != null)
                Log.LogInfo("Q3 grip edge: grip=" + gripValue.ToString("F2") +
                    " idx=" + indexValue.ToString("F2"));
            KeyboardChord.Advance(frame, Time.unscaledTime, indexValue, gripValue);
            RecenterChord.Advance(frame, Time.unscaledTime, leftGripValue, gripValue);

            UiAssistHudLink.UpdatePanelOrbitInput(RightGripTrigger.Pressed, rightStick,
                !InputRuntimeActive || KeyboardVisible || RadialMenuVisible || SliderDragActive ||
                ClothingDragActive || ClothingDragCandidate != null || PinnedTilesCapturingGrip ||
                (KeyboardChord != null && KeyboardChord.Active));
            ShortcutGestures.Advance(frame, Time.unscaledTime,
                leftIndexValue, leftGripValue, indexValue, gripValue,
                aValue, leftAValue,
                SliderDragActive || ClothingDragActive || RadialMenuVisible || PinnedTilesCapturingGrip || GripPitchCapturing || UiAssistHudLink.PanelOrbitCapturing);

            if (Instance != null)
                Instance.RefreshPitchInputMode();
        }

internal static bool SuppressRightInput()
        {
            SampleInputs();
            int frame = Time.frameCount;
            return (KeyboardChord != null && KeyboardChord.SuppressInput(frame)) ||
                   GripPitchCapturing ||
				   (!SliderDragActive && !KeyboardVisible && Trigger != null &&
                    (Trigger.LongPressActive || Trigger.LongPressReleaseFrame == frame));
        }
    }

	internal static class VrSliderPointer
	{
		private static readonly BindingFlags Fields = BindingFlags.Instance |
			BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		internal static bool IsRightPointerOverSlider()
		{
			try
			{
				FieldInfo singletonField = typeof(LookInputModule).GetField("_singleton", Fields);
				LookInputModule module = singletonField == null
					? null : singletonField.GetValue(null) as LookInputModule;
				if (module == null) return false;
				FieldInfo lookField = typeof(LookInputModule).GetField("currentLookRight", Fields);
				GameObject target = lookField == null ? null : lookField.GetValue(module) as GameObject;
				return target != null &&
					(target.GetComponentInParent<Slider>() != null ||
					 target.GetComponentInParent<Scrollbar>() != null ||
					 target.GetComponentInParent<SliderControl>() != null);
			}
			catch { return false; }
		}
	}

    internal static class SyntheticRightUiClick
    {
        private static readonly SyntheticClickState State = new SyntheticClickState();
        internal static void Begin(int frame) { State.Begin(frame); }
        internal static bool ConsumeUp(int frame) { return State.ConsumeUp(frame); }
    }

    // Left-hand counterpart: while a left press is owned by a drag
    // candidate the real submit edges are withheld, so a tap replays the
    // click as a synthetic down+up pair (same model as the right hand).
    internal static class SyntheticLeftUiClick
    {
        private static readonly SyntheticClickState State = new SyntheticClickState();
        internal static void Begin(int frame) { State.Begin(frame); }
        internal static bool ConsumeUp(int frame) { return State.ConsumeUp(frame); }
    }

    [HarmonyPatch(typeof(LookInputModule), "GetSubmitRightButtonDown")]
    internal static class GetSubmitRightButtonDownPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (!Quest3TriggerUIPlugin.InputRuntimeActive)
                return true;

            Quest3TriggerUIPlugin.SampleInputs();
            int frame = Time.frameCount;
			if (Quest3TriggerUIPlugin.SliderDragActive)
			{
				__result = Quest3TriggerUIPlugin.Trigger != null &&
					Quest3TriggerUIPlugin.Trigger.PressedDownFrame == frame;
				return false;
			}
            if (Quest3TriggerUIPlugin.SuppressRightInput())
            {
                __result = false;
                return false;
            }

            if (Quest3TriggerUIPlugin.KeyboardVisible)
            {
                __result = Quest3TriggerUIPlugin.Trigger != null &&
                           Quest3TriggerUIPlugin.Trigger.PressedDownFrame == frame;
                return false;
            }

            bool triggerTap = Quest3TriggerUIPlugin.Trigger != null &&
                              Quest3TriggerUIPlugin.Trigger.TapFrame == frame;
            if (triggerTap)
                SyntheticRightUiClick.Begin(frame);
            // A/X are gesture chords now, not click sources.
            __result = triggerTap;
            return false;
        }
    }

    [HarmonyPatch(typeof(LookInputModule), "GetSubmitRightButtonUp")]
    internal static class GetSubmitRightButtonUpPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (!Quest3TriggerUIPlugin.InputRuntimeActive)
                return true;

            Quest3TriggerUIPlugin.SampleInputs();
            int frame = Time.frameCount;
			if (Quest3TriggerUIPlugin.SliderDragActive)
			{
				__result = Quest3TriggerUIPlugin.Trigger != null &&
					Quest3TriggerUIPlugin.Trigger.ReleasedFrame == frame;
				return false;
			}
            if (Quest3TriggerUIPlugin.SuppressRightInput())
            {
                __result = false;
                return false;
            }

            if (Quest3TriggerUIPlugin.KeyboardVisible)
            {
                __result = Quest3TriggerUIPlugin.Trigger != null &&
                           Quest3TriggerUIPlugin.Trigger.ReleasedFrame == frame;
                return false;
            }

            __result = SyntheticRightUiClick.ConsumeUp(frame);
            return false;
        }
    }

	[HarmonyPatch(typeof(LookInputModule), "GetSubmitLeftButtonDown")]
	internal static class GetSubmitLeftButtonDownPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref bool __result)
		{
			if (!Quest3TriggerUIPlugin.InputRuntimeActive) return true;
			Quest3TriggerUIPlugin.SampleInputs();
			int frame = Time.frameCount;
			// A left press on a drag source is owned by the drag pipeline —
			// the real submit down would click the slot underneath the
			// press before a long-press could turn it into a drag.
			bool leftOwned = Quest3TriggerUIPlugin.ClothingDragCandidate != null &&
				!Quest3TriggerUIPlugin.ClothingDragCandidate.RightPointer;
			bool tap = leftOwned &&
				Quest3TriggerUIPlugin.LeftTrigger != null &&
				Quest3TriggerUIPlugin.LeftTrigger.TapFrame == frame;
			if (tap) SyntheticLeftUiClick.Begin(frame);
			__result = tap || (!leftOwned &&
				Quest3TriggerUIPlugin.LeftTrigger != null &&
				Quest3TriggerUIPlugin.LeftTrigger.PressedDownFrame == frame);
			return false;
		}
	}

	[HarmonyPatch(typeof(LookInputModule), "GetSubmitLeftButtonUp")]
	internal static class GetSubmitLeftButtonUpPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(ref bool __result)
		{
			if (!Quest3TriggerUIPlugin.InputRuntimeActive) return true;
			Quest3TriggerUIPlugin.SampleInputs();
			int frame = Time.frameCount;
			// While a left drag owns the press the real release is withheld
			// — the drop already committed in EndFavoriteDrag; on taps the
			// synthetic pair replays the click a frame later instead.
			bool leftOwned = Quest3TriggerUIPlugin.ClothingDragCandidate != null &&
				!Quest3TriggerUIPlugin.ClothingDragCandidate.RightPointer;
			__result = SyntheticLeftUiClick.ConsumeUp(frame) || (!leftOwned &&
				Quest3TriggerUIPlugin.LeftTrigger != null &&
				Quest3TriggerUIPlugin.LeftTrigger.ReleasedFrame == frame);
			return false;
		}
	}

    internal static class RightTriggerGrabResult
    {
        internal static bool TryGetLongPressStart(bool isOvr, out bool result)
        {
            if (!isOvr && !OpenVrInputBridge.IsActive)
            {
                result = false;
                return false;
            }

            Quest3TriggerUIPlugin.SampleInputs();
            if (Quest3TriggerUIPlugin.KeyboardVisible || Quest3TriggerUIPlugin.SuppressRightInput())
            {
                result = false;
                return true;
            }

            result = false;
            return true;
        }

        internal static bool TryGetLongPressRelease(bool isOvr, out bool result)
        {
            if (!isOvr && !OpenVrInputBridge.IsActive)
            {
                result = false;
                return false;
            }

            Quest3TriggerUIPlugin.SampleInputs();
            if (Quest3TriggerUIPlugin.KeyboardVisible || Quest3TriggerUIPlugin.SuppressRightInput())
            {
                result = false;
                return true;
            }

            result = false;
            return true;
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightGrab")]
    internal static class GetRightGrabPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, ref bool __result)
        {
            return !RightTriggerGrabResult.TryGetLongPressStart(___isOVR, out __result);
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightRemoteGrab")]
    internal static class GetRightRemoteGrabPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, bool ___rightGUIInteract, ref bool __result)
        {
            if ((___isOVR || OpenVrInputBridge.IsActive) && ___rightGUIInteract)
            {
                __result = false;
                return false;
            }
            return !RightTriggerGrabResult.TryGetLongPressStart(___isOVR, out __result);
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightGrabRelease")]
    internal static class GetRightGrabReleasePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, ref bool __result)
        {
            return !RightTriggerGrabResult.TryGetLongPressRelease(___isOVR, out __result);
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightRemoteGrabRelease")]
    internal static class GetRightRemoteGrabReleasePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, ref bool __result)
        {
            return !RightTriggerGrabResult.TryGetLongPressRelease(___isOVR, out __result);
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightGrabVal")]
    internal static class GetRightGrabValPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, ref float __result)
        {
            if (!___isOVR && !OpenVrInputBridge.IsActive)
                return true;

            Quest3TriggerUIPlugin.SampleInputs();
            if (Quest3TriggerUIPlugin.KeyboardVisible ||
                Quest3TriggerUIPlugin.RadialMenuVisible || Quest3TriggerUIPlugin.PinnedTilesCapturingGrip ||
                Quest3TriggerUIPlugin.GripPitchCapturing || UiAssistHudLink.PanelOrbitCapturing)
            {
                __result = 0f;
                return false;
            }

            if (!___isOVR)
            {
                __result = Quest3TriggerUIPlugin.RightGripTrigger != null &&
                           Quest3TriggerUIPlugin.RightGripTrigger.Pressed
                    ? Quest3TriggerUIPlugin.RightGripTrigger.AnalogValue
                    : 0f;
                return false;
            }

            __result = Quest3TriggerUIPlugin.KeyboardChord != null &&
                       Quest3TriggerUIPlugin.KeyboardChord.LongHoldActive &&
                       Quest3TriggerUIPlugin.Trigger != null
                ? Quest3TriggerUIPlugin.Trigger.AnalogValue
                : 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightHoldGrab")]
    internal static class GetRightHoldGrabPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, ref bool __result)
        {
            if (!___isOVR && !OpenVrInputBridge.IsActive)
                return true;
			Quest3TriggerUIPlugin.SampleInputs();
			int frame = Time.frameCount;
            if (Quest3TriggerUIPlugin.KeyboardVisible ||
                Quest3TriggerUIPlugin.RadialMenuVisible || Quest3TriggerUIPlugin.PinnedTilesCapturingGrip ||
                Quest3TriggerUIPlugin.GripPitchCapturing || UiAssistHudLink.PanelOrbitCapturing)
            {
                __result = false;
                return false;
            }
			if (Quest3TriggerUIPlugin.KeyboardChord != null &&
				Quest3TriggerUIPlugin.KeyboardChord.LongHoldStartFrame == frame)
			{
				__result = true;
				return false;
			}
			if (Quest3TriggerUIPlugin.SuppressRightInput())
			{
				__result = false;
				return false;
			}
			__result = Quest3TriggerUIPlugin.RightGripTrigger != null &&
				Quest3TriggerUIPlugin.RightGripTrigger.LongPressStartFrame == frame;
            return false;
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetRightRemoteHoldGrab")]
    internal static class GetRightRemoteHoldGrabPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool ___isOVR, bool ___rightGUIInteract, ref bool __result)
        {
            if (!___isOVR && !OpenVrInputBridge.IsActive)
                return true;
			Quest3TriggerUIPlugin.SampleInputs();
			int frame = Time.frameCount;
            if (Quest3TriggerUIPlugin.KeyboardVisible ||
                Quest3TriggerUIPlugin.RadialMenuVisible || Quest3TriggerUIPlugin.PinnedTilesCapturingGrip ||
                Quest3TriggerUIPlugin.GripPitchCapturing || UiAssistHudLink.PanelOrbitCapturing || ___rightGUIInteract)
            {
                __result = false;
                return false;
            }
			if (Quest3TriggerUIPlugin.KeyboardChord != null &&
				Quest3TriggerUIPlugin.KeyboardChord.LongHoldStartFrame == frame)
			{
				__result = true;
				return false;
			}
			if (Quest3TriggerUIPlugin.SuppressRightInput())
			{
				__result = false;
				return false;
			}
			__result = Quest3TriggerUIPlugin.RightGripTrigger != null &&
				Quest3TriggerUIPlugin.RightGripTrigger.LongPressStartFrame == frame;
            return false;
        }
    }

    [HarmonyPatch(typeof(Application), "set_runInBackground")]
    internal static class RunInBackgroundPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref bool __0)
        {
            if (!__0)
                __0 = true;
        }
    }
}





