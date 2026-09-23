using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Quest3TriggerUI
{
    // Entry-name source for the incremental rescan content classifier.
    //
    // The BA VFS lister (JayJayWon.Utils.GetFilesAtPathRecursive, called as
    // "<pkg>.var:") returned a non-null but EMPTY set for a package that
    // demonstrably contains Custom/Clothing, Custom/Hair and
    // Custom/Atom/Person/Morphs entries, and a silent empty set used to read
    // as "harmless" - the unsafe direction, because the scene-side clothing /
    // hair / morph lists were then not rebuilt. This probe therefore reads the
    // package archive itself: central directory only, no decompression, no
    // extra dependency. The BA lister stays as a fallback and both sources
    // being empty is reported as unavailable so the caller stays conservative.
    internal static class BaDirtyContentProbe
    {
        // The one-off category-memory warm-up walks every installed package;
        // its per-package detail lines would flood the log, so the caller
        // silences them while it runs.
        internal static bool Quiet;

        private const int SigCentralDir = 0x02014b50;
        private const int SigEocd = 0x06054b50;
        private const int SigZip64Locator = 0x07064b50;
        private const int SigZip64Eocd = 0x06064b50;
        private const long MaxCentralDirBytes = 64L * 1024L * 1024L;

        internal static List<string> PackageEntries(string absPath)
        {
            try
            {
                if (string.IsNullOrEmpty(absPath)) return null;
                string target = absPath;
                if (!File.Exists(target) && !Directory.Exists(target) &&
                    File.Exists(target + ".disabled"))
                    target = target + ".disabled";
                if (Directory.Exists(target))
                {
                    List<string> folder = FolderEntries(target);
                    if (folder != null && folder.Count > 0)
                    {
                        LogDetail(target, "解包目录 " + folder.Count + " 个文件");
                        return folder;
                    }
                    LogDetail(target, "解包目录无法枚举，按保守处理");
                    return null;
                }
                if (!File.Exists(target)) return null;
                List<string> names = ZipEntryNames(target);
                if (names != null && names.Count > 0)
                {
                    LogDetail(target, "zip 清单 " + names.Count + " 项");
                    return names;
                }
                List<string> viaBa =
                    BrowserAssistScanAccelerator.ListPackageFiles(target);
                if (viaBa != null && viaBa.Count > 0)
                {
                    LogDetail(target, "zip 不可读，改用 BA 清单 " +
                        viaBa.Count + " 项");
                    return viaBa;
                }
                LogDetail(target, "zip 与 BA 清单均无内容，按保守处理");
                return null;
            }
            catch
            {
                return null;
            }
        }

        // Central directory walk; returns null when the archive cannot be
        // parsed (then the caller falls back to the BA lister).
        private static List<string> ZipEntryNames(string file)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                {
                // Length must come from the opened stream: every package is
                // reached through a BA symlink under ___VarsLink___, and
                // FileInfo.Length reports the link itself (0) instead of the
                // 601 MB target, which used to abort the parse immediately.
                long length = fs.Length;
                if (length < 22) return null;
                int tailLen = (int)Math.Min(length, 66560L);
                byte[] tail = new byte[tailLen];
                if (!ReadAt(fs, length - tailLen, tail, tailLen)) return null;
                int eocd = -1;
                for (int i = tailLen - 22; i >= 0; i--)
                    if (BitConverter.ToInt32(tail, i) == SigEocd)
                    { eocd = i; break; }
                if (eocd < 0) return null;
                long count = BitConverter.ToUInt16(tail, eocd + 10);
                long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
                long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
                if (count == 0xFFFF || cdSize == 0xFFFFFFFFL ||
                    cdOffset == 0xFFFFFFFFL)
                {
                    int loc = eocd - 20;
                    byte[] z = new byte[56];
                    if (loc >= 0 && BitConverter.ToInt32(tail, loc) ==
                        SigZip64Locator &&
                        ReadAt(fs, BitConverter.ToInt64(tail, loc + 8), z, 56) &&
                        BitConverter.ToInt32(z, 0) == SigZip64Eocd)
                    {
                        count = BitConverter.ToInt64(z, 32);
                        cdSize = BitConverter.ToInt64(z, 40);
                        cdOffset = BitConverter.ToInt64(z, 48);
                    }
                }
                if (cdSize <= 0 || cdSize > MaxCentralDirBytes) return null;
                if (cdOffset < 0 || cdOffset + cdSize > length) return null;
                byte[] cd = new byte[cdSize];
                if (!ReadAt(fs, cdOffset, cd, (int)cdSize)) return null;
                List<string> list = new List<string>();
                int p = 0;
                for (long i = 0; i < count; i++)
                {
                    if (p + 46 > cd.Length) break;
                    if (BitConverter.ToInt32(cd, p) != SigCentralDir) break;
                    int nameLen = BitConverter.ToUInt16(cd, p + 28);
                    int extraLen = BitConverter.ToUInt16(cd, p + 30);
                    int cmtLen = BitConverter.ToUInt16(cd, p + 32);
                    int nameStart = p + 46;
                    if (nameLen < 0 || nameStart + nameLen > cd.Length) break;
                    list.Add(Encoding.UTF8.GetString(cd, nameStart, nameLen));
                    p = nameStart + nameLen + extraLen + cmtLen;
                }
                return list;
                }
            }
            catch { return null; }
        }

        private static bool ReadAt(Stream s, long offset, byte[] buf, int n)
        {
            s.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < n)
            {
                int k = s.Read(buf, read, n - read);
                if (k <= 0) return false;
                read += k;
            }
            return true;
        }

        private static List<string> FolderEntries(string dir)
        {
            try
            {
                return new List<string>(Directory.GetFiles(dir, "*.*",
                    SearchOption.AllDirectories));
            }
            catch { return null; }
        }

        private static void LogDetail(string path, string what)
        {
            if (Quiet) return;
            try
            {
                string name = Path.GetFileName(path);
                BrowserAssistScanAccelerator.Log(
                    "差异判定清单：" + name + " — " + what);
            }
            catch { }
        }
    }
}
