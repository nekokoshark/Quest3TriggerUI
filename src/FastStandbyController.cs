using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Keeps the initialized scene and package registry resident.  Standby only
    // pauses simulation/audio and limits cameras to the plug-in UI layer, so
    // leaving standby performs no disk access, package scan or asset reload.
    internal sealed class FastStandbyController
    {
        private sealed class CameraState
        {
            internal Camera Camera;
            internal int CullingMask;
            internal CameraClearFlags ClearFlags;
            internal Color BackgroundColor;
        }

        private readonly List<CameraState> _cameraStates = new List<CameraState>();
        private bool _active;
        private bool _freezeAnimation;
        private bool _pauseAutoSimulation;
        private bool _audioPaused;
        private int _targetFrameRate;
        private int _vSyncCount;
        private int _origTextureLimit;
        private ShadowQuality _origShadows;
        private int _origAntiAliasing;
        private int _origPixelLightCount;
        private int _origAsyncSlice;
        private int _origAsyncBuffer;
        private float _restoreAsyncAt = -1f;
        private float _pendingCompressAt = -1f;
        private readonly List<KeyValuePair<HairSimControl, bool>> _hairFrozen =
            new List<KeyValuePair<HairSimControl, bool>>();
        private readonly List<KeyValuePair<ClothSimControl, bool>> _clothFrozen =
            new List<KeyValuePair<ClothSimControl, bool>>();
        private const int StandbyFramesPerSecond = 8;
        private readonly StandbyFrameLimiter _frameLimiter =
            new StandbyFrameLimiter(StandbyFramesPerSecond);

        internal bool Active
        {
            get { return _active; }
        }

        internal void Toggle(Action<string> completed)
        {
            if (_active)
                Exit(completed);
            else
                Enter(completed);
        }

        internal void RestoreImmediately()
        {
            if (_active)
                Exit(null);
        }

        internal void ThrottleFrame()
        {
            if (_restoreAsyncAt > 0f && Time.unscaledTime >= _restoreAsyncAt)
            {
                _restoreAsyncAt = -1f;
                QualitySettings.asyncUploadTimeSlice = _origAsyncSlice;
                QualitySettings.asyncUploadBufferSize = _origAsyncBuffer;
            }
            if (_pendingCompressAt > 0f && Time.unscaledTime >= _pendingCompressAt)
            {
                _pendingCompressAt = -1f;
                CompressMemory();
            }
            if (!_active)
            {
                _frameLimiter.Reset();
                return;
            }
            _frameLimiter.WaitForNextFrame();
        }

        private void Enter(Action<string> completed)
        {
            Stopwatch timer = Stopwatch.StartNew();
            SuperController controller = SuperController.singleton;
            _freezeAnimation = controller != null && controller.freezeAnimation;
            _pauseAutoSimulation = controller != null && controller.pauseAutoSimulation;
            _audioPaused = AudioListener.pause;
            _targetFrameRate = Application.targetFrameRate;
            _vSyncCount = QualitySettings.vSyncCount;
            _origTextureLimit = QualitySettings.masterTextureLimit;
            _origShadows = QualitySettings.shadows;
            _origAntiAliasing = QualitySettings.antiAliasing;
            _origPixelLightCount = QualitySettings.pixelLightCount;
            _origAsyncSlice = QualitySettings.asyncUploadTimeSlice;
            _origAsyncBuffer = QualitySettings.asyncUploadBufferSize;

            // VRAM diet is deferred ~0.8s: the texture-limit change re-uploads
            // every texture and UnloadUnusedAssets is a full GC, so running
            // them on the button press froze the world for seconds.  The
            // screen is already black by the time the hitch lands.
            _pendingCompressAt = Time.unscaledTime + 0.8f;

            _cameraStates.Clear();
            Camera[] cameras = Camera.allCameras;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0)
                uiLayer = 5;
            int uiMask = 1 << uiLayer;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null || !camera.enabled)
                    continue;
                _cameraStates.Add(new CameraState {
                    Camera = camera,
                    CullingMask = camera.cullingMask,
                    ClearFlags = camera.clearFlags,
                    BackgroundColor = camera.backgroundColor
                });
                camera.cullingMask = uiMask;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
            }

            // Use VaM's coordinated switches instead of changing Time.timeScale
            // or Physics.autoSimulation in the middle of a character step.  The
            // direct Unity switches can resume joints from mismatched poses and
            // twist a moving character.
            if (controller != null)
            {
                if (!_freezeAnimation)
                    controller.SetFreezeAnimation(true);
                if (!_pauseAutoSimulation)
                    controller.pauseAutoSimulation = true;
            }
            AudioListener.pause = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = StandbyFramesPerSecond;
            _frameLimiter.Reset();
            _active = true;
            timer.Stop();
            Report(completed,
                "已进入待机：VaM已协调暂停动画/物理并将主线程限制为约8 FPS，场景和VAR保持在内存；再次点按可立即恢复（" +
                timer.ElapsedMilliseconds + " ms）。");
        }

        // Eighth-res textures (~98% off the texture heap), shadow pool fully
        // released, no MSAA buffers, no pixel lights, plus one sweep for
        // unreferenced assets.  Runtime-only, all restored on exit.
        private void CompressMemory()
        {
            QualitySettings.masterTextureLimit = 3;
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.antiAliasing = 0;
            QualitySettings.pixelLightCount = 0;
            FreezeGpuSims();
            try { Resources.UnloadUnusedAssets(); }
            catch { }
        }

        // pauseAutoSimulation only stops PhysX — GPUTools hair/cloth still
        // integrate every frame.  Freeze them behind the black screen too;
        // the per-item enable flags are read per-frame and restored on exit.
        private void FreezeGpuSims()
        {
            _hairFrozen.Clear();
            _clothFrozen.Clear();
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            List<Atom> atoms = sc.GetAtoms();
            if (atoms == null) return;
            List<HairSimControl> hairs = new List<HairSimControl>();
            List<ClothSimControl> cloths = new List<ClothSimControl>();
            for (int i = 0; i < atoms.Count; i++)
            {
                Atom atom = atoms[i];
                if (atom == null) continue;
                hairs.Clear();
                cloths.Clear();
                try
                {
                    atom.GetComponentsInChildren(true, hairs);
                    atom.GetComponentsInChildren(true, cloths);
                }
                catch { continue; }
                for (int j = 0; j < hairs.Count; j++)
                {
                    HairSimControl h = hairs[j];
                    if (h == null || h.hairSettings == null ||
                        h.hairSettings.PhysicsSettings == null) continue;
                    _hairFrozen.Add(new KeyValuePair<HairSimControl, bool>(
                        h, h.hairSettings.PhysicsSettings.IsEnabled));
                    h.hairSettings.PhysicsSettings.IsEnabled = false;
                }
                for (int j = 0; j < cloths.Count; j++)
                {
                    ClothSimControl c = cloths[j];
                    if (c == null || c.clothSettings == null) continue;
                    _clothFrozen.Add(new KeyValuePair<ClothSimControl, bool>(
                        c, c.clothSettings.IntegrateEnabled));
                    c.clothSettings.IntegrateEnabled = false;
                }
            }
        }

        private void Exit(Action<string> completed)
        {
            Stopwatch timer = Stopwatch.StartNew();
            _pendingCompressAt = -1f;
            for (int i = 0; i < _cameraStates.Count; i++)
            {
                CameraState state = _cameraStates[i];
                if (state.Camera == null)
                    continue;
                state.Camera.cullingMask = state.CullingMask;
                state.Camera.clearFlags = state.ClearFlags;
                state.Camera.backgroundColor = state.BackgroundColor;
            }
            _cameraStates.Clear();

            // Open the async-upload throttle wide BEFORE restoring the
            // texture limit — the re-upload is the resume hitch, and 8ms/64MB
            // drains it several times faster than stock.  ThrottleFrame
            // hands the originals back after the burst settles.
            QualitySettings.asyncUploadTimeSlice = 8;
            QualitySettings.asyncUploadBufferSize = 64;
            _restoreAsyncAt = Time.unscaledTime + 6f;
            QualitySettings.masterTextureLimit = _origTextureLimit;
            QualitySettings.shadows = _origShadows;
            QualitySettings.antiAliasing = _origAntiAliasing;
            QualitySettings.pixelLightCount = _origPixelLightCount;
            for (int i = 0; i < _hairFrozen.Count; i++)
            {
                try
                {
                    HairSimControl h = _hairFrozen[i].Key;
                    if (h != null && h.hairSettings != null &&
                        h.hairSettings.PhysicsSettings != null)
                        h.hairSettings.PhysicsSettings.IsEnabled = _hairFrozen[i].Value;
                }
                catch { }
            }
            _hairFrozen.Clear();
            for (int i = 0; i < _clothFrozen.Count; i++)
            {
                try
                {
                    ClothSimControl c = _clothFrozen[i].Key;
                    if (c != null && c.clothSettings != null)
                        c.clothSettings.IntegrateEnabled = _clothFrozen[i].Value;
                }
                catch { }
            }
            _clothFrozen.Clear();

            SuperController controller = SuperController.singleton;
            if (controller != null)
            {
                // Resume simulation before animation so controllers never jump
                // ahead while physics is still paused.
                if (controller.pauseAutoSimulation != _pauseAutoSimulation)
                    controller.pauseAutoSimulation = _pauseAutoSimulation;
                if (controller.freezeAnimation != _freezeAnimation)
                    controller.SetFreezeAnimation(_freezeAnimation);
            }
            AudioListener.pause = _audioPaused;
            QualitySettings.vSyncCount = _vSyncCount;
            Application.targetFrameRate = _targetFrameRate;
            _frameLimiter.Reset();
            _active = false;
            timer.Stop();
            Report(completed,
                "待机恢复完成（" + timer.ElapsedMilliseconds +
                " ms）：未读盘、未重载场景、未重扫VAR。");
        }

        private static void Report(Action<string> completed, string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(message);
            if (completed != null)
                completed(message);
        }
    }
}
