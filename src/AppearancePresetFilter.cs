using System;
using System.Collections.Generic;
using SimpleJSON;

namespace Quest3TriggerUI
{
    internal static class AppearancePresetFilter
    {
        internal static int StripClothing(JSONClass preset)
        {
            return StripClothing(preset, false);
        }

        internal static int StripClothing(JSONClass preset, bool leaveEmptyClothingList)
        {
            if (preset == null)
                return 0;

            JSONArray storables = preset["storables"].AsArray;
            if (storables == null)
                return 0;

            JSONClass geometry = null;
            for (int i = 0; i < storables.Count; i++)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable != null && string.Equals(
                    storable["id"], "geometry", StringComparison.OrdinalIgnoreCase))
                {
                    geometry = storable;
                    break;
                }
            }
            if (geometry == null)
                return 0;

            HashSet<string> clothingPrefixes = new HashSet<string>(StringComparer.Ordinal);
            JSONArray clothing = geometry["clothing"].AsArray;
            if (clothing != null)
            {
                for (int i = 0; i < clothing.Count; i++)
                {
                    JSONClass item = clothing[i].AsObject;
                    if (item == null)
                        continue;
                    string prefix = item["internalId"];
                    if (string.IsNullOrEmpty(prefix))
                        prefix = item["id"];
                    AddPrefix(clothingPrefixes, item["internalId"]);
                    AddPrefix(clothingPrefixes, item["id"]);
                }
            }

            int removed = clothing == null ? 0 : clothing.Count;
            if (leaveEmptyClothingList)
                geometry["clothing"] = new JSONArray();
            else
                geometry.Remove("clothing");

            // Clothing simulation/material storables are dynamically named from
            // the clothing internalId.  Removing them prevents a shared item in
            // the selected preset from overwriting the currently worn instance.
            for (int i = storables.Count - 1; i >= 0; i--)
            {
                JSONClass storable = storables[i].AsObject;
                if (storable == null)
                    continue;
                string storableId = storable["id"];
                if (!HasPrefix(storableId, clothingPrefixes))
                    continue;
                storables.Remove(i);
                removed++;
            }
            return removed;
        }

        internal static JSONClass CreateClothingOnly(JSONClass source)
        {
            return CreateClothingOnly(source, null);
        }

        internal static JSONClass CreateClothingOnly(
            JSONClass source, JSONClass geometryFallback)
        {
            JSONArray sourceStorables = source == null
                ? null
                : source["storables"].AsArray;

            JSONClass sourceGeometry = null;
            for (int i = 0; sourceStorables != null && i < sourceStorables.Count; i++)
            {
                JSONClass storable = sourceStorables[i].AsObject;
                if (storable != null && string.Equals(
                    storable["id"], "geometry", StringComparison.OrdinalIgnoreCase))
                {
                    sourceGeometry = storable;
                    break;
                }
            }
            if (sourceGeometry == null)
                sourceGeometry = geometryFallback;
            if (sourceGeometry == null)
                return null;

            JSONArray clothing = sourceGeometry["clothing"].AsArray;
            if (clothing == null)
                clothing = new JSONArray();

            HashSet<string> prefixes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < clothing.Count; i++)
            {
                JSONClass item = clothing[i].AsObject;
                if (item == null)
                    continue;
                AddPrefix(prefixes, item["internalId"]);
                AddPrefix(prefixes, item["id"]);
            }

            JSONClass result = new JSONClass();
            result["setUnlistedParamsToDefault"] = "false";
            JSONArray resultStorables = new JSONArray();
            result["storables"] = resultStorables;
            JSONClass geometry = new JSONClass();
            geometry["id"] = "geometry";
            geometry["clothing"] = Clone(clothing);
            resultStorables.Add(geometry);

            for (int i = 0; sourceStorables != null && i < sourceStorables.Count; i++)
            {
                JSONClass storable = sourceStorables[i].AsObject;
                if (storable == null || !HasPrefix(storable["id"], prefixes))
                    continue;
                resultStorables.Add(Clone(storable));
            }
            return result;
        }

        private static JSONNode Clone(JSONNode node)
        {
            return node == null ? null : JSON.Parse(node.ToString());
        }

        private static void AddPrefix(HashSet<string> prefixes, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            prefixes.Add(value);
            string normalized = Normalize(value);
            if (!string.IsNullOrEmpty(normalized))
                prefixes.Add(normalized);
        }

        private static bool HasPrefix(string value, HashSet<string> prefixes)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            foreach (string prefix in prefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    Normalize(value).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            char[] buffer = new char[value.Length];
            int length = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsLetterOrDigit(value[i]))
                    buffer[length++] = char.ToLowerInvariant(value[i]);
            }
            return new string(buffer, 0, length);
        }
    }
}
