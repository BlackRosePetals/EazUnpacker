using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Unpacker;

/// <summary>Recovers cached UTF8 strings from RVA tables with an index-XOR initializer.</summary>
internal static class StringTablePass
{
    internal static int Process(string input, string output)
    {
        using var module = ModuleDefMD.Load(input);
        var decoded = new Dictionary<MethodDef, string>();
        foreach (var type in module.GetTypes())
        {
            var decoder = type.Methods.FirstOrDefault(m => m.IsStatic && m.HasBody &&
                m.MethodSig.ToString() == "System.String (System.Int32,System.Int32,System.Int32)" &&
                m.Body.Instructions.Any(i => i.Operand is IMethod t && t.DeclaringType.FullName == "System.Text.Encoding" && t.Name == "get_UTF8"));
            var init = type.FindStaticConstructor();
            if (decoder == null || init?.Body == null) continue;
            var instructions = init.Body.Instructions;
            byte[]? data = null; int? key = null;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode.Code == Code.Ldtoken && instructions[i].Operand is FieldDef field && field.HasFieldRVA)
                    data = field.InitialValue?.ToArray();
                if (i >= 2 && i + 3 < instructions.Count && instructions[i].IsLdcI4() &&
                    instructions[i - 1].OpCode.Code == Code.Xor && instructions[i - 2].IsLdloc() &&
                    instructions[i + 1].OpCode.Code == Code.Xor && instructions[i + 2].OpCode.Code == Code.Conv_U1 && instructions[i + 3].OpCode.Code == Code.Stelem_I1)
                    key = instructions[i].GetLdcI4Value();
            }
            if (data == null || !key.HasValue) continue;
            // Require a single byte array and the exact index-XOR byte loop; arbitrary initializers are not executed.
            if (type.Fields.Count(f => f.FieldType.FullName == "System.Byte[]") != 1 ||
                instructions.Count(i => i.OpCode.Code == Code.Stelem_I1) != 1 ||
                !instructions.Any(i => i.OpCode.Code == Code.Ldelem_U1)) continue;
            for (int i = 0; i < data.Length; i++) data[i] ^= (byte)(i ^ key.Value);
            foreach (var wrapper in type.Methods.Where(m => m.IsStatic && m.HasBody && m.MethodSig.ToString() == "System.String ()"))
            {
                var body = wrapper.Body.Instructions;
                if (body.Count != 11 || wrapper.Body.ExceptionHandlers.Count != 0 ||
                    body[0].OpCode.Code != Code.Ldsfld || body[0].Operand is not FieldDef cache || cache.FieldType.FullName != "System.String[]" ||
                    !body[1].IsLdcI4() || body[2].OpCode.Code != Code.Ldelem_Ref || body[3].OpCode.Code != Code.Dup ||
                    body[4].OpCode.Code is not (Code.Brtrue or Code.Brtrue_S) || body[4].Operand != body[10] || body[5].OpCode.Code != Code.Pop ||
                    body[10].OpCode.Code != Code.Ret) continue;
                for (int i = 3; i < body.Count; i++)
                {
                    if (body[i].OpCode.Code != Code.Call || body[i].Operand != decoder ||
                        !body[i - 1].IsLdcI4() || !body[i - 2].IsLdcI4() || !body[i - 3].IsLdcI4()) continue;
                    int offset = body[i - 2].GetLdcI4Value(), size = body[i - 1].GetLdcI4Value();
                    if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
                        throw new InvalidDataException("RVA string table slice exceeds payload.");
                    decoded[wrapper] = new UTF8Encoding(false, true).GetString(data, offset, size);
                    break;
                }
            }
        }
        if (decoded.Count == 0) return 0;
        int changes = 0;
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            if (decoded.TryGetValue(method, out var own))
            {
                method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, own)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); changes++;
                continue;
            }
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code == Code.Call && instruction.Operand is MethodDef target && decoded.TryGetValue(target, out var text))
                {
                    instruction.OpCode = OpCodes.Ldstr; instruction.Operand = text; changes++;
                }
            }
        }
        module.Write(output, new ModuleWriterOptions(module) { MetadataOptions = { Flags = MetadataFlags.PreserveAll } });
        return changes;
    }
}
