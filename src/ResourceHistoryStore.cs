using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Quest3TriggerUI
{
    // Disk-only metadata. No Unity object, instance ID or runtime reference count
    // is persisted. All methods run on the history worker, never Unity's thread.
    internal sealed class ResourceHistoryStore
    {
        internal sealed class Record
        {
            internal string Kind, Source, Physical, Package, Status;
            internal long Length = -1, Modified, Seen;
            internal string[] Dependencies = new string[0], Assets = new string[0];
            internal DateTime MissingSince;
        }
        internal readonly Dictionary<string, Record> Records = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        private readonly string _directory;
        private readonly List<string> _keys = new List<string>();
        private int _cursor;
        internal int Errors, Deleted, Hits, Invalidated;
        internal ResourceHistoryStore(string directory) { _directory = directory; }
        internal static string Key(string kind, string source) { return kind + "|" + source.Replace('\\', '/'); }
        private string FileFor(string key)
        {
            using (var hash = SHA256.Create())
                return Path.Combine(_directory, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))).Replace("-", "") + ".ledger");
        }
        // Backslash escape keeps metadata textual and round-trips Unicode/tab/newline.
        private static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }
        private static string Unescape(string s)
        {
            var b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { b.Append(s[i]); continue; }
                if (++i == s.Length) throw new FormatException("trailing escape");
                char c = s[i];
                if (c == 't') b.Append('\t'); else if (c == 'r') b.Append('\r');
                else if (c == 'n') b.Append('\n'); else if (c == '\\') b.Append('\\');
                else throw new FormatException("unknown escape");
            }
            return b.ToString();
        }
        private static string Row(string[] values)
        { var a = new string[values.Length]; for (int i = 0; i < a.Length; i++) a[i] = Escape(values[i]); return string.Join("\t", a); }
        private static string[] Columns(string line)
        { if (line == null) throw new EndOfStreamException(); var a = line.Split('\t'); for (int i = 0; i < a.Length; i++) a[i] = Unescape(a[i]); return a; }
        internal void Load()
        {
            Directory.CreateDirectory(_directory);
            foreach (string file in Directory.GetFiles(_directory, "*.ledger"))
            {
                try
                {
                    Record r = Read(file, false); string key = Key(r.Kind, r.Source);
                    if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(FileFor(key)), StringComparison.OrdinalIgnoreCase)) throw new FormatException("record identity");
                    // Even a previous process's deletion suspicion isn't reusable.
                    Records[key] = r;
                }
                catch (Exception e) { if (!(e is IOException || e is UnauthorizedAccessException || e is FormatException || e is OverflowException)) throw; Errors++; }
            }
            _keys.Clear(); _keys.AddRange(Records.Keys);
        }
        private Record Read(string file, bool detail)
        {
            using (var reader = new StreamReader(file, Encoding.UTF8, true))
            {
                if (reader.ReadLine() != "Q3_RESOURCE_HISTORY_V1") throw new FormatException("history version");
                string[] h = Columns(reader.ReadLine()); if (h.Length != 9) throw new FormatException("history header");
                var r = new Record { Kind = h[0], Source = h[1], Physical = h[2], Package = h[3], Status = h[4],
                    Length = long.Parse(h[5], CultureInfo.InvariantCulture), Modified = long.Parse(h[6], CultureInfo.InvariantCulture), Seen = long.Parse(h[7], CultureInfo.InvariantCulture) };
                if (h[8] != "partial-observation" || (r.Kind != "appearance" && r.Kind != "clothing-preset" && r.Kind != "clothing")) throw new FormatException("history scope");
                string[] deps = Columns(reader.ReadLine()); if (deps[0] != "dependencies") throw new FormatException("dependency row");
                r.Dependencies = new string[deps.Length - 1]; Array.Copy(deps, 1, r.Dependencies, 0, r.Dependencies.Length);
                if (detail)
                {
                    var assets = new List<string>(); string line;
                    while ((line = reader.ReadLine()) != null) assets.Add(Unescape(line));
                    r.Assets = assets.ToArray();
                }
                return r;
            }
        }
        internal Record Detail(string key) { return Read(FileFor(key), true); }
        private void Save(Record r)
        {
            string path = FileFor(Key(r.Kind, r.Source)), temp = path + ".tmp";
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("Q3_RESOURCE_HISTORY_V1");
                writer.WriteLine(Row(new[] {r.Kind, r.Source, r.Physical, r.Package, r.Status,
                    r.Length.ToString(CultureInfo.InvariantCulture), r.Modified.ToString(CultureInfo.InvariantCulture), r.Seen.ToString(CultureInfo.InvariantCulture), "partial-observation"}));
                var deps = new string[r.Dependencies.Length + 1]; deps[0] = "dependencies"; Array.Copy(r.Dependencies, 0, deps, 1, r.Dependencies.Length);
                writer.WriteLine(Row(deps)); foreach (string a in r.Assets) writer.WriteLine(Escape(a));
            }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        // present / confirmed-missing-file / unknown. A missing/inaccessible parent
        // (offline drive, unavailable mapped folder) is UNKNOWN, never deletion.
        internal static int Stat(string path, out long length, out long modified)
        {
            length = -1; modified = 0;
            if (string.IsNullOrEmpty(path)) return 0;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) return 0;
                var info = new FileInfo(path); length = info.Length; modified = info.LastWriteTimeUtc.Ticks; return 1;
            }
            catch (FileNotFoundException)
            {
                try { File.GetAttributes(Path.GetDirectoryName(path)); return -1; }
                catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; }
            }
            catch (DirectoryNotFoundException) { return 0; }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }
        internal void Observe(Record r)
        {
            if (string.IsNullOrEmpty(r.Source)) return;
            string key = Key(r.Kind, r.Source); Record old;
            bool known = Records.TryGetValue(key, out old);
            if (string.IsNullOrEmpty(r.Physical) && known) r.Physical = old.Physical;
            long length, ticks; int state = Stat(r.Physical, out length, out ticks);
            if (state == -1) return; // A deleted source is not resurrected by a still-live instance.
            if (known && state == 1 && old.Length == length && old.Modified == ticks && old.Status == "observed") Hits++;
            r.Length = length; r.Modified = ticks; r.Seen = DateTime.UtcNow.Ticks;
            if (state != 1) r.Status = r.Source.StartsWith("builtin:", StringComparison.Ordinal) ? "builtin-observed" : "unavailable";
            Save(r);
            // Retain metadata only, not all historical per-resource descriptions.
            r.Assets = new string[0]; Records[key] = r;
            if (!known) _keys.Add(key);
        }
        internal void Audit(int maxRecords, DateTime now)
        {
            maxRecords = Math.Min(maxRecords, _keys.Count);
            for (int n = 0; n < maxRecords && _keys.Count > 0; n++)
            {
                if (_cursor >= _keys.Count) _cursor = 0;
                string key = _keys[_cursor++]; Record r;
                if (!Records.TryGetValue(key, out r)) continue;
                try
                {
                    long length, modified; int state = Stat(r.Physical, out length, out modified);
                    if (state == -1 && r.Length >= 0)
                    {
                        if (r.MissingSince == default(DateTime)) r.MissingSince = now;
                        else if ((now - r.MissingSince).TotalSeconds >= 60) Remove(key);
                    }
                    else
                    {
                        r.MissingSince = default(DateTime);
                        string status = state == 0 ? (r.Source.StartsWith("builtin:", StringComparison.Ordinal) ? "builtin-observed" : "unavailable") : (length != r.Length || modified != r.Modified ? "changed" : r.Status);
                        if (status != r.Status)
                        {
                            Record detail = Detail(key); detail.Status = status; Save(detail); r.Status = status; Invalidated++;
                        }
                    }
                }
                catch (IOException) { Errors++; } catch (UnauthorizedAccessException) { Errors++; }
                catch (FormatException) { Errors++; } catch (OverflowException) { Errors++; }
            }
        }
        private void Remove(string key)
        {
            // Deletes our own hashed manifest, never a preset, VAR or game asset.
            File.Delete(FileFor(key)); Records.Remove(key); _keys.Remove(key); _cursor = 0; Deleted++;
            foreach (var other in Records)
            {
                if (!Array.Exists(other.Value.Dependencies, delegate(string dependency) { return string.Equals(dependency, key, StringComparison.OrdinalIgnoreCase); })) continue;
                Record detail = Detail(other.Key);
                var dependencies = new List<string>(detail.Dependencies);
                dependencies.RemoveAll(delegate(string dependency) { return string.Equals(dependency, key, StringComparison.OrdinalIgnoreCase); });
                detail.Dependencies = dependencies.ToArray(); detail.Status = "dependency-missing";
                // Old descriptions are not a valid current dependency observation.
                detail.Assets = new string[0]; Save(detail);
                other.Value.Dependencies = detail.Dependencies; other.Value.Status = detail.Status;
            }
        }
    }
}
