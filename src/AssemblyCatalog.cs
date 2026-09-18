using System;
using System.Collections.Generic;
using System.Reflection;

namespace Quest3TriggerUI
{
    // Central cache for "locate a type or assembly provided by a third-party
    // plugin". Callers used to rescan AppDomain.GetAssemblies() on every
    // attempt; the snapshot is now reused for one second so a plugin that
    // loads late is still discovered without per-call rescans.
    internal static class AssemblyCatalog
    {
        private const int RefreshMillis = 1000;
        private static readonly List<Assembly> _loaded = new List<Assembly>();
        private static int _lastRefresh = int.MinValue;

        private static List<Assembly> Loaded
        {
            get
            {
                if (Environment.TickCount - _lastRefresh < RefreshMillis &&
                    _loaded.Count > 0)
                    return _loaded;
                _loaded.Clear();
                _loaded.AddRange(AppDomain.CurrentDomain.GetAssemblies());
                _lastRefresh = Environment.TickCount;
                return _loaded;
            }
        }

        internal static Assembly Find(string simpleName)
        {
            List<Assembly> loaded = Loaded;
            for (int i = 0; i < loaded.Count; i++)
                if (string.Equals(loaded[i].GetName().Name, simpleName,
                    StringComparison.OrdinalIgnoreCase))
                    return loaded[i];
            return null;
        }

        internal static Type FindType(string fullName)
        {
            Assembly assembly = FindByType(fullName);
            return assembly == null ? null : assembly.GetType(fullName, false);
        }

        internal static Assembly FindByType(string fullName)
        {
            List<Assembly> loaded = Loaded;
            for (int i = 0; i < loaded.Count; i++)
                if (loaded[i].GetType(fullName, false) != null)
                    return loaded[i];
            return null;
        }
    }
}
