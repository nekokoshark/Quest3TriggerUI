using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Quest3TriggerUI
{
    /// <summary>
    /// Sustained haptic buzzes for the plugin's own VR UI. Preferred path is
    /// the SteamVR "default_Haptic" vibration action (frequency + amplitude
    /// control — Quest's crisp "3D" feel needs the right Hz, not just buzz
    /// length). Falls back to legacy TriggerHapticPulse re-fired per frame,
    /// then OVRInput.SetControllerVibration on the Oculus path.
    /// </summary>
    internal static class VrHaptics
    {
        internal static bool Enabled = true;
        internal static float Strength = 1f; // 0..1, scales buzz duration
        // VaM's own UI haptics call hapticAction.Execute at amplitude 1.0 —
        // full-power single-cycle thumps that read as harsh buzzing. The
        // Execute prefix rescales every native call; OwnCall exempts ours.
        internal static float VamScale = 0.18f;
        internal static bool OwnCall;

        private const float MinIntervalSeconds = 0.08f;
        private static float _lastRight = -10f;
        private static float _lastLeft = -10f;
        private static float _untilRight;
        private static float _untilLeft;
        private static bool _initTried;
        private static bool _logged;

        private static object _cvrSystem;
        private static MethodInfo _roleIndex;
        private static MethodInfo _pulse;
        private static object _roleRight;
        private static object _roleLeft;
        private static int _rightIndex = -1;
        private static int _leftIndex = -1;

        private static MethodInfo _ovrVibrate;
        private static object _ovrRight;
        private static object _ovrLeft;

        private static object _hapticAction;
        private static MethodInfo _hapticExecute;
        private static object _srcRight;
        private static object _srcLeft;

        // Quest 3's wideband VCM feel = soft micro-transients, not sustained
        // tones. Amplitudes stay low (0.1-0.3); a real click splits into a
        // single short press tick, then on release a stepped sequence that
        // builds light->heavy (the "paragraph-style" flow).
        // Segments: {delaySec, durSec, freqHz, amplitude}
        private static readonly float[][] SegHover = {
            new[] { 0f, 0.006f, 180f, 0.12f } };
        private static readonly float[][] SegPress = {
            new[] { 0f, 0.006f, 220f, 0.26f } };
        private static readonly float[][] SegRelease = {
            new[] { 0.060f, 0.007f, 150f, 0.08f },
            new[] { 0.085f, 0.007f, 140f, 0.13f },
            new[] { 0.112f, 0.008f, 130f, 0.19f },
            new[] { 0.142f, 0.009f, 118f, 0.25f } };
        private static readonly float[][] SegConfirm = {
            new[] { 0f, 0.010f, 170f, 0.32f },
            new[] { 0.011f, 0.016f, 110f, 0.18f } };

        internal static void Hover() { HoverOn(PointerIsRight()); }
        internal static void Press() { PressOn(PointerIsRight()); }
        internal static void Release() { ReleaseOn(PointerIsRight()); }
        internal static void Confirm() { ConfirmOn(PointerIsRight()); }

        internal static void HoverOn(bool right) { Pulse(right, SegHover, 12); }
        internal static void PressOn(bool right) { Pulse(right, SegPress, 20); }
        internal static void ReleaseOn(bool right) { Pulse(right, SegRelease, 45); }
        internal static void ConfirmOn(bool right) { Pulse(right, SegConfirm, 35); }

        private static void Pulse(bool right, float[][] segments, int legacyMillis)
        {
            if (!Enabled || Strength <= 0f) return;
            float now = Time.unscaledTime;
            if (now - (right ? _lastRight : _lastLeft) < MinIntervalSeconds) return;
            if (right) _lastRight = now;
            else _lastLeft = now;
            if (FireAction(right, segments)) return;
            float until = now + legacyMillis * Strength * 0.001f;
            if (right) { if (until > _untilRight) _untilRight = until; }
            else { if (until > _untilLeft) _untilLeft = until; }
        }

        // Called once per plugin Update. Cheap early-out when nothing buzzes.
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            bool r = now < _untilRight;
            bool l = now < _untilLeft;
            if (r) Fire(true, now);
            if (l) Fire(false, now);
            TickGlobal();
        }

        // Global UI haptics: the HandlePointerExitAndEnter postfix feeds every
        // pointer-enter across all input modules (native panels, scene UI,
        // plugin UI) — the currentLook fields on LookInputModule turn out to
        // be unreliable on native panels, so this patch is the tracker.
        private static GameObject _lookR;
        private static GameObject _lookL;
        private static bool _pressOnUiR;
        private static bool _pressOnUiL;
        private static FieldInfo _lookDataRightField;
        private static FieldInfo _lookDataField;
        private static FieldInfo _lookDataMouseField;
        private static bool _lookFieldsTried;

        // Postfix args: (PointerInputModule __instance,
        // PointerEventData currentPointerData, GameObject newEnterTarget).
        private static bool _feedLogged;

        internal static void OnPointerEnter(object module,
            object eventData, GameObject newTarget)
        {
            EnsureInit();
            if (!_feedLogged)
            {
                _feedLogged = true;
                string msg = "pointer-enter fired: module=" +
                    (module == null ? "null" : module.GetType().Name) +
                    " target=" + (newTarget == null ? "null" : newTarget.name);
                Diag(msg);
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo("VR haptics: " + msg);
            }
            if (!Enabled || Strength <= 0f) return;
            bool right;
            LookInputModule look = module as LookInputModule;
            if (look != null)
            {
                EnsureLookFields();
                object ed = eventData;
                if (_lookDataRightField != null &&
                    ReferenceEquals(ed, _lookDataRightField.GetValue(look)))
                    right = true;
                else if (_lookDataField != null &&
                    ReferenceEquals(ed, _lookDataField.GetValue(look)))
                    right = false;
                else return; // lookDataMouse / unknown pointer — skip
            }
            else right = PointerIsRight();
            GameObject t = NormalizeLookTarget(newTarget);
            if (right)
            {
                if (t == _lookR) return;
                _lookR = t;
            }
            else
            {
                if (t == _lookL) return;
                _lookL = t;
            }
            if (t != null) HoverOn(right);
        }

        private static void EnsureLookFields()
        {
            if (_lookFieldsTried) return;
            _lookFieldsTried = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            _lookDataRightField = typeof(LookInputModule).GetField("lookDataRight", F);
            _lookDataField = typeof(LookInputModule).GetField("lookData", F);
            _lookDataMouseField = typeof(LookInputModule).GetField("lookDataMouse", F);
        }

        private static void TickGlobal()
        {
            if (!Enabled || Strength <= 0f) return;
            int frame = Time.frameCount;
            TriggerStateMachine trigR = Quest3TriggerUIPlugin.Trigger;
            TriggerStateMachine trigL = Quest3TriggerUIPlugin.LeftTrigger;
            if (trigR != null)
            {
                if (trigR.PressedDownFrame == frame)
                { _pressOnUiR = _lookR != null; if (_pressOnUiR) PressOn(true); }
                if (trigR.ReleasedFrame == frame && _pressOnUiR)
                { ReleaseOn(true); _pressOnUiR = false; }
            }
            if (trigL != null)
            {
                if (trigL.PressedDownFrame == frame)
                { _pressOnUiL = _lookL != null; if (_pressOnUiL) PressOn(false); }
                if (trigL.ReleasedFrame == frame && _pressOnUiL)
                { ReleaseOn(false); _pressOnUiL = false; }
            }
        }

        // The raycast can hit a button's Text child — normalize to the owning
        // Selectable so sliding inside one control doesn't re-tick.
        private static GameObject NormalizeLookTarget(GameObject target)
        {
            if (target == null) return null;
            try
            {
                UnityEngine.UI.Selectable sel =
                    target.GetComponentInParent<UnityEngine.UI.Selectable>();
                return sel == null ? target : sel.gameObject;
            }
            catch { return target; }
        }

        private static void Fire(bool right, float now)
        {
            try
            {
                if (FireOpenVr(right)) return;
                FireOvr(right);
            }
            catch { }
        }

        private static bool PointerIsRight()
        {
            try
            {
                if (VrPointerPresentation.CurrentLookTarget(true) != null)
                    return true;
                if (VrPointerPresentation.CurrentLookTarget(false) != null)
                    return false;
            }
            catch { }
            return true;
        }

        private static void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                Assembly steamVr = FindAssembly("SteamVR");
                if (steamVr != null)
                {
                    Type openVr = steamVr.GetType("Valve.VR.OpenVR", false);
                    PropertyInfo system = openVr == null ? null : openVr.GetProperty(
                        "System", BindingFlags.Static | BindingFlags.Public);
                    _cvrSystem = system == null ? null : system.GetValue(null, null);
                    if (_cvrSystem != null)
                    {
                        Type roles = steamVr.GetType("Valve.VR.ETrackedControllerRole", false);
                        _roleIndex = _cvrSystem.GetType().GetMethod(
                            "GetTrackedDeviceIndexForControllerRole",
                            BindingFlags.Instance | BindingFlags.Public);
                        _pulse = _cvrSystem.GetType().GetMethod(
                            "TriggerHapticPulse",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (roles != null)
                        {
                            _roleRight = Enum.Parse(roles, "RightHand");
                            _roleLeft = Enum.Parse(roles, "LeftHand");
                        }
                    }
                }
            }
            catch { }
            try
            {
                Assembly actions = FindAssembly("SteamVR_Actions");
                Type actionsType = actions == null ? null :
                    actions.GetType("Valve.VR.SteamVR_Actions", false);
                PropertyInfo haptic = actionsType == null ? null :
                    actionsType.GetProperty("default_Haptic",
                        BindingFlags.Static | BindingFlags.Public);
                _hapticAction = haptic == null ? null :
                    haptic.GetValue(null, null);
                if (_hapticAction != null)
                {
                    _hapticExecute = _hapticAction.GetType().GetMethod(
                        "Execute", BindingFlags.Instance | BindingFlags.Public);
                    Type sources = FindType("Valve.VR.SteamVR_Input_Sources");
                    if (sources != null)
                    {
                        _srcRight = Enum.Parse(sources, "RightHand");
                        _srcLeft = Enum.Parse(sources, "LeftHand");
                    }
                }
            }
            catch { }
            try
            {
                Type ovrInput = FindType("OVRInput");
                if (ovrInput != null)
                {
                    Type controller = ovrInput.GetNestedType("Controller");
                    if (controller != null)
                    {
                        _ovrVibrate = ovrInput.GetMethod(
                            "SetControllerVibration",
                            BindingFlags.Static | BindingFlags.Public,
                            null,
                            new Type[] { typeof(float), typeof(float), controller },
                            null);
                        if (_ovrVibrate != null)
                        {
                            _ovrRight = Enum.Parse(controller, "RTouch");
                            _ovrLeft = Enum.Parse(controller, "LTouch");
                        }
                    }
                }
            }
            catch { }

            if (!_logged && Quest3TriggerUIPlugin.Log != null)
            {
                _logged = true;
                Quest3TriggerUIPlugin.Log.LogInfo("VR haptics ready: action=" +
                    (_hapticExecute != null) + " openvr=" + (_pulse != null) +
                    " ovr=" + (_ovrVibrate != null));
            }
        }

        // SteamVR action vibration — full frequency/amplitude control.
        // Works whenever SteamVR input is up; VD translates it to Meta's
        // runtime where the Hz value actually shapes the waveform.
        private static bool FireAction(bool right, float[][] segments)
        {
            EnsureInit();
            if (_hapticAction == null || _hapticExecute == null) return false;
            object hand = right ? _srcRight : _srcLeft;
            if (hand == null) return false;
            try
            {
                OwnCall = true;
                foreach (float[] s in segments)
                {
                    _hapticExecute.Invoke(_hapticAction, new object[] {
                        s[0], s[1], s[2],
                        Mathf.Clamp01(s[3] * Strength), hand });
                }
                return true;
            }
            catch { return false; }
            finally { OwnCall = false; }
        }

        private static bool FireOpenVr(bool right)
        {
            EnsureInit();
            SuperController sc = SuperController.singleton;
            if (sc == null || !sc.isOpenVR || _cvrSystem == null ||
                _roleIndex == null || _pulse == null) return false;
            object role = right ? _roleRight : _roleLeft;
            if (role == null) return false;
            int index = right ? _rightIndex : _leftIndex;
            if (index < 0)
            {
                object value = _roleIndex.Invoke(_cvrSystem, new object[] { role });
                // Return type is ETrackedDeviceIndex (enum) — Convert.ToInt32,
                // "is int" never matches a boxed enum.
                try { index = Convert.ToInt32(value); }
                catch { index = -1; }
                if (index < 0 || index >= 64) return false;
                if (right) _rightIndex = index; else _leftIndex = index;
            }
            _pulse.Invoke(_cvrSystem,
                new object[] { (uint)index, 0u, (ushort)3999 });
            return true;
        }

        private static bool FireOvr(bool right)
        {
            EnsureInit();
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isOpenVR || _ovrVibrate == null) return false;
            object hand = right ? _ovrRight : _ovrLeft;
            if (hand == null) return false;
            _ovrVibrate.Invoke(null, new object[] { 1f, 0.6f, hand });
            return true;
        }

        private static Assembly FindAssembly(string simpleName)
        {
            Assembly loaded = AssemblyCatalog.Find(simpleName);
            if (loaded != null) return loaded;
            try { return Assembly.Load(simpleName); }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        // SteamVR_Action_Vibration lives in SteamVR.dll — not compile-time
        // referenced, so this patch is applied manually after PatchAll.
        private static void Diag(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        BepInEx.Paths.ConfigPath, "q3haptics-diag.txt"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " +
                    msg + "\r\n");
            }
            catch { }
        }

        internal static void ApplyExecutePatch(Harmony harmony)
        {
            Diag("ApplyExecutePatch entered, log=" +
                (Quest3TriggerUIPlugin.Log == null ? "null" : "ok") +
                " harmony=" + (harmony == null ? "null" : "ok"));
            try
            {
                Type type = FindType("Valve.VR.SteamVR_Action_Vibration");
                Diag("vibration type=" + (type == null ? "null" : "ok"));
                if (type == null)
                {
                    if (Quest3TriggerUIPlugin.Log != null)
                        Quest3TriggerUIPlugin.Log.LogWarning(
                            "VR haptics: SteamVR_Action_Vibration not found.");
                    return;
                }
                Type sources = FindType("Valve.VR.SteamVR_Input_Sources");
                MethodInfo method = sources == null ? null : type.GetMethod(
                    "Execute", BindingFlags.Instance | BindingFlags.Public,
                    null, new Type[] { typeof(float), typeof(float),
                        typeof(float), typeof(float), sources }, null);
                Diag("sources=" + (sources == null ? "null" : "ok") +
                    " method=" + (method == null ? "null" : "ok"));
                if (method == null || harmony == null)
                {
                    if (Quest3TriggerUIPlugin.Log != null)
                        Quest3TriggerUIPlugin.Log.LogWarning(
                            "VR haptics: Execute signature not resolved.");
                    return;
                }
                harmony.Patch(method, prefix: new HarmonyMethod(
                    typeof(VrHaptics).GetMethod("SoftenNativePrefix",
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Diag("execute patch ok");
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "VR haptics: native-Execute softening patch applied.");
            }
            catch (System.Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning(
                        "VR haptics: Execute patch failed: " + e.Message);
            }

            // Belt-and-suspenders: also install the pointer-enter postfix
            // manually so coverage doesn't depend on PatchAll ordering.
            try
            {
                MethodInfo enter = typeof(BaseInputModule).GetMethod(
                    "HandlePointerExitAndEnter",
                    BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                Diag("enter method=" + (enter == null ? "null" : "ok"));
                if (enter != null && harmony != null)
                {
                    harmony.Patch(enter, postfix: new HarmonyMethod(
                        typeof(UiPointerEnterHapticsPatch).GetMethod("Postfix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    Diag("enter patch ok");
                    if (Quest3TriggerUIPlugin.Log != null)
                        Quest3TriggerUIPlugin.Log.LogInfo(
                            "VR haptics: pointer-enter patch applied.");
                }
            }
            catch (System.Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning(
                        "VR haptics: enter patch failed: " + e.Message);
            }
            Diag("ApplyExecutePatch done");
        }

        // Execute(float, float, float, float amplitude, inputSource) —
        // rescale the amplitude of every native VaM haptic call.
        private static void SoftenNativePrefix(ref float amplitude)
        {
            if (!OwnCall) amplitude *= VamScale;
        }
    }

    /// <summary>
    /// Global pointer-enter feed. VaM's UnityEngine.UI is a custom build:
    /// BaseInputModule carries its own HandlePointerExitAndEnter and every
    /// module's call site (LookInputModule.ProcessRight/ProcessMain, VRHit,
    /// OVRInputModule...) targets THAT method — the PointerInputModule and
    /// LookInputModule copies are never invoked. Patch the real call target.
    /// </summary>
    [HarmonyPatch(typeof(BaseInputModule), "HandlePointerExitAndEnter")]
    internal static class UiPointerEnterHapticsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(BaseInputModule __instance,
            PointerEventData currentPointerData, GameObject newEnterTarget)
        {
            try
            {
                VrHaptics.OnPointerEnter(__instance,
                    currentPointerData, newEnterTarget);
            }
            catch { }
        }
    }
}
