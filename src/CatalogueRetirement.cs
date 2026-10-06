using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Quest3TriggerUI
{
    // One-time migration of the old startup-only catalogue clones. The real
    // character banks, asset prefabs and shared arrays are never cleared.
    internal static class CatalogueRetirement
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo Running = typeof(DAZMorphBank).GetField("_threadsRunning", Fields);
        private static readonly FieldInfo Task = typeof(DAZMorphBank).GetField("applyMorphsTask", Fields);
        private static readonly string[] LegacyNames = {
            "FemaleCharacterMorphBank(Clone)", "FemaleGenMorphBank(Clone)",
            "MaleCharacterMorphBank(Clone)", "MaleGenMorphBank(Clone)", "MaleGen2MorphBank(Clone)"
        };
        private static bool _done;

        internal static void Tick()
        {
            if (_done) return;
            var sc = SuperController.singleton;
            if (sc == null || sc.mainHUD == null || sc.isLoading || SceneLoadAccelerator.SceneLoadActive ||
                LoadWindow.PresetBusy || WardrobeJanitor.ImagesBusy()) return;
            if (Running == null || Running.FieldType != typeof(bool) || Task == null ||
                Task.FieldType != typeof(DAZMorphTaskInfo))
                throw new MissingFieldException("Startup catalogue worker mapping changed");
            _done = true;
            var seen = new HashSet<GameObject>();
            int retired = 0, preserved = 0;
            foreach (var bank in Resources.FindObjectsOfTypeAll<DAZMorphBank>())
            {
                if (bank == null) continue;
                var parent = bank.transform.parent;
                if (parent == null || parent.name != "Q3-PreheatCatalogue") continue;
                var holder = parent.gameObject;
                if (!seen.Add(holder)) continue;
                if (!CanRetire(holder)) { preserved++; continue; }
                // OnDestroy follows the native lifecycle. The guards require
                // no thread/task at all, so no busy-wait or thread race occurs.
                UnityEngine.Object.Destroy(holder);
                retired++;
            }
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[catalogue-retire] startupPreheat=removed typedDiscovery=once" +
                    " retiredHolders=" + retired + " preservedHolders=" + preserved +
                    " currentBanks=preserved sharedCaches=unchanged gcPolicy=unchanged countsNotBytes=True");
        }

        private static bool CanRetire(GameObject holder)
        {
            if (holder.activeSelf || holder.transform.parent != null ||
                holder.transform.childCount != LegacyNames.Length) return false;
            var rootComponents = holder.GetComponents<Component>();
            if (rootComponents.Length != 1 || !(rootComponents[0] is Transform)) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int n = 0; n < holder.transform.childCount; n++)
            {
                var child = holder.transform.GetChild(n);
                if (Array.IndexOf(LegacyNames, child.name) < 0 || !names.Add(child.name)) return false;
                var banks = child.GetComponents<DAZMorphBank>();
                if (banks.Length != 1) return false;
                var bank = banks[0];
                if (!ReferenceEquals(bank.connectedMesh, null) || !ReferenceEquals(bank.morphBones, null) ||
                    !ReferenceEquals(bank.morphBones2, null) || bank.onMorphFavoriteChangedHandlers != null ||
                    (bool)Running.GetValue(bank) || Task.GetValue(bank) != null) return false;
            }
            // Reject mixed/custom hierarchies rather than deleting anything
            // solely because its parent uses our historical name.
            foreach (var component in holder.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !(component is Transform || component is DAZMorphBank || component is DAZMorphSubBank))
                    return false;
                if (component is DAZMorphBank && component.transform.parent != holder.transform)
                    return false;
            }
            return true;
        }
    }
}
