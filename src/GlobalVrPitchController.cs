using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class GlobalVrPitchController
    {
        private readonly GlobalPitchState _state;
        private readonly GripPitchInputState _input;
        private bool _pitchApplied;
        private int _lastAdvanceFrame = -1;
        private float _appliedDegrees;
        private Vector3 _appliedPivot;
        private Vector3 _appliedAxis;

        internal GlobalVrPitchController(
            float degreesPerSecond, float maximumDegrees, float deadzone, bool invert)
        {
            _state = new GlobalPitchState(
                degreesPerSecond, maximumDegrees, deadzone, invert);
            _input = new GripPitchInputState(deadzone);
        }

        internal float PitchDegrees
        {
            get { return _state.PitchDegrees; }
        }

        internal void BeforeSuperControllerUpdate(SuperController controller)
        {
            RemoveAppliedPitch(controller);
        }

        internal bool CapturingGrip
        {
            get { return _input.CapturingGrip; }
        }

        internal void UpdateInputMode(bool embodyActive, bool gripPressed,
            bool blockInput, float thumbstickY)
        {
            _input.Update(embodyActive, gripPressed, blockInput, thumbstickY);
        }

        internal void BeforeControllerInteraction(SuperController controller) {
            if (controller == null)
                return;

            RemoveAppliedPitch(controller);

            if (!Quest3TriggerUIPlugin.InputRuntimeActive || controller.navigationRig == null ||
                controller.lookCamera == null)
                return;

            // Update once before raycasts, never after cached cursor/laser results.
            if (_lastAdvanceFrame != Time.frameCount)
            {
                _lastAdvanceFrame = Time.frameCount;
                _state.Advance(_input.AllowAdjustment ? _input.ThumbstickY : 0f, Time.unscaledDeltaTime);
            }
            ApplyPitch(controller);
        }

        internal void BeforeNativeNavigation(SuperController controller)
        {
            // Native stick movement projects against navigationRig.up. Keep the
            // rig neutral while VaM processes locomotion/yaw/height so a visual
            // pitch never tilts the movement plane or steals stick semantics.
            RemoveAppliedPitch(controller);
        }

        internal void BeforePointerFinalization(SuperController controller)
        {
            if (controller == null || controller.navigationRig == null ||
                controller.lookCamera == null || _pitchApplied)
                return;
            ApplyPitch(controller);
        }

        internal void SuspendForExternalRigControl(SuperController controller)
        {
            RemoveAppliedPitch(controller);
            _input.Reset();
        }

        internal void RestorePitch(float degrees)
        {
            _state.Set(degrees);
            _input.Reset();
        }

        internal void Reset(SuperController controller)
        {
            RemoveAppliedPitch(controller);
            _state.Reset();
            _lastAdvanceFrame = Time.frameCount;
            _input.Reset();
        }

        private void ApplyPitch(SuperController controller)
        {
            float degrees = _state.PitchDegrees;
            if (Mathf.Abs(degrees) < 0.001f)
                return;

            Transform camera = controller.lookCamera.transform;
            Transform rig = controller.navigationRig;
            _appliedPivot = camera.position;
            _appliedAxis = camera.right.normalized;
            _appliedDegrees = degrees;
            rig.RotateAround(_appliedPivot, _appliedAxis, _appliedDegrees);
            _pitchApplied = true;
        }

        private void RemoveAppliedPitch(SuperController controller)
        {
            if (!_pitchApplied)
                return;

            if (controller != null && controller.navigationRig != null)
            {
                controller.navigationRig.RotateAround(
                    _appliedPivot, _appliedAxis, -_appliedDegrees);
            }
            _pitchApplied = false;
            _appliedDegrees = 0f;
        }
    }

    [HarmonyPatch(typeof(SuperController), "GetFreeNavigateVector")]
    internal static class SuppressNativeVerticalNavigationForGripPitchPatch
    {
        private static void Postfix(ref Vector4 __result)
        {
            if (Quest3TriggerUIPlugin.GripPitchCapturing)
                __result.w = 0f;
            if (PluginListMode.ConsumeNavigation)
                __result = Vector4.zero;
        }
    }

    [HarmonyPatch(typeof(SuperController), "Update")]
    internal static class RemoveGlobalPitchBeforeNavigationPatch
    {
        private static void Prefix(SuperController __instance)
        {
            Quest3TriggerUIPlugin instance = Quest3TriggerUIPlugin.Instance;
            if (instance != null)
                instance.BeforeSuperControllerUpdate(__instance);
        }
    }

    // PrepControllers is called after lookCamera selection and before ProcessUI,
    // including the start screen, where ProcessUI itself may be skipped.
    [HarmonyPatch(typeof(SuperController), "PrepControllers")]
    internal static class ApplyGlobalPitchBeforeControllerInteractionPatch
    {
        private static void Prefix(SuperController __instance)
        {
            Quest3TriggerUIPlugin instance = Quest3TriggerUIPlugin.Instance;
            if (instance != null)
                instance.BeforeControllerInteraction(__instance);
        }
    }
    [HarmonyPatch(typeof(SuperController), "ResetNavigationRigPositionRotation")]
    internal static class ResetGlobalPitchWithNavigationRigPatch
    {
        private static void Prefix(SuperController __instance)
        {
            Quest3TriggerUIPlugin instance = Quest3TriggerUIPlugin.Instance;
            if (instance != null)
                instance.ResetGlobalPitch(__instance);
        }
    }

    [HarmonyPatch(typeof(SuperController), "ProcessPlayerNavMove")]
    internal static class RemoveGlobalPitchBeforeNativeNavigationPatch
    {
        private static void Prefix(SuperController __instance)
        {
            Quest3TriggerUIPlugin instance = Quest3TriggerUIPlugin.Instance;
            if (instance != null)
                instance.BeforeNativeNavigation(__instance);
        }
    }

    [HarmonyPatch(typeof(SuperController), "SyncCursor")]
    internal static class ReapplyGlobalPitchBeforePointerFinalizationPatch
    {
        private static void Prefix(SuperController __instance)
        {
            Quest3TriggerUIPlugin instance = Quest3TriggerUIPlugin.Instance;
            if (instance != null)
                instance.BeforePointerFinalization(__instance);
        }
    }
}



