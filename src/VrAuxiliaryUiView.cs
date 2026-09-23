using System;
using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Auxiliary stereo cameras parented to the tracked eye can receive another
    // tracked pose. Keep their UI view identical to the real eye for this pass.
    // No culling masks, projection matrices, targets or plugin enable flags change.
    internal sealed class VrAuxiliaryUiView : MonoBehaviour
    {
        private sealed class SavedView
        {
            internal Camera Camera;
            internal Matrix4x4 Left;
            internal bool Applied;
        }
        private readonly Dictionary<int, SavedView> _views = new Dictionary<int, SavedView>();
        private readonly HashSet<int> _eligibleEyes = new HashSet<int>();
        private int _lastPruneFrame = -1;

        internal static VrAuxiliaryUiView Begin()
        {
            var go = new GameObject("Q3 Auxiliary UI View");
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<VrAuxiliaryUiView>();
        }
        private void OnEnable()
        {
            Camera.onPreCull += BeforeCull;
            Camera.onPostRender += AfterRender;
        }
        private void OnDisable()
        {
            Camera.onPreCull -= BeforeCull;
            Camera.onPostRender -= AfterRender;
            foreach (SavedView view in _views.Values) Restore(view);
            _views.Clear();
            _eligibleEyes.Clear();
        }
        private void BeforeCull(Camera camera)
        {
            if (camera == null || !camera.stereoEnabled ||
                (camera.name != "CameraHook" && camera.name != "VamDlssNr.UICamera")) return;
            int cameraId = camera.GetInstanceID();
            Camera eye;
            if (!_eligibleEyes.Contains(cameraId))
            {
                Transform parent = camera.transform.parent;
                eye = parent == null ? null : parent.GetComponent<Camera>();
                if (eye == null || !eye.stereoEnabled || eye.name != "Camera (eye)") return;
                _eligibleEyes.Add(cameraId);
            }
            else
            {
                Transform parent = camera.transform.parent;
                eye = parent == null ? null : parent.GetComponent<Camera>();
                if (eye == null || !eye.stereoEnabled) return;
            }
            SavedView saved;
            int id = cameraId;
            if (!_views.TryGetValue(id, out saved))
            {
                saved = new SavedView { Camera = camera };
                _views.Add(id, saved);
            }
            if (saved.Applied) return;
            saved.Left = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            saved.Applied = true;
            camera.worldToCameraMatrix = eye.worldToCameraMatrix;
            camera.SetStereoViewMatrix(Camera.StereoscopicEye.Left, eye.GetStereoViewMatrix(Camera.StereoscopicEye.Left));
            camera.SetStereoViewMatrix(Camera.StereoscopicEye.Right, eye.GetStereoViewMatrix(Camera.StereoscopicEye.Right));
            // Logging is intentionally omitted from the per-frame render path;
            // alignment was verified during deployment and no diagnostics are
            // needed while the correction is active.
        }
        private void AfterRender(Camera camera)
        {
            if (camera == null) return;
            SavedView saved;
            if (_views.TryGetValue(camera.GetInstanceID(), out saved)) Restore(saved);
            if (_lastPruneFrame != Time.frameCount && Time.frameCount % 120 == 0)
            {
                _lastPruneFrame = Time.frameCount;
                PruneDestroyedCameras();
            }
        }
        private void PruneDestroyedCameras()
        {
            var dead = new List<int>();
            foreach (KeyValuePair<int, SavedView> pair in _views)
                if (pair.Value.Camera == null) dead.Add(pair.Key);
            for (int i = 0; i < dead.Count; i++) { _views.Remove(dead[i]); _eligibleEyes.Remove(dead[i]); }
        }
        private static void Restore(SavedView saved)
        {
            if (!saved.Applied) return;
            if (saved.Camera != null)
            {
                // Release the per-pass overrides, returning the tracked cameras
                // to Unity's normal automatic view calculation.
                saved.Camera.ResetWorldToCameraMatrix();
                saved.Camera.ResetStereoViewMatrices();
            }
            saved.Applied = false;
        }
    }
}


