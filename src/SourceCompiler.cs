using System.Threading;
using System.Xml.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using Newtonsoft.Json;

namespace Unpacker;

internal static class SourceCompiler
{
    internal static int Run(string assembly,string output,string report,string originalInput)
    {
        assembly=Path.GetFullPath(assembly);output=Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        using var pe=new PEFile(assembly);
        var resolver=new UniversalAssemblyResolver(assembly,false,pe.DetectTargetFrameworkId());
        var searchDirectories=DependencyDirectories(assembly,originalInput);
        foreach(string directory in searchDirectories)resolver.AddSearchDirectory(directory);
        var settings=new DecompilerSettings {UseSdkStyleProjectFormat=true,UseDebugSymbols=true,
            ThrowOnAssemblyResolveErrors=false,RemoveDeadCode=true,RemoveDeadStores=true,AsyncAwait=true,YieldReturn=false,
            AnonymousMethods=false,LocalFunctions=false,InitAccessors=false,NamedArguments=false,NonTrailingNamedArguments=false,OptionalArguments=false};
        var pdb=Path.ChangeExtension(assembly,".pdb");
        var data=new SourceDataPlan(assembly);
        var readability=new ReadabilityStats();
        var decompiler=new RecoveredProjectDecompiler(settings,resolver,File.Exists(pdb)?new RecoveredDebugInfo(pdb):null,data,readability){MaxDegreeOfParallelism=4};
        decompiler.ProgressIndicator=new DecompilerProgress();
        ProgressEvents.Send(6,"info","反编译类型/方法并导出工程文件");
        decompiler.DecompileProject(pe,output,CancellationToken.None);
        ProgressEvents.Send(6,"info","清理字面量条件、整理异步兼容文件与 RVA 数据");
        SourceReadability.Finish(output,readability);
        data.WriteReport(Path.Combine(output,"field-data.json"));
        if(decompiler.Errors.Count>0)
        {
            File.WriteAllText(report,JsonConvert.SerializeObject(new{Status="failed",Errors=decompiler.Errors.Select(e=>e.ToString())},Formatting.Indented));
            throw new InvalidOperationException("Decompiler failed to export complete method bodies; see "+report);
        }
        string project=Directory.GetFiles(output,"*.csproj").Single();
        ProgressEvents.Send(6,"info","导出原始资源、引用依赖与模块初始化器");
        File.WriteAllText(Path.Combine(output,"UnpackExceptionCompatibility.cs"),ExceptionCompatibility.HelperSource);
        File.WriteAllText(Path.Combine(output,"UnpackMetadataCompatibility.cs"),MetadataCompatibility.HelperSource);
        var xml=XDocument.Load(project);
        var properties=new XElement("PropertyGroup",new XElement("LangVersion","latest"),new XElement("GenerateAssemblyInfo","false"),
            new XElement("GenerateTargetFrameworkAttribute","false"),new XElement("EnableDefaultCompileItems","true"),
            new XElement("EnableDefaultEmbeddedResourceItems","false"),new XElement("EnableDefaultPageItems","false"),new XElement("EnableDefaultApplicationDefinition","false"),new XElement("AllowUnsafeBlocks","true"),new XElement("Nullable","disable"),
            new XElement("UseSharedCompilation","false"),
            new XElement("EnableUnsafeBinaryFormatterSerialization","true"),new XElement("GenerateResourceUsePreserializedResources","true"));
        xml.Root!.Add(properties);
        int duplicateResources=0;
        foreach(var duplicate in xml.Descendants("EmbeddedResource").GroupBy(r=>(string?)r.Attribute("LogicalName")??(string?)r.Attribute("Include")).SelectMany(g=>g.Skip(1)).ToArray())
        {duplicate.Remove();duplicateResources++;}
        // ILSpy may export .resources as resx and rely on SDK default items.
        // Export every original resource explicitly as raw bytes instead: this
        // also preserves opaque/encrypted payloads and avoids lossy resx rebuilds.
        xml.Descendants("EmbeddedResource").Remove();
        // WholeProjectDecompiler also expands WPF .g.resources into Resource/Page
        // items. Rebuilding those alongside the raw original duplicates the manifest
        // name and may change BAML bytes. Keep the original resource container only.
        xml.Descendants("Resource").Remove();
        xml.Descendants("Page").Remove();
        xml.Descendants("ApplicationDefinition").Remove();
        var resourceItems=new XElement("ItemGroup");xml.Root.Add(resourceItems);
        var resourceMap=new List<object>();
        var resourceNames=new List<object>();
        using(var module=dnlib.DotNet.ModuleDefMD.Load(assembly))
        {
            string resourceDirectory="UnpackOriginalResources";
            while(Directory.Exists(Path.Combine(output,resourceDirectory)))resourceDirectory+="_";
            Directory.CreateDirectory(Path.Combine(output,resourceDirectory));
            int index=0;
            var occupied=new HashSet<string>(module.Resources.Select(r=>r.Name.String),StringComparer.Ordinal);
            foreach(var group in module.Resources.GroupBy(r=>r.Name.String))
            {
                if(group.First() is not dnlib.DotNet.EmbeddedResource resource)throw new NotSupportedException("External/linked resource needs explicit export: "+group.Key);
                string file=resourceDirectory+"/resource_"+(index++).ToString("D4")+".bin";
                byte[] bytes=resource.CreateReader().ToArray();File.WriteAllBytes(Path.Combine(output,file),bytes);
                string name=resource.Name.String;
                if(string.IsNullOrWhiteSpace(name))
                {
                    string marker=SourceResourceNames.Marker(name,occupied);resourceNames.Add(new {Original=name,Placeholder=marker});name=marker;
                }
                resourceItems.Add(new XElement("EmbeddedResource",new XAttribute("Include",file),new XAttribute("LogicalName",name)));
                resourceMap.Add(new {Name=resource.Name.String,File=file,Bytes=bytes.Length});
            }
        }
        File.WriteAllText(Path.Combine(output,"embedded-resources.json"),JsonConvert.SerializeObject(resourceMap,Formatting.Indented));
        SourceResourceNames.Configure(xml,output,resourceNames);
        bool needsUnsafe=Directory.EnumerateFiles(output,"*.cs",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f).Contains("System.Runtime.CompilerServices.Unsafe."));
        if(needsUnsafe && xml.Descendants("TargetFramework").First().Value.StartsWith("net4"))
        {
            xml.Descendants("TargetFramework").First().Value="net48";
            xml.Root.Add(new XElement("ItemGroup",new XElement("PackageReference",new XAttribute("Include","System.Runtime.CompilerServices.Unsafe"),new XAttribute("Version","6.0.0"))));
        }
        int initializerCalls=ExportModuleInitializer(assembly,output,xml.Descendants("TargetFramework").First().Value.StartsWith("net4"));
        foreach(var reference in xml.Descendants("Reference").ToArray())
        {
            string name=((string?)reference.Attribute("Include")??"").Split(',')[0];
            string? dependency=searchDirectories.SelectMany(directory=>new[]{Path.Combine(directory,name+".dll"),Path.Combine(directory,name+".exe")}).FirstOrDefault(File.Exists);
            if(dependency==null && ((string?)reference.Element("HintPath"))?.IndexOf("dotnet\\shared\\",StringComparison.OrdinalIgnoreCase)>=0)
            {reference.Remove();continue;}
            if(dependency==null)continue;
            string references=Path.Combine(output,"references");Directory.CreateDirectory(references);
            string fileName=Path.GetFileName(dependency);
            File.Copy(dependency,Path.Combine(references,fileName),true);
            reference.Elements("HintPath").Remove();reference.Add(new XElement("HintPath","references/"+fileName));
        }
        if(xml.Descendants("TargetFramework").First().Value.StartsWith("net4")
            && (xml.Descendants("OutputType").FirstOrDefault()?.Value??"Library")!="Library")
        {
            string runtimeConfig=Path.Combine(output,"UnpackRuntime.config");
            RuntimeCompatibility.WriteConfiguration(originalInput+".config","",runtimeConfig);
            xml.Descendants("ApplicationConfig").Remove();
            properties.Add(new XElement("AppConfig","UnpackRuntime.config"));
        }
        xml.Save(project);
        ProgressEvents.Send(6,"success","源码导出完成："+Directory.GetFiles(output,"*.cs",SearchOption.AllDirectories).Length+" 个 C# 文件；"+resourceMap.Count+" 个资源；"+readability.AsyncMethods+" 个 async 方法");
        ProgressEvents.Send(7,"start","dotnet build Release；完整编译日志："+Path.Combine(output,"build.log"));
        string dotnet=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");
        string previous=Environment.GetEnvironmentVariable("MSBuildEnableWorkloadResolver")??"";
        Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver","false");
        int code;
        int warnings=0,errors=0;
        void BuildLine(string line)
        {
            string message=line.Trim();
            int diagnostic=Math.Max(line.IndexOf(": warning ",StringComparison.Ordinal),line.IndexOf(": error ",StringComparison.Ordinal));
            if(diagnostic>=0){
                int directory=line.LastIndexOfAny(new[]{'\\','/'},diagnostic);
                if(directory>=0)message=line.Substring(directory+1);
                int projectSuffix=message.LastIndexOf(" [",StringComparison.Ordinal);
                if(projectSuffix>=0)message=message.Substring(0,projectSuffix);
            }
            if(message.Length>600)message=message.Substring(0,600)+"…";
            if(line.Contains(": warning ")){if(++warnings<=3)ProgressEvents.Send(7,"warning",message);}
            else if(line.Contains(": error ")){if(++errors<=8)ProgressEvents.Send(7,"error",message);}
            else if(line.Contains("->") || line.Contains("正在确定要还原") || line.Contains("Restored ") || line.Contains("已还原 "))ProgressEvents.Send(7,"info",message.Trim());
        }
        try {code=ProcessRunner.Run(dotnet,new[]{"build",project,"-c","Release","--nologo","-v:minimal"},Path.Combine(output,"build.log"),900,output:BuildLine);}
        finally{Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver",previous);}
        if(warnings>3)ProgressEvents.Send(7,"warning","其余编译警告保存在 build.log（含构建汇总中的重复行）");
        ProgressEvents.Send(7,code==0?"success":"error","C# 编译退出码 "+code+"；日志："+Path.Combine(output,"build.log"));
        string assemblyName=xml.Descendants("AssemblyName").First().Value;
        string framework=xml.Descendants("TargetFramework").First().Value;
        string binaryName=assemblyName+(framework.StartsWith("net4") && (xml.Descendants("OutputType").FirstOrDefault()?.Value??"Library")!="Library"?".exe":".dll");
        string rebuilt=code==0?Directory.GetFiles(Path.Combine(output,"bin","Release"),binaryName,SearchOption.AllDirectories).Single():"";
        if(code==0){ProgressEvents.Send(7,"info","逐项核对重编译资源名称与原始字节");RebuiltValidation.VerifyResources(assembly,rebuilt,Path.Combine(output,"resource-validation.json"));}
        string appHost=Path.ChangeExtension(rebuilt,".exe");
        File.WriteAllText(report,JsonConvert.SerializeObject(new{Status=code==0?"success":"failed",Project=project,ExitCode=code,RebuiltAssembly=rebuilt,AssemblyName=assemblyName,AppHost=code==0 && !framework.StartsWith("net4") && File.Exists(appHost)?appHost:"",DecompilerErrors=decompiler.Errors.Count,DuplicateResourceNames=duplicateResources,ModuleInitializerCalls=initializerCalls,Log=Path.Combine(output,"build.log")},Formatting.Indented));
        Console.WriteLine("C# build exit "+code+": "+project);
        return code==0?0:1;
    }

    internal static string[] DependencyDirectories(string assembly,string originalInput)
    {
        string originalDirectory=Path.GetDirectoryName(Path.GetFullPath(originalInput))!;
        var directories=new List<string>{Path.GetDirectoryName(Path.GetFullPath(assembly))!,originalDirectory};
        string config=originalInput+".config";
        if(File.Exists(config))
        {
            // Read data only; do not activate AppDomain managers or config extensions.
            var settings=new System.Xml.XmlReaderSettings {DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null};
            using var reader=System.Xml.XmlReader.Create(config,settings);
            var xml=XDocument.Load(reader);
            XNamespace ns="urn:schemas-microsoft-com:asm.v1";
            foreach(string path in xml.Descendants(ns+"probing").SelectMany(p=>((string?)p.Attribute("privatePath")??"").Split(';')))
            {
                if(string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))continue;
                string directory=Path.GetFullPath(Path.Combine(originalDirectory,path.Trim()));
                if(directory.StartsWith(originalDirectory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(directory))directories.Add(directory);
            }
        }
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed class DecompilerProgress : IProgress<DecompilationProgress>
    {
        private readonly ProgressEvents.Counter counter=new(6);
        public void Report(DecompilationProgress value)=>counter.Report("源码导出"+(string.IsNullOrEmpty(value.Status)?"":"："+value.Status),value.UnitsCompleted,value.TotalUnits);
    }

    private static int ExportModuleInitializer(string assembly,string output,bool framework)
    {
        using var module=dnlib.DotNet.ModuleDefMD.Load(assembly);
        var global=module.GlobalType;
        if(global==null)return 0;
        if(global.Fields.Count!=0 || global.Methods.Any(m=>!m.IsStaticConstructor))
            throw new NotSupportedException("Global fields or methods require explicit C# export; refusing to omit them.");
        var initializer=global.FindStaticConstructor();
        if(initializer==null)return 0;
        var calls=new List<string>();
        var instructions=initializer.Body?.Instructions ?? throw new NotSupportedException("Module initializer has no IL body.");
        if(initializer.Body!.ExceptionHandlers.Count!=0 || initializer.Body.Variables.Count!=0
            || instructions.Count==0 || instructions.Last().OpCode.Code!=dnlib.DotNet.Emit.Code.Ret)
            throw new NotSupportedException("Complex module initializer requires explicit C# export.");
        foreach(var instruction in instructions.Take(instructions.Count-1))
        {
            if(instruction.OpCode.Code==dnlib.DotNet.Emit.Code.Nop)continue;
            if(instruction.OpCode.Code!=dnlib.DotNet.Emit.Code.Call || instruction.Operand is not dnlib.DotNet.MethodDef method
                || !method.IsStatic || method.MethodSig.Params.Count!=0 || method.MethodSig.RetType.ElementType!=dnlib.DotNet.ElementType.Void
                || method.HasGenericParameters || method.DeclaringType.HasGenericParameters)
                throw new NotSupportedException("Unsupported module initialization instruction: "+instruction);
            calls.Add("        global::"+method.DeclaringType.ReflectionFullName.Replace('+','.')+"."+method.Name+"();");
        }
        if(calls.Count==0)return 0;
        string source="internal static class UnpackModuleInitializer\n{\n    [System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Initialize()\n    {\n"+string.Join("\n",calls)+"\n    }\n}\n";
        if(framework)source+="namespace System.Runtime.CompilerServices\n{\n    [global::System.AttributeUsage(global::System.AttributeTargets.Method)]\n    internal sealed class ModuleInitializerAttribute : global::System.Attribute { }\n}\n";
        File.WriteAllText(Path.Combine(output,"UnpackModuleInitializer.cs"),source);
        return calls.Count;
    }
}
