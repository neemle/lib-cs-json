// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Neemle.DBAL.Tools;

internal static class MetadataMethodOperands
{
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(code => unchecked((ushort)code.Value)).ToDictionary(group => group.Key, group => group.First());

    internal static IReadOnlyList<(OpCode Code, EntityHandle Handle)> Read(MethodBodyBlock body)
    {
        var bytes = body.GetILBytes() ?? throw new BadImageFormatException("Missing method body.");
        var result = new List<(OpCode, EntityHandle)>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var value = (ushort)bytes[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            if (!Codes.TryGetValue(value, out var code)) throw new BadImageFormatException("Unknown IL opcode.");
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))),
                _ => 4
            };
            if (size < 0 || offset + size > bytes.Length) throw new BadImageFormatException("Truncated IL operand.");
            if (code.OperandType is OperandType.InlineType or OperandType.InlineTok or OperandType.InlineMethod or OperandType.InlineField)
                result.Add((code, MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)))));
            offset += size;
        }

        return result;
    }
}
