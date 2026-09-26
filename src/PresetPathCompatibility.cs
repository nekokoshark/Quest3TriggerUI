using System;
using System.Reflection;
using MVR.FileManagement;
using SimpleJSON;

namespace Quest3TriggerUI
{
    internal static class PresetPathCompatibility
    {
        private static readonly MethodInfo Prepare = typeof(MeshVR.PresetManager)
            .GetMethod("LoadPresetPreFromJSON", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new Type[] { typeof(JSONClass), typeof(bool) }, null);
        private static readonly FieldInfo Loading = typeof(MeshVR.PresetManagerControl)
            .GetField("isLoadingPreset", BindingFlags.Instance | BindingFlags.NonPublic);

        // Legacy user-renamed files must keep their paths and favorites.
        // Run the native prepare/SetLastRestoredData/post sequence against the
        // selected file, without parsing its filename into a stale presetName.
        internal static bool TryLoad(Atom target, JSONStorable storable, string path)
        {
            if (!PresetFilenameRules.NeedsExactLoad(path)) return false;
            MeshVR.PresetManagerControl control = storable as MeshVR.PresetManagerControl;
            MeshVR.PresetManager manager = UiAssistHudLink.PresetManagerOf(control);
            if (manager == null || Prepare == null || Loading == null)
                throw new InvalidOperationException("Exact preset loader is unavailable: " + path);
            JSONClass json = JSON.Parse(FileManager.ReadAllText(path, false)).AsObject;
            if (json == null || json["storables"] == null)
                throw new InvalidOperationException("Invalid preset JSON: " + path);
            bool previousLoading = (bool)Loading.GetValue(control);
            FileManager.PushLoadDirFromFilePath(path, false);
            try
            {
                Loading.SetValue(control, true);
                Prepare.Invoke(manager, new object[] { json, false });
                target.SetLastRestoredData(manager.lastLoadedJSON,
                    manager.includeAppearance, manager.includePhysical);
                if (!manager.LoadPresetPost())
                    throw new InvalidOperationException("Selected preset failed to restore: " + path);
                ResourceHistoryRuntime.PresetLoaded(manager, path);
                return true;
            }
            finally
            {
                Loading.SetValue(control, previousLoading);
                FileManager.PopLoadDir();
            }
        }
    }
}
