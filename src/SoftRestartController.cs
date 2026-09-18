using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class SoftRestartController
    {
        private const float FastResetTimeoutSeconds = 6f;
        private static readonly BindingFlags PrivateInstance =
            BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo IsLoadingField =
            typeof(SuperController).GetField("_isLoading", PrivateInstance);
        private static readonly FieldInfo LoadFlagField =
            typeof(SuperController).GetField("loadFlag", PrivateInstance);
        private static readonly FieldInfo HoldLoadFlagsField =
            typeof(SuperController).GetField("holdLoadCompleteFlags", PrivateInstance);
        private static readonly FieldInfo RemoveLoadFlagsField =
            typeof(SuperController).GetField("removeLoadFlags", PrivateInstance);
        private static readonly FieldInfo LoadingIconFlagsField =
            typeof(SuperController).GetField("loadingIconFlags", PrivateInstance);
        private static readonly MethodInfo ClearGrabbedMethod =
            typeof(SuperController).GetMethod(
                "ClearAllGrabbedControllers", PrivateInstance, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ClearBrowserPathMethod =
            typeof(SuperController).GetMethod(
                "ClearFileBrowsersCurrentPath", PrivateInstance, null, Type.EmptyTypes, null);

        private readonly MonoBehaviour _host;
        private readonly FastStandbyController _standby;
        private bool _busy;

        internal SoftRestartController(MonoBehaviour host, FastStandbyController standby)
        {
            _host = host;
            _standby = standby;
        }

        internal void Begin(Action<string> completed)
        {
            if (_busy)
            {
                Complete(completed, "软重启已经在进行。", false);
                return;
            }

            SuperController controller = SuperController.singleton;
            if (controller == null)
            {
                Complete(completed, "软重启：VaM 控制器尚未就绪。", false);
                return;
            }

            _busy = true;
            _host.StartCoroutine(RestartRoutine(controller, completed));
        }

        private IEnumerator RestartRoutine(SuperController controller, Action<string> completed)
        {
            float started = Time.realtimeSinceStartup;
            bool recoveredStuckLoad = controller.isLoading;

            _standby.RestoreImmediately();
            RestoreRuntimeSwitches(controller);
            if (recoveredStuckLoad)
                CancelStuckLoad(controller);

            ClearTransientInteraction(controller);
            Complete(completed, recoveredStuckLoad
                ? "正在强制结束失灵的加载并快速重建空场景……"
                : "正在快速重建空场景；保留进程、VAR索引和运行时缓存……", false);

            // VaM's embedded new scene is the fast path: it destroys and rebuilds
            // scene state without restarting the process or scanning packages.
            controller.NewScenePlayMode();
            yield return null;

            while (Time.realtimeSinceStartup - started < FastResetTimeoutSeconds)
            {
                if (!controller.isLoading && Time.realtimeSinceStartup - started >= 0.25f)
                {
                    _busy = false;
                    Complete(completed,
                        "软重启完成（" + ElapsedMilliseconds(started) +
                        " ms）：场景和加载状态已刷新，进程与VAR索引保持。", true);
                    yield break;
                }
                yield return null;
            }

            // Only a failed fast reset reaches the near-hard fallback. This keeps
            // normal soft restarts fast while recovering a wedged scene loader.
            Complete(completed, "快速重建超时，正在执行场景级彻底复位……", false);
            RestoreRuntimeSwitches(controller);
            CancelStuckLoad(controller);
            controller.HardReset();
            _busy = false;
        }

        private static void CancelStuckLoad(SuperController controller)
        {
            RaiseFlag(LoadFlagField == null ? null : LoadFlagField.GetValue(controller));
            RaiseAndClearFlags(controller, HoldLoadFlagsField);
            RaiseAndClearFlags(controller, RemoveLoadFlagsField);
            RaiseAndClearFlags(controller, LoadingIconFlagsField);

            controller.loadJson = null;
            controller.disableLoadSceneButton = false;
            SetActive(controller.loadConfirmPanel, false);
            SetActive(controller.loadingIcon, false);
            SetActive(controller.loadingUI, false);
            SetActive(controller.loadingUIAlt, false);
            SetLoadingField(controller, false);

            SetAllActive(controller.loadSceneButtons, true);
            SetAllActive(controller.loadSceneDisabledButtons, false);
        }

        private static void ClearTransientInteraction(SuperController controller)
        {
            Invoke(ClearGrabbedMethod, controller);
            controller.ClearPossess();
            controller.ClearSelection(true);
            Invoke(ClearBrowserPathMethod, controller);
            controller.ClearErrors();
            controller.ClearMessages();
        }

        private static void Invoke(MethodInfo method, object target)
        {
            if (method != null)
                method.Invoke(target, null);
        }

        private static void RestoreRuntimeSwitches(SuperController controller)
        {
            controller.pauseRender = false;
            controller.pauseAutoSimulation = false;
            controller.SetFreezeAnimation(false);
            AudioListener.pause = false;
        }

        private static void RaiseAndClearFlags(object target, FieldInfo field)
        {
            if (field == null)
                return;

            IList flags = field.GetValue(target) as IList;
            if (flags == null)
                return;

            object[] snapshot = new object[flags.Count];
            flags.CopyTo(snapshot, 0);
            for (int index = 0; index < snapshot.Length; index++)
                RaiseFlag(snapshot[index]);
            flags.Clear();
        }

        private static void RaiseFlag(object flag)
        {
            if (flag == null)
                return;
            MethodInfo raise = flag.GetType().GetMethod(
                "Raise", BindingFlags.Instance | BindingFlags.Public);
            if (raise != null)
                raise.Invoke(flag, null);
        }

        private static void SetLoadingField(SuperController controller, bool value)
        {
            if (IsLoadingField != null)
                IsLoadingField.SetValue(controller, value);
        }

        private static void SetActive(Transform transform, bool active)
        {
            if (transform != null)
                transform.gameObject.SetActive(active);
        }

        private static void SetAllActive(Transform[] transforms, bool active)
        {
            if (transforms == null)
                return;
            for (int index = 0; index < transforms.Length; index++)
                SetActive(transforms[index], active);
        }

        private static int ElapsedMilliseconds(float started)
        {
            return Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f);
        }

        private static void Complete(Action<string> completed, string message, bool success)
        {
            if (Quest3TriggerUIPlugin.Log != null)
            {
                if (success)
                    Quest3TriggerUIPlugin.Log.LogInfo(message);
                else
                    Quest3TriggerUIPlugin.Log.LogMessage(message);
            }
            if (completed != null)
                completed(message);
        }
    }
}
