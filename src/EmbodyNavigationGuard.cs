using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class EmbodyNavigationGuard
    {
        private bool _hasSnapshot;
        private bool _lastActive;
        private int _restoreFrames;
        private bool _horizonOnlyRestore;
        private Vector3 _rigPosition;
        private Quaternion _rigRotation;
        private float _playerHeight;
        private Vector3 _monitorPosition;
        private Quaternion _monitorRotation;
        private float _thirdPersonPitch;
        internal void BeforeToggle(bool wasActive, SuperController controller,
            GlobalVrPitchController pitch)
        {
            if (controller == null || controller.navigationRig == null) return;
            if (pitch != null) pitch.SuspendForExternalRigControl(controller);
            if (!wasActive)
            {
                Capture(controller, pitch == null ? 0f : pitch.PitchDegrees);
            }
        }

        internal void AfterToggle(bool wasActive, bool active)
        {
            _lastActive = active;
            if (wasActive && !active)
            {
                _restoreFrames = 3;
                _horizonOnlyRestore = !_hasSnapshot;
            }
            else if (!wasActive && !active)
                _restoreFrames = _hasSnapshot ? 1 : 0;
        }

        internal void Observe(bool active, SuperController controller,
            GlobalVrPitchController pitch)
        {
            if (_lastActive && !active && _hasSnapshot)
            {
                if (pitch != null) pitch.SuspendForExternalRigControl(controller);
                _restoreFrames = 3;
                _horizonOnlyRestore = false;
            }
            _lastActive = active;
        }

        internal void BeforeFrame(SuperController controller,
            GlobalVrPitchController pitch)
        {
            if (_restoreFrames <= 0 || controller == null ||
                controller.navigationRig == null)
                return;

            if (pitch != null) pitch.SuspendForExternalRigControl(controller);
            if (_horizonOnlyRestore)
            {
                NormalizeRigKeepingHeadPosition(controller);
                if (pitch != null) pitch.RestorePitch(0f);
                _restoreFrames--;
                if (_restoreFrames == 0) { _horizonOnlyRestore = false; }
                return;
            }
            if (!_hasSnapshot) return;
            controller.navigationRig.position = _rigPosition;
            controller.navigationRig.rotation = _rigRotation;
            controller.playerHeightAdjust = _playerHeight;
            if (controller.MonitorCenterCamera != null)
            {
                controller.MonitorCenterCamera.transform.position = _monitorPosition;
                controller.MonitorCenterCamera.transform.rotation = _monitorRotation;
            }
            if (pitch != null) pitch.RestorePitch(_thirdPersonPitch);
            _restoreFrames--;
            if (_restoreFrames == 0)
            { _hasSnapshot = false; }
        }

        internal void ForceNavigationReset(SuperController controller,
            GlobalVrPitchController pitch)
        {
            if (controller == null || controller.navigationRig == null)
                return;

            if (pitch != null)
            {
                pitch.SuspendForExternalRigControl(controller);
                pitch.RestorePitch(0f);
            }
            NormalizeRigKeepingHeadPosition(controller);

            // Embody restores its own rig snapshot once more on the following
            // frame. Repeat the neutralization long enough to win that race.
            _hasSnapshot = false;
            _lastActive = false;
            _horizonOnlyRestore = true;
            _restoreFrames = 4;
        }

        internal void Reset()
        {
            _hasSnapshot = false;
            _lastActive = false;
            _restoreFrames = 0;
            _horizonOnlyRestore = false;
        }

        private void Capture(SuperController controller, float pitch)
        {
            _rigPosition = controller.navigationRig.position;
            _rigRotation = controller.navigationRig.rotation;
            _playerHeight = controller.playerHeightAdjust;
            if (controller.MonitorCenterCamera != null)
            {
                _monitorPosition = controller.MonitorCenterCamera.transform.position;
                _monitorRotation = controller.MonitorCenterCamera.transform.rotation;
            }
            _thirdPersonPitch = pitch;
            _hasSnapshot = true;
            _restoreFrames = 0;
            _horizonOnlyRestore = false;
        }

        private static void NormalizeRigKeepingHeadPosition(
            SuperController controller)
        {
            Transform rig = controller.navigationRig;
            Transform camera = controller.lookCamera == null
                ? null : controller.lookCamera.transform;
            Vector3 headPosition = camera == null ? Vector3.zero : camera.position;
            float yaw = rig.eulerAngles.y;
            rig.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (camera != null)
                rig.position += headPosition - camera.position;
        }
    }
}
