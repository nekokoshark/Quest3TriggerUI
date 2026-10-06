using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Quest3TriggerUI
{
    internal static class PathReplacementStream
    {
        internal static bool Supported(Dictionary<string, string> rules)
        {
            foreach (var rule in rules)
                // Quote-containing keys cross our lexical span boundary. Regex
                // replacement '$' operators can reference the full input. Keep
                // the exact original regex implementation for those rules.
                if (rule.Key.IndexOf('"') >= 0 || rule.Value.IndexOf('$') >= 0) return false;
            return true;
        }

        internal static string ApplyOriginal(string text, Dictionary<string, string> rules)
        {
            foreach (var rule in rules)
                if (text.Contains(rule.Key))
                    text = new Regex("\"[^\"]*" + Regex.Escape(rule.Key) + "[^\"]*\"").Replace(text, "\"" + rule.Value + "\"");
            return text;
        }

        internal static Utf16Spool Apply(Utf16Spool input, Dictionary<string, string> rules)
        {
            Utf16Spool current = input;
            try
            {
                int total = 0;
                foreach (var rule in rules)
                {
                    SuperController.LogMessage("[路径替换] 正在检查关键字: \"" + rule.Key + "\"");
                    bool contains;
                    int count = Pass(current.Rewind(), null, rule.Key, rule.Value, out contains);
                    if (!contains)
                    {
                        SuperController.LogMessage("[路径替换] 关键字 '" + rule.Key + "' 在JSON中不存在，跳过");
                        continue;
                    }
                    SuperController.LogMessage("[路径替换] 正则匹配结果数量: " + count);
                    if (count == 0) continue;
                    var next = new Utf16Spool();
                    try
                    {
                        Pass(current.Rewind(), next, rule.Key, rule.Value, out contains);
                        if (current != input) current.Dispose();
                    }
                    catch { next.Dispose(); throw; }
                    current = next;
                    total += count;
                    SuperController.LogMessage("[路径替换] 替换为: \"" + rule.Value + "\" (共 " + count + " 处)");
                }
                SuperController.LogMessage("[路径替换] >>> 场景加载时共进行了 " + total + " 处插件路径替换 <<<");
                return current;
            }
            catch
            {
                if (current != input) current.Dispose();
                throw;
            }
        }

        // A regex match consumes both raw quotes. An unmatched closing quote
        // stays eligible as the next opening quote, including escaped quotes
        // and text outside JSON strings. This is NOT JSON-token replacement.
        internal static int Pass(TextReader reader, Utf16Spool output, string key, string value, out bool contains)
        {
            var source = new BufferedChars(reader);
            var span = new StringBuilder();
            bool quote = false;
            contains = false;
            int matches = 0, next;
            while ((next = source.Next()) >= 0)
            {
                if (next != '"') { span.Append((char)next); continue; }
                string text = span.ToString();
                span.Length = 0;
                bool hit = text.Contains(key);
                contains |= hit;
                if (quote && hit)
                {
                    matches++;
                    if (output != null)
                    {
                        SuperController.LogMessage("[路径替换] 匹配到原始路径: \"" + text + "\"");
                        output.Write("\"" + value + "\"");
                    }
                    quote = false;
                }
                else
                {
                    if (output != null) output.Write((quote ? "\"" : "") + text);
                    quote = true;
                }
            }
            string tail = span.ToString();
            contains |= tail.Contains(key);
            if (output != null) output.Write((quote ? "\"" : "") + tail);
            return matches;
        }
    }
}
