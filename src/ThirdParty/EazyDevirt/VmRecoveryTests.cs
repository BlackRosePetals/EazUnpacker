using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions.Interfaces;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Core.IO;
using EazyDevirt.Devirtualization.Pipeline;
using EazyDevirt.PatternMatching;
using EazyDevirt.PatternMatching.Patterns.OpCodes;
using MethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;
using TypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;

namespace EazyDevirt;

/// <summary>Executable regression fixtures for shared indirect-store recovery.</summary>
public static class VmRecoveryTests
{
    public static void Run()
    {
        KeyExpressionEvaluatorTests.Run();
        EazyDevirt.Logging.LoggingTests.Run();
        CheckPrivateAccessBridge();
        CheckByRefInlineAdapter();
        CheckInlineAdapterLabels();
        var module = new ModuleDefinition("StoreFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("StoreFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Stores", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var cases = new (string Name, Type Type, object Value, CilCode Code)[] {
            ("Byte", typeof(byte), (byte)231, CilCode.Stind_I1),
            ("Int32", typeof(int), -123456789, CilCode.Stind_I4),
            ("Single", typeof(float), 3.25f, CilCode.Stind_R4),
            ("String", typeof(string), "reference write", CilCode.Stind_Ref),
            ("DateTime", typeof(DateTime), new DateTime(2026, 10, 5), CilCode.Stobj)
        };
        foreach (var test in cases)
        {
            // Import the reflection signature including CLASS/VALUETYPE. A
            // Framework-corlib fixture cannot resolve modern CoreLib TypeRefs
            // just to rediscover DateTime's value-type bit.
            var element = module.DefaultImporter.ImportTypeSignature(test.Type);
            var method = new MethodDefinition(test.Name, MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, new ByReferenceTypeSignature(element), element));
            type.Methods.Add(method);
            var vm = Build(method, new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldarg_1),
                new CilInstruction(CilOpCodes.Stind_I), new CilInstruction(CilOpCodes.Ret));
            IndirectStoreResolver.Rewrite(vm);
            if (vm.Instructions[2].OpCode.Code != test.Code) throw new Exception("Wrong indirect store: " + test.Name);
            foreach (var instruction in vm.Instructions) method.CilMethodBody!.Instructions.Add(instruction);
        }
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = Assembly.Load(bytes.ToArray()).GetType("Fixture.Stores")!;
        foreach (var test in cases)
        {
            object[] arguments = { test.Type.IsValueType ? Activator.CreateInstance(test.Type)! : null!, test.Value };
            loaded.GetMethod(test.Name)!.Invoke(null, arguments);
            if (!Equals(arguments[0], test.Value)) throw new Exception("Indirect store execution failed: " + test.Name);
        }
        var forward = Build(type.Methods[0], new CilInstruction(CilOpCodes.Ldarg_0),
            new CilInstruction(CilOpCodes.Br, (uint)2), new CilInstruction(CilOpCodes.Ldarg_1),
            new CilInstruction(CilOpCodes.Stind_I), new CilInstruction(CilOpCodes.Ret));
        IndirectStoreResolver.Rewrite(forward);
        if (forward.Instructions[3].OpCode.Code != CilCode.Stind_I1) throw new Exception("Rejected a proven forward store address.");
        var ambiguous = Build(type.Methods[0], new CilInstruction(CilOpCodes.Ldc_I4_0),
            new CilInstruction(CilOpCodes.Brtrue, (uint)4), new CilInstruction(CilOpCodes.Ldarg_0),
            new CilInstruction(CilOpCodes.Br, (uint)5), new CilInstruction(CilOpCodes.Ldarg_1),
            new CilInstruction(CilOpCodes.Ldarg_1), new CilInstruction(CilOpCodes.Stind_I), new CilInstruction(CilOpCodes.Ret));
        try { IndirectStoreResolver.Rewrite(ambiguous); throw new Exception("Accepted an unresolved store join."); }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS: byte/int/float/reference/struct indirect stores execute correctly; unresolved joins rejected.");
        CheckFieldLoadPatterns();
        CheckSwitchOffsets();
        CheckPatternSearchOffsets();
        CheckSharedComparisonResults();
        CheckExistingGenericSignatures();
        CheckRuntimePatternNormalization();
        CheckInlineVmAdapters();
        CheckSharedBleSignedness();
        CheckEmptyGenericOwners();
        CheckNestedFinallyContinuation();
        CheckMultipleVmCallSites();
    }

    private static void CheckMultipleVmCallSites()
    {
        var module = new ModuleDefinition("MultiVmFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var type = new TypeDefinition("Fixture", "Calls", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type); module.TopLevelTypes.Add(type);
        var getter = new MethodDefinition("GetStream", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.DefaultImporter.ImportTypeSignature(typeof(Stream)))); type.Methods.Add(getter);
        getter.CilMethodBody = new CilMethodBody(); getter.CilMethodBody.Instructions.Add(CilOpCodes.Ldnull); getter.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var executor = new MethodDefinition("Execute", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, module.DefaultImporter.ImportTypeSignature(typeof(Stream)), module.CorLibTypeFactory.String, new SzArrayTypeSignature(module.CorLibTypeFactory.Object))); type.Methods.Add(executor);
        executor.CilMethodBody = new CilMethodBody(); executor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var host = new MethodDefinition("Host", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void)); type.Methods.Add(host); host.CilMethodBody = new CilMethodBody();
        foreach (string key in new[] { "key-first", "key-second" })
        { host.CilMethodBody.Instructions.Add(CilOpCodes.Call, getter); host.CilMethodBody.Instructions.Add(CilOpCodes.Ldstr, key); host.CilMethodBody.Instructions.Add(CilOpCodes.Ldnull); host.CilMethodBody.Instructions.Add(CilOpCodes.Call, executor); }
        host.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var shuffled = new MethodDefinition("Shuffled", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(shuffled); shuffled.CilMethodBody = new CilMethodBody();
        var lateCall = new CilInstruction(CilOpCodes.Call, executor);
        var nextGetter = new CilInstruction(CilOpCodes.Call, getter);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Call, getter), new CilInstruction(CilOpCodes.Ldstr, "shuffled-first"),
            new CilInstruction(CilOpCodes.Ldnull), new CilInstruction(CilOpCodes.Br, lateCall.CreateLabel()), nextGetter,
            new CilInstruction(CilOpCodes.Ldstr, "shuffled-second"), new CilInstruction(CilOpCodes.Ldnull), new CilInstruction(CilOpCodes.Call, executor),
            new CilInstruction(CilOpCodes.Ret), lateCall, new CilInstruction(CilOpCodes.Br, nextGetter.CreateLabel()) }) shuffled.CilMethodBody.Instructions.Add(instruction);
        var genericExecutor = new MethodDefinition("ExecuteGeneric", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, module.DefaultImporter.ImportTypeSignature(typeof(Stream)), module.CorLibTypeFactory.String,
                new SzArrayTypeSignature(module.CorLibTypeFactory.Object), module.DefaultImporter.ImportTypeSignature(typeof(Type[])), module.DefaultImporter.ImportTypeSignature(typeof(Type[])), new SzArrayTypeSignature(module.CorLibTypeFactory.Object)));
        type.Methods.Add(genericExecutor); genericExecutor.CilMethodBody = new CilMethodBody(); genericExecutor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var genericHost = new MethodDefinition("GenericWholeMethod", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, 1, Array.Empty<TypeSignature>()));
        genericHost.GenericParameters.Add(new GenericParameter("T")); type.Methods.Add(genericHost); genericHost.CilMethodBody = new CilMethodBody();
        genericHost.CilMethodBody.Instructions.Add(CilOpCodes.Call, getter); genericHost.CilMethodBody.Instructions.Add(CilOpCodes.Ldstr, "generic-whole-key");
        for (int i = 0; i < 4; i++) genericHost.CilMethodBody.Instructions.Add(CilOpCodes.Ldnull);
        genericHost.CilMethodBody.Instructions.Add(CilOpCodes.Call, genericExecutor); genericHost.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        using var bytes = new MemoryStream(); module.Write(bytes);
        var parsed = ModuleDefinition.FromBytes(bytes.ToArray()).TopLevelTypes.Single(t => t.Name == "Calls");
        var calls = MethodDiscovery.FindCallSites(parsed.Methods.Single(m => m.Name == "Host"), parsed.Methods.Single(m => m.Name == "GetStream").MetadataToken).ToArray();
        if (calls.Length != 2 || calls[0].Key != "key-first" || calls[1].Key != "key-second" || ReferenceEquals(calls[0].Executor, calls[1].Executor))
            throw new Exception("Multiple VM getter/executor pairs in one host were collapsed or keys crossed call-site boundaries.");
        var genericCalls = MethodDiscovery.FindCallSites(parsed.Methods.Single(m => m.Name == "GenericWholeMethod"), parsed.Methods.Single(m => m.Name == "GetStream").MetadataToken).ToArray();
        var shuffledCalls = MethodDiscovery.FindCallSites(parsed.Methods.Single(m => m.Name == "Shuffled"), parsed.Methods.Single(m => m.Name == "GetStream").MetadataToken).ToArray();
        if (shuffledCalls.Length != 2 || !shuffledCalls.Any(c => c.Key == "shuffled-first") || !shuffledCalls.Any(c => c.Key == "shuffled-second"))
            throw new Exception("Shuffled VM blocks lost a getter/key/executor pair across another physical getter.");
        if (genericCalls.Length != 1 || genericCalls[0].Key != "generic-whole-key" || (genericCalls[0].Executor.Operand as IMethodDescriptor)?.Signature?.ParameterTypes.Count != 6)
            throw new Exception("Six-parameter generic VM executor was omitted from whole-method discovery.");
        Console.WriteLine("PASS: all VM call sites in a multi-VM host retain independent executor/key pairs.");
    }

    private static void CheckEmptyGenericOwners()
    {
        var module = new ModuleDefinition("GenericOwnerFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("GenericOwnerFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Owner`2", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        type.GenericParameters.Add(new GenericParameter("T")); type.GenericParameters.Add(new GenericParameter("U"));
        var empty = new GenericInstanceTypeSignature(type, false, Array.Empty<TypeSignature>()).ToTypeDefOrRef();
        var owner = Resolver.OpenGenericOwner(empty, module)!;
        if (owner.ToTypeSignature() is not GenericInstanceTypeSignature { TypeArguments.Count: 2 } ||
            Resolver.OpenGenericOwner(type, module)?.FullName != owner.FullName ||
            !ReferenceEquals(Resolver.OpenGenericOwner(owner, module), owner))
            throw new Exception("Empty/naked generic owners were not repaired with exactly the declared open arguments.");
        var target = new MethodDefinition("Target", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        target.CilMethodBody = new CilMethodBody(); target.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_7); target.CilMethodBody.Instructions.Add(CilOpCodes.Ret); type.Methods.Add(target);
        var forward = new MethodDefinition("Forward", MethodAttributes.Public | MethodAttributes.Static, target.Signature);
        forward.CilMethodBody = new CilMethodBody(); forward.CilMethodBody.Instructions.Add(CilOpCodes.Call, owner.CreateMemberReference(target.Name, target.Signature!)); forward.CilMethodBody.Instructions.Add(CilOpCodes.Ret); type.Methods.Add(forward);
        using var bytes = new MemoryStream(); module.Write(bytes);
        var runtime = Assembly.Load(bytes.ToArray()).GetType("Fixture.Owner`2")!.MakeGenericType(typeof(int), typeof(string));
        if ((int)runtime.GetMethod("Forward")!.Invoke(null, null)! != 7) throw new Exception("Repaired open generic owner failed CLR execution.");
        Console.WriteLine("PASS: naked and empty-argument generic owners become explicit !n owners; serialized generic call executes in CLR.");
    }

    private static void CheckNestedFinallyContinuation()
    {
        var module = new ModuleDefinition("FinallyFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("FinallyFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0)); assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Finalies", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type); module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Execute", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32)); type.Methods.Add(method);
        var body = method.CilMethodBody = new CilMethodBody { InitializeLocals = true }; body.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Int32));
        var innerEnd = new CilInstruction(CilOpCodes.Endfinally); var finish = new CilInstruction(CilOpCodes.Ldloc_0);
        var outerHandler = new CilInstruction(CilOpCodes.Nop); var innerFinally = new CilInstruction(CilOpCodes.Ldc_I4_7);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Nop), new CilInstruction(CilOpCodes.Leave, finish.CreateLabel()), outerHandler,
            new CilInstruction(CilOpCodes.Nop), new CilInstruction(CilOpCodes.Leave, innerEnd.CreateLabel()), innerFinally,
            new CilInstruction(CilOpCodes.Stloc_0), new CilInstruction(CilOpCodes.Endfinally), innerEnd, finish, new CilInstruction(CilOpCodes.Ret) }) body.Instructions.Add(instruction);
        body.Instructions.CalculateOffsets();
        int end = MethodDevirtualizer.FindFinallyHandlerEnd(body.Instructions, 2);
        if (end != 9) throw new Exception("Outer finally ended at its nested leave/inner finally instead of the outer endfinally.");
        body.ExceptionHandlers.Add(new CilExceptionHandler { HandlerType = CilExceptionHandlerType.Finally, TryStart = body.Instructions[0].CreateLabel(), TryEnd = outerHandler.CreateLabel(), HandlerStart = outerHandler.CreateLabel(), HandlerEnd = body.Instructions[end].CreateLabel() });
        body.ExceptionHandlers.Add(new CilExceptionHandler { HandlerType = CilExceptionHandlerType.Finally, TryStart = body.Instructions[3].CreateLabel(), TryEnd = innerFinally.CreateLabel(), HandlerStart = innerFinally.CreateLabel(), HandlerEnd = innerEnd.CreateLabel() });
        using var bytes = new MemoryStream(); module.Write(bytes);
        if ((int)Assembly.Load(bytes.ToArray()).GetType("Fixture.Finalies")!.GetMethod("Execute")!.Invoke(null, null)! != 7)
            throw new Exception("Nested finally continuation did not execute its inner cleanup in CLR.");
        Console.WriteLine("PASS: nested try/finally leave continuation stays inside the outer finally; serialized handlers execute in CLR.");
    }

    private static void CheckSwitchOffsets()
    {
        var module = new ModuleDefinition("SwitchFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("SwitchFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Switches", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Select", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32));
        type.Methods.Add(method);
        var vm = Build(method, new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Switch, new[] { 4, 6 }),
            new CilInstruction(CilOpCodes.Ldc_I4_M1), new CilInstruction(CilOpCodes.Ret),
            new CilInstruction(CilOpCodes.Ldc_I4_7), new CilInstruction(CilOpCodes.Ret),
            new CilInstruction(CilOpCodes.Ldc_I4, 9), new CilInstruction(CilOpCodes.Ret));
        vm.CodeSize = vm.Instructions.Count;
        var offsets = MethodDevirtualizer.GetVirtualOffsets(vm);
        vm.Instructions[1].Operand = new ICilLabel[] { new CilOffsetLabel(offsets[4]), new CilOffsetLabel(offsets[6]) };
        var resolved = MethodDevirtualizer.GetVirtualOffsets(vm);
        if (offsets.Any(pair => resolved[pair.Key] != pair.Value) || offsets[4] != 16)
            throw new Exception("Switch entries were omitted from CIL branch offsets.");
        foreach (var instruction in vm.Instructions) method.CilMethodBody!.Instructions.Add(instruction);
        method.CilMethodBody!.MaxStack = method.CilMethodBody.ComputeMaxStack();
        method.CilMethodBody.VerifyLabels();
        using var bytes = new MemoryStream(); module.Write(bytes);
        var select = Assembly.Load(bytes.ToArray()).GetType("Fixture.Switches")!.GetMethod("Select")!;
        foreach (var test in new[] { (Input: -1, Result: -1), (Input: 0, Result: 7), (Input: 1, Result: 9), (Input: 2, Result: -1) })
            if (!Equals(select.Invoke(null, new object[] { test.Input }), test.Result))
                throw new Exception("Recovered switch execution differs at " + test.Input);
        Console.WriteLine("PASS: raw/resolved switch offsets agree and emitted switch executes all cases/default correctly.");
    }

    private sealed record OffsetPattern : IPattern
    {
        public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Ldc_I4_1 };
        public override bool MatchEntireBody => false;
        public override bool Verify(CilInstructionCollection instructions, int index = 0) => index == 2;
    }

    private sealed record OffsetOpcode : IOpCodePattern
    {
        public override IList<CilOpCode> Pattern => new[] { CilOpCodes.Ldc_I4_1 };
        public override bool MatchEntireBody => false;
        public override bool Verify(VMOpCode opcode, int index = 0) => index == 2;
    }

    private static void CheckPatternSearchOffsets()
    {
        var module = new ModuleDefinition("OffsetFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var type = new TypeDefinition("Fixture", "Offsets", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var field = new FieldDefinition("Code", AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes.Public,
            new FieldSignature(module.CorLibTypeFactory.Int32));
        type.Fields.Add(field);
        var method = new MethodDefinition("Handler", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Nop), new CilInstruction(CilOpCodes.Nop),
                     new CilInstruction(CilOpCodes.Ldc_I4_1), new CilInstruction(CilOpCodes.Pop), new CilInstruction(CilOpCodes.Ret) })
            method.CilMethodBody.Instructions.Add(instruction);
        if (PatternMatcher.GetAllMatchingInstructions(new OffsetPattern(), method, 1).Count != 1 ||
            PatternMatcher.GetAllMatchingInstructions(new OffsetPattern(), method.CilMethodBody.Instructions, 1).Count != 1)
            throw new Exception("Pattern verification added its nonzero search offset twice.");
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = ModuleDefinition.FromBytes(bytes.ToArray()).TopLevelTypes.Single(t => t.Name == "Offsets");
        var opcode = new VMOpCode((SerializedFieldDefinition)loaded.Fields[0], (SerializedMethodDefinition)loaded.Methods[0]);
        if (PatternMatcher.GetAllMatchingInstructions(new OffsetOpcode(), opcode, 1).Count != 1)
            throw new Exception("VM opcode verification added its nonzero search offset twice.");
        Console.WriteLine("PASS: collection, method and VM pattern verification receive the absolute nonzero match index once.");
    }

    private static void CheckSharedComparisonResults()
    {
        var module = new ModuleDefinition("ComparatorFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var operand = new TypeDefinition("Fixture", "Operand", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        var integer = new TypeDefinition("Fixture", "Integer", TypeAttributes.Public, operand);
        var runtime = new TypeDefinition("Fixture", "Runtime", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(operand); module.TopLevelTypes.Add(integer); module.TopLevelTypes.Add(runtime);
        var getter = new MethodDefinition("Get", MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.Int32));
        integer.Methods.Add(getter); integer.Methods.Add(constructor);
        var push = new MethodDefinition("Push", MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, operand.ToTypeSignature()));
        runtime.Methods.Add(push);
        var helper = new MethodDefinition("Compare", MethodAttributes.Private | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean, operand.ToTypeSignature(), operand.ToTypeSignature()));
        runtime.Methods.Add(helper); helper.CilMethodBody = new CilMethodBody();
        helper.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Boolean));
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Castclass, integer),
                     new CilInstruction(CilOpCodes.Callvirt, getter), new CilInstruction(CilOpCodes.Ldarg_1),
                     new CilInstruction(CilOpCodes.Castclass, integer), new CilInstruction(CilOpCodes.Callvirt, getter),
                     new CilInstruction(CilOpCodes.Cgt), new CilInstruction(CilOpCodes.Stloc_0),
                     new CilInstruction(CilOpCodes.Ldloc_0), new CilInstruction(CilOpCodes.Ret) })
            helper.CilMethodBody.Instructions.Add(instruction);
        var handler = new MethodDefinition("Handler", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, runtime.ToTypeSignature()));
        runtime.Methods.Add(handler); handler.CilMethodBody = new CilMethodBody();
        var consumer = new CilInstruction(CilOpCodes.Newobj, constructor);
        var trueValue = new CilInstruction(CilOpCodes.Ldc_I4_1);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldnull),
                     new CilInstruction(CilOpCodes.Ldnull), new CilInstruction(CilOpCodes.Call, helper),
                     new CilInstruction(CilOpCodes.Brtrue, trueValue.CreateLabel()), new CilInstruction(CilOpCodes.Ldc_I4_0), consumer,
                     new CilInstruction(CilOpCodes.Callvirt, push), new CilInstruction(CilOpCodes.Ret), trueValue,
                     new CilInstruction(CilOpCodes.Br, consumer.CreateLabel()) })
            handler.CilMethodBody.Instructions.Add(instruction);
        handler.CilMethodBody.Instructions.CalculateOffsets();
        if (!new SharedCgt().VerifyMethod(handler, 3) || new SharedCgtUnsigned().VerifyMethod(handler, 3))
            throw new Exception("Factored comparator result was missed or signedness was ignored.");
        trueValue.OpCode = CilOpCodes.Ldc_I4_2;
        if (new SharedCgt().VerifyMethod(handler, 3)) throw new Exception("Accepted a comparator which materializes 2 instead of true.");
        Console.WriteLine("PASS: shared comparator tails follow branch targets and reject wrong signedness/non-Boolean constants.");
    }

    private static void CheckExistingGenericSignatures()
    {
        var module = new ModuleDefinition("ReferenceFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var external = new AssemblyReference("MissingDependency", new Version(1, 2, 3, 4));
        module.AssemblyReferences.Add(external);
        var target = new TypeReference(module, external, "External", "Factory");
        var reference = target.CreateMemberReference("Create", MethodSignature.CreateStatic(
            new GenericParameterSignature(module, GenericParameterType.Method, 0), 1, module.CorLibTypeFactory.Int32));
        var type = new TypeDefinition("Fixture", "References", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Call", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.String, module.CorLibTypeFactory.Int32));
        type.Methods.Add(method); method.CilMethodBody = new CilMethodBody();
        method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldarg_0));
        method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Call, reference.MakeGenericInstanceMethod(module.CorLibTypeFactory.String)));
        method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        var valueBox = new TypeReference(module, external, "External", "ValueBox`1");
        var valueReference = target.CreateMemberReference("CreateBox", MethodSignature.CreateStatic(
            new GenericInstanceTypeSignature(valueBox, true, new GenericParameterSignature(module, GenericParameterType.Method, 0)),
            1, module.CorLibTypeFactory.Int32));
        var valueMethod = new MethodDefinition("CallBox", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(new GenericInstanceTypeSignature(valueBox, true, module.CorLibTypeFactory.String), module.CorLibTypeFactory.Int32));
        type.Methods.Add(valueMethod); valueMethod.CilMethodBody = new CilMethodBody();
        valueMethod.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldarg_0));
        valueMethod.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Call, valueReference.MakeGenericInstanceMethod(module.CorLibTypeFactory.String)));
        valueMethod.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = ModuleDefinition.FromBytes(bytes.ToArray());
        var existing = loaded.GetImportedMemberReferences().Single(r => r.Name == "Create");
        var context = GenericContext.FromType(existing.DeclaringType!).WithMethod(new GenericInstanceMethodSignature(loaded.CorLibTypeFactory.String));
        var expected = ((MethodSignature)existing.Signature!).InstantiateGenericTypes(context);
        var found = Resolver.FindExistingMethodReference(loaded, "Create", existing.DeclaringType!, expected, context);
        if (found == null || ((MethodSignature)found.Signature!).ReturnType is not GenericParameterSignature ||
            Resolver.FindExistingMethodReference(loaded, "Different", existing.DeclaringType!, expected, context) != null ||
            Resolver.FindExistingMethodReference(loaded, "Create", existing.DeclaringType!,
                MethodSignature.CreateStatic(loaded.CorLibTypeFactory.Int32, 1, loaded.CorLibTypeFactory.Int32), context) != null)
            throw new Exception("Missing-dependency resolution lost !!0 or accepted a mismatching metadata signature.");
        if (Resolver.FindExistingTypeClassification(loaded, loaded.GetImportedTypeReferences().Single(t => t.Name == "ValueBox`1")) != true ||
            Resolver.FindExistingTypeClassification(loaded, new TypeReference(loaded, external, "External", "Unknown")) != null)
            throw new Exception("Missing-dependency struct classification was guessed instead of recovered from metadata.");
        Console.WriteLine("PASS: missing dependencies use an exact existing generic metadata signature; unknown signatures stay unresolved.");
    }

    private static void CheckRuntimePatternNormalization()
    {
        var module = new ModuleDefinition("RuntimeNormalization.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("RuntimeNormalization" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Runtime", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var original = new MethodDefinition("Original", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32));
        type.Methods.Add(original); original.CilMethodBody = new CilMethodBody();
        var pop = new CilInstruction(CilOpCodes.Pop);
        var compare = new CilInstruction(CilOpCodes.Ldc_I4_0);
        var positive = new CilInstruction(CilOpCodes.Ldc_I4, 11);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldc_I4, 9),
                     new CilInstruction(CilOpCodes.Br, pop.CreateLabel()), new CilInstruction(CilOpCodes.Ldc_I4, 999),
                     new CilInstruction(CilOpCodes.Ret), pop, new CilInstruction(CilOpCodes.Br, compare.CreateLabel()), positive,
                     new CilInstruction(CilOpCodes.Ret), compare, new CilInstruction(CilOpCodes.Bgt, positive.CreateLabel()),
                     new CilInstruction(CilOpCodes.Ldc_I4_M1), new CilInstruction(CilOpCodes.Ret) })
            original.CilMethodBody.Instructions.Add(instruction);
        original.CilMethodBody.Instructions.CalculateOffsets();
        var originalBody = original.CilMethodBody;
        var snapshot = originalBody.Instructions.Select(i => (i.OpCode, i.Operand)).ToArray();
        CilMethodBody normalized;
        using (var scope = new RuntimePatternNormalization(type))
        {
            normalized = original.CilMethodBody!;
            if (normalized == originalBody || normalized.Instructions.Any(i => i.OpCode.Code == CilCode.Pop))
                throw new Exception("Temporary VM normalization did not linearize a scattered pure push/pop.");
            normalized.MaxStack = normalized.ComputeMaxStack(); normalized.VerifyLabels();
        }
        if (original.CilMethodBody != originalBody || !originalBody.Instructions.Select(i => (i.OpCode, i.Operand)).SequenceEqual(snapshot))
            throw new Exception("Temporary VM normalization mutated the original body.");
        var recovered = new MethodDefinition("Normalized", MethodAttributes.Public | MethodAttributes.Static, original.Signature);
        type.Methods.Add(recovered); recovered.CilMethodBody = normalized;
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = Assembly.Load(bytes.ToArray()).GetType("Fixture.Runtime")!;
        foreach (int input in new[] { -3, 0, 1, 42 })
            if (!Equals(loaded.GetMethod("Original")!.Invoke(null, new object[] { input }), loaded.GetMethod("Normalized")!.Invoke(null, new object[] { input })))
                throw new Exception("Temporary VM normalization changed branch semantics.");

        // The second entry reaches pop through a branch hop, without executing
        // the preceding constant. Removing that constant/pop pair would change
        // the stack seen by this path, so both instructions must be retained.
        var indirect = new CilMethodBody();
        var entryPop = new CilInstruction(CilOpCodes.Pop);
        var hop = new CilInstruction(CilOpCodes.Br, entryPop.CreateLabel());
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldarg_0),
                     new CilInstruction(CilOpCodes.Brtrue, hop.CreateLabel()), new CilInstruction(CilOpCodes.Pop),
                     new CilInstruction(CilOpCodes.Ldc_I4_1), entryPop, new CilInstruction(CilOpCodes.Ldc_I4_0),
                     new CilInstruction(CilOpCodes.Ret), hop }) indirect.Instructions.Add(instruction);
        indirect.Instructions.CalculateOffsets();
        var protectedEntry = RuntimePatternNormalization.Normalize(indirect);
        if (!protectedEntry.Instructions.Any(i => i.OpCode.Code == CilCode.Ldc_I4_1) ||
            protectedEntry.Instructions.Count(i => i.OpCode.Code == CilCode.Pop) != 2)
            throw new Exception("Normalization removed a pop reached through an independent branch alias.");
        Console.WriteLine("PASS: temporary VM helper normalization preserves branch execution, original bodies and indirect pop entries.");
    }

    private static void CheckFieldLoadPatterns()
    {
        var module=new ModuleDefinition("FieldLoadFixture.dll",KnownCorLibs.MsCorLib_v4_0_0_0);
        var type=new TypeDefinition("Fixture","FieldLoads",TypeAttributes.Public,module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var getValue=module.DefaultImporter.ImportMethod(typeof(FieldInfo).GetMethod("GetValue",new[]{typeof(object)})!);
        var fieldType=module.DefaultImporter.ImportMethod(typeof(FieldInfo).GetProperty("FieldType")!.GetGetMethod()!);
        var declaringType=module.DefaultImporter.ImportMethod(typeof(MemberInfo).GetProperty("DeclaringType")!.GetGetMethod()!);
        var convert=new MethodDefinition("Convert",MethodAttributes.Private|MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Object,module.CorLibTypeFactory.Object,module.DefaultImporter.ImportType(typeof(Type)).ToTypeSignature()));
        var push=new MethodDefinition("Push",MethodAttributes.Private,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void,module.CorLibTypeFactory.Object));
        type.Methods.Add(convert);type.Methods.Add(push);
        var cases=new[]{
            (Field:1,Instance:3,Property:1,Long:false,CorrectProperty:true,Match:true),
            (Field:1,Instance:2,Property:1,Long:false,CorrectProperty:true,Match:true),
            (Field:5,Instance:6,Property:5,Long:true,CorrectProperty:true,Match:true),
            (Field:1,Instance:3,Property:2,Long:false,CorrectProperty:true,Match:false),
            (Field:5,Instance:6,Property:5,Long:true,CorrectProperty:false,Match:false)
        };
        foreach(var test in cases)
        {
            var handler=new MethodDefinition("Handler",MethodAttributes.Private,
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void,module.CorLibTypeFactory.Object));
            type.Methods.Add(handler);
            handler.CilMethodBody=new CilMethodBody();
            for(int i=0;i<8;i++)handler.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Object));
            CilInstruction Load(int slot)=>test.Long
                ?new CilInstruction(CilOpCodes.Ldloc,handler.CilMethodBody.LocalVariables[slot])
                :slot switch {
                    1=>new CilInstruction(CilOpCodes.Ldloc_1),2=>new CilInstruction(CilOpCodes.Ldloc_2),
                    3=>new CilInstruction(CilOpCodes.Ldloc_3),_=>throw new ArgumentOutOfRangeException(nameof(slot))};
            var instructions=handler.CilMethodBody.Instructions;
            instructions.Add(new CilInstruction(CilOpCodes.Nop));
            instructions.Add(new CilInstruction(CilOpCodes.Ldarg_0));
            instructions.Add(Load(test.Field));instructions.Add(Load(test.Instance));
            instructions.Add(new CilInstruction(CilOpCodes.Callvirt,getValue));
            instructions.Add(Load(test.Property));
            instructions.Add(new CilInstruction(CilOpCodes.Callvirt,test.CorrectProperty?fieldType:declaringType));
            instructions.Add(new CilInstruction(CilOpCodes.Call,convert));
            instructions.Add(new CilInstruction(CilOpCodes.Callvirt,push));
            instructions.Add(new CilInstruction(CilOpCodes.Ret));
            if(PatternMatcher.MatchesPattern(new Ldfld(),handler)!=test.Match)
                throw new Exception("Instance field load recognition depends on local slots or accepts a different field/property.");
        }
        Console.WriteLine("PASS: instance field loads recognize shuffled/long local slots and reject different FieldInfo/property loads.");
    }

    private static void CheckInlineVmAdapters()
    {
        var module = new ModuleDefinition("InlineVmFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("InlineVmFixture" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "InlineVm", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var host = new MethodDefinition("Host", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        var executor = new MethodDefinition("Execute", MethodAttributes.Public,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Object, module.DefaultImporter.ImportTypeSignature(typeof(Stream)),
                module.CorLibTypeFactory.String, module.CorLibTypeFactory.Object.MakeSzArrayType()));
        type.Methods.Add(host); type.Methods.Add(executor);
        host.CilMethodBody = new CilMethodBody();
        host.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        var originalHostBody = host.CilMethodBody;
        var vm = new VMMethod(host, "", 123) { ExecutorInstruction = new CilInstruction(CilOpCodes.Call, executor) };
        vm.RecoveredAdapter = InlineVmCallAdapter.Create(vm, type,
            new TypeSignature[] { module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32 });
        var body = new CilMethodBody { InitializeLocals = true };
        var negative = new CilInstruction(CilOpCodes.Ldc_I4_M1);
        foreach (var instruction in new[] {
            new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldc_I4_0),
            new CilInstruction(CilOpCodes.Blt, negative.CreateLabel()),
            new CilInstruction(CilOpCodes.Ldarga, (ushort)1), new CilInstruction(CilOpCodes.Ldarg_1),
            new CilInstruction(CilOpCodes.Ldc_I4_1), new CilInstruction(CilOpCodes.Add), new CilInstruction(CilOpCodes.Stind_I4),
            new CilInstruction(CilOpCodes.Ldarg_1), new CilInstruction(CilOpCodes.Starg, (ushort)0),
            new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ret), negative, new CilInstruction(CilOpCodes.Ret) })
            body.Instructions.Add(instruction);
        body.Instructions.CalculateOffsets();
        vm.RecoveredAdapter.CilMethodBody = body;
        InlineVmCallAdapter.RewriteBody(vm, body, module.CorLibTypeFactory.Int32);
        type.Methods.Add(vm.RecoveredAdapter);
        // Give the unused executor a valid body for assembly emission.
        executor.CilMethodBody = new CilMethodBody();
        executor.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldnull));
        executor.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        body.MaxStack = body.ComputeMaxStack(); body.VerifyLabels();
        if (!ReferenceEquals(host.CilMethodBody, originalHostBody) || originalHostBody.Instructions.Count != 1)
            throw new Exception("An inlined VM adapter replaced the containing host body.");
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = Assembly.Load(bytes.ToArray()).GetType("Fixture.InlineVm")!;
        var adapter = loaded.GetMethod(vm.RecoveredAdapter.Name!, BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var test in new[] { (First: 1, Second: 7, Result: 8), (First: -1, Second: 9, Result: -1) })
        {
            var result = adapter.Invoke(null, new object?[] { null, null, null, new object[] { test.First, test.Second } });
            if (!Equals(result, test.Result)) throw new Exception("Inlined VM argument/branch/return adaptation changed its result.");
        }
        try
        {
            InlineVmCallAdapter.Create(vm, type, new TypeSignature[] { new PointerTypeSignature(module.CorLibTypeFactory.Int32) });
            throw new Exception("Accepted an inlined VM adapter with unmanaged pointer arguments.");
        }
        catch (NotSupportedException) { }
        Console.WriteLine("PASS: inlined VM adapters preserve host bodies and execute boxed arguments, ldarga/starg, branches and boxed returns; unmanaged pointers are rejected.");
    }

    private static void CheckSharedBleSignedness()
    {
        var module = new ModuleDefinition("BleSharedFixture.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("BleSharedFixture", new Version(1, 0)); assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "SharedBle", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        type.Fields.Add(new FieldDefinition("Code", AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes.Public,
            new FieldSignature(module.CorLibTypeFactory.Int32)));
        MethodDefinition Helper(string name, TypeSignature value, CilOpCode compare)
        {
            var getter = new MethodDefinition("Get" + name, MethodAttributes.Public, MethodSignature.CreateInstance(value));
            type.Methods.Add(getter); getter.CilMethodBody = new CilMethodBody();
            getter.CilMethodBody.Instructions.Add(new CilInstruction(compare.Code == CilCode.Cgt ? CilOpCodes.Ldc_I4_0 : CilOpCodes.Ldc_I8,
                compare.Code == CilCode.Cgt ? null : (object)0L));
            getter.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean, module.CorLibTypeFactory.Object, module.CorLibTypeFactory.Object));
            type.Methods.Add(method); method.CilMethodBody = new CilMethodBody { InitializeLocals = true };
            method.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Boolean));
            foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Castclass, type),
                new CilInstruction(CilOpCodes.Callvirt, getter), new CilInstruction(CilOpCodes.Ldarg_1),
                new CilInstruction(CilOpCodes.Castclass, type), new CilInstruction(CilOpCodes.Callvirt, getter),
                new CilInstruction(compare), new CilInstruction(CilOpCodes.Stloc_0), new CilInstruction(CilOpCodes.Ldloc_0), new CilInstruction(CilOpCodes.Ret) })
                method.CilMethodBody.Instructions.Add(instruction);
            return method;
        }
        var signed = Helper("Signed", module.CorLibTypeFactory.Int32, CilOpCodes.Cgt);
        var unsigned = Helper("Unsigned", module.CorLibTypeFactory.Int64, CilOpCodes.Cgt_Un);
        var handler = new MethodDefinition("Handler", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32));
        type.Methods.Add(handler); handler.CilMethodBody = new CilMethodBody { InitializeLocals = true };
        handler.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Object));
        handler.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Object));
        handler.CilMethodBody.LocalVariables.Add(new CilLocalVariable(module.CorLibTypeFactory.Boolean));
        var alternative = new CilInstruction(CilOpCodes.Ldloc_1);
        var join = new CilInstruction(CilOpCodes.Ldloc_2);
        var end = new CilInstruction(CilOpCodes.Ret);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldarg_1),
            new CilInstruction(CilOpCodes.Bne_Un_S, alternative.CreateLabel()), new CilInstruction(CilOpCodes.Ldloc_1),
            new CilInstruction(CilOpCodes.Ldloc_0), new CilInstruction(CilOpCodes.Call, signed), new CilInstruction(CilOpCodes.Ldc_I4_0),
            new CilInstruction(CilOpCodes.Ceq), new CilInstruction(CilOpCodes.Stloc_2), join,
            new CilInstruction(CilOpCodes.Brfalse_S, end.CreateLabel()), new CilInstruction(CilOpCodes.Ret), end,
            alternative, new CilInstruction(CilOpCodes.Ldloc_0), new CilInstruction(CilOpCodes.Call, unsigned),
            new CilInstruction(CilOpCodes.Ldc_I4_0), new CilInstruction(CilOpCodes.Ceq), new CilInstruction(CilOpCodes.Stloc_2),
            new CilInstruction(CilOpCodes.Br, join.CreateLabel()) }) handler.CilMethodBody.Instructions.Add(instruction);
        using var bytes = new MemoryStream(); module.Write(bytes);
        var parsed = ModuleDefinition.FromBytes(bytes.ToArray()).TopLevelTypes.Single(t => t.Name == "SharedBle");
        var opcode = new VMOpCode((SerializedFieldDefinition)parsed.Fields[0], (SerializedMethodDefinition)parsed.Methods.Single(m => m.Name == "Handler"));
        if (!PatternMatcher.MatchesPattern(new BleUnsignedSharedResult(), opcode) || PatternMatcher.MatchesPattern(new BleSharedResult(), opcode))
            throw new Exception("Shared Ble_Un tails were missed or confused with ordered/signed Ble.");
        var instructions = opcode.SerializedDelegateMethod.CilMethodBody!.Instructions;
        var first = instructions[5].Operand; instructions[5].Operand = instructions[15].Operand; instructions[15].Operand = first;
        if (PatternMatcher.MatchesPattern(new BleUnsignedSharedResult(), opcode) || !PatternMatcher.MatchesPattern(new BleSharedResult(), opcode))
            throw new Exception("Shared Ble tails did not preserve the swapped comparator signedness.");
        Console.WriteLine("PASS: shared Ble/Ble_Un comparator tails recognize only their exact signed/unsigned helper order.");
    }

    private static void CheckInlineAdapterLabels()
    {
        var module=new ModuleDefinition("AdapterLabels.dll",KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly=new AssemblyDefinition("AdapterLabels"+Guid.NewGuid().ToString("N"),new Version(1,0));assembly.Modules.Add(module);
        var type=new TypeDefinition("Fixture","Labels",TypeAttributes.Public,module.CorLibTypeFactory.Object.Type);module.TopLevelTypes.Add(type);
        var executor=new MethodDefinition("Execute",MethodAttributes.Public|MethodAttributes.Static,MethodSignature.CreateStatic(module.CorLibTypeFactory.Object,module.CorLibTypeFactory.Object,module.CorLibTypeFactory.String,module.CorLibTypeFactory.Object.MakeSzArrayType()));
        type.Methods.Add(executor);executor.CilMethodBody=new CilMethodBody();executor.CilMethodBody.Instructions.Add(CilOpCodes.Ldnull);executor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var host=new MethodDefinition("Host",MethodAttributes.Public|MethodAttributes.Static,MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));type.Methods.Add(host);
        var vm=Build(host);vm.ExecutorInstruction=new CilInstruction(CilOpCodes.Call,executor);host.CilMethodBody!.Instructions.Add(vm.ExecutorInstruction);
        var adapter=InlineVmCallAdapter.Create(vm,type,new[]{module.CorLibTypeFactory.Int32});vm.RecoveredAdapter=adapter;type.Methods.Add(adapter);
        host.CilMethodBody.Instructions.Clear();host.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var body=new CilMethodBody{InitializeLocals=true};adapter.CilMethodBody=body;var local=new CilLocalVariable(module.CorLibTypeFactory.Int32);body.LocalVariables.Add(local);
        var start=new CilInstruction(CilOpCodes.Ldarg_0);var zero=new CilInstruction(CilOpCodes.Ldc_I4_3);
        var one=new CilInstruction(CilOpCodes.Newobj,module.DefaultImporter.ImportMethod(typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!));
        var handler=new CilInstruction(CilOpCodes.Pop);var exit=new CilInstruction(CilOpCodes.Ldloc,local);
        foreach(var instruction in new[]{start,new CilInstruction(CilOpCodes.Switch,new ICilLabel[]{zero.CreateLabel(),one.CreateLabel()}),new CilInstruction(CilOpCodes.Ldc_I4,9),new CilInstruction(CilOpCodes.Stloc,local),new CilInstruction(CilOpCodes.Leave,exit.CreateLabel()),
            zero,new CilInstruction(CilOpCodes.Stloc,local),new CilInstruction(CilOpCodes.Leave,exit.CreateLabel()),one,new CilInstruction(CilOpCodes.Throw),
            handler,new CilInstruction(CilOpCodes.Ldc_I4_7),new CilInstruction(CilOpCodes.Stloc,local),new CilInstruction(CilOpCodes.Leave,exit.CreateLabel()),exit,new CilInstruction(CilOpCodes.Ret)})body.Instructions.Add(instruction);
        body.Instructions.CalculateOffsets();
        // Use offset-only labels, including all EH boundaries. Inserting the
        // prologue/boxing must bind them to original identities, not old offsets.
        foreach(var instruction in body.Instructions)
        {
            if(instruction.Operand is ICilLabel label)instruction.Operand=new CilOffsetLabel(label.Offset);
            else if(instruction.Operand is IList<ICilLabel> labels)instruction.Operand=labels.Select(l=>(ICilLabel)new CilOffsetLabel(l.Offset)).ToArray();
        }
        body.ExceptionHandlers.Add(new CilExceptionHandler{HandlerType=CilExceptionHandlerType.Exception,ExceptionType=module.DefaultImporter.ImportType(typeof(InvalidOperationException)),TryStart=new CilOffsetLabel(start.Offset),TryEnd=new CilOffsetLabel(handler.Offset),HandlerStart=new CilOffsetLabel(handler.Offset),HandlerEnd=new CilOffsetLabel(exit.Offset)});
        InlineVmCallAdapter.RewriteBody(vm,body,module.CorLibTypeFactory.Int32);
        if(body.ExceptionHandlers[0].TryStart!.Offset==0||body.ExceptionHandlers[0].HandlerEnd!.Offset!=exit.Offset)throw new Exception("Inline adapter EH labels stayed at stale offsets.");
        using var bytes=new MemoryStream();module.Write(bytes);var loaded=Assembly.Load(bytes.ToArray()).GetType("Fixture.Labels")!.GetMethod(adapter.Name!,BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(int value in new[]{-1,0,1,2})
        {
            int expected=value==0?3:value==1?7:9;
            if(!Equals(loaded.Invoke(null,new object?[]{null,"",new object[]{value}}),expected))throw new Exception("Inline switch/catch/leave labels changed behavior.");
        }
        Console.WriteLine("PASS: batched inline rewrites bind switch, leave and offset-only EH labels to original instruction identities; serialized catch paths execute correctly.");
    }

    private static void CheckByRefInlineAdapter()
    {
        var module = new ModuleDefinition("RefAdapter.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("RefAdapter" + Guid.NewGuid().ToString("N"), new Version(1, 0)); assembly.Modules.Add(module);
        var type = new TypeDefinition("Fixture", "Refs", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type); module.TopLevelTypes.Add(type);
        var executor = new MethodDefinition("Executor", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Object, module.CorLibTypeFactory.Object, module.CorLibTypeFactory.String, module.CorLibTypeFactory.Object.MakeSzArrayType()));
        type.Methods.Add(executor); executor.CilMethodBody = new CilMethodBody();
        executor.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldnull)); executor.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        var host = new MethodDefinition("Host", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void)); type.Methods.Add(host);
        var vm = Build(host); vm.ExecutorInstruction = new CilInstruction(CilOpCodes.Call, executor);
        host.CilMethodBody!.Instructions.Add(vm.ExecutorInstruction); host.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        var adapter = InlineVmCallAdapter.Create(vm, type, new TypeSignature[] {
            module.CorLibTypeFactory.Int32.MakeByReferenceType(), module.CorLibTypeFactory.String.MakeByReferenceType(), module.CorLibTypeFactory.Boolean });
        vm.RecoveredAdapter = adapter; type.Methods.Add(adapter); var body = new CilMethodBody(); adapter.CilMethodBody = body;
        var success = new CilInstruction(CilOpCodes.Ldc_I4, 7);
        foreach (var instruction in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldc_I4, 42), new CilInstruction(CilOpCodes.Stind_I4),
            new CilInstruction(CilOpCodes.Ldarg_1), new CilInstruction(CilOpCodes.Ldstr, "changed"), new CilInstruction(CilOpCodes.Stind_Ref),
            new CilInstruction(CilOpCodes.Ldarg_2), new CilInstruction(CilOpCodes.Brfalse, success.CreateLabel()),
            new CilInstruction(CilOpCodes.Ldnull), new CilInstruction(CilOpCodes.Throw), success, new CilInstruction(CilOpCodes.Ret) }) body.Instructions.Add(instruction);
        body.Instructions.CalculateOffsets(); InlineVmCallAdapter.RewriteBody(vm, body, module.CorLibTypeFactory.Int32);
        host.CilMethodBody = new CilMethodBody(); host.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
        using var bytes = new MemoryStream(); module.Write(bytes);
        var method = Assembly.Load(bytes.ToArray()).GetType("Fixture.Refs")!.GetMethod(adapter.Name!, BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (bool fail in new[] { false, true })
        {
            var values = new object[] { 1, "before", fail };
            try {
                var result = method.Invoke(null, new object?[] { null, null, values });
                if (fail || !Equals(result, 7)) throw new Exception("Byref adapter returned incorrectly.");
            }
            catch (TargetInvocationException ex) when (fail && ex.InnerException is NullReferenceException) { }
            if (!Equals(values[0], 42) || !Equals(values[1], "changed") || !Equals(values[2], fail))
                throw new Exception("Byref adapter lost reference/value-type write-back on success or exception.");
        }
        Console.WriteLine("PASS: inlined byref adapters write boxed value/reference arguments back on both return and exception.");
    }

    private static void CheckPrivateAccessBridge()
    {
        var module = new ModuleDefinition("PrivateAccess.dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        var assembly = new AssemblyDefinition("PrivateAccess" + Guid.NewGuid().ToString("N"), new Version(1, 0));
        assembly.Modules.Add(module);
        var owner = new TypeDefinition("Fixture", "Owner", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        var caller = new TypeDefinition("Fixture", "Caller", TypeAttributes.Public, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(owner); module.TopLevelTypes.Add(caller);
        var target = new MethodDefinition("Secret", MethodAttributes.Private | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32));
        owner.Methods.Add(target); target.CilMethodBody = new CilMethodBody();
        foreach (var i in new[] { new CilInstruction(CilOpCodes.Ldarg_0), new CilInstruction(CilOpCodes.Ldc_I4_1),
            new CilInstruction(CilOpCodes.Add), new CilInstruction(CilOpCodes.Ret) }) target.CilMethodBody.Instructions.Add(i);
        MethodDefinition Call(string name)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32));
            caller.Methods.Add(method); method.CilMethodBody = new CilMethodBody();
            method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ldarg_0));
            method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Call, target));
            method.CilMethodBody.Instructions.Add(new CilInstruction(CilOpCodes.Ret));
            return method;
        }
        var first = VmAccessBridge.Rewrite(Call("First"));
        var second = VmAccessBridge.Rewrite(Call("Second"));
        if (!target.IsPrivate || first.Count != 1 || !ReferenceEquals(first[0], second.Single()) || owner.Methods.Count != 2)
            throw new Exception("Private VM access widened API or duplicated its bridge.");
        using var bytes = new MemoryStream(); module.Write(bytes);
        var loaded = Assembly.Load(bytes.ToArray()).GetType("Fixture.Caller")!;
        foreach (string name in new[] { "First", "Second" })
            if (!Equals(loaded.GetMethod(name)!.Invoke(null, new object[] { 41 }), 42))
                throw new Exception("Private VM bridge did not preserve argument/result semantics.");
        Console.WriteLine("PASS: cross-type private VM calls use a shared internal bridge without changing private API.");
    }

    private static VMMethod Build(MethodDefinition method, params CilInstruction[] instructions)
    {
        method.CilMethodBody = new CilMethodBody();
        return new VMMethod(method, "", 0) { Instructions = instructions.ToList(),
            VirtualOffsets = Enumerable.Range(0, instructions.Length).Select(i => (uint)i).ToList() };
    }
}
