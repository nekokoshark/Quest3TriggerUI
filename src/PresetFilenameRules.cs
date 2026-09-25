using System;

namespace Quest3TriggerUI
{
    internal static class PresetFilenameRules
    {
        internal static bool IsPreset(string name)
        {
            return name != null && name.EndsWith(".vap", StringComparison.OrdinalIgnoreCase);
        }

        internal static string EditName(string name)
        {
            string result = (name ?? "").Trim();
            if (IsPreset(result)) result = result.Substring(0, result.Length - 4);
            if (result.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(7);
            return result.Trim();
        }

        internal static string FileName(string name)
        {
            string result = EditName(name);
            return result.Length == 0 ? "" : "Preset_" + result + ".vap";
        }

        internal static bool NeedsExactLoad(string path)
        {
            string leaf = path.Replace('\\', '/');
            leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);
            return IsPreset(leaf) && !leaf.StartsWith("Preset_", StringComparison.Ordinal);
        }

        internal static bool IsLegacyOverwrite(string selected, string destination)
        {
            if (string.IsNullOrEmpty(selected) || selected.Contains(":/") ||
                !NeedsExactLoad(selected)) return false;
            string source = selected.Replace('\\', '/');
            int slash = source.LastIndexOf('/');
            string canonical = source.Substring(0, slash + 1) + FileName(source.Substring(slash + 1));
            return canonical == destination.Replace('\\', '/');
        }
    }
}
