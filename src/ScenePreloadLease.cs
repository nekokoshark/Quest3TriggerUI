using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AssetBundles;
using HarmonyLib;
namespace Quest3TriggerUI
{
    // Native scene speculation acquires a lease with no consumer. Let the
    // unchanged real LoadAssetAsync consumer initiate and own its request.
    internal static class ScenePreloadLease
    {
        private static Harmony _harmony;
        private static long _deferred;
        internal static void Preload(string name)
        {
            _deferred++;
            if (_deferred <= 4 || _deferred % 64 == 0)
                Log("consumerDeferred total=" + _deferred + " speculativeLease=notAcquired actualLoad=native countsNotBytes=True");
        }
        private static MethodInfo Target()
        {
            var type=typeof(SuperController).GetNestedType("<LoadCo>d__1347",BindingFlags.NonPublic);
            return type==null?null:type.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code=new List<CodeInstruction>(instructions);
            var native=typeof(AssetBundleManager).GetMethod("PreloadAssetBundle",new[]{typeof(string)});
            var replacement=typeof(ScenePreloadLease).GetMethod("Preload",BindingFlags.NonPublic|BindingFlags.Static);
            int count=0;
            foreach(var instruction in code)
            {
                if(instruction.Calls(native)) { instruction.opcode=OpCodes.Call;instruction.operand=replacement;count++; }
                if(instruction.opcode==OpCodes.Ldstr)
                {
                    string text=instruction.operand as string;
                    if(text!=null) instruction.operand=text.Replace("AB预加载完成: 触发","AB预加载延后: 候选").Replace("AB预加载: bundle=","AB预加载延后: bundle=");
                }
            }
            if(count!=1) throw new InvalidOperationException("LoadCo speculative preload anchors="+count);
            return code;
        }
        internal static int CheckAnchor()
        {
            var code=Rewrite(PatchProcessor.GetOriginalInstructions(Target(),(ILGenerator)null));
            var replacement=typeof(ScenePreloadLease).GetMethod("Preload",BindingFlags.NonPublic|BindingFlags.Static);
            int count=0;foreach(var i in code) if(i.Calls(replacement)) count++;
            return count;
        }
        internal static void Install()
        {
            if(_harmony!=null) return;
            var target=Target();
            if(target==null || target.ReturnType!=typeof(bool)) throw new MissingMemberException("LoadCo state machine changed");
            _harmony=new Harmony("quest3triggerui.scenepreloadlease."+typeof(ScenePreloadLease).Namespace);
            try
            {
                _harmony.Patch(target,transpiler:new HarmonyMethod(typeof(ScenePreloadLease),"Rewrite"));
                Log("installed preloadAnchors=1 realConsumers=native gcPolicy=unchanged");
            }
            catch { Shutdown();throw; }
        }
        internal static void Shutdown()
        {
            if(_harmony==null) return;
            _harmony.UnpatchAll(_harmony.Id);_harmony=null;_deferred=0;
        }
        private static void Log(string value)
        {
            if(Quest3TriggerUIPlugin.Log!=null) Quest3TriggerUIPlugin.Log.LogInfo("[scene-preload-lease] "+value);
        }
    }
}
