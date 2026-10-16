using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

// DNGuard Lab-inspired policy: evidence first, stable numbered fallback otherwise.
// These are generated role names, never a claim to recover destroyed author names.
internal sealed class ReadableNameHints
{
    internal sealed class Hint
    {
        internal string Name="", Evidence="";
    }
    private readonly Dictionary<object,Dictionary<string,string>> hints=new();
    internal readonly HashSet<MethodDef> EventHandlers=new();

    internal Hint? Get(object member)
    {
        if(!hints.TryGetValue(member,out var values) || values.Count!=1)return null;
        return new Hint{Name=values.Keys.Single(),Evidence=values.Values.Single()};
    }
    private void Add(object member,string name,string evidence)
    {
        if(!Regex.IsMatch(name,@"^[A-Za-z_][A-Za-z0-9_]*$") || NameRecoveryPass.NeedsName(name))return;
        if(!hints.TryGetValue(member,out var values))hints[member]=values=new Dictionary<string,string>();
        values[name]=evidence+"; inferred-role; original-name-unavailable";
    }

    internal ReadableNameHints(ModuleDefMD module,Func<MemberRef,FieldDef?> ownField,Func<MemberRef,MethodDef?> ownMethod)
    {
        FieldDef? Field(object? operand)=>operand switch {
            FieldDef field when field.Module==module=>field,
            MemberRef {IsFieldRef:true} reference=>ownField(reference),_=>null};
        MethodDef? Method(object? operand)=>operand switch {MethodDef method when method.Module==module=>method,
            MemberRef {IsMethodRef:true} reference=>ownMethod(reference),_=>null};
        foreach(var method in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody))
        {
            var il=method.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
            for(int i=2;i<il.Length;i++)
            {
                if(il[i].Operand is not IMethod called || il[i].OpCode.Code is not (Code.Call or Code.Callvirt))continue;
                if(called.Name=="set_Name" && called.MethodSig.Params.Count==1 && il[i-1].Operand is string name && il[i-1].OpCode.Code==Code.Ldstr
                    && called.DeclaringType.FullName.StartsWith("System.Windows.Forms.",StringComparison.Ordinal))
                {
                    if(il[i-2].OpCode.Code==Code.Ldfld && Field(il[i-2].Operand) is { } field)Add(field,name,"control.Name literal");
                    else if(il[i-2].IsLdarg() && il[i-2].GetParameter(method.Parameters)?.IsHiddenThisParameter==true
                        && method.DeclaringType.BaseType?.FullName is "System.Windows.Forms.Form" or "System.Windows.Forms.UserControl")
                        Add(method.DeclaringType,name,"this.Name literal on form/control");
                }
            }
        }
        foreach(var type in module.GetTypes())
        {
            foreach(var executor in type.Methods.Where(m=>m.HasBody && m.IsStatic==false && m.ReturnType.ElementType==ElementType.I4
                && m.MethodSig.Params.Count==2 && m.MethodSig.Params[0].ElementType==ElementType.String && m.MethodSig.Params[1].FullName=="System.IntPtr"))
            {
                var literals=executor.Body.Instructions.Where(i=>i.OpCode.Code==Code.Ldstr).Select(i=>(string)i.Operand).ToArray();
                if(!literals.Contains("--license-command") || !literals.Contains("--quiet"))continue;
                Add(executor,"ExecuteLicenseCommand","literal --license-command/--quiet plus int(string,IntPtr) signature");
                Add(executor.Parameters.Single(p=>p.MethodSigIndex==0),"command","license-command argument position");
                Add(executor.Parameters.Single(p=>p.MethodSigIndex==1),"ownerWindowHandle","IntPtr passed with --app-activation-hwnd");
                Add(type,"LicenseCommandService","contains evidenced external license-command executor; business logic retained");
                foreach(var wrapper in type.Methods.Where(m=>m.HasBody && m!=executor))
                {
                    var il=wrapper.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
                    var localCalls=il.Where(i=>i.OpCode.Code is Code.Call or Code.Callvirt).Select(i=>Method(i.Operand)).ToArray();
                    var commands=il.Where(i=>i.OpCode.Code==Code.Ldstr).Select(i=>(string)i.Operand).ToArray();
                    if(il.Length>16 || localCalls.Length!=2 || localCalls[0]!=executor || commands.Length!=1
                        || !Regex.IsMatch(commands[0],"^[A-Za-z0-9_]{1,16}$") || il.LastOrDefault()?.OpCode.Code!=Code.Ret)continue;
                    var handler=localCalls[1];
                    if(handler==null || handler.ReturnType.ElementType!=ElementType.Boolean || handler.MethodSig.Params.Count!=1
                        || handler.MethodSig.Params[0].ElementType!=ElementType.I4)continue;
                    Add(wrapper,"ExecuteLicenseCommand_"+commands[0],"literal subcommand forwarded to evidenced license-command executor; identifier is not ciphertext");
                    Add(handler,"ApplyLicenseCommandExitCode","consumes int result of evidenced license-command executor; business state updates retained");
                    foreach(var parameter in wrapper.Parameters.Where(p=>!p.IsHiddenThisParameter && !p.IsReturnTypeParameter && p.Type.FullName=="System.IntPtr"))
                        Add(parameter,"ownerWindowHandle","IntPtr forwarded to evidenced license-command executor");
                    Add(handler.Parameters.Single(p=>p.MethodSigIndex==0),"exitCode","exit code from evidenced license-command executor");
                }
            }
            foreach(var property in type.Properties.Where(p=>!NameRecoveryPass.NeedsName(p.Name)))
            {
                var getter=property.GetMethod;
                var il=getter?.Body?.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
                if(il?.Length==3 && il[0].IsLdarg() && il[1].OpCode.Code==Code.Ldfld && il[2].OpCode.Code==Code.Ret && Field(il[1].Operand) is { } field)
                    Add(field,"_"+char.ToLowerInvariant(property.Name.String[0])+property.Name.String.Substring(1),"direct property backing field");
            }
            foreach(var method in type.Methods)
            {
                if(method.HasImplMap && method.ImplMap!=null)Add(method,method.ImplMap.Name,"P/Invoke entry point");
                if(!method.HasBody)continue;
                var il=method.Body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
                var calls=il.Select(i=>i.Operand).OfType<IMethod>().ToArray();
                if(calls.Any(c=>c.Name=="SuspendLayout") && calls.Any(c=>c.Name=="ResumeLayout")
                    && il.Any(i=>i.OpCode.Code==Code.Newobj && i.Operand is IMethod c && c.DeclaringType.FullName.StartsWith("System.Windows.Forms.",StringComparison.Ordinal)))
                    Add(method,"InitializeComponent","control construction plus SuspendLayout/ResumeLayout");
                for(int i=3;i<il.Length;i++)
                {
                    if(il[i].Operand is not IMethod called || !called.Name.String.StartsWith("add_",StringComparison.Ordinal)
                        || !called.DeclaringType.FullName.StartsWith("System.Windows.Forms.",StringComparison.Ordinal)
                        || il[i-1].OpCode.Code!=Code.Newobj || il[i-2].OpCode.Code!=Code.Ldftn || Method(il[i-2].Operand) is not { } handler)continue;
                    string? receiver=null;
                    if(i>=4 && il[i-4].OpCode.Code==Code.Ldfld && Field(il[i-4].Operand) is { } field)receiver=Get(field)?.Name;
                    if(receiver==null && i>=4 && il[i-4].IsLdarg() && il[i-4].GetParameter(method.Parameters)?.IsHiddenThisParameter==true)receiver=Get(type)?.Name??"Form";
                    if(receiver!=null)
                    {Add(handler,receiver+"_"+called.Name.String.Substring(4),"direct event delegate registration");EventHandlers.Add(handler);}
                }
            }
        }
    }
}
