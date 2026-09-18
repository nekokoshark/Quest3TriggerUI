using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WindowsKeyboardInteropTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static string _root;

    [STAThread]
    public static int Main(string[] args)
    {
        _root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        string pluginPath = Path.Combine(
            _root, "BepInEx", "plugins", "Quest3TriggerUI", "MODIFIED_FILE.dll");
        Assembly assembly = Assembly.LoadFrom(pluginPath);
        Type keyboard = assembly.GetType("Quest3TriggerUI.WindowsKeyboard", true);
        MethodInfo send = keyboard.GetMethod(
            "SendToWindow", BindingFlags.Static | BindingFlags.NonPublic);
        const ushort f24 = 0x87;

        IntPtr foregroundBefore = GetForegroundWindow();
        TestWindow target = new TestWindow();
        bool downPosted = (bool)send.Invoke(
            null, new object[] { target.Handle, f24, false, false });
        Application.DoEvents();
        bool targetDown = target.DownCount == 1;
        bool globalDown = (GetAsyncKeyState(f24) & 0x8000) != 0;

        bool upPosted = (bool)send.Invoke(
            null, new object[] { target.Handle, f24, true, false });
        Application.DoEvents();
        bool targetUp = target.UpCount == 1;
        bool globalUp = (GetAsyncKeyState(f24) & 0x8000) == 0;
        bool foregroundUnchanged = foregroundBefore == GetForegroundWindow();
        target.Dispose();

        bool pass = downPosted && upPosted && targetDown && targetUp &&
                    !globalDown && globalUp && foregroundUnchanged;
        Console.WriteLine(
            "TARGETED KEYBOARD RESULT=" + (pass ? "PASS" : "FAIL") +
            "; Key=F24; DownPosted=" + downPosted +
            "; TargetDown=" + targetDown +
            "; UpPosted=" + upPosted +
            "; TargetUp=" + targetUp +
            "; ForegroundUnchanged=" + foregroundUnchanged +
            "; GlobalKeyStateUntouched=" + (!globalDown && globalUp));
        return pass ? 0 : 1;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string[] directories = new string[] {
            Path.Combine(_root, "BepInEx", "core"),
            Path.Combine(_root, "VaM_Data", "Managed")
        };

        for (int i = 0; i < directories.Length; i++)
        {
            string candidate = Path.Combine(directories[i], name);
            if (File.Exists(candidate))
                return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    private sealed class TestWindow : NativeWindow, IDisposable
    {
        internal int DownCount;
        internal int UpCount;

        internal TestWindow()
        {
            CreateParams parameters = new CreateParams();
            parameters.Caption = "Quest3 targeted keyboard test";
            CreateHandle(parameters);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0100)
                DownCount++;
            if (message.Msg == 0x0101)
                UpCount++;
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
