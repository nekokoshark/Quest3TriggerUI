using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // VaM's built-in Package Manager (activeUI=8) scans every .var on open —
    // a multi-second freeze — and the queued thumbnail decodes keep running
    // after the panel closes, which is the occasional hitching users feel.
    // Two-part guard:
    //   1. Block every entry: the only code path that sets activeUI=8 is
    //      SuperController.OpenPackageManager, so one prefix disables the
    //      HUD button, DAZMorph "open in package manager", Hub item "open in
    //      package manager", and OpenPackageInManager alike.
    //   2. On leaving the manager (covers sessions that opened it before
    //      this payload loaded, or with the block disabled), cancel every
    //      pending thumbnail decode and purge the thumbnail cache so no
    //      residual work or memory survives the close.
    internal static class PackageManagerGuard
    {
        internal static bool BlockEntry = true;

        private const int ActiveUiPackageManager = 8;
        private static readonly FieldInfo ActiveUiField =
            typeof(SuperController).GetField("_activeUI",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo QueuedImagesField =
            typeof(ImageLoaderThreaded).GetField("queuedImages",
                BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void RemoveEntryButtons()
        {
            if (!BlockEntry) return;
            try
            {
                SuperController sc = SuperController.singleton;
                if (sc == null || sc.mainHUD == null) return;
                int hid = 0;
                foreach (Transform t in sc.mainHUD
                    .GetComponentsInChildren<Transform>(true))
                {
                    // ButtonOpenPackageManager + its Disabled twin, and any
                    // other prefab button whose name points at the manager.
                    if (t.name.StartsWith("Button", StringComparison.Ordinal) &&
                        t.name.IndexOf("PackageManager",
                            StringComparison.Ordinal) >= 0 &&
                        t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(false);
                        hid++;
                    }
                }
                if (hid > 0 && Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Q3: hid " + hid + " PackageManager entry buttons");
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogError(
                        "Q3 pkgmgr button hide: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(SuperController), "OpenPackageManager")]
        private static class BlockOpenPatch
        {
            private static bool Prefix()
            {
                if (!BlockEntry) return true;
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Q3: PackageManager entry blocked");
                return false;
            }
        }

        // Remember the outgoing activeUI so the postfix can tell when the
        // manager is being closed.
        [HarmonyPatch(typeof(SuperController), "set_activeUI")]
        private static class CloseCleanupPatch
        {
            private static void Prefix(SuperController __instance,
                out int __state)
            {
                __state = -1;
                if (ActiveUiField == null || __instance == null) return;
                object v = ActiveUiField.GetValue(__instance);
                if (v != null) __state = (int)v;
            }

            private static void Postfix(int __state)
            {
                if (__state == ActiveUiPackageManager)
                    CleanupAfterClose();
            }
        }

        // Called once at plugin init: if a package-manager session from
        // before this payload loaded left decoded thumbnails cached (the
        // lingering-GC-stutter residue), drop them now instead of waiting
        // for a close transition that the entry block prevents entirely.
        internal static void PurgeResiduals()
        {
            CleanupAfterClose();
        }

        private static void CleanupAfterClose()
        {
            try
            {
                ImageLoaderThreaded loader = ImageLoaderThreaded.singleton;
                if (loader == null) return;
                int canceled = 0;
                if (QueuedImagesField != null)
                {
                    // queuedImages is only mutated on the main thread
                    // (DispatchPendingImages pops canceled items without
                    // decoding), so enumerating here is safe.
                    IEnumerable queue = QueuedImagesField.GetValue(loader)
                        as IEnumerable;
                    if (queue != null)
                        foreach (object item in queue)
                        {
                            if (item == null) continue;
                            Type it = item.GetType();
                            FieldInfo thumb = it.GetField("isThumbnail");
                            FieldInfo cancel = it.GetField("cancel");
                            if (cancel != null &&
                                (thumb == null ||
                                 (bool)thumb.GetValue(item)))
                            {
                                cancel.SetValue(item, true);
                                canceled++;
                            }
                        }
                }
                loader.PurgeAllThumbnailTextures();
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo(
                        "Q3: purged image-loader residue — canceled " +
                        canceled + " queued thumbnails, cleared thumb cache");
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogError(
                        "Q3 pkgmgr cleanup: " + e.Message);
            }
        }
    }
}
