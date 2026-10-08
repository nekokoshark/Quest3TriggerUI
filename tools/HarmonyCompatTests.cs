using System;
using System.Reflection;
using HarmonyLib;
namespace Quest3TriggerUI {
class CompatTests {
 static bool Prefix(ref int __result) { __result=42; return false; }
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
 static int Original() { return 7; }
 static int Main() {
  try {
   var h=new Harmony("q3.compat.test");
   var target=typeof(CompatTests).GetMethod("Original",BindingFlags.Static|BindingFlags.NonPublic);
   var prefix=new HarmonyMethod(typeof(CompatTests).GetMethod("Prefix",BindingFlags.Static|BindingFlags.NonPublic));
   var resolve=typeof(HarmonyCompat).GetField("PatchMethod",BindingFlags.NonPublic|BindingFlags.Static);
   var method=(MethodInfo)resolve.GetValue(null);
   Console.WriteLine("Harmony="+typeof(Harmony).Assembly.GetName().Version+" Return="+method.ReturnType.Name);
   HarmonyCompat.Patch(h,target,prefix:prefix);
   if(Original()!=42)throw new Exception("Prefix not active");
   h.UnpatchAll("q3.compat.test");
   if(Original()!=7)throw new Exception("Original not restored");
   Console.WriteLine("PATCH=42 UNPATCH=7 PASS");return 0;
  }catch(Exception e){Console.WriteLine("TEST_ERROR: "+e);return 1;}
 }
}}
