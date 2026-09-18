using System;
using System.IO;
using System.Reflection;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    internal static class VarRegistration
    {
        private static readonly MethodInfo Register = typeof(FileManager).GetMethod("RegisterPackage", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Unregister = typeof(FileManager).GetMethod("UnregisterPackage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        internal static string RelativePath(string path)
        {
            string root = Path.GetFullPath(FileManager.PackageFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("VAR不在AddonPackages目录内：" + path);
            // Preserve VaM's logical junction path; do not resolve to another drive.
            string relativeRoot = FileManager.PackageFolder;
            if (Path.IsPathRooted(relativeRoot))
            {
                string cwd = Path.GetFullPath(".").TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (!root.StartsWith(cwd, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("AddonPackages不在VaM工作目录内");
                relativeRoot = root.Substring(cwd.Length).TrimEnd('\\', '/');
            }
            return relativeRoot.TrimEnd('\\', '/') + "\\" + full.Substring(root.Length).Replace('/', '\\');
        }
        internal static bool Incomplete(VarPackage package)
        {
            // Native RegisterPackage inserts Path then FullPath with Dictionary.Add.
            // An absolute Path duplicates that key before group/resource registration.
            return package != null && Path.IsPathRooted(package.Path);
        }
        internal static VarPackage RegisterFile(string path, out bool changed)
        {
            changed = false;
            string relative = RelativePath(path);
            VarPackage old = FileManager.GetPackage(Path.GetFileNameWithoutExtension(path));
            if (old != null)
            {
                bool same = string.Equals(Path.GetFullPath(old.FullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                if (!Incomplete(old) && (same || File.Exists(old.FullPath))) return old;
                if (Unregister == null) throw new MissingMethodException("FileManager.UnregisterPackage");
                // Removes the incomplete UID/aliases and closes its retained ZIP read handle.
                Unregister.Invoke(null, new object[] { old });
            }
            if (Register == null) throw new MissingMethodException("FileManager.RegisterPackage");
            VarPackage result;
            try { result = Register.Invoke(null, new object[] { relative }) as VarPackage; }
            catch
            {
                VarPackage partial = FileManager.GetPackage(Path.GetFileNameWithoutExtension(path));
                if (partial != null && string.Equals(Path.GetFullPath(partial.FullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && Unregister != null)
                    Unregister.Invoke(null, new object[] { partial });
                throw;
            }
            if (result == null) throw new InvalidOperationException("包注册返回空值：" + Path.GetFileName(path));
            if (Incomplete(result)) throw new InvalidOperationException("包注册路径仍为绝对路径");
            changed = true;
            return result;
        }
    }
}

