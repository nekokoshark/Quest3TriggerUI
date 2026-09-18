using System;
using System.IO;
using System.Reflection;

internal static class AssemblyLoadTests
{
    private static string _root;

    public static int Main(string[] args)
    {
        _root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        string path = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(_root, "BepInEx", "plugins", "Quest3TriggerUI", "MODIFIED_FILE.dll");
        try
        {
            Assembly assembly = Assembly.LoadFrom(path);
            AssemblyName[] references = assembly.GetReferencedAssemblies();
            for (int i = 0; i < references.Length; i++)
                Console.WriteLine("REF=" + references[i].Name + ", Version=" + references[i].Version);
            Type[] types = assembly.GetTypes();
            Console.WriteLine("ASSEMBLY TYPES RESULT=PASS; TypeCount=" + types.Length);
            return 0;
        }
        catch (ReflectionTypeLoadException exception)
        {
            Console.WriteLine("ASSEMBLY TYPES RESULT=FAIL");
            for (int i = 0; i < exception.LoaderExceptions.Length; i++)
                Console.WriteLine(exception.LoaderExceptions[i]);
            return 1;
        }
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
}
