using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using EazyDevirt.Core.Abstractions;
using EazyDevirt.Core.Architecture;
using EazyDevirt.Core.IO;

namespace EazyDevirt.Devirtualization.Pipeline;

internal class MethodDevirtualizer : StageBase
{
    private CryptoStreamV3 VMStream { get; set; }
    private VMBinaryReader VMStreamReader { get; set; }
    
    private Resolver Resolver { get; set; }
    private VMHeaderLayout HeaderLayout { get; set; }
    private VMExceptionLayout ExceptionLayout { get; set; }
    private HomomorphicReader Homomorphic { get; set; }
    
    public override bool Run()
    {
        if (!Init()) return false;
        HeaderLayout = VMHeaderLayout.Discover(Ctx.VMDeclaringType);
        Ctx.HeaderLayout=HeaderLayout;
        ExceptionLayout = VMExceptionLayout.Discover(Ctx.VMDeclaringType);
        Homomorphic = new HomomorphicReader(Ctx);
        
        VMStream = new CryptoStreamV3(Ctx.VMStream, Ctx.MethodCryptoKey, true);
        VMStreamReader = new VMBinaryReader(VMStream);
        
        Resolver = new Resolver(Ctx);
        CalibrateHeader();
        Ctx.Progress?.Invoke("VM 方法还原",0,Ctx.VMMethods.Count);
        for (int pass = 0; pass < 3; pass++)
        {
        int restoredBefore = Ctx.VMMethods.Count(m=>m.SuccessfullyDevirtualized);
        int restoredInPass=restoredBefore;
        foreach (var vmMethod in Ctx.VMMethods.Where(m=>!m.SuccessfullyDevirtualized))
        {
            var originalBody=vmMethod.Parent.CilMethodBody;
            try {
                VMStream.Seek(vmMethod.MethodKey, SeekOrigin.Begin);
                ReadVMMethod(vmMethod);
                if(vmMethod.SuccessfullyDevirtualized)vmMethod.Failure=null;
            }
            catch(Exception ex) {
                if(vmMethod.Parent.CilMethodBody!=originalBody)vmMethod.Parent.CilMethodBody=originalBody;
                vmMethod.SuccessfullyDevirtualized=false;
                vmMethod.Failure = ex.GetType().Name + ": " + ex.Message;
                Ctx.Console.Error($"[{vmMethod.Parent.MetadataToken}] Preserved original body: {ex.GetType().Name}: {ex.Message}");
                if (Ctx.Options.VeryVeryVerbose) Ctx.Console.Warning(ex.StackTrace ?? "");
                if (ex is AsmResolver.DotNet.Code.Cil.StackImbalanceException && vmMethod.Instructions != null)
                    foreach (var instruction in vmMethod.Instructions.Skip(Math.Max(0, vmMethod.Instructions.Count - 45))) Ctx.Console.Info("Rejected IL: " + instruction);
            }
            
            if (Ctx.Options.VeryVerbose)
                Ctx.Console.Info(vmMethod);
            if(vmMethod.SuccessfullyDevirtualized)restoredInPass++;
            Ctx.Progress?.Invoke("VM 方法还原（第 "+(pass+1)+" 轮，当前 "+vmMethod.Parent.MetadataToken+"）",restoredInPass,Ctx.VMMethods.Count);
        }
        if(Ctx.VMMethods.Count(m=>m.SuccessfullyDevirtualized)==restoredBefore)break;
        }
        
        VMStreamReader.Dispose();
        return true;
    }

    private void CalibrateHeader()
    {
        var candidates=new HashSet<int>(new[]{1,2,4,8,16,32,64,128});
        foreach(var method in Ctx.VMMethods)
        {
            try {
                VMStream.Position=method.MethodKey;
                var info=new VMMethodInfo(VMStreamReader,HeaderLayout);
                candidates.RemoveWhere(mask=>((info.BindingFlags & mask)!=0)!=method.Parent.IsStatic);
                if(HeaderLayout.TypeSlotsCalibrated)continue;
                string first=Resolver.ResolveType(info.VMDeclaringType)!.FullName;
                string second=Resolver.ResolveType(info.VMReturnType)!.FullName;
                string declaring=method.Parent.DeclaringType!.FullName,returns=method.Parent.Signature!.ReturnType.FullName;
                if(first==declaring && second==returns || first==returns && second==declaring) {
                    HeaderLayout.FirstTypeIsReturn=first==returns;
                    HeaderLayout.TypeSlotsCalibrated=true;
                }
            }
            catch(Exception ex) { Ctx.Console.Warning("Header calibration skipped: "+ex.Message); }
        }
        if(candidates.Count==1) {HeaderLayout.StaticMask=candidates.Single();HeaderLayout.StaticMaskCalibrated=true;}
        Console.WriteLine($"VM header calibration: type slots={HeaderLayout.TypeSlotsCalibrated}, first return={HeaderLayout.FirstTypeIsReturn}, static mask={HeaderLayout.StaticMask} ({HeaderLayout.StaticMaskCalibrated})");
    }
    
    private void ReadVMMethod(VMMethod vmMethod)
    {
        vmMethod.MethodInfo = new VMMethodInfo(VMStreamReader,HeaderLayout);

        ReadExceptionHandlers(vmMethod);
        
        // These types already exist in the original method signature; header slots are shuffled independently.
        vmMethod.MethodInfo.DeclaringType = vmMethod.Parent.DeclaringType!;
        vmMethod.MethodInfo.ReturnType = vmMethod.Parent.Signature!.ReturnType.ToTypeDefOrRef()!;
        
        ResolveLocalsAndParameters(vmMethod);

        ReadInstructions(vmMethod);
        if (!vmMethod.SuccessfullyDevirtualized && (!Ctx.Options.SaveAnyway || Ctx.Options.OnlySaveDevirted))
            return;

        // these need all instructions to be successfully devirtualized to work
        IndirectStoreResolver.Rewrite(vmMethod);
        ResolveBranchTargets(vmMethod);
        ResolveExceptionHandlers(vmMethod);

        // recompile method
        var newBody=new CilMethodBody();
        newBody.InitializeLocals=true;
        vmMethod.Locals.ForEach(x=>newBody.LocalVariables.Add(x));
        vmMethod.ExceptionHandlers.ForEach(x=>newBody.ExceptionHandlers.Add(x));
        vmMethod.Instructions.ForEach(x=>newBody.Instructions.Add(x));
        newBody.VerifyLabelsOnBuild=false;newBody.ComputeMaxStackOnBuild=false;
        vmMethod.Parent.CilMethodBody=newBody;
        if (vmMethod.SuccessfullyDevirtualized && !Ctx.Options.NoVerify)
        {
            newBody.MaxStack=newBody.ComputeMaxStack();
            newBody.VerifyLabels();
        }
    }
    
    private void ReadExceptionHandlers(VMMethod vmMethod)
    {
        vmMethod.VMExceptionHandlers = new List<VMExceptionHandler>(VMStreamReader.ReadInt16());
        for (var i = 0; i < vmMethod.VMExceptionHandlers.Capacity; i++)
            vmMethod.VMExceptionHandlers.Add(new VMExceptionHandler(VMStreamReader, ExceptionLayout));

        vmMethod.VMExceptionHandlers.Sort((first, second) =>
            first.TryStart == second.TryStart
                ? first.TryLength.CompareTo(second.TryLength)
                : second.TryStart.CompareTo(first.TryStart));
    }
    
    private void ResolveLocalsAndParameters(VMMethod vmMethod)
    {
        vmMethod.Locals = new List<CilLocalVariable>();
        foreach (var local in vmMethod.MethodInfo.VMLocals)
        {
            var type = Resolver.ResolveType(local.VMType)!;
            vmMethod.Locals.Add(new CilLocalVariable(type.ToTypeSignature()));

            // if (Ctx.Options.VeryVeryVerbose)
            //     Ctx.Console.Info($"[{vmMethod.MethodInfo.Name}] Local: {type.Name}");
        }
        
        // the parameters should already be the correct types and in the correct order so we don't need to resolve those
    }

    private void ReadInstructions(VMMethod vmMethod)
    {
        vmMethod.Instructions = new List<CilInstruction>();
        vmMethod.EncryptedRegions=0;
        vmMethod.VirtualOffsets = new List<uint>();
        vmMethod.CodeSize = VMStreamReader.ReadInt32();
        if (vmMethod.CodeSize <= 0 || vmMethod.CodeSize > VMStream.Length - VMStream.Position)
            throw new InvalidDataException("Invalid VM code size.");
        vmMethod.InitialCodeStreamPosition = VMStream.Position;
        vmMethod.SuccessfullyDevirtualized = true;
        var finalPosition = VMStream.Position + vmMethod.CodeSize;
        ReadInstructionRange(vmMethod, VMStream, finalPosition, vmMethod.InitialCodeStreamPosition, false, 0);
    }

    private void AddInstruction(VMMethod method, CilInstruction instruction)
    {
        method.VirtualOffsets.Add(checked((uint)method.CodePosition));
        method.Instructions.Add(instruction);
    }

    private void ReadInstructionRange(VMMethod vmMethod, Stream stream, long finalPosition, long virtualOrigin, bool encrypted, int regionId)
    {
        while (stream.Position < finalPosition)
        {
            vmMethod.CodePosition = stream.Position - virtualOrigin;
            var virtualOpCode = VMStreamReader.ReadInt32Special();
            var vmOpCode = Ctx.PatternMatcher.GetOpCodeValue(virtualOpCode);
            if (!vmOpCode.HasVirtualCode)
            {
                throw new InvalidDataException($"Unknown VM opcode {virtualOpCode} at {vmMethod.Instructions.Count}.");
            }

            if (!vmOpCode.IsIdentified)
            {
                throw new NotSupportedException($"VM opcode {virtualOpCode} has no verified CIL mapping.");
            }

            if (vmOpCode.SpecialOpCode == SpecialOpCodes.StartHomomorphic)
            {
                long virtualStart = stream.Position - virtualOrigin;
                var block = Homomorphic.Read(vmMethod, vmOpCode, VMStreamReader, finalPosition - stream.Position);
                for (int i = 0; i < 3; i++) AddInstruction(vmMethod, new CilInstruction(CilOpCodes.Pop));
                var previousReader = VMStreamReader;
                using var nested = new MemoryStream(block.Code, false);
                using var nestedReader = new VMBinaryReader(nested, true);
                VMStreamReader = nestedReader;
                try { ReadInstructionRange(vmMethod, nested, nested.Length, -virtualStart, true, block.Id); }
                finally { VMStreamReader = previousReader; }
                vmMethod.EncryptedRegions++;
                vmMethod.HasHomomorphicEncryption = true;
                continue;
            }
            if (vmOpCode.SpecialOpCode == SpecialOpCodes.EndHomomorphic)
            {
                if (!encrypted || vmMethod.Instructions.Count == 0 ||
                    !vmMethod.Instructions.Last().IsLdcI4() || vmMethod.Instructions.Last().GetLdcI4Constant() != regionId || stream.Position != finalPosition)
                    throw new InvalidDataException("Encrypted region has no matching final identifier.");
                AddInstruction(vmMethod, new CilInstruction(CilOpCodes.Pop));
                return;
            }
            if (vmOpCode.IsSpecial && vmOpCode.SpecialOpCode is not (SpecialOpCodes.EazCall or SpecialOpCodes.NoBody))
                throw new NotSupportedException("Unsupported special VM opcode: " + vmOpCode.SpecialOpCode);

            if (vmOpCode is { IsSpecial: true, CilOpCode: null })
            {
                vmOpCode.CilOpCode = ResolveSpecialCilOpCode(vmOpCode, vmMethod);
                if (vmOpCode.CilOpCode != null && Ctx.Options.VeryVerbose)
                    Ctx.Console.InfoStr($"Resolved special opcode {vmOpCode.SpecialOpCode.ToString()!} to CIL opcode {vmOpCode.CilOpCode.ToString()}", vmOpCode.SerializedDelegateMethod.MetadataToken);
            }

            var operand = vmOpCode.IsSpecial ? ReadSpecialOperand(vmOpCode, vmMethod) : ReadOperand(vmOpCode, vmMethod);
            if (vmOpCode.CilOpCode != null)
            {
                // Log these for now since they're special cases. 
                if (vmOpCode.CilOpCode.Value.Mnemonic.StartsWith("stind"))
                    Ctx.Console.Warning($"Placing stind instruction at #{vmMethod.Instructions.Count}");
                else if (vmOpCode.SpecialOpCode == SpecialOpCodes.NoBody)
                    Ctx.Console.Warning($"Placing NoBody instruction at #{vmMethod.Instructions.Count}");

                if (vmOpCode.CilOpCode.Value.OperandType != CilOperandType.InlineNone && operand == null)
                    throw new InvalidDataException("Unresolved CIL operand: " + vmOpCode.CilOpCode);

                var instruction =
                    new CilInstruction(vmOpCode.CilOpCode.Value, operand);
                AddInstruction(vmMethod, instruction);
            }
        }

        if (stream.Position != finalPosition) throw new InvalidDataException("VM operand exceeded method code boundary.");
        if (encrypted) throw new InvalidDataException("Encrypted region end marker is missing.");
    }

    private Dictionary<uint, int> GetVirtualOffsets(VMMethod vmMethod)
    {
        var virtualOffsets = new Dictionary<uint, int>(vmMethod.Instructions.Count);
        var lastCilOffset = 0;
        for (int i = 0; i < vmMethod.Instructions.Count; i++)
        {
            var ins = vmMethod.Instructions[i];
            ins.Offset = lastCilOffset;
            if (!virtualOffsets.ContainsKey(vmMethod.VirtualOffsets[i])) virtualOffsets.Add(vmMethod.VirtualOffsets[i], lastCilOffset);
            lastCilOffset += ins.Size;
        }
        virtualOffsets.Add((uint)vmMethod.CodeSize, lastCilOffset);

        return virtualOffsets;
    }
    
    private void ResolveBranchTargets(VMMethod vmMethod)
    {
        var virtualOffsets = GetVirtualOffsets(vmMethod);

        for (var i = 0; i < vmMethod.Instructions.Count; i++)
        {
            var ins = vmMethod.Instructions[i];
            switch (ins.OpCode.OperandType)
            {
                case CilOperandType.InlineBrTarget:
                case CilOperandType.ShortInlineBrTarget:
                    ins.Operand = vmMethod.SuccessfullyDevirtualized
                        ? new CilOffsetLabel(virtualOffsets[(uint)ins.Operand!])
                        : new CilOffsetLabel(0);
                    break;
                case CilOperandType.InlineSwitch:
                    var offsets = (int[])ins.Operand!;
                    var labels = new ICilLabel[offsets!.Length];
                    for (var x = 0; x < offsets.Length; x++)
                        labels[x] = vmMethod.SuccessfullyDevirtualized
                            ? new CilOffsetLabel(virtualOffsets[unchecked((uint)offsets[x])])
                            : new CilOffsetLabel(0);
                    ins.Operand = labels;
                    break;
            }
        }
    }

    private void ResolveExceptionHandlers(VMMethod vmMethod)
    {
        vmMethod.ExceptionHandlers = new List<CilExceptionHandler>();
        if (!vmMethod.SuccessfullyDevirtualized) return;
        
        var virtualOffsets = GetVirtualOffsets(vmMethod);
        foreach (var vmExceptionHandler in vmMethod.VMExceptionHandlers)
        {
            var exceptionHandler = new CilExceptionHandler
            {
                HandlerType = vmExceptionHandler.HandlerType,
                ExceptionType = vmExceptionHandler.HandlerType == CilExceptionHandlerType.Exception ? Resolver.ResolveType(vmExceptionHandler.CatchType) : null
            };

            var handlerStart = vmMethod.Instructions.GetByOffset(virtualOffsets[vmExceptionHandler.HandlerStart]);
            exceptionHandler.HandlerStart = handlerStart?.CreateLabel();

            // HandlerEnd is not explicitly defined, and we don't have a length, so we need to find it ourselves
            var handlerEndIndex = vmMethod.Instructions.GetIndexByOffset(virtualOffsets[vmExceptionHandler.HandlerStart]);
            var foundHandlerEnd = false;
            while (!foundHandlerEnd && vmMethod.Instructions.Count - 1 > handlerEndIndex)
            {
                var possibleHandlerEnd = vmMethod.Instructions[handlerEndIndex];
                
                // if there is a branch, skip past it to ensure the correct HandlerEnd is found
                if (possibleHandlerEnd.IsBranch() && possibleHandlerEnd.OpCode.Code is not (CilCode.Leave or CilCode.Leave_S))
                {
                    handlerEndIndex = vmMethod.Instructions.GetIndexByOffset(((ICilLabel)possibleHandlerEnd.Operand!).Offset);
                    continue;
                }
                
                switch (possibleHandlerEnd.OpCode.Code)
                {
                    case CilCode.Endfilter:
                        if (vmExceptionHandler.HandlerType == CilExceptionHandlerType.Filter)
                            foundHandlerEnd = true;
                        break;
                    case CilCode.Endfinally:
                        if (vmExceptionHandler.HandlerType == CilExceptionHandlerType.Finally)
                            foundHandlerEnd = true;
                        break;
                    case CilCode.Leave:
                    case CilCode.Leave_S:
                        if (possibleHandlerEnd.Operand is ICilLabel target &&
                            target.Offset >= exceptionHandler.HandlerStart?.Offset)
                            foundHandlerEnd = true;
                        break;
                    case CilCode.Ret:
                        // this shouldn't happen, but this makes the handler end set on the ret instruction instead of one after it
                        if (handlerEndIndex == vmMethod.Instructions.Count - 1)
                            handlerEndIndex--;
                        foundHandlerEnd = true;
                        break;
                    case CilCode.Rethrow:
                    case CilCode.Throw:
                        foundHandlerEnd = true;
                        break;
                }

                handlerEndIndex++;
            }

            exceptionHandler.HandlerEnd = vmMethod.Instructions[handlerEndIndex].CreateLabel();

            exceptionHandler.TryStart = vmMethod.Instructions.GetByOffset(virtualOffsets[vmExceptionHandler.TryStart])?.CreateLabel();

            // TryEnd is equal to TryStart + TryLength + 1
            var tryEndIndex = vmMethod
                .Instructions.GetIndexByOffset(
                    virtualOffsets[vmExceptionHandler.TryStart + vmExceptionHandler.TryLength]);
            exceptionHandler.TryEnd = vmMethod
                .Instructions[tryEndIndex + (vmMethod.Instructions.Count - 2 >= tryEndIndex ? 1 : 0)].CreateLabel();

            if (vmExceptionHandler.HandlerType == CilExceptionHandlerType.Filter)
                exceptionHandler.FilterStart = vmMethod.Instructions.GetByOffset(virtualOffsets[vmExceptionHandler.FilterStart])?.CreateLabel();
            
            vmMethod.ExceptionHandlers.Add(exceptionHandler);
        }
    }

    private object? ReadOperand(VMOpCode vmOpCode, VMMethod vmMethod)
    {
        if (vmOpCode.CilOperandType == CilOperandType.InlineI && vmOpCode.CilOpCode?.OperandType is
            CilOperandType.InlineTok or CilOperandType.InlineType or CilOperandType.InlineField or CilOperandType.InlineMethod or CilOperandType.InlineString)
            return ReadInlineTok(vmOpCode);
        return
        vmOpCode.CilOperandType switch // maybe switch this to vmOpCode.CilOpCode.OperandType and add more handlers
        {
            CilOperandType.InlineI => VMStreamReader.ReadInt32Special(),
            CilOperandType.ShortInlineI => VMStreamReader.ReadSByte(),
            CilOperandType.InlineI8 => VMStreamReader.ReadInt64(),
            CilOperandType.InlineR => VMStreamReader.ReadDouble(),
            CilOperandType.ShortInlineR => VMStreamReader.ReadSingle(),
            CilOperandType.InlineVar => VMStreamReader.ReadUInt16(),    // IsInlineArgument(vmOpCode.CilOpCode) ? GetArgument(vmMethod, VMStreamReader.ReadUInt16()) : GetLocal(vmMethod, VMStreamReader.ReadUInt16()),
            CilOperandType.ShortInlineVar => VMStreamReader.ReadByte(), // IsInlineArgument(vmOpCode.CilOpCode) ? GetArgument(vmMethod, VMStreamReader.ReadByte()) : GetLocal(vmMethod, VMStreamReader.ReadByte()),
            CilOperandType.InlineTok => ReadInlineTok(vmOpCode),
            CilOperandType.InlineSwitch => ReadInlineSwitch(),
            CilOperandType.InlineBrTarget => VMStreamReader.ReadUInt32(),
            CilOperandType.InlineArgument => VMStreamReader.ReadUInt16(),    // GetArgument(vmMethod, VMStreamReader.ReadUInt16()),  // this doesn't seem to be used, might not be correct
            CilOperandType.ShortInlineArgument => VMStreamReader.ReadByte(), // GetArgument(vmMethod, VMStreamReader.ReadByte()),    // this doesn't seem to be used, might not be correct
            CilOperandType.InlineNone => null,
            _ => throw new NotSupportedException("Unsupported VM operand: " + vmOpCode.CilOperandType)
        };
    }

    private object? ReadSpecialOperand(VMOpCode vmOpCode, VMMethod vmMethod) =>
        vmOpCode.SpecialOpCode switch
        {
            SpecialOpCodes.EazCall => Resolver.ResolveEazCall(VMStreamReader.ReadInt32Special()),
            SpecialOpCodes.StartHomomorphic => ReadHomomorphicEncryption(vmMethod),
            _ => null
        };

    /// <summary>
    /// Resolves special opcodes with no CIL opcode.
    /// </summary>
    /// <param name="vmOpCode"></param>
    /// <param name="vmMethod"></param>
    /// <returns>
    /// A CIL opcode that matches the special opcode.
    /// TODO: maybe a list of op codes?
    /// </returns>
    private static CilOpCode? ResolveSpecialCilOpCode(VMOpCode vmOpCode, VMMethod vmMethod)
    {
        switch (vmOpCode.SpecialOpCode)
        {
            // case SpecialOpCodes.Stind:
            // case SpecialOpCodes.StartHomomorphic:
            case SpecialOpCodes.NoBody:
                // TODO: Analyze vm method instructions / stack to determine CIL opcode (2 opcode handlers have this pattern)
                return CilOpCodes.Nop;
        }

        return CilOpCodes.Nop;
    }

    /// <summary>
    /// Processes homomorphic encryption data into CIL instructions 
    /// </summary>
    /// <param name="vmMethod"></param>
    /// <returns>
    /// branch offset
    /// </returns>
    private int? ReadHomomorphicEncryption(VMMethod vmMethod)
    {
        Ctx.Console.Info($"[{vmMethod.Parent.MetadataToken}] Detected homomorphic encryption.");

        vmMethod.HasHomomorphicEncryption = true;
        return null;
    }

    private object? ReadInlineTok(VMOpCode vmOpCode) =>
        vmOpCode.CilOpCode?.OperandType switch
        {
            CilOperandType.InlineString => Resolver.ResolveString(VMStreamReader.ReadInt32Special()),
            _ => Resolver.ResolveToken(VMStreamReader.ReadInt32Special())
        };

    private int[] ReadInlineSwitch()
    {
        var destCount = VMStreamReader.ReadInt32Special();
        if (destCount < 0 || destCount > 1000000) throw new InvalidDataException("Invalid VM switch length.");
        var branchDests = new int[destCount];
        for (var i = 0; i < destCount; i++)
            branchDests[i] = VMStreamReader.ReadInt32Special();
        return branchDests;
    }

#pragma warning disable CS8618
    public MethodDevirtualizer(DevirtualizationContext ctx) : base(ctx)
    {
    }
#pragma warning restore CS8618
}
