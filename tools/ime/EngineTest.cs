// Standalone harness for PinyinEngine logic — compile together with the real
// PinyinEngine.cs plus stubs below. Run from a dir containing the dicts.
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityEngine { }

namespace BepInEx
{
    internal static class Paths
    {
        internal static string PluginPath = ".";
        internal static string ConfigPath = ".";
    }
}

namespace Quest3TriggerUI
{
    internal sealed class ImeCandidateSnapshot
    {
        internal readonly List<string> Candidates = new List<string>();
        internal int Selection;
        internal int PageStart;
        internal int PageSize;
        internal string Composition = string.Empty;
        internal bool ChineseMode;
    }

    internal static class Quest3TriggerUIPlugin
    {
        internal sealed class LogStub
        {
            internal void LogInfo(string m) { Console.WriteLine("[log] " + m); }
            internal void LogError(string m) { Console.WriteLine("[err] " + m); }
        }
        internal static readonly LogStub Log = new LogStub();
    }

    internal static class VrTextInputBridge
    {
        internal static char Character(ushort key, bool shift, bool caps) { return '?'; }
    }

    internal static class EngineTest
    {
        static int fails;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS" : "FAIL") + " " + name);
            if (!ok) fails++;
        }

        static void Type(string letters)
        {
            foreach (char c in letters)
            {
                string commit;
                PinyinEngine.HandleKey((ushort)(char.ToUpper(c)), false, out commit);
            }
        }

        static void Reset()
        {
            string commit;
            PinyinEngine.HandleKey(0x1B, false, out commit);
        }

        static bool Has(string word)
        {
            return PinyinEngine.Snapshot.Candidates.Contains(word);
        }

        static void Dump(int n)
        {
            var c = PinyinEngine.Snapshot.Candidates;
            Console.WriteLine("   comp=\"" + PinyinEngine.Snapshot.Composition +
                "\" cands=" + c.Count + ": " +
                string.Join(" ", c.GetRange(0, Math.Min(n, c.Count))));
        }

        static int Main()
        {
            PinyinEngine.CloudEnabled = false;   // deterministic local-only
            PinyinEngine.EnsureLoaded();   // kicks background load
            for (int i = 0; i < 600 && !PinyinEngine.Available; i++)
                System.Threading.Thread.Sleep(100);
            Check(PinyinEngine.Available, "dicts load");

            Type("nihao");
            Dump(9);
            Check(Has("你好"), "nihao -> 你好");
            Reset();

            Type("zhonghuarenmin");
            Dump(9);
            Check(Has("中华人民") || Has("中华人民共和国"), "long phrase partial/full");
            Reset();

            Type("zang");
            Dump(9);
            Check(Has("张") || Has("站") || Has("章"), "fuzzy zh->z (zang 出翘舌字)");
            Reset();

            Type("nh");
            Dump(9);
            Check(Has("你好"), "initials nh -> 你好");
            Reset();

            Type("niha");
            Dump(9);
            Check(Has("你好"), "completion niha -> 你好");
            Reset();

            Type("xian");
            Dump(9);
            Check(Has("先") && Has("西安"), "xian ambiguous -> 先+西安");
            Reset();

            // Partial commit: pick 中华 from zhonghua… input then continue
            Type("zhonghuarenmin");
            int idx = PinyinEngine.Snapshot.Candidates.IndexOf("中华");
            string commit;
            if (idx >= 0)
            {
                PinyinEngine.CommitAt(idx, out commit);
                Check(commit == "中华", "commit 中华");
                Check(PinyinEngine.Snapshot.Composition.Contains("ren") ||
                      PinyinEngine.Snapshot.Candidates.Count > 0,
                      "remainder keeps composing: " + PinyinEngine.Snapshot.Composition);
            }
            else Check(false, "中华 present for partial commit");
            Reset();

            Type("shuru");
            Dump(9);
            Check(Has("输入"), "shuru -> 输入");
            Reset();

            Type("beijing");
            Dump(9);
            Check(Has("北京"), "beijing -> 北京");
            Reset();

            Type("leidianjiangjun");
            Dump(9);
            Check(Has("雷电将军"), "leidianjiangjun -> 雷电将军 (rime-ice)");
            Reset();

            Type("ldjj");
            Dump(9);
            Check(Has("雷电将军"), "ldjj 简拼 -> 雷电将军");
            Reset();

            // Learning: commit 雷电将军 via full pinyin, then it must top
            // the initials recall of ldjj.
            Type("leidianjiangjun");
            int li = PinyinEngine.Snapshot.Candidates.IndexOf("雷电将军");
            if (li >= 0)
            {
                string lc;
                PinyinEngine.CommitAt(li, out lc);
                Check(lc == "雷电将军", "learn: commit 雷电将军");
            }
            else Check(false, "learn: 雷电将军 present");
            Reset();
            Type("ldjj");
            Dump(9);
            Check(PinyinEngine.Snapshot.Candidates.Count > 0 &&
                  PinyinEngine.Snapshot.Candidates[0] == "雷电将军",
                  "learn: ldjj -> 雷电将军 top after commit");
            Reset();
            Type("leidianjiangjun");
            Check(PinyinEngine.Snapshot.Candidates.Count > 0 &&
                  PinyinEngine.Snapshot.Candidates[0] == "雷电将军",
                  "learn: full recall top too");
            Reset();
            Type("leidian");
            Dump(9);
            Check(Has("雷电将军"), "learn: full-key prefix leidian recalls");
            Reset();
            Type("ld");
            Dump(9);
            Check(Has("雷电将军"), "learn: init prefix ld recalls");
            Reset();

            PinyinEngine.FlushUserDict();
            Check(File.Exists("Quest3TriggerUI.pinyin-user.txt"),
                  "userdict file saved");

            Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
            return fails;
        }
    }
}
