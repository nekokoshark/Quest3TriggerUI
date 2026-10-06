using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Reflection.Emit;
using System.Collections;
using HarmonyLib;
using UnityEngine;
namespace Quest3TriggerUI
{
    internal sealed class ExpressionSceneShield : IDisposable
    {
        private Harmony harmony;
        private static ExpressionSceneShield current;
        private Atom actor;
        private static readonly Dictionary<MethodBase, MethodInfo> gates=new Dictionary<MethodBase, MethodInfo>();
        internal int PatchedCalls { get; private set; }
        private readonly HashSet<object> scenes = new HashSet<object>();
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<object, bool> decisions = new Dictionary<object, bool>();
        internal bool Active { get { return harmony != null; } }
        internal void Enable(Atom selected, string directory)
        {
            Dispose(); actor = selected;
            if (actor == null) throw new InvalidOperationException("视线前方没有女性角色");
            foreach (string n in File.ReadAllLines(Path.Combine(directory,"face_names.txt"))) names.Add(Normal(n));
            foreach (var def in SevenSeasonExpressionLibrary.All)
                foreach (var value in def.ResetValues) names.Add(Normal(value.Parameter));
            var types = new HashSet<Type>();
            foreach (string id in actor.GetStorableIDs())
            {
                JSONStorable st = actor.GetStorableByID(id);
                if (st == null || !st.GetType().FullName.EndsWith("VamTimeline.AtomPlugin",StringComparison.Ordinal)) continue;
                string label=st.GetStringParamValue("pluginLabel") ?? "";
                if (label.StartsWith("Quest3 表情",StringComparison.Ordinal) || label.StartsWith("Q3UJVAM",StringComparison.Ordinal)) continue;
                var prop=st.GetType().GetProperty("animation",BindingFlags.Public|BindingFlags.Instance);
                var field=st.GetType().GetField("animation",BindingFlags.Public|BindingFlags.Instance);
                object a=prop!=null?prop.GetValue(st,null):field==null?null:field.GetValue(st);
                if(a!=null){scenes.Add(a);types.Add(a.GetType());}
            }
            if(scenes.Count==0)throw new InvalidOperationException("当前女性没有可屏蔽的原场景Timeline");
            harmony=new Harmony("q3.expression.scene-shield."+GetType().Assembly.GetName().Name);current=this;
            try
            {
                foreach(Type t in types)
                {
                    MethodInfo method=t.GetMethod("SampleFloatParam",BindingFlags.NonPublic|BindingFlags.Instance);
                    MethodInfo parent=t.GetMethod("SampleFloatParams",BindingFlags.NonPublic|BindingFlags.Instance);
                    if(method==null || parent==null)continue;
                    Type[] args=new Type[]{t,method.GetParameters()[0].ParameterType,method.GetParameters()[1].ParameterType};
                    var gate=new DynamicMethod("Q3FaceGate",typeof(void),args,typeof(ExpressionSceneShield),true);
                    ILGenerator il=gate.GetILGenerator();Label run=il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call,typeof(ExpressionSceneShield).GetMethod("Prefix",BindingFlags.NonPublic|BindingFlags.Static));
                    il.Emit(OpCodes.Brtrue,run);il.Emit(OpCodes.Ret);il.MarkLabel(run);
                    il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Call,method);il.Emit(OpCodes.Ret);
                    gates[method]=gate;
                    harmony.Patch(parent,transpiler:new HarmonyMethod(typeof(ExpressionSceneShield),"ReplaceCalls"));
                }
                if(PatchedCalls==0)throw new InvalidOperationException("当前Timeline未找到可屏蔽的面部采样调用；未启用屏蔽");
            }
            catch { Dispose();throw; }
        }
        private static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                MethodInfo gate;
                var method=instruction.operand as MethodBase;
                if(method!=null && gates.TryGetValue(method,out gate))
                {
                    instruction.opcode=OpCodes.Call;instruction.operand=gate;
                    if(current!=null)current.PatchedCalls++;
                }
                yield return instruction;
            }
        }

        private static string Normal(string n){if(n==null)return "";int p=n.IndexOf(':');return n.StartsWith("morph",StringComparison.Ordinal)&&p>=0?n.Substring(p+1).Trim():n;}
        private static bool Prefix(object __instance,object __0)
        {
            var s=current;if(s==null || !s.scenes.Contains(__instance) || __0==null)return true;
            bool block;
            if(!s.decisions.TryGetValue(__0,out block))
            {
                Type t=__0.GetType();var f=t.GetField("_atom",BindingFlags.Instance|BindingFlags.NonPublic);
                var n=t.GetField("floatParamName",BindingFlags.Public|BindingFlags.Instance);
                var st=t.GetField("storableId",BindingFlags.Public|BindingFlags.Instance);
                block=f!=null && n!=null && st!=null && object.ReferenceEquals(f.GetValue(__0),s.actor) &&
                    object.Equals(st.GetValue(__0),"geometry") && s.names.Contains(Normal(n.GetValue(__0) as string));
                if(s.decisions.Count>=4096)s.decisions.Clear();s.decisions[__0]=block;
            }
            return !block;
        }
        public void Dispose()
        {
            if(harmony!=null){harmony.UnpatchAll(harmony.Id);harmony=null;}
            if(current==this)current=null;actor=null;scenes.Clear();names.Clear();decisions.Clear();gates.Clear();PatchedCalls=0;
        }
    }
}
