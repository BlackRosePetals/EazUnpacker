using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using EazFixer.Processors;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;
using FieldAttributes = dnlib.DotNet.FieldAttributes;

namespace EazFixer
{
    /// <summary>Executable fixtures for multi-decoder recovery and conservative constant propagation.</summary>
    public static class FixerCompatibilityTests
    {
        /// <summary>Runs the synthetic compatibility regression suite.</summary>
        public static void Run()
        {
            using (var fixture = new ModuleDefUser("StringFixture.dll"))
            {
                var assembly = new AssemblyDefUser("StringFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
                assembly.Modules.Add(fixture);
                var type = new TypeDefUser("Fixture", "Strings", fixture.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                fixture.Types.Add(type);
                var effect = new FieldDefUser("Effect", new FieldSig(fixture.CorLibTypes.Int32), FieldAttributes.Public | FieldAttributes.Static);
                type.Fields.Add(effect);
                var a = Decoder(fixture, type, "a", "first", MethodAttributes.Assembly);
                var b = Decoder(fixture, type, "b", "second", MethodAttributes.Private);
                var n = Decoder(fixture, type, "n", null, MethodAttributes.Public);
                var ctor = Decoder(fixture, type, "ctor", ".ctor", MethodAttributes.Assembly);
                var formatterHelper = Add(fixture, type, "FormatHelper", fixture.CorLibTypes.String, fixture.CorLibTypes.Int32, fixture.CorLibTypes.Boolean);
                formatterHelper.Body.Variables.Add(new Local(fixture.CorLibTypes.Double));
                Emit(formatterHelper, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Conv_R8),
                    Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Ldstr, "{0}%"),
                    Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Box, fixture.CorLibTypes.Double.TypeDefOrRef),
                    Instruction.Create(OpCodes.Call, fixture.Import(typeof(string).GetMethod("Format", new[] { typeof(string), typeof(object) }))),
                    Instruction.Create(OpCodes.Ret));
                var formatter = Add(fixture, type, "Percent", fixture.CorLibTypes.String, fixture.CorLibTypes.Int32);
                Emit(formatter, Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Call, formatterHelper), Instruction.Create(OpCodes.Ret));
                var direct = Add(fixture, type, "Direct", fixture.CorLibTypes.String);
                Emit(direct, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                var local = Add(fixture, type, "Local", fixture.CorLibTypes.String);
                local.Body.Variables.Add(new Local(fixture.CorLibTypes.Int32));
                Emit(local, Instruction.CreateLdcI4(4), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Nop), Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                var expression = Add(fixture, type, "Expression", fixture.CorLibTypes.String);
                Emit(expression, Instruction.CreateLdcI4(6), Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Xor), Instruction.Create(OpCodes.Nop), Instruction.Create(OpCodes.Call, b), Instruction.Create(OpCodes.Ret));
                var sideEffect = Add(fixture, type, "SideEffect", fixture.CorLibTypes.String);
                Emit(sideEffect, Instruction.CreateLdcI4(7), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Stsfld, effect), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                var failure = Add(fixture, type, "Failure", fixture.CorLibTypes.String);
                Emit(failure, Instruction.CreateLdcI4(99), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                var afterFailure = Add(fixture, type, "AfterFailure", fixture.CorLibTypes.String);
                Emit(afterFailure, Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Call, b), Instruction.Create(OpCodes.Ret));
                var nullResult = Add(fixture, type, "NullResult", fixture.CorLibTypes.String);
                Emit(nullResult, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Call, n), Instruction.Create(OpCodes.Ret));
                var constructor = Add(fixture, type, "Constructor", fixture.CorLibTypes.String);
                Emit(constructor, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Call, ctor), Instruction.Create(OpCodes.Ret));
                var join = Add(fixture, type, "Join", fixture.CorLibTypes.String, fixture.CorLibTypes.Boolean);
                var alternative = Instruction.CreateLdcI4(2); var joinCall = Instruction.Create(OpCodes.Call, a);
                Emit(join, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brfalse, alternative), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Br, joinCall), alternative, joinCall, Instruction.Create(OpCodes.Ret));
                var mutate = Add(fixture, type, "Mutate", fixture.CorLibTypes.Void, new ByRefSig(fixture.CorLibTypes.Int32));
                Emit(mutate, Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(99), Instruction.Create(OpCodes.Stind_I4), Instruction.Create(OpCodes.Ret));
                var escaped = Add(fixture, type, "Escaped", fixture.CorLibTypes.String);
                escaped.Body.Variables.Add(new Local(fixture.CorLibTypes.Int32));
                Emit(escaped, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Ldloca_S, escaped.Body.Variables[0]), Instruction.Create(OpCodes.Call, mutate), Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                var escapedLater = Add(fixture, type, "EscapedLater", fixture.CorLibTypes.String);
                escapedLater.Body.Variables.Add(new Local(fixture.CorLibTypes.Int32));
                escapedLater.Body.Variables.Add(new Local(new ByRefSig(fixture.CorLibTypes.Int32)));
                Emit(escapedLater, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Ldloca_S, escapedLater.Body.Variables[0]),
                    Instruction.Create(OpCodes.Stloc_1), Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Stloc_0), Instruction.Create(OpCodes.Ldloc_1),
                    Instruction.Create(OpCodes.Call, mutate), Instruction.Create(OpCodes.Ldloc_0), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Ret));
                // The original short edge spans exactly 127 bytes. Ten non-adjacent
                // replacements each grow the body by one byte, requiring a long edge.
                var shortEdge = Add(fixture, type, "ShortEdge", fixture.CorLibTypes.String, fixture.CorLibTypes.Boolean);
                var shortTarget = Instruction.Create(OpCodes.Ldstr, "branch");
                Emit(shortEdge, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brfalse_S, shortTarget));
                for (int index = 0; index < 10; index++)
                    Emit(shortEdge, Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Nop), Instruction.Create(OpCodes.Call, a), Instruction.Create(OpCodes.Pop));
                for (int index = 0; index < 41; index++) Emit(shortEdge, Instruction.Create(OpCodes.Nop));
                Emit(shortEdge, Instruction.Create(OpCodes.Ldstr, "fallthrough"), Instruction.Create(OpCodes.Ret), shortTarget, Instruction.Create(OpCodes.Ret));
                using (var stream = new MemoryStream())
                {
                    fixture.Write(stream); var bytes = stream.ToArray();
                    using (var module = ModuleDefMD.Load(bytes))
                    {
                        EazContext.PreserveSerializedMaxStack(module);
                        bool oldVirt = Flags.VirtFix; var oldToken = Flags.StrDecTok;
                        try
                        {
                            Flags.VirtFix = true; Flags.StrDecTok = new MDToken(0u);
                            if (StringFixUtils.FindStringDecryptMethods(module).Count() != 4) throw new Exception("Multiple/private/public decoders or numeric formatter classification: " + string.Join(", ", StringFixUtils.FindStringDecryptMethods(module).Select(m => m.Name.String)));
                            var processor = new StringFixer();
                            var ctx = new EazContext(module, Assembly.Load(bytes), new ProcessorBase[] { processor });
                            processor.Initialize(ctx); processor.Process();
                            if (!processor.Processed || processor.RecoveredCount != 16 || processor.WarningCount == 0)
                                throw new Exception("Recovery failed or per-item failure was not reported: " + processor.DetailMessage);
                            foreach (string name in new[] { "Failure", "Join", "Escaped", "EscapedLater", "Constructor" })
                                if (!module.GetTypes().SelectMany(item => item.Methods).Single(method => method.Name == name).Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Call))
                                    throw new Exception("Unresolved/VM-dependent decoder call was removed: " + name);
                            using (var output = new MemoryStream())
                            {
                                FixerBridge.PrepareForWrite(module);
                                if (!module.GetTypes().SelectMany(item => item.Methods).Single(method => method.Name == "ShortEdge").Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Brfalse))
                                    throw new Exception("Expanded string body retained an out-of-range short branch.");
                                module.Write(output); var loaded = Assembly.Load(output.ToArray()).GetType("Fixture.Strings");
                                Check(loaded, "Direct", "first"); Check(loaded, "Local", "first"); Check(loaded, "Expression", "second");
                                Check(loaded, "SideEffect", "first"); Check(loaded, "AfterFailure", "second"); Check(loaded, "NullResult", null);
                                Check(loaded, "Constructor", ".ctor"); Check(loaded, "Join", "first", true); Check(loaded, "Join", "first", false);
                                Check(loaded, "ShortEdge", "fallthrough", true); Check(loaded, "ShortEdge", "branch", false);
                                if ((int)loaded.GetField("Effect").GetValue(null) != 7) throw new Exception("Argument producer side effect lost.");
                                foreach (string name in new[] { "Failure", "Escaped", "EscapedLater" })
                                {
                                    try { loaded.GetMethod(name).Invoke(null, null); throw new Exception("Original decoder exception was lost: " + name); }
                                    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
                                }
                            }
                        }
                        finally { Flags.VirtFix = oldVirt; Flags.StrDecTok = oldToken; }
                    }
                }
            }
            CheckResourcesAndManifests();
            CheckInitializationStatus();
            Console.WriteLine("PASS: multi-decoder visibility, arithmetic/local arguments, preserved side effects, nulls, failure isolation, joins/address escapes and VM constructor dependencies.");
        }
        private static void CheckResourcesAndManifests()
        {
            using (var target = new ModuleDefUser("Resources.dll"))
            {
                var resource = new EmbeddedResource("payload", new byte[] { 1, 2, 3 }, ManifestResourceAttributes.Private);
                if (!ResourceResolver.TryAddRecoveredResource(target, resource, out var error) || error != null || target.Resources.Count != 1)
                    throw new Exception("Resource clone failed.");
                if (ResourceResolver.TryAddRecoveredResource(target, resource, out error) || error != null || target.Resources.Count != 1)
                    throw new Exception("Identical resource was not deduplicated.");
                if (ResourceResolver.TryAddRecoveredResource(target, new EmbeddedResource("payload", new byte[] { 4 }, ManifestResourceAttributes.Private), out error) || error == null ||
                    !((EmbeddedResource)target.Resources[0]).CreateReader().ToArray().SequenceEqual(new byte[] { 1, 2, 3 }))
                    throw new Exception("Conflicting resource overwrote the original.");
            }
            var name = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Dependency, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
            var filename = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Dependency.dll"));
            var manifest = name + ",abcf|resource," + filename + ",";
            foreach (var text in new[] { manifest, "1," + manifest })
                if (!Processors.AssemblyResolver.TryParseManifest(text, out var parsed) || parsed.Count != 1 || !parsed[0].Encrypted || !parsed[0].Compressed ||
                    !parsed[0].MustLoadfromDisk || !parsed[0].AlternateDirectory || parsed[0].Filename != "Dependency.dll")
                    throw new Exception("Old/new embedded assembly manifest parsing failed.");
            if (Processors.AssemblyResolver.TryParseManifest("not,an,assembly,manifest", out _)) throw new Exception("Malformed manifest accepted.");
            Console.WriteLine("PASS: resource cloning/deduplication/conflict retention and old/new assembly manifest formats.");
        }

        private static void CheckInitializationStatus()
        {
            using (var source = new ModuleDefUser("Initializer.dll"))
            {
                var initializer = Add(source, source.GlobalType, ".cctor", source.CorLibTypes.Void);
                initializer.Attributes = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                Emit(initializer, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw));
                initializer.Body.MaxStack = 17;
                initializer.Body.KeepOldMaxStack = true;
                using (var bytes = new MemoryStream())
                {
                    source.Write(bytes);
                    using (var outputModel = ModuleDefMD.Load(bytes.ToArray()))
                    using (var runtimeModel = ModuleDefMD.Load(bytes.ToArray()))
                    {
                        EazContext.PreserveSerializedMaxStack(outputModel);
                        EazContext.PreserveSerializedMaxStack(runtimeModel);
                        if (!EazContext.SuppressRuntimeInitializer(runtimeModel) || runtimeModel.GlobalType.FindStaticConstructor().Body.Instructions.Count != 1 ||
                            runtimeModel.GlobalType.FindStaticConstructor().Body.Instructions[0].OpCode.Code != Code.Ret ||
                            outputModel.GlobalType.FindStaticConstructor().Body.Instructions.Count != 2 ||
                            outputModel.GlobalType.FindStaticConstructor().Body.Instructions[1].OpCode.Code != Code.Throw)
                            throw new Exception("Decoder runtime changed the original module initializer.");
                        using (var savedOutput = new MemoryStream())
                        using (var savedRuntime = new MemoryStream())
                        {
                            outputModel.Write(savedOutput); runtimeModel.Write(savedRuntime);
                            using (var reloadedOutput = ModuleDefMD.Load(savedOutput.ToArray()))
                            using (var reloadedRuntime = ModuleDefMD.Load(savedRuntime.ToArray()))
                                if (reloadedOutput.GlobalType.FindStaticConstructor().Body.MaxStack != 17 || reloadedRuntime.GlobalType.FindStaticConstructor().Body.MaxStack != 17)
                                    throw new Exception("No-change/runtime save-copy recomputed a serialized max-stack upper bound.");
                        }
                    }
                }
                var context = new EazContext(source, null, new ProcessorBase[0]);
                var absent = new InitializationFixture(false); absent.Initialize(context);
                var broken = new InitializationFixture(true); broken.Initialize(context);
                if (!absent.NotDetected || absent.Initialized || broken.NotDetected || broken.Initialized)
                    throw new Exception("Initialization errors were misclassified as absent protections.");
                var ordinary = new TypeDefUser("Fixture", "OrdinaryAssemblyLoader", source.CorLibTypes.Object.TypeDefOrRef);
                source.Types.Add(ordinary);
                var register = Add(source, ordinary, "Register", source.CorLibTypes.Void);
                // A normal event registration without an Eaz extraction iterator
                // must not be reported as a malformed protected manifest.
                var eventType = new TypeRefUser(source,"System","AppDomain",source.CorLibTypes.AssemblyRef);
                var addResolver = new MemberRefUser(source,"add_AssemblyResolve",MethodSig.CreateInstance(source.CorLibTypes.Void,source.CorLibTypes.Object),eventType);
                Emit(register,Instruction.Create(OpCodes.Callvirt,addResolver),Instruction.Create(OpCodes.Ret));
                var ordinaryResolver = new EazFixer.Processors.AssemblyResolver();
                ordinaryResolver.Initialize(context);
                if(!ordinaryResolver.NotDetected)throw new Exception("An ordinary assembly resolver was mistaken for an Eaz embedded assembly runtime.");
            }
            Console.WriteLine("PASS: decoder-only initializer suppression and serialized max-stack save-copy preservation; absent protections differ from initialization failures.");
        }

        private sealed class InitializationFixture : ProcessorBase
        {
            private readonly bool _present;
            internal InitializationFixture(bool present) { _present = present; }
            protected override void InitializeInternal()
            {
                if (!_present) throw new ProtectionNotFoundException("absent");
                throw new InvalidOperationException("ambiguous init");
            }
            protected override void ProcessInternal() { }
            protected override void CleanupInternal() { }
        }
        private static void Check(Type type, string name, string expected, params object[] args)
        {
            if (!Equals(type.GetMethod(name).Invoke(null, args), expected)) throw new Exception("Recovered string semantics differ: " + name);
        }
        private static MethodDef Decoder(ModuleDef module, TypeDef type, string name, string result, MethodAttributes visibility)
        {
            var helper = Add(module, type, name + "Helper", module.CorLibTypes.String, module.CorLibTypes.Int32, module.CorLibTypes.Boolean);
            var normal = result == null ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldstr, result);
            Emit(helper, Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(99), Instruction.Create(OpCodes.Bne_Un, normal),
                Instruction.Create(OpCodes.Newobj, module.Import(typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes))), Instruction.Create(OpCodes.Throw), normal, Instruction.Create(OpCodes.Ret));
            var decoder = Add(module, type, name, module.CorLibTypes.String, module.CorLibTypes.Int32);
            decoder.Attributes = visibility | MethodAttributes.Static;
            Emit(decoder, Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(0), Instruction.Create(OpCodes.Call, helper), Instruction.Create(OpCodes.Ret));
            return decoder;
        }
        private static MethodDef Add(ModuleDef module, TypeDef type, string name, TypeSig result, params TypeSig[] args)
        {
            var method = new MethodDefUser(name, MethodSig.CreateStatic(result, args), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            type.Methods.Add(method); return method;
        }
        private static void Emit(MethodDef method, params Instruction[] instructions)
        {
            foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
        }
    }
}
