using System.Collections.Generic;
using UnityEngine;
namespace Quest3TriggerUI
{
    internal static class ExpressionEntryOrder
    {
        internal static bool MoveBefore(List<string> keys,string moved,string anchor)
        {
            if(moved==anchor||!keys.Contains(moved)||!keys.Contains(anchor))return false;
            if(keys.IndexOf(moved)+1==keys.IndexOf(anchor))return false;
            keys.Remove(moved);keys.Insert(keys.IndexOf(anchor),moved);return true;
        }
    }
    internal sealed class ExpressionEntryTag : MonoBehaviour
    {internal string Key;internal ExpressionBrowserPanel Owner;}
}
