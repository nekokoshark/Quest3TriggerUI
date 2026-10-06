using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace Quest3TriggerUI
{
    internal static class JsonStreamParser
    {
        private static readonly MethodInfo JsonParse = typeof(JSON).GetMethod("Parse");
        private static readonly MethodInfo NodeParse = typeof(JSONNode).GetMethod("Parse");
        private static readonly MethodInfo EntryRead = typeof(FileEntryStreamReader).GetMethod("ReadToEnd");
        private static readonly MethodInfo ReaderRead = typeof(StreamReader).GetMethod("ReadToEnd");
        private static readonly MethodInfo TextRead = typeof(TextReader).GetMethod("ReadToEnd");

        internal static bool CanStream
        {
            get
            {
                foreach (var method in new[] { JsonParse, NodeParse, EntryRead, ReaderRead, TextRead })
                {
                    var patches = Harmony.GetPatchInfo(method);
                    if (patches != null && patches.Owners.Count > 0) return false;
                }
                return true;
            }
        }

        internal static bool CanStreamEntry(FileEntryStreamReader reader)
        {
            return reader != null && reader.GetType().GetMethod("ReadToEnd", Type.EmptyTypes).DeclaringType == typeof(FileEntryStreamReader)
                && (reader.StreamReader == null || reader.StreamReader.GetType() == typeof(StreamReader)) && CanStream;
        }

        internal static JSONNode ParseEntry(FileEntryStreamReader reader)
        {
            if (!CanStreamEntry(reader)) return JSON.Parse(reader.ReadToEnd());
            if (reader.StreamReader == null) return JSON.Parse(null);
            return Parse(reader.StreamReader);
        }

        internal static JSONNode Parse(TextReader reader)
        {
            if (!CanStream) return JSON.Parse(reader.ReadToEnd());
            var input = new BufferedChars(reader);
            try { return ParseCore(input); }
            catch
            {
                // ReadToEnd completes IO before parsing. After a syntax failure,
                // a later input/decoder failure must still win. Never retry IO.
                if (!input.Failed) input.Drain();
                throw;
            }
        }

        private static JSONNode ParseCore(BufferedChars input)
        {
            Stack<JSONNode> stack = new Stack<JSONNode>();
            JSONNode jSONNode = null;
            int next;
            bool flag = false;
            StringBuilder stringBuilder = new StringBuilder();
            StringBuilder stringBuilder2 = new StringBuilder();
            bool flag2 = false;
            while ((next = input.Next()) >= 0)
            {
                switch ((char)next)
                {
                case '{':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                        break;
                    }
                    stack.Push(new JSONClass());
                    if (jSONNode != null)
                    {
                        if (jSONNode is JSONArray)
                        {
                            jSONNode.Add(stack.Peek());
                        }
                        else if (stringBuilder2.Length > 0)
                        {
                            string text = stringBuilder2.ToString().Trim();
                            if (text.Length > 0)
                            {
                                jSONNode.Add(text, stack.Peek());
                            }
                        }
                    }
                    stringBuilder2.Length = 0;
                    stringBuilder.Length = 0;
                    flag = false;
                    jSONNode = stack.Peek();
                    break;
                case '[':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                        break;
                    }
                    stack.Push(new JSONArray());
                    if (jSONNode != null)
                    {
                        if (jSONNode is JSONArray)
                        {
                            jSONNode.Add(stack.Peek());
                        }
                        else if (stringBuilder2.Length > 0)
                        {
                            string text2 = stringBuilder2.ToString().Trim();
                            if (text2.Length > 0)
                            {
                                jSONNode.Add(text2, stack.Peek());
                            }
                        }
                    }
                    stringBuilder2.Length = 0;
                    stringBuilder.Length = 0;
                    flag = false;
                    jSONNode = stack.Peek();
                    break;
                case ']':
                case '}':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                        break;
                    }
                    if (stack.Count == 0)
                    {
                        throw new Exception("JSON Parse: Too many closing brackets");
                    }
                    stack.Pop();
                    if (flag)
                    {
                        if (jSONNode is JSONArray)
                        {
                            jSONNode.Add(stringBuilder.ToString());
                        }
                        else if (stringBuilder2.Length > 0)
                        {
                            string text3 = stringBuilder2.ToString().Trim();
                            if (text3.Length > 0)
                            {
                                jSONNode.Add(text3, stringBuilder.ToString());
                            }
                        }
                    }
                    stringBuilder2.Length = 0;
                    stringBuilder.Length = 0;
                    flag = false;
                    if (stack.Count > 0)
                    {
                        jSONNode = stack.Peek();
                    }
                    break;
                case ':':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                        break;
                    }
                    stringBuilder2.Length = 0;
                    stringBuilder2.Append(stringBuilder);
                    stringBuilder.Length = 0;
                    flag = false;
                    break;
                case '"':
                    flag2 = (byte)((flag2 ? 1 : 0) ^ 1) != 0;
                    flag = true;
                    break;
                case ',':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                        break;
                    }
                    if (flag)
                    {
                        if (jSONNode is JSONArray)
                        {
                            jSONNode.Add(stringBuilder.ToString());
                        }
                        else if (stringBuilder2.Length > 0)
                        {
                            jSONNode.Add(stringBuilder2.ToString(), stringBuilder.ToString());
                        }
                    }
                    stringBuilder2.Length = 0;
                    stringBuilder.Length = 0;
                    flag = false;
                    break;
                case '\t':
                case ' ':
                    if (flag2)
                    {
                        stringBuilder.Append((char)next);
                    }
                    break;
                case '\\':
                    next = input.Next();
                    if (flag2)
                    {
                        if (next < 0) throw new IndexOutOfRangeException();
                        char c = (char)next;
                        switch (c)
                        {
                        case 't':
                            stringBuilder.Append('\t');
                            break;
                        case 'r':
                            stringBuilder.Append('\r');
                            break;
                        case 'n':
                            stringBuilder.Append('\n');
                            break;
                        case 'b':
                            stringBuilder.Append('\b');
                            break;
                        case 'f':
                            stringBuilder.Append('\f');
                            break;
                        case 'u':
                        {
                            string s = input.UnicodeDigits();
                            stringBuilder.Append((char)int.Parse(s, NumberStyles.AllowHexSpecifier));
                            break;
                        }
                        default:
                            stringBuilder.Append(c);
                            break;
                        }
                    }
                    break;
                default:
                    stringBuilder.Append((char)next);
                    flag = true;
                    break;
                case '\n':
                case '\r':
                    break;
                }
            }
            if (flag2)
            {
                throw new Exception("JSON Parse: Quotation marks seems to be messed up.");
            }
            return jSONNode;
        }
    }
}
