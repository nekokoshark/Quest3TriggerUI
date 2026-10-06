using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace Quest3TriggerUI
{
    // Token numbers vary with the cslist and its namespace prefix. Resolve them
    // to identities, but retain opcodes, branches, locals and lambda bodies.
    internal static class MorphFilterMethodAudit
    {
        private static readonly Dictionary<short, OpCode> Codes = BuildCodes();

        private static Dictionary<short, OpCode> BuildCodes()
        {
            var result = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode))
                {
                    var code = (OpCode)field.GetValue(null);
                    result.Add(code.Value, code);
                }
            return result;
        }

        internal static string Fingerprint(MethodInfo method)
        {
            var text = new StringBuilder();
            AppendBody(text, method, method.DeclaringType, new HashSet<MethodBase>());
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }

        private static string TypeKey(Type type, Type owner)
        {
            if (type == owner) return "SELF";
            if (type.IsByRef) return TypeKey(type.GetElementType(), owner) + "&";
            if (type.IsArray) return TypeKey(type.GetElementType(), owner) + "[" + type.GetArrayRank() + "]";
            if (type.IsGenericParameter) return "!" + type.GenericParameterPosition;
            if (type.IsGenericType)
            {
                var text = new StringBuilder(type.GetGenericTypeDefinition().FullName);
                text.Append('<');
                foreach (Type argument in type.GetGenericArguments()) text.Append(TypeKey(argument, owner)).Append(';');
                return text.Append('>').ToString();
            }
            return type.Assembly.GetName().Name + ":" + type.FullName;
        }

        private static string MemberKey(MemberInfo member, Type owner)
        {
            var text = new StringBuilder(TypeKey(member.DeclaringType, owner)).Append("::").Append(member.Name);
            var field = member as FieldInfo;
            if (field != null) return text.Append(':').Append(TypeKey(field.FieldType, owner)).Append(':').Append(field.IsStatic).ToString();
            var method = member as MethodBase;
            if (method == null) throw new InvalidDataException("Unexpected morph filter IL member");
            text.Append(':').Append(method.IsStatic).Append('(');
            foreach (ParameterInfo parameter in method.GetParameters()) text.Append(TypeKey(parameter.ParameterType, owner)).Append(';');
            text.Append(')');
            var info = member as MethodInfo;
            if (info != null)
            {
                text.Append(':').Append(TypeKey(info.ReturnType, owner));
                if (info.IsGenericMethod)
                    foreach (Type argument in info.GetGenericArguments()) text.Append('<').Append(TypeKey(argument, owner)).Append('>');
            }
            return text.ToString();
        }

        private static void AppendBody(StringBuilder text, MethodBase method, Type owner, HashSet<MethodBase> seen)
        {
            if (!seen.Add(method)) return;
            MethodBody body = method.GetMethodBody();
            if (body == null) throw new InvalidDataException("Missing filter body");
            text.Append(MemberKey(method, owner)).Append('|').Append(body.InitLocals).Append('|');
            foreach (LocalVariableInfo local in body.LocalVariables)
                text.Append(TypeKey(local.LocalType, owner)).Append(':').Append(local.IsPinned).Append(';');
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                text.Append("EH:").Append(clause.Flags).Append(':').Append(clause.TryOffset).Append(':').Append(clause.TryLength)
                    .Append(':').Append(clause.HandlerOffset).Append(':').Append(clause.HandlerLength);
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) text.Append(':').Append(TypeKey(clause.CatchType, owner));
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter) text.Append(':').Append(clause.FilterOffset);
                text.Append(';');
            }
            byte[] il = body.GetILAsByteArray();
            var children = new List<MethodBase>();
            using (var reader = new BinaryReader(new MemoryStream(il, false)))
                while (reader.BaseStream.Position < il.Length)
                {
                    byte first = reader.ReadByte();
                    short value = first == 0xfe ? (short)(0xfe00 | reader.ReadByte()) : first;
                    OpCode code = Codes[value];
                    text.Append('\n').Append(code.Name).Append(' ');
                    switch (code.OperandType)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar: text.Append(reader.ReadByte()); break;
                        case OperandType.InlineVar: text.Append(reader.ReadUInt16()); break;
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineI: text.Append(reader.ReadInt32()); break;
                        case OperandType.InlineI8: text.Append(reader.ReadInt64()); break;
                        case OperandType.ShortInlineR: text.Append(BitConverter.ToString(reader.ReadBytes(4))); break;
                        case OperandType.InlineR: text.Append(BitConverter.ToString(reader.ReadBytes(8))); break;
                        case OperandType.InlineSwitch:
                            int count = reader.ReadInt32();
                            for (int i = 0; i < count; i++) text.Append(reader.ReadInt32()).Append(';');
                            break;
                        case OperandType.InlineType:
                            text.Append(TypeKey(method.Module.ResolveType(reader.ReadInt32()), owner));
                            break;
                        case OperandType.InlineString:
                            text.Append(method.Module.ResolveString(reader.ReadInt32()));
                            break;
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineTok:
                            MemberInfo member = method.Module.ResolveMember(reader.ReadInt32());
                            var type = member as Type;
                            text.Append(type != null ? TypeKey(type, owner) : MemberKey(member, owner));
                            var child = member as MethodBase;
                            if (child != null && child.DeclaringType == owner) children.Add(child);
                            break;
                        default: throw new InvalidDataException("Unaudited operand " + code.OperandType);
                    }
                }
            foreach (MethodBase child in children) AppendBody(text, child, owner, seen);
        }
    }
}
