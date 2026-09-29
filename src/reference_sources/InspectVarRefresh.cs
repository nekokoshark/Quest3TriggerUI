using System;
using System.Reflection;

internal static class InspectVarRefresh
{
    private static void PrintType(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Console.WriteLine("TYPE " + type.FullName);
        foreach (FieldInfo field in type.GetFields(flags))
        {
            string name = field.Name.ToLowerInvariant();
            if (name.Contains("package") || name.Contains("refresh") || name.Contains("scan") || name.Contains("cache"))
                Console.WriteLine("FIELD " + field.FieldType + " " + field.Name);
        }
        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            string name = property.Name.ToLowerInvariant();
            if (name.Contains("package") || name.Contains("refresh") || name.Contains("scan") || name.Contains("cache"))
                Console.WriteLine("PROP " + property.PropertyType + " " + property.Name);
        }
        foreach (MethodInfo method in type.GetMethods(flags))
        {
            string name = method.Name.ToLowerInvariant();
            if (name.Contains("package") || name.Contains("refresh") || name.Contains("scan") || name.Contains("cache"))
                Console.WriteLine("METHOD " + method);
        }
    }

    private static int Main()
    {
        Assembly assembly = Assembly.LoadFrom(@"F:\vam1.22.0.12\VaM_Data\Managed\Assembly-CSharp.dll");
        PrintType(assembly.GetType("MVR.FileManagement.FileManager", true));
        PrintType(assembly.GetType("SuperController", true));
        return 0;
    }
}
