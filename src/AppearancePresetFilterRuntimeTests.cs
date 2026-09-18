using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SimpleJSON;

internal static class AppearancePresetFilterRuntimeTests
{
    private static string _gameRoot;

    public static int Main(string[] args)
    {
        _gameRoot = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        return Run(args.Length > 1 ? args[1] : null);
    }

    private static int Run(string path)
    {
        if (String.IsNullOrEmpty(path) || !File.Exists(path))
            throw new ArgumentException("Appearance preset fixture is missing.");

        string source = File.ReadAllText(path);
        JSONClass selected = JSON.Parse(source).AsObject;
        JSONClass current = JSON.Parse(source).AsObject;
        JSONClass originalGeometry = FindGeometry(current);
        int originalClothing = originalGeometry["clothing"].AsArray.Count;
        int originalHair = originalGeometry["hair"].AsArray.Count;

        JSONClass clothingOnly = Quest3TriggerUI.AppearancePresetFilter.CreateClothingOnly(current);
        JSONClass clothingGeometry = FindGeometry(clothingOnly);
        int snapshotClothing = clothingGeometry["clothing"].AsArray.Count;
        int snapshotHair = clothingGeometry["hair"].AsArray == null
            ? 0 : clothingGeometry["hair"].AsArray.Count;
        JSONClass fallbackOnly =
            Quest3TriggerUI.AppearancePresetFilter.CreateClothingOnly(
                new JSONClass(), originalGeometry);
        int fallbackClothing = FindGeometry(fallbackOnly)["clothing"].AsArray.Count;

        int removed = Quest3TriggerUI.AppearancePresetFilter.StripClothing(selected, true);
        JSONClass filteredGeometry = FindGeometry(selected);
        int filteredClothing = filteredGeometry["clothing"].AsArray.Count;
        int filteredHair = filteredGeometry["hair"].AsArray.Count;

        bool pass = originalClothing > 0 &&
                    snapshotClothing == originalClothing && snapshotHair == 0 &&
                    fallbackClothing == originalClothing &&
                    filteredClothing == 0 && filteredHair == originalHair &&
                    removed >= originalClothing &&
                    File.ReadAllText(path) == source;
        Console.WriteLine(
            "APPEARANCE TRANSACTION RESULT=" + (pass ? "PASS" : "FAIL") +
            "; OriginalClothing=" + originalClothing +
            "; SelectedClothingAfterFilter=" + filteredClothing +
            "; ClothingSnapshot=" + snapshotClothing +
            "; GeometryFallbackClothing=" + fallbackClothing +
            "; SnapshotHair=" + snapshotHair +
            "; HairPreserved=" + (filteredHair == originalHair) +
            "; SourceUntouched=" + (File.ReadAllText(path) == source));
        return pass ? 0 : 1;
    }

    private static JSONClass FindGeometry(JSONClass preset)
    {
        JSONArray storables = preset["storables"].AsArray;
        for (int i = 0; i < storables.Count; i++)
        {
            JSONClass storable = storables[i].AsObject;
            if (storable != null && String.Equals(
                storable["id"], "geometry", StringComparison.OrdinalIgnoreCase))
                return storable;
        }
        throw new InvalidDataException("geometry storable is missing.");
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string[] directories = new string[] {
            Path.Combine(Path.Combine(_gameRoot, "VaM_Data"), "Managed"),
            Path.Combine(Path.Combine(_gameRoot, "BepInEx"), "core")
        };
        for (int i = 0; i < directories.Length; i++)
        {
            string candidate = Path.Combine(directories[i], name);
            if (File.Exists(candidate))
                return Assembly.LoadFrom(candidate);
        }
        return null;
    }
}
