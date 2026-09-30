using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DynamicCSharp;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Reuse code, never plugin instances/domains. Narrow audited source allowlist.
    internal static class ClothingScriptAssemblyReuse
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal const int Capacity = 8;
        private static readonly string[] Sources = {
            "MVRPlugin_Stopper_ClothingPluginManager_7__Custom_Scripts_Stopper_ClothingPluginManager_ClothingPluginManager_cs_",
            "MVRPlugin_Stopper_ClothingDrivenMorphs_1__Custom_Scripts_Stopper_ClothingDrivenMorphs_cslist_"
        };
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private static readonly Queue<string> Order = new Queue<string>();
        private static readonly FieldInfo Compiler = typeof(DynamicCSharp.Compiler.ScriptCompiler).GetField("compiler", All);
        private static readonly FieldInfo Sandbox = typeof(ScriptDomain).GetField("sandbox", All);
        private static readonly MethodInfo Reset = typeof(DynamicCSharp.Compiler.ScriptCompiler).GetMethod("ResetCompiler", All);
        private static readonly ConstructorInfo Wrap = typeof(ScriptAssembly).GetConstructor(All, null, new[] { typeof(ScriptDomain), typeof(Assembly) }, null);
        private static Harmony _harmony;
        private static int _hits, _misses;
        internal sealed class Ticket { internal string Key, Source; internal bool Hit; }

        internal static bool Allowed(string prefix)
        {
            if (prefix == null) return false;
            foreach (string stem in Sources)
            {
                if (!prefix.StartsWith(stem, StringComparison.Ordinal) || prefix.Length != stem.Length + 32) continue;
                for (int i = stem.Length; i < prefix.Length; i++)
                    if (!(prefix[i] >= '0' && prefix[i] <= '9') && !(prefix[i] >= 'a' && prefix[i] <= 'f')) return false;
                return true;
            }
            return false;
        }
        internal static string Key(string prefix, string[] sources, string environment)
        {
            if (!Allowed(prefix) || sources == null || sources.Length == 0) return null;
            using (var hash = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(prefix); writer.Write(environment); writer.Write(sources.Length);
                foreach (string source in sources)
                {
                    if (source == null || source.Length > 1024 * 1024) return null;
                    writer.Write(source);
                }
                writer.Flush(); stream.FlushFinalBlock();
                return Convert.ToBase64String(hash.Hash);
            }
        }
        private static CompilerParameters Parameters(ScriptDomain domain)
        {
            var compiler = Compiler.GetValue(domain.CompilerService);
            if (compiler == null || compiler.GetType().FullName != "DynamicCSharp.Compiler.McsMarshal") return null;
            return (CompilerParameters)compiler.GetType().GetField("parameters", All).GetValue(compiler);
        }
        private static string EnvironmentKey(CompilerParameters parameters)
        {
            var settings = DynamicCSharp.DynamicCSharp.Settings;
            var text = new StringBuilder();
            text.Append(typeof(ScriptDomain).Assembly.ManifestModule.ModuleVersionId).Append('|')
                .Append(parameters.CompilerOptions).Append('|').Append(parameters.GenerateExecutable).Append('|')
                .Append(parameters.TreatWarningsAsErrors).Append('|').Append(parameters.WarningLevel).Append('|')
                .Append(parameters.IncludeDebugInformation).Append('|').Append(settings.debugMode).Append('|')
                .Append(settings.securityCheckCode).Append('|').Append(settings.compilerWorkingDirectory).Append('|')
                .Append(Path.GetFullPath("."));
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string reference in parameters.ReferencedAssemblies) refs.Add(reference);
            foreach (string reference in settings.assemblyReferences) refs.Add(reference);
            var orderedRefs = new List<string>(refs); orderedRefs.Sort(StringComparer.Ordinal);
            foreach (string reference in orderedRefs)
            {
                string path = reference;
                if (!File.Exists(path)) path = Path.Combine(settings.compilerWorkingDirectory ?? "", reference);
                text.Append('\n').Append(reference);
                if (File.Exists(path))
                {
                    var file = new FileInfo(path);
                    text.Append('|').Append(file.FullName).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
                }
                else
                {
                    // Bare framework names are resolved by the running runtime.
                    // Only known already-loaded references may use its MVID.
                    bool found = false;
                    string name = Path.GetFileNameWithoutExtension(reference);
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                        if (assembly.GetName().Name == name)
                        { text.Append('|').Append(assembly.ManifestModule.ModuleVersionId); found = true; break; }
                    if (!found) return null;
                }
            }
            return text.ToString();
        }
        internal static bool Begin(ScriptDomain domain, string[] input, bool files, out Ticket ticket, out ScriptAssembly result)
        {
            ticket = null; result = null;
            try
            {
                if (domain == null || domain.CompilerService == null || domain.CompilerService.IsCompiling ||
                    Sandbox.GetValue(domain) != AppDomain.CurrentDomain) return true;
                var parameters = Parameters(domain);
                if (parameters == null || !Allowed(parameters.OutputAssembly)) return true;
                string prefix = parameters.OutputAssembly;
                var sources = input;
                if (files)
                {
                    if (input == null) return true;
                    sources = new string[input.Length];
                    for (int i = 0; i < input.Length; i++)
                    {
                        if (!File.Exists(input[i]) || new FileInfo(input[i]).Length > 4 * 1024 * 1024) return true;
                        sources[i] = File.ReadAllText(input[i]);
                    }
                }
                string environment = EnvironmentKey(parameters);
                if (environment == null) { Log("native: unresolved compiler reference"); return true; }
                string key = Key(prefix, sources, environment);
                if (key == null) return true;
                ticket = new Ticket { Key = key, Source = prefix.StartsWith(Sources[0], StringComparison.Ordinal) ? "ClothingPluginManager.7" : "ClothingDrivenMorphs.1" };
                Assembly raw;
                lock (Gate) { if (!Cache.TryGetValue(key, out raw)) { _misses++; return true; } }
                result = (ScriptAssembly)Wrap.Invoke(new object[] { domain, raw });
                // Match native per-compile reset and reference consumption, without
                // retaining prior assembly bytes/errors or creating another assembly.
                Reset.Invoke(domain.CompilerService, null);
                parameters.ReferencedAssemblies.Clear();
                ticket.Hit = true;
                Log("hit source=" + ticket.Source + " hits=" + (++_hits) + " misses=" + _misses + " entries=" + Cache.Count + " newWrapper=True");
                return false;
            }
            catch (Exception e) { ticket = null; result = null; Log("native: " + e.GetType().Name + ": " + e.Message); return true; }
        }
        internal static void Complete(Ticket ticket, ScriptAssembly result)
        {
            if (ticket == null || ticket.Hit || result == null || result.RawAssembly == null) return;
            lock (Gate)
            {
                if (Cache.ContainsKey(ticket.Key)) return;
                while (Cache.Count >= Capacity) Cache.Remove(Order.Dequeue());
                Cache.Add(ticket.Key, result.RawAssembly); Order.Enqueue(ticket.Key);
                Log("stored source=" + ticket.Source + " entries=" + Cache.Count + " codeOnly=True");
            }
        }
        private static bool ManyPrefix(ScriptDomain __instance, string[] __0, MethodBase __originalMethod, ref ScriptAssembly __result, out Ticket __state)
        { ScriptAssembly result; bool run = Begin(__instance, __0, __originalMethod.Name.EndsWith("Files", StringComparison.Ordinal), out __state, out result); if (!run) __result = result; return run; }
        private static void ManyPostfix(ScriptAssembly __result, Ticket __state) { Complete(__state, __result); }
        private static bool SinglePrefix(ScriptDomain __instance, string __0, MethodBase __originalMethod, ref ScriptType __result, out Ticket __state)
        {
            ScriptAssembly result;
            bool run = Begin(__instance, new[] { __0 }, __originalMethod.Name.EndsWith("File", StringComparison.Ordinal), out __state, out result);
            if (!run) __result = result.MainType;
            return run;
        }
        private static void SinglePostfix(ScriptType __result, Ticket __state) { Complete(__state, __result == null ? null : __result.Assembly); }
        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                if (Compiler == null || Sandbox == null || Reset == null || Wrap == null) throw new MissingMemberException("script reuse binding");
                // This exact VaM build has a ret-only checker. Refuse reuse if a
                // different build actually inspects bytes: never bypass that check.
                var checker = typeof(ScriptDomain).Assembly.GetType("DynamicCSharp.Security.AssemblyChecker", true)
                    .GetMethod("SecurityCheckAssembly", All).GetMethodBody().GetILAsByteArray();
                if (checker.Length != 1 || checker[0] != 0x2a) throw new InvalidOperationException("active bytecode checker requires native loading");
                _harmony = new Harmony("Quest3TriggerUI.clothing-script-assembly-reuse");
                foreach (string name in new[] { "CompileAndLoadScriptSources", "CompileAndLoadScriptFiles", "CompileAndLoadScriptSource", "CompileAndLoadScriptFile" })
                {
                    bool single = name.EndsWith("Source", StringComparison.Ordinal) || name.EndsWith("File", StringComparison.Ordinal);
                    var method = typeof(ScriptDomain).GetMethod(name, All, null, new[] { single ? typeof(string) : typeof(string[]) }, null);
                    var info = Harmony.GetPatchInfo(method);
                    if (info != null && info.Owners.Count > 0) throw new InvalidOperationException(name + " already patched: " + string.Join(",", new List<string>(info.Owners).ToArray()));
                    _harmony.Patch(method, prefix: Hook(single ? "SinglePrefix" : "ManyPrefix"), postfix: Hook(single ? "SinglePostfix" : "ManyPostfix"));
                }
                Log("installed routes=4 allowlist=2 cap=8 instanceCreation=native");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }
        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(ClothingScriptAssemblyReuse).GetMethod(name, All)); }
        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
            lock (Gate) { Cache.Clear(); Order.Clear(); _hits = _misses = 0; }
        }
        private static void Log(string message)
        { if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[script-reuse] " + message); }
    }
}
