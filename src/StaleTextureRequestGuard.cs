using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Stop a proven dead native receiver BEFORE Finish allocates a GPU texture.
    // No cache walk, no Destroy, no change to live/shared/preloaded/UI textures.
    internal static class StaleTextureRequestGuard
    {
        private static Harmony _harmony;
        private static bool _tried;
        private static int _discarded;
        private static long _rawBytes;
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic;
        private static readonly MethodInfo ValidImage = typeof(DAZCharacterTextureControl)
            .GetMethod("CheckIfImageIsStillValid", All, null,
                new Type[] { typeof(ImageLoaderThreaded.QueuedImage) }, null);

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                if (ValidImage == null || ValidImage.ReturnType != typeof(bool))
                    throw new MissingMethodException("CheckIfImageIsStillValid");
                MethodInfo finish = typeof(ImageLoaderThreaded.QueuedImage)
                    .GetMethod("Finish", All, null, Type.EmptyTypes, null);
                if (finish == null) throw new MissingMethodException("QueuedImage.Finish");
                _harmony = new Harmony("Quest3TriggerUI.stale-texture-request");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(finish, prefix: new HarmonyMethod(
                    typeof(StaleTextureRequestGuard).GetMethod("BeforeFinish", All | BindingFlags.Static)));
                Log("installed: expired native requests skip GPU upload; existing caches retained");
                TextureDecodeBudget.Install();
            }
            catch (Exception e)
            {
                if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
                _harmony = null;
                Log("install failed: " + e.Message);
            }
        }

        // Every subscriber must be a known native callback that would return
        // without touching tex. A third-party/multicast live receiver keeps it.
        internal static bool CanDiscard(ImageLoaderThreaded.QueuedImage q)
        {
            if (q == null || q.finished || q.hadError || q.isThumbnail ||
                q.isPreload || q.skipCache || q.tex != null ||
                q.rawImageToLoad != null || q.callback == null) return false;
            foreach (Delegate cb in q.callback.GetInvocationList())
            {
                MethodInfo method = cb.Method;
                if (method.DeclaringType == typeof(DAZCharacterTextureControl) &&
                    method.Name == "OnImageLoaded")
                {
                    var owner = cb.Target as DAZCharacterTextureControl;
                    // Unity's overloaded null includes a destroyed component.
                    if (owner == null) continue;
                    if (ValidImage == null ||
                        (bool)ValidImage.Invoke(owner, new object[] { q })) return false;
                }
                else if (method.DeclaringType == typeof(MaterialOptions) &&
                    IsNativeMaterialCallback(method.Name))
                {
                    if (cb.Target as MaterialOptions != null) return false;
                }
                else return false;
            }
            return true;
        }

        private static bool IsNativeMaterialCallback(string name)
        {
            return name == "OnTexture1Loaded" || name == "OnTexture2Loaded" ||
                name == "OnTexture3Loaded" || name == "OnTexture4Loaded" ||
                name == "OnTexture5Loaded" || name == "OnTexture6Loaded";
        }

        internal static void BeforeFinish(ImageLoaderThreaded.QueuedImage __instance)
        {
            try
            {
                if (!CanDiscard(__instance)) return;
                if (__instance.raw != null) _rawBytes += __instance.raw.LongLength;
                __instance.raw = null;
                __instance.finished = true;
                __instance.skipCache = true;
                // Do NOT skip native Finish: it disposes webRequest first.
                // Do NOT set cancel: native progress/flags must still drain.
                // DoCallback still runs, preserving its normal completion path.
                _discarded++;
                if (_discarded == 1 || _discarded % 16 == 0)
                    Log("discarded=" + _discarded + " rawBytes=" + _rawBytes +
                        " (CPU payload bytes, not measured VRAM)");
            }
            catch (Exception e)
            {
                // Failed classification changes nothing; retain native behavior.
                Log("classification failed; native request retained: " + e.Message);
            }
        }

        internal static void Shutdown()
        {
            TextureDecodeBudget.Shutdown();
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            _tried = false;
        }

        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-guard] " + message);
        }
    }
}
