using System;
using Mono.Cecil;

internal static class DumpVarRefreshIL
{
    private static void Dump(TypeDefinition type, string methodName)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name != methodName)
                continue;
            Console.WriteLine("=== " + type.FullName + "." + method.Name + " " + method.Attributes + " ===");
            if (!method.HasBody)
                continue;
            foreach (var instruction in method.Body.Instructions)
                Console.WriteLine(instruction);
        }
    }

    private static int Main()
    {
        AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            @"F:\vam1.22.0.12\VaM_Data\Managed\Assembly-CSharp.dll");
        Dump(assembly.MainModule.GetType("MVR.FileManagement.FileManager"), "Refresh");
        Dump(assembly.MainModule.GetType("MVR.FileManagement.FileManager"), "RegisterPackage");
        Dump(assembly.MainModule.GetType("SuperController"), "RescanPackages");
        Dump(assembly.MainModule.GetType("SuperController"), "OnPackageRefresh");
        return 0;
    }
}
