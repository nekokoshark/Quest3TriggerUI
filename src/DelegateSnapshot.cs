using System;
using System.Collections.Generic;
using System.Reflection;

namespace Quest3TriggerUI
{
    // Unity's legacy Mono GetInvocationList writes kpm_next into shared nodes.
    // Clone each node separately instead: never write into the source chain.
    internal static class DelegateSnapshot
    {
        private static readonly FieldInfo Previous = typeof(MulticastDelegate)
            .GetField("prev", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Next = typeof(MulticastDelegate)
            .GetField("kpm_next", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static Delegate[] GetInvocationList(Delegate value)
        {
            if (value == null) throw new ArgumentNullException("value");
            // Other runtimes use an invocation array, not these linked fields.
            if (Previous == null || Next == null) return value.GetInvocationList();
            List<Delegate> result = new List<Delegate>();
            for (Delegate node = value; node != null;
                node = Previous.GetValue(node) as Delegate)
            {
                Delegate copy = (Delegate)node.Clone();
                Previous.SetValue(copy, null);
                Next.SetValue(copy, null);
                result.Add(copy);
            }
            result.Reverse();
            return result.ToArray();
        }
    }
}
