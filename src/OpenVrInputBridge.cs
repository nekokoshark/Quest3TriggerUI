using System;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    /// <summary>
    /// Reads the same VaM SteamVR actions that SuperController uses.  Keeping
    /// this bridge reflection based avoids taking a hard assembly dependency
    /// on SteamVR_Actions.dll in the Oculus/VD payload while allowing the
    /// exact same gesture state machines to run under -vrmode OpenVR.
    /// </summary>
    internal static class OpenVrInputBridge
    {
        private static Type _actionsType;
        private static Type _sourcesType;
        private static object _rightHand;
        private static object _leftHand;
        private static object _uiInteract;
        private static object _grabVal;
        private static object _holdGrab;
        private static object _freeModeMove;
        private static object _freeMove;
        private static MethodInfo _getState;
        private static MethodInfo _getAxisFloat;
        private static MethodInfo _getAxisVector;
        private static object[] _rightInvokeArgs;
        private static object[] _leftInvokeArgs;
        private static int _lastAttemptFrame = -1;
        private static bool _loggedReady;

        internal static bool IsActive
        {
            get
            {
                SuperController controller = SuperController.singleton;
                return controller != null && controller.isOpenVR && EnsureInitialized();
            }
        }

        internal static bool TryGetInput(
            out float indexTrigger, out float rightGrip, out float leftGrip,
            out float aButton, out Vector2 rightStick)
        {
            indexTrigger = rightGrip = leftGrip = aButton = 0f;
            rightStick = Vector2.zero;
            if (!IsActive)
                return false;

            indexTrigger = InvokeAxis(_grabVal, _getAxisFloat, _rightInvokeArgs);
            rightGrip = InvokeState(_holdGrab, _rightInvokeArgs) ? 1f : 0f;
            leftGrip = InvokeState(_holdGrab, _leftInvokeArgs) ? 1f : 0f;
            aButton = InvokeState(_uiInteract, _rightInvokeArgs) ? 1f : 0f;
            rightStick = InvokeVectorAxis(_freeModeMove, _rightInvokeArgs);
            if (rightStick == Vector2.zero)
                rightStick = InvokeVectorAxis(_freeMove, _rightInvokeArgs);
            return true;
        }

        internal static bool TryGetSticks(out Vector2 right, out Vector2 left)
        {
            right = left = Vector2.zero;
            if (!IsActive)
                return false;
            right = InvokeVectorAxis(_freeModeMove, _rightInvokeArgs);
            if (right == Vector2.zero)
                right = InvokeVectorAxis(_freeMove, _rightInvokeArgs);
            left = InvokeVectorAxis(_freeModeMove, _leftInvokeArgs);
            if (left == Vector2.zero)
                left = InvokeVectorAxis(_freeMove, _leftInvokeArgs);
            return true;
        }

        internal static bool TryGetLeftIndexTrigger(out float value)
        {
            value = 0f;
            if (!IsActive)
                return false;

            value = InvokeAxis(_grabVal, _getAxisFloat, _leftInvokeArgs);
            return true;
        }

        // After a SteamVR runtime restart, a digital action can freeze at its
        // last reported state. Dropping our cached handles lets the next poll
        // pick up refreshed action objects if VaM reinitialized its input.
        internal static void ForceReResolve()
        {
            _actionsType = null;
            _uiInteract = _grabVal = _holdGrab = null;
            _freeModeMove = _freeMove = null;
            _lastAttemptFrame = -1;
        }

        private static bool EnsureInitialized()
        {
            if (_actionsType != null && _uiInteract != null && _grabVal != null &&
                _holdGrab != null && (_freeModeMove != null || _freeMove != null))
                return true;

            if (_lastAttemptFrame == Time.frameCount)
                return false;
            _lastAttemptFrame = Time.frameCount;
            try
            {
                Assembly actionsAssembly = FindAssembly("SteamVR_Actions");
                if (actionsAssembly == null)
                    return false;
                _actionsType = actionsAssembly.GetType("Valve.VR.SteamVR_Actions", false);
                Assembly inputAssembly = FindAssembly("SteamVR");
                _sourcesType = inputAssembly == null ? null : inputAssembly.GetType("Valve.VR.SteamVR_Input_Sources", false);
                if (_actionsType == null || _sourcesType == null)
                    return false;
                _rightHand = Enum.Parse(_sourcesType, "RightHand");
                _leftHand = Enum.Parse(_sourcesType, "LeftHand");
                // Reused invoke arguments: the hand enum instances never change.
                _rightInvokeArgs = new object[] { _rightHand };
                _leftInvokeArgs = new object[] { _leftHand };
                _uiInteract = GetAction("default_UIInteract");
                _grabVal = GetAction("default_GrabVal");
                _holdGrab = GetAction("default_HoldGrab");
                _freeModeMove = GetAction("default_FreeModeMove");
                _freeMove = GetAction("default_FreeMove");
                _getState = FindMethod(_holdGrab, "GetState");
                _getAxisFloat = FindMethod(_grabVal, "GetAxis");
                _getAxisVector = FindMethod(_freeModeMove ?? _freeMove, "GetAxis");
                bool ready = _uiInteract != null && _grabVal != null && _holdGrab != null &&
                             (_freeModeMove != null || _freeMove != null);
                if (ready && !_loggedReady && Quest3TriggerUIPlugin.Log != null)
                {
                    _loggedReady = true;
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "OpenVR input bridge ready: SteamVR Actions default UIInteract/GrabVal/HoldGrab/FreeModeMove.");
                }
                return ready;
            }
            catch (Exception exception)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogDebug("OpenVR input bridge is not ready: " + exception.Message);
                return false;
            }
        }

        private static Assembly FindAssembly(string simpleName)
        {
            Assembly loaded = AssemblyCatalog.Find(simpleName);
            if (loaded != null)
                return loaded;
            try { return Assembly.Load(simpleName); }
            catch { return null; }
        }

        private static object GetAction(string propertyName)
        {
            PropertyInfo property = _actionsType.GetProperty(
                propertyName, BindingFlags.Static | BindingFlags.Public);
            return property == null ? null : property.GetValue(null, null);
        }

        private static MethodInfo FindMethod(object action, string name)
        {
            return action == null ? null : action.GetType().GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public);
        }

        private static bool InvokeState(object action, object[] invokeArgs)
        {
            if (action == null || invokeArgs == null)
                return false;
            MethodInfo method = _getState;
            return method != null && (bool)method.Invoke(action, invokeArgs);
        }

        private static float InvokeAxis(object action, MethodInfo method, object[] invokeArgs)
        {
            if (action == null || invokeArgs == null || method == null)
                return 0f;
            object value = method.Invoke(action, invokeArgs);
            return value is float ? (float)value : 0f;
        }

        private static Vector2 InvokeVectorAxis(object action, object[] invokeArgs)
        {
            if (action == null || invokeArgs == null)
                return Vector2.zero;
            MethodInfo axis = _getAxisVector;
            if (axis == null)
                return Vector2.zero;
            object value = axis.Invoke(action, invokeArgs);
            return value is Vector2 ? (Vector2)value : Vector2.zero;
        }
    }
}

