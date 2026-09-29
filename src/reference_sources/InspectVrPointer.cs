using System;
using System.IO;
using System.Reflection;

internal static class InspectVrPointer
{
    private const string Root = @"F:\vam1.22.0.12";

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        string[] directories = {
            Path.Combine(Root, "VaM_Data", "Managed"),
            Path.Combine(Root, "BepInEx", "core")
        };
        foreach (string directory in directories)
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Assembly assembly = Assembly.LoadFrom(@"F:\vam1.22.0.12\VaM_Data\Managed\Assembly-CSharp.dll");
        foreach (Type type in assembly.GetTypes())
        {
            string name = type.FullName.ToLowerInvariant();
            if (!name.Contains("pointer") && !name.Contains("laser") && !name.Contains("eventsystem") && !name.Contains("inputmodule"))
                continue;
            Console.WriteLine("=== " + type.FullName + " ===");
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine("FIELD " + field.FieldType + " " + field.Name);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine("METHOD " + method);
        }
        return 0;
    }
}
