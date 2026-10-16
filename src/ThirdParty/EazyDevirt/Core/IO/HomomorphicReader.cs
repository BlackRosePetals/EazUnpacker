using System.Reflection;
using System.Security.Cryptography;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Devirtualization;

namespace EazyDevirt.Core.IO;

internal sealed class HomomorphicReader
{
    private readonly DevirtualizationContext _context;
    private Assembly? _runtime;
    private readonly Dictionary<string, object> _verifiedKeys = new();

    public HomomorphicReader(DevirtualizationContext context) => _context = context;

    internal (byte[] Code, int Id) Read(VMMethod method, VMOpCode opcode, BinaryReader reader, long available)
    {
        var instructions = method.Instructions;
        int count = instructions.Count;
        if (count < 3 || !instructions[count - 3].IsLdcI4() || instructions[count - 1].OpCode.Code != CilCode.Ldc_I8)
            throw new NotSupportedException("Encrypted region has no supported identifier/IV setup.");
        int id = instructions[count - 3].GetLdcI4Constant();
        long iv = (long)instructions[count - 1].Operand!;
        _context.Console.Info($"[{method.Parent.MetadataToken}] Cipher key operand: {instructions[count - 2]}");

        var guards = instructions.Select((i, n) => (Instruction: i, Index: n)).Where(p =>
            p.Instruction.Operand is IMethodDescriptor descriptor && descriptor.Signature is { } signature &&
            signature.ReturnType.FullName == "System.Boolean" && signature.ParameterTypes.Count is 3 or 4 &&
            signature.ParameterTypes[0].FullName == "System.Object" && signature.ParameterTypes[1].FullName == "System.Byte[]" &&
            signature.ParameterTypes[2].FullName == "System.UInt64");
        var keyLoad = instructions[count - 2];
        var guard = guards.LastOrDefault(p => !keyLoad.IsLdloc() ||
            instructions.Take(p.Index).LastOrDefault(i => i.IsStloc()) is { } store &&
            store.GetLocalVariable(method.Locals) == keyLoad.GetLocalVariable(method.Locals));
        if (guard.Instruction?.Operand is not IMethodDescriptor guardDescriptor || guardDescriptor.Resolve() is not { } verifierDefinition)
            throw new NotSupportedException("Cannot identify encrypted region key verifier.");
        int saltIndex = guard.Index - (guardDescriptor.Signature!.ParameterTypes.Count == 4 ? 2 : 1);
        if (saltIndex < 0 || instructions[saltIndex].OpCode.Code != CilCode.Ldc_I8)
            throw new NotSupportedException("Encrypted region verifier salt is not constant.");
        ulong salt = unchecked((ulong)(long)instructions[saltIndex].Operand!);
        int flag = guardDescriptor.Signature.ParameterTypes.Count == 4 && instructions[guard.Index - 1].IsLdcI4()
            ? instructions[guard.Index - 1].GetLdcI4Constant() : 0;
        var dataField = instructions.Take(saltIndex).LastOrDefault(i => i.OpCode.Code == CilCode.Ldtoken &&
            i.Operand is IFieldDescriptor field && field.Resolve()?.FieldRva is DataSegment)?.Operand as IFieldDescriptor;
        byte[] verifierBytes = (dataField?.Resolve()?.FieldRva as DataSegment)?.Data
            ?? throw new NotSupportedException("Encrypted region verifier is not an RVA byte array.");

        _runtime ??= Assembly.LoadFile(_context.Options.Assembly.FullName);
        var verifier = (MethodInfo)_runtime.ManifestModule.ResolveMethod(verifierDefinition.MetadataToken.ToInt32());
        string cacheId = verifierDefinition.MetadataToken + ":" + Convert.ToBase64String(verifierBytes) + ":" + salt + ":" + flag;
        if (!_verifiedKeys.TryGetValue(cacheId, out object? key))
        {
            bool Verify(object value)
            {
                object[] args = guardDescriptor.Signature.ParameterTypes.Count == 4
                    ? new object[] { value, verifierBytes, salt, flag } : new object[] { value, verifierBytes, salt };
                try { return verifier.Invoke(null, args) is true; }
                catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException || exception.InnerException is InvalidCastException)
                { return false; }
            }
            var candidates = KeyCandidates(verifierBytes, salt, flag).ToArray();
            object? found = candidates.Cast<object>().FirstOrDefault(Verify);
            if (found == null)
                found = StringKeyCandidates(method, verifierBytes, salt, flag).FirstOrDefault(Verify);
            if (found == null && int.TryParse(Environment.GetEnvironmentVariable("UNPACKER_VM_KEY_SEARCH_LIMIT"), out int limit) && limit > 0)
            {
                limit = Math.Min(limit, 10000000);
                _context.Console.Info($"[{method.Parent.MetadataToken}] Checking {limit} bounded Int32 candidates with verifier {verifierDefinition.MetadataToken}.");
                var verifyFour = guardDescriptor.Signature.ParameterTypes.Count == 4
                    ? (Func<object, byte[], ulong, int, bool>)Delegate.CreateDelegate(typeof(Func<object, byte[], ulong, int, bool>), verifier) : null;
                var verifyThree = guardDescriptor.Signature.ParameterTypes.Count == 3
                    ? (Func<object, byte[], ulong, bool>)Delegate.CreateDelegate(typeof(Func<object, byte[], ulong, bool>), verifier) : null;
                for (int value = 0; value < limit; value++)
                {
                    if (verifyFour?.Invoke(value, verifierBytes, salt, flag) ?? verifyThree!(value, verifierBytes, salt)) { found = value; break; }
                }
            }
            if (found == null)
            {
                foreach (var instruction in instructions.Take(guard.Index + 1).Take(35).Concat(instructions.Take(guard.Index + 1).Skip(Math.Max(0, guard.Index - 45))))
                    _context.Console.Info($"[{method.Parent.MetadataToken}] Key setup: {instruction}");
                _context.Console.Info($"[{method.Parent.MetadataToken}] Guard {verifierDefinition.MetadataToken}; salt={salt}; flag={flag}; RVA={Convert.ToBase64String(verifierBytes)}");
                throw new NotSupportedException($"Encrypted region {id} needs a verified Int32 key; {candidates.Length} constant candidates rejected. Supply the valid runtime value through UNPACKER_VM_INT32_KEYS.");
            }
            key = found;
            using (var sha = SHA256.Create())
                _context.Console.Info("Verified key profile: " + BitConverter.ToString(sha.ComputeHash(verifierBytes)).Replace("-", "").ToLowerInvariant() + "|" + salt + "|" + flag + "|" +
                    (key is string textKey?"s:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(textKey)):key.ToString()));
            _verifiedKeys.Add(cacheId, key);
        }

        var constructor = opcode.SerializedDelegateMethod.CilMethodBody!.Instructions
            .Where(i => i.OpCode.Code == CilCode.Newobj).Select(i => i.Operand).OfType<IMethodDescriptor>()
            .FirstOrDefault(m => m.Signature?.ParameterTypes.Count == 2 &&
                m.Signature.ParameterTypes[0].FullName == "System.Byte[]" && m.Signature.ParameterTypes[1].FullName == "System.Int64")
            ?.Resolve() ?? throw new NotSupportedException("Cannot identify encrypted region cipher constructor.");
        var cipherType = constructor.DeclaringType!.BaseType!.Resolve()!;
        var decryptDefinition = cipherType.Methods.Single(m => m.Signature?.ReturnType.FullName == "System.Byte[]" &&
            m.Signature.ParameterTypes.Count == 2 && m.Signature.ParameterTypes[0].FullName == "System.Byte[]" &&
            m.Signature.ParameterTypes[1].FullName == "System.Boolean");
        var sizeDefinition = cipherType.Methods.Single(m => m.IsStatic && m.Signature?.ReturnType.FullName == "System.Int32" &&
            m.Signature.ParameterTypes.Count == 1 && m.CilMethodBody!.Instructions.Any(i =>
                i.Operand is MethodDefinition called && called.DeclaringType == cipherType));
        var constructorInfo = (ConstructorInfo)_runtime.ManifestModule.ResolveMethod(constructor.MetadataToken.ToInt32());
        byte[] keyBytes;
        if (key is int integer)
            keyBytes = new byte[] { (byte)(integer >> 24), (byte)(integer >> 16), (byte)(integer >> 8), (byte)integer };
        else if (key is string text)
            keyBytes = System.Text.Encoding.Unicode.GetBytes(text);
        else throw new NotSupportedException("Unsupported verified key serialization.");
        object cipher = constructorInfo.Invoke(new object[] { keyBytes, iv });
        var decrypt = (MethodInfo)_runtime.ManifestModule.ResolveMethod(decryptDefinition.MetadataToken.ToInt32());
        try
        {
            byte[] prefix = reader.ReadBytes(4);
            if (prefix.Length != 4) throw new EndOfStreamException("Truncated encrypted region header.");
            byte[] decryptedPrefix = (byte[])decrypt.Invoke(cipher, new object[] { prefix, false })!;
            int length = BitConverter.ToInt32(decryptedPrefix, 0);
            if (length <= 0 || length > available - 4) throw new InvalidDataException("Encrypted region length is outside its method.");
            var getSize = (MethodInfo)_runtime.ManifestModule.ResolveMethod(sizeDefinition.MetadataToken.ToInt32());
            int encryptedLength = (int)getSize.Invoke(null, new object[] { length })!;
            if (encryptedLength < length + 4 || encryptedLength > available || encryptedLength > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid encrypted region padded length.");
            var encrypted = new byte[encryptedLength];
            Buffer.BlockCopy(prefix, 0, encrypted, 0, 4);
            byte[] tail = reader.ReadBytes(encryptedLength - 4);
            if (tail.Length != encryptedLength - 4) throw new EndOfStreamException("Truncated encrypted region data.");
            Buffer.BlockCopy(tail, 0, encrypted, 4, tail.Length);
            var plaintext = (byte[])decrypt.Invoke(cipher, new object[] { encrypted, false })!;
            if (plaintext.Length < length + 4 || BitConverter.ToInt32(plaintext, 0) != length)
                throw new InvalidDataException("Encrypted region length changed during full decryption.");
            var code = new byte[length];
            Buffer.BlockCopy(plaintext, 4, code, 0, length);
            _context.Console.Info($"[{method.Parent.MetadataToken}] Decrypted region {id}: {length} bytes; key verified against the original guard.");
            return (code, id);
        }
        finally { if (cipher is IDisposable disposable) disposable.Dispose(); }
    }

    private IEnumerable<int> KeyCandidates(byte[] verifier, ulong salt, int flag)
    {
        var supplied = (Environment.GetEnvironmentVariable("UNPACKER_VM_INT32_KEYS") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse);
        var profiles = new List<int>();
        string profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vm-key-profiles.txt");
        if (File.Exists(profilePath))
        {
            using var sha = SHA256.Create();
            string digest = BitConverter.ToString(sha.ComputeHash(verifier)).Replace("-", "").ToLowerInvariant();
            foreach (string line in File.ReadLines(profilePath).Where(l => l.Length > 0 && !l.StartsWith("#")))
            {
                var parts = line.Split('|');
                if (parts.Length == 4 && parts[0] == digest && parts[1] == salt.ToString() && parts[2] == flag.ToString())
                    if(int.TryParse(parts[3],out int value))profiles.Add(value);
            }
        }
        var constants = _context.Module.GetAllTypes().SelectMany(t => t.Methods).Where(m => m.CilMethodBody != null)
            .SelectMany(m => m.CilMethodBody!.Instructions).Where(i => i.IsLdcI4()).Select(i => i.GetLdcI4Constant());
        var metadataConstants = _context.Module.GetAllTypes().SelectMany(t=>t.Fields)
            .Select(f=>f.Constant?.InterpretData()).OfType<int>();
        return supplied.Concat(profiles).Concat(constants).Concat(metadataConstants)
            .Concat(new[]{unchecked((int)0x800704C7),unchecked((int)0x80004005),unchecked((int)0x80070057),unchecked((int)0x80070002)})
            .Concat(Enumerable.Range(0, 32)).Distinct().Take(10000);
    }

    private IEnumerable<string> StringKeyCandidates(VMMethod method,byte[] verifier,ulong salt,int flag)
    {
        var profiles=new List<string>();
        string profilePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"vm-key-profiles.txt");
        if(File.Exists(profilePath))
        {
            using var sha=SHA256.Create();
            string digest=BitConverter.ToString(sha.ComputeHash(verifier)).Replace("-", "").ToLowerInvariant();
            foreach(string line in File.ReadLines(profilePath))
            {
                var parts=line.Split('|');
                if(parts.Length==4 && parts[0]==digest && parts[1]==salt.ToString() && parts[2]==flag.ToString() && parts[3].StartsWith("s:"))
                    profiles.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[3].Substring(2))));
            }
        }
        var definitions = _context.Module.GetAllTypes();
        var literals = definitions.SelectMany(t => t.Methods).Where(m => m.CilMethodBody != null)
            .SelectMany(m => m.CilMethodBody!.Instructions).Where(i => i.OpCode.Code == CilCode.Ldstr).Select(i => (string)i.Operand!);
        var metadataNames = definitions.Select(t => t.FullName).Concat(_context.Module.GetImportedTypeReferences().Select(t => t.FullName));
        return profiles.Concat(method.Instructions.Where(i=>i.OpCode.Code==CilCode.Ldstr).Select(i=>(string)i.Operand!))
            .Concat(new[] { "System.Collections.Generic.List`1", "System.Nullable`1", "System.String", "System.Byte[]", "System.Resources.ResXFileRef", "System.Drawing.Bitmap", "System.Drawing.Icon", "System.Drawing.Image", "ResourceTypeCode.ByteArray", "ResourceTypeCode.String", "ResourceTypeCode.Stream" })
            .Concat(new[]{"Sign in", "Sign In", "Sign in...", "Sign In...", "Sign out", "Sign Out", "Activate", "License", "Offline license", "Offline License", "Manage offline license", "Manage Offline License", "Enter license key", "Enter License Key", "Manage license", "Get a license", "Buy a license", "Trial", "Evaluation"})
            .Concat(literals).Concat(metadataNames).Concat(ResourceKeyCandidates()).Where(s=>s.Length>0 && s.Length<512).Distinct().Take(10000);
    }

    private IEnumerable<string> ResourceKeyCandidates()
    {
        var result=new List<string>();
        foreach(var resource in _context.Module.Resources.Where(r=>r.IsEmbedded))
        {
            byte[]? bytes=resource.GetData();
            if(bytes==null || bytes.Length<4 || BitConverter.ToUInt32(bytes,0)!=0xBEEFCACE)continue;
            using var stream=new MemoryStream(bytes,false);
            using var reader=new System.Resources.ResourceReader(stream);
            var entries=reader.GetEnumerator();
            while(entries.MoveNext())
            {
                string name=(string)entries.Key;
                reader.GetResourceData(name,out string type,out byte[] data);
                result.Add(name);result.Add(type);result.Add(type.Split(',')[0]);
                if(type=="ResourceTypeCode.String")
                {using var value=new BinaryReader(new MemoryStream(data,false));result.Add(value.ReadString());}
            }
        }
        return result;
    }
}
