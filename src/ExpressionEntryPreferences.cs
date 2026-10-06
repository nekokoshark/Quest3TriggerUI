using System;
using System.Collections.Generic;
using System.IO;
using SimpleJSON;
namespace Quest3TriggerUI
{
    internal sealed class ExpressionEntryPreferences
    {
        internal readonly Dictionary<string,string> Aliases=new Dictionary<string,string>();
        internal readonly HashSet<string> Favorites=new HashSet<string>();
        internal readonly HashSet<string> Deleted=new HashSet<string>();
        internal readonly HashSet<string> Merge=new HashSet<string>();
        internal void Clear(){Aliases.Clear();Favorites.Clear();Deleted.Clear();Merge.Clear();}
        internal void Load(string path)
        {
            Clear();if(!File.Exists(path))return;
            var node=JSON.Parse(File.ReadAllText(path));
            var aliases=node["aliases"].AsObject;if(aliases!=null)foreach(string key in aliases.Keys)Aliases[key]=aliases[key];
            Read(node["favorites"],Favorites);Read(node["deleted"],Deleted);Read(node["merge"],Merge);Merge.Remove("s:0");
        }
        private static void Read(JSONNode node,HashSet<string> set){var array=node.AsArray;if(array!=null)foreach(JSONNode value in array.Childs)set.Add(value);}
        internal void Save(string path)
        {
            var node=new JSONClass();var aliases=new JSONClass();foreach(var pair in Aliases)aliases[pair.Key]=pair.Value;node["aliases"]=aliases;
            node["favorites"]=Array(Favorites);node["deleted"]=Array(Deleted);node["merge"]=Array(Merge);
            string temp=path+".new";File.WriteAllText(temp,node.ToString());if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        private static JSONArray Array(IEnumerable<string> values){var a=new JSONArray();foreach(string s in values)a.Add(s);return a;}
    }
}
