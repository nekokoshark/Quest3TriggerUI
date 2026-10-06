using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class SceneJsonRuntime
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly MethodInfo Scene = typeof(SuperController).GetMethod("LoadInternal", All);
        private static readonly MethodInfo Load = typeof(SuperController).GetMethod("LoadJSON", All);
        private static readonly MethodInfo Import = typeof(MeshVR.DAZImport).GetMethod("ReadJSON", All);
        private static readonly MethodInfo RulesMethod = typeof(SuperController).GetMethod("LoadPluginPathReplacements", All);
        private static readonly MethodInfo ApplyMethod = typeof(SuperController).GetMethod("ApplyPluginPathReplacements", All);
        private static readonly Func<SuperController, Dictionary<string, string>> LoadRules =
            (Func<SuperController, Dictionary<string, string>>)Delegate.CreateDelegate(typeof(Func<SuperController, Dictionary<string, string>>), RulesMethod);
        private static readonly Func<SuperController, string, string> ApplyOriginal =
            (Func<SuperController, string, string>)Delegate.CreateDelegate(typeof(Func<SuperController, string, string>), ApplyMethod);
        private static Harmony harmony;
        private static int loadAnchors, importAnchors, sceneAnchors;

        internal static string Status
        {
            get
            {
                return "[scene-json] installed namespace=" + typeof(SceneJsonRuntime).Namespace
                    + " LoadJSON=" + loadAnchors + "/1 ReadJSON=" + importAnchors
                    + "/2 LoadInternal=" + sceneAnchors + "/1 inputBlockBytes=32768 necessaryTreePreserved=True";
            }
        }

        internal static void Install()
        {
            if (harmony != null) return;
            Require(Load, "93A27B28B8B6185700F341B374CFA59CDC0E79C859D4759841FE4BF1F1DF0A7E");
            Require(Scene, "29F63DE2DFBD24E8F21979EF9BC1D92B899F5D88BE755E6417B811B21BB0FCC3");
            Require(Import, "8F898E68BF5DB76F0C84FE87B6BF55FA3DEA6DBE5EE41B83484CC1369A169D0F");
            Require(ApplyMethod, "94A054610EC9E09C032D367CBBA40C83E27C86C9AD6041F806BC5C3FA22DD746");
            Require(RulesMethod, "EA56D4477428D0F5AEDD4A2C47B98171623790291D5DF17CAFB3C77C1FB0AF4C");
            Require(typeof(JSONNode).GetMethod("Parse"), "F32FF0D2F70E90029F86EAC094171E895CA6B63204F9D8F679F4C0C8EBF0BE83");
            harmony = new Harmony("quest3triggerui.scenejsonstreaming." + typeof(SceneJsonRuntime).Namespace);
            try
            {
                harmony.Patch(Load, transpiler: new HarmonyMethod(typeof(SceneJsonRuntime).GetMethod("Adjacent", All)));
                harmony.Patch(Import, transpiler: new HarmonyMethod(typeof(SceneJsonRuntime).GetMethod("Adjacent", All)));
                harmony.Patch(Scene, transpiler: new HarmonyMethod(typeof(SceneJsonRuntime).GetMethod("SceneBlock", All)));
            }
            catch { Shutdown(); throw; }
        }

        internal static void Shutdown()
        {
            if (harmony == null) return;
            harmony.UnpatchAll(harmony.Id);
            harmony = null;
            loadAnchors = importAnchors = sceneAnchors = 0;
        }

        private static bool Calls(CodeInstruction code, Type type, string name)
        {
            var method = code.operand as MethodInfo;
            return (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                && method != null && method.DeclaringType == type && method.Name == name;
        }

        private static bool OwnCall(CodeInstruction code, string typeName, string name)
        {
            var method = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && method != null && method.Name == name && method.DeclaringType.Name == typeName
                && method.DeclaringType.Namespace != null
                && (method.DeclaringType.Namespace == "Quest3TriggerUI" || method.DeclaringType.Namespace.StartsWith("Quest3TriggerUI.", StringComparison.Ordinal));
        }

        private static IEnumerable<CodeInstruction> Adjacent(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            if (__originalMethod == Import) importAnchors = 0;
            else if (__originalMethod == Load) loadAnchors = 0;
            var codes = new List<CodeInstruction>(instructions);
            var received = codes.ConvertAll(code => new CodeInstruction(code));
            int anchors = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (OwnCall(codes[i], "JsonStreamParser", "ParseEntry") || OwnCall(codes[i], "JsonStreamParser", "Parse"))
                {
                    codes[i].operand = typeof(JsonStreamParser).GetMethod(((MethodInfo)codes[i].operand).Name, All);
                    anchors++;
                }
                else if (i + 1 < codes.Count && Calls(codes[i + 1], typeof(JSON), "Parse")
                    && (Calls(codes[i], typeof(FileEntryStreamReader), "ReadToEnd") || Calls(codes[i], typeof(TextReader), "ReadToEnd")))
                {
                    if (codes[i + 1].labels.Count != 0 || codes[i + 1].blocks.Count != 0) return received;
                    string name = Calls(codes[i], typeof(FileEntryStreamReader), "ReadToEnd") ? "ParseEntry" : "Parse";
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = typeof(JsonStreamParser).GetMethod(name, All);
                    codes.RemoveAt(i + 1);
                    anchors++;
                }
            }
            if (anchors != (__originalMethod == Import ? 2 : 1)) return received;
            if (__originalMethod == Import) importAnchors = anchors;
            else if (__originalMethod == Load) loadAnchors = anchors;
            return codes;
        }

        private static IEnumerable<CodeInstruction> SceneBlock(IEnumerable<CodeInstruction> instructions)
        {
            sceneAnchors = 0;
            var codes = new List<CodeInstruction>(instructions);
            foreach (var code in codes)
                if (OwnCall(code, "SceneJsonRuntime", "ParseScene"))
                {
                    code.operand = typeof(SceneJsonRuntime).GetMethod("ParseScene", All);
                    foreach (var companion in codes)
                        if (OwnCall(companion, "SceneJsonRuntime", "Completed")) companion.operand = typeof(SceneJsonRuntime).GetMethod("Completed", All);
                    sceneAnchors = 1;
                    return codes;
                }
            int read = codes.FindIndex(code => Calls(code, typeof(FileEntryStreamReader), "ReadToEnd"));
            if (read < 1) return codes;
            int end = codes.FindIndex(read, code => Calls(code, typeof(SuperController), "PerfLog"));
            int start = read - 1;
            // Harmony/MonoMod can renumber locals. Reuse the reader operand and
            // the timestamp load from the actual straight-line read-time delta;
            // never confuse a remapped local index with the original IL index.
            if (end < read || codes[start].opcode != OpCodes.Ldloc_S || !Calls(codes[read + 2], typeof(Time), "get_realtimeSinceStartup")
                || codes[read + 3].opcode != OpCodes.Ldloc_S)
                return codes;
            var timestamp = new CodeInstruction(codes[read + 3]);
            for (int i = start + 1; i <= end; i++)
                if (codes[i].labels.Count != 0 || codes[i].blocks.Count != 0) return codes;
            var replacement = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(codes[start]),
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(timestamp),
                new CodeInstruction(OpCodes.Call, typeof(SceneJsonRuntime).GetMethod("ParseScene", All)),
                new CodeInstruction(OpCodes.Stfld, typeof(SuperController).GetField("loadJson", All)),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(timestamp),
                new CodeInstruction(OpCodes.Call, typeof(SceneJsonRuntime).GetMethod("Completed", All))
            };
            // Keep the original finally-region entry on the first new opcode.
            replacement[1].labels.Clear(); replacement[1].blocks.Clear();
            replacement[0].labels.AddRange(codes[start].labels);
            replacement[0].blocks.AddRange(codes[start].blocks);
            codes.RemoveRange(start, end - start + 1);
            codes.InsertRange(start, replacement);
            sceneAnchors = 1;
            return codes;
        }

        internal static JSONNode ParseScene(FileEntryStreamReader reader, SuperController owner, string path, float started)
        {
            var applyPatches = Harmony.GetPatchInfo(ApplyMethod);
            if (!JsonStreamParser.CanStreamEntry(reader) || reader.StreamReader == null
                || (applyPatches != null && applyPatches.Owners.Count > 0))
                return JSON.Parse(ApplyOriginal(owner, reader.ReadToEnd()));
            Utf16Spool input;
            try { input = new Utf16Spool(); }
            catch (IOException) { return JSON.Parse(ApplyOriginal(owner, reader.ReadToEnd())); }
            catch (UnauthorizedAccessException) { return JSON.Parse(ApplyOriginal(owner, reader.ReadToEnd())); }
            using (input)
            {
                input.CopyFrom(reader.StreamReader); // Complete source IO before config loading or parsing.
                int readMs = (int)((Time.realtimeSinceStartup - started) * 1000f);
                SuperController.LogMessage("[路径替换] 开始处理场景文件: " + path);
                SuperController.LogMessage("[路径替换] JSON内容长度: " + input.Length + " 字符, 文件读取: " + readMs + "ms");
                var rules = LoadRules(owner);
                if (rules == null) rules = new Dictionary<string, string>();
                JSONNode result;
                if (!PathReplacementStream.Supported(rules))
                {
                    string text = PathReplacementStream.ApplyOriginal(input.Rewind().ReadToEnd(), rules);
                    result = JSON.Parse(text);
                }
                else
                {
                    Utf16Spool replaced = PathReplacementStream.Apply(input, rules);
                    try
                    {
                        result = JsonStreamParser.Parse(replaced.Rewind());
                    }
                    finally { if (replaced != input) replaced.Dispose(); }
                }
                return result;
            }
        }

        private static void Completed(string path, float started)
        {
            // As in the installed block, the public loadJson field is assigned
            // BEFORE completion logging and BEFORE the reader's finally closes.
            SuperController.PerfLog("[LoadPerf] JSON流式读取/替换/解析完成, 合计: "
                + (int)((Time.realtimeSinceStartup - started) * 1000f) + "ms, 文件: " + path);
        }

        private static void Require(MethodInfo method, string expected)
        {
            var body = method.GetMethodBody();
            var text = new StringBuilder(BitConverter.ToString(body.GetILAsByteArray()));
            text.Append('|').Append(body.InitLocals);
            foreach (var local in body.LocalVariables) text.Append('|').Append(local.LocalType.FullName).Append(':').Append(local.IsPinned);
            foreach (var clause in body.ExceptionHandlingClauses)
            {
                text.Append('|').Append(clause.Flags).Append(':').Append(clause.TryOffset).Append(':').Append(clause.TryLength)
                    .Append(':').Append(clause.HandlerOffset).Append(':').Append(clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(':').Append(clause.CatchType.FullName);
            }
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "") != expected)
                    throw new InvalidDataException("JSON input method fingerprint changed: " + method.Name);
        }
    }
}
