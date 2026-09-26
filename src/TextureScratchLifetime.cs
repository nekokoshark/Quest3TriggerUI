using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    // Only moves disposal of the four local GDI objects. Pixel transforms, raw
    // buffer layout, native tail labels and the three-row bump patch stay intact.
    internal static class TextureScratchLifetime
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-scratch-lifetime");
                _harmony.UnpatchAll(_harmony.Id);
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("ProcessFromStream",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    transpiler: new HarmonyMethod(typeof(TextureScratchLifetime).GetMethod("Transpile", Static)));
                Log("installed; aligned RGB24 direct copy; other layouts retain native drawing; early GDI release");
            }
            catch (Exception e) { Shutdown(); Log("not installed: " + e.Message); }
        }

        private static bool Active() { return Enabled == null || Enabled.Value; }
        internal static long DirectCopies { get { return System.Threading.Interlocked.Read(ref _directCopies); } }
        private static long _directCopies;
        private static bool DirectCopy(int sourceFormat, int targetFormat, int sourceWidth, int sourceHeight,
            int width, int height, bool setSize)
        {
            // Native Marshal.Copy reads width*height*3 contiguously, without Stride.
            // RGB24 rows must be 4-byte aligned or source padding leaks into pixels.
            // Drawing into a fresh bitmap gives different padding from decoded data.
            bool reuse = Active() && !setSize && sourceFormat == 137224 && targetFormat == 137224 &&
                width > 0 && height > 0 && (width & 3) == 0 &&
                sourceWidth == width && sourceHeight == height;
            if (reuse) System.Threading.Interlocked.Increment(ref _directCopies);
            return reuse;
        }
        private static void DisposeLocal(IDisposable item) { if (item != null) item.Dispose(); }

        private static bool Call(CodeInstruction c, string type, string method)
        {
            var m = c.operand as MethodInfo;
            return (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt) && m != null &&
                m.DeclaringType.FullName == type && m.Name == method;
        }

        private static CodeInstruction Store(CodeInstruction load)
        {
            if (load.opcode == OpCodes.Ldloc_0) return new CodeInstruction(OpCodes.Stloc_0);
            if (load.opcode == OpCodes.Ldloc_1) return new CodeInstruction(OpCodes.Stloc_1);
            if (load.opcode == OpCodes.Ldloc_2) return new CodeInstruction(OpCodes.Stloc_2);
            if (load.opcode == OpCodes.Ldloc_3) return new CodeInstruction(OpCodes.Stloc_3);
            if (load.opcode == OpCodes.Ldloc || load.opcode == OpCodes.Ldloc_S) return new CodeInstruction(OpCodes.Stloc, load.operand);
            throw new InvalidOperationException("GDI disposal local changed");
        }

        private static void InsertRelease(List<CodeInstruction> code, int at, CodeInstruction[] loads, ILGenerator generator)
        {
            Label skip = generator.DefineLabel();
            var begin = new CodeInstruction(OpCodes.Call, typeof(TextureScratchLifetime).GetMethod("Active", Static));
            begin.labels.AddRange(code[at].labels);
            code[at].labels.Clear();
            code[at].labels.Add(skip);
            var insert = new List<CodeInstruction> { begin, new CodeInstruction(OpCodes.Brfalse, skip) };
            foreach (var load in loads)
            {
                insert.Add(new CodeInstruction(load.opcode, load.operand));
                insert.Add(new CodeInstruction(OpCodes.Call, typeof(TextureScratchLifetime).GetMethod("DisposeLocal", Static)));
                insert.Add(new CodeInstruction(OpCodes.Ldnull));
                insert.Add(Store(load));
            }
            code.InsertRange(at, insert);
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            int end = code.Count - 9;
            string[] types = { "System.Drawing.Brush", "System.Drawing.Graphics", "System.Drawing.Image", "System.Drawing.Image" };
            if (end < 0 || code[code.Count - 1].opcode != OpCodes.Ret)
                throw new InvalidOperationException("GDI tail changed");
            var loads = new CodeInstruction[4];
            for (int i = 0; i < 4; i++)
            {
                if (!Call(code[end + i * 2 + 1], types[i], "Dispose") || code[end + i * 2 + 1].blocks.Count != 0)
                    throw new InvalidOperationException("GDI disposal tail changed");
                loads[i] = code[end + i * 2];
                Store(loads[i]); // Validate every local before editing instructions.
            }
            int locked = code.FindIndex(c => Call(c, "System.Drawing.Bitmap", "LockBits"));
            int unlocked = code.FindIndex(c => Call(c, "System.Drawing.Bitmap", "UnlockBits"));
            if (locked < 5 || unlocked <= locked ||
                code.FindAll(c => Call(c, "System.Drawing.Bitmap", "LockBits")).Count != 1 ||
                code.FindAll(c => Call(c, "System.Drawing.Bitmap", "UnlockBits")).Count != 1 ||
                !Call(code[locked - 1], "System.Drawing.Image", "get_PixelFormat") ||
                !Call(code[locked - 6], "System.Drawing.Graphics", "DrawImage") ||
                code[locked - 5].blocks.Count != 0 || code[unlocked + 1].blocks.Count != 0)
                throw new InvalidOperationException("GDI copy anchors changed");
            // All four variables are only disposed after UnlockBits. Check that
            // no later code reuses a GDI object before the native disposal tail.
            for (int i = unlocked + 1; i < end; i++)
                foreach (var load in loads)
                    if (code[i].opcode == load.opcode && Equals(code[i].operand, load.operand))
                        throw new InvalidOperationException("GDI local used after pixel copy");
            for (int i = 0; i < 4; i++)
            {
                code[end + i * 2 + 1].opcode = OpCodes.Call;
                code[end + i * 2 + 1].operand = typeof(TextureScratchLifetime).GetMethod("DisposeLocal", Static);
            }
            // Descending insertion keeps both native indexes valid. Source image
            // and drawing context end before allocating the managed raw buffer.
            InsertRelease(code, unlocked + 1, new[] { loads[3] }, generator);
            InsertRelease(code, locked - 5, new[] { loads[0], loads[1], loads[2] }, generator);
            InsertDirectCopy(code, loads, generator);
            return code;
        }

        private static void InsertDirectCopy(List<CodeInstruction> code, CodeInstruction[] loads, ILGenerator generator)
        {
            int ctor = code.FindIndex(c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo &&
                ((ConstructorInfo)c.operand).DeclaringType.FullName == "System.Drawing.Bitmap" &&
                ((ConstructorInfo)c.operand).GetParameters().Length == 3);
            int rect = code.FindIndex(c => c.operand is ConstructorInfo &&
                ((ConstructorInfo)c.operand).DeclaringType.FullName == "System.Drawing.Rectangle");
            int locked = code.FindIndex(c => Call(c, "System.Drawing.Bitmap", "LockBits"));
            if (ctor < 5 || rect < ctor || locked < rect || code[ctor - 4].opcode != OpCodes.Ldfld ||
                code[ctor - 2].opcode != OpCodes.Ldfld || code[rect - 7].opcode != OpCodes.Ldloca_S)
                throw new InvalidOperationException("direct RGB copy anchors changed");
            var width = (FieldInfo)code[ctor - 4].operand;
            var height = (FieldInfo)code[ctor - 2].operand;
            if (width.Name != "width" || height.Name != "height") throw new InvalidOperationException("image geometry changed");
            var imageType = ((MethodInfo)code[locked - 1].operand).DeclaringType;
            Label native = generator.DefineLabel(), ready = generator.DefineLabel();
            var insert = new List<CodeInstruction>();
            Action<CodeInstruction> copy = c => insert.Add(new CodeInstruction(c.opcode, c.operand));
            copy(loads[2]); insert.Add(new CodeInstruction(OpCodes.Callvirt, imageType.GetProperty("PixelFormat").GetGetMethod()));
            copy(code[ctor - 1]);
            foreach (string name in new[] { "Width", "Height" })
            { copy(loads[2]); insert.Add(new CodeInstruction(OpCodes.Callvirt, imageType.GetProperty(name).GetGetMethod())); }
            insert.Add(new CodeInstruction(OpCodes.Ldarg_0)); insert.Add(new CodeInstruction(OpCodes.Ldfld, width));
            insert.Add(new CodeInstruction(OpCodes.Ldarg_0)); insert.Add(new CodeInstruction(OpCodes.Ldfld, height));
            insert.Add(new CodeInstruction(OpCodes.Ldarg_0)); insert.Add(new CodeInstruction(OpCodes.Ldfld, width.DeclaringType.GetField("setSize")));
            insert.Add(new CodeInstruction(OpCodes.Call, typeof(TextureScratchLifetime).GetMethod("DirectCopy", Static)));
            insert.Add(new CodeInstruction(OpCodes.Brfalse, native));
            for (int i = rect - 7; i <= rect; i++)
            {
                if (code[i].blocks.Count != 0) throw new InvalidOperationException("rectangle exception boundary changed");
                copy(code[i]);
            }
            copy(loads[2]); insert.Add(Store(loads[3]));
            insert.Add(new CodeInstruction(OpCodes.Ldnull)); insert.Add(Store(loads[2]));
            insert.Add(new CodeInstruction(OpCodes.Br, ready));
            // The fast path transfers source ownership to destination. The existing
            // post-UnlockBits release frees it once; null-safe tail retains brush cleanup.
            code[locked - 5].labels.Add(ready);
            insert[0].labels.AddRange(code[ctor - 5].labels);
            code[ctor - 5].labels.Clear(); code[ctor - 5].labels.Add(native);
            code.InsertRange(ctor - 5, insert);
        }

        internal static void Shutdown()
        {
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[texture-scratch] " + message);
        }
    }
}
