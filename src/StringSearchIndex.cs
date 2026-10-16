using System.Diagnostics;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker;

// Search-only dependency artifacts. Never deploy these copies or deserialize
// custom resource objects. The real executable and dependency files stay intact.
internal static class StringSearchIndex
{
    internal static int Run(string recovered,string original,string directory)
    {
        Directory.CreateDirectory(directory);
        string copies=Path.Combine(directory,"assemblies");Directory.CreateDirectory(copies);
        var paths=SourceCompiler.DependencyDirectories(recovered,original);
        var queue=new Queue<(string Origin,string Analysis)>();queue.Enqueue((original,recovered));
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules=new List<object>();var errors=new List<string>();int rows=0,decoded=0,remainingCalls=0;
        var elapsed=Stopwatch.StartNew();
        using(var json=new StreamWriter(Path.Combine(directory,"strings.jsonl"),false,new System.Text.UTF8Encoding(false)))
        using(var text=new StreamWriter(Path.Combine(directory,"strings.txt"),false,new System.Text.UTF8Encoding(true)))
        {
            void Add(string origin,string analysis,string kind,string owner,string token,long offset,string key,string value)
            {
                json.WriteLine(JsonConvert.SerializeObject(new{Module=Path.GetFileName(origin),SourcePath=origin,AnalysisAssembly=analysis,Kind=kind,Owner=owner,Token=token,Offset=offset,Key=key,Value=value}));
                text.WriteLine(Path.GetFileName(origin)+" | "+kind+" | "+token+" | "+owner+" | "+key+" | "+value.Replace("\r","\\r").Replace("\n","\\n"));rows++;
            }
            while(queue.Count>0 && seen.Count<256 && elapsed.Elapsed.TotalSeconds<180)
            {
                var item=queue.Dequeue();if(!seen.Add(Path.GetFullPath(item.Origin)))continue;
                try
                {
                    string before=Hash(item.Origin),analysis=item.Analysis,status="static";int strings=0,resources=0,unresolved=0;
                    var remainingSites=new List<object>();
                    using(var probe=ModuleDefMD.Load(analysis))
                    {
                        bool encrypted=EazFixer.StringFixUtils.FindStringDecryptMethod(probe)!=null;
                        if(item.Origin!=original && encrypted)
                        {
                            if(decoded>=32){errors.Add("Decoder budget reached: "+item.Origin);status="encrypted-not-decoded";}
                            else
                            {
                                string name=seen.Count.ToString("D3")+"-"+Path.GetFileName(item.Origin);
                                string candidate=Path.Combine(copies,name),report=candidate+".json";decoded++;
                                ProgressEvents.Send(6,"info","字符串索引：恢复依赖 "+Path.GetFileName(item.Origin)+"（仅搜索副本）");
                                int code=ProcessRunner.Run(Assembly.GetExecutingAssembly().Location,new[]{"--index-strings",item.Origin,candidate,report,original},candidate+".log",Math.Max(1,Math.Min(40,180-(int)elapsed.Elapsed.TotalSeconds)));
                                if(code==0 && File.Exists(candidate)){analysis=candidate;status="decoded-search-copy";}
                                else {errors.Add("Decoder exit "+code+": "+item.Origin+"; see "+report);status="decode-failed";}
                            }
                        }
                    }
                    using(var module=ModuleDefMD.Load(analysis))
                    {
                        foreach(var reference in module.GetAssemblyRefs())Enqueue(reference.Name.String);
                        var decoder=EazFixer.StringFixUtils.FindStringDecryptMethod(module);
                        foreach(var method in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody))
                        foreach(var instruction in method.Body.Instructions)
                        {
                            if(decoder!=null && instruction.Operand is IMethod called && called.FullName==decoder.FullName && instruction.OpCode.Code is Code.Call or Code.Callvirt)
                            {
                                unresolved++;
                                int position=method.Body.Instructions.IndexOf(instruction);
                                var previous=position>0?method.Body.Instructions[position-1]:null;
                                remainingSites.Add(new{Method="0x"+method.MDToken.Raw.ToString("X8"),Offset=instruction.Offset,
                                    Argument=previous?.IsLdcI4()==true?(int?)previous.GetLdcI4Value():null,
                                    Reason=previous?.IsLdcI4()==true?"constant call retained by string backend (including protected constructor dispatch)":"nonliteral argument; not guessed"});
                            }
                            if(instruction.OpCode.Code!=Code.Ldstr || instruction.Operand is not string value)continue;
                            Add(item.Origin,analysis,"ldstr",method.FullName,"0x"+method.MDToken.Raw.ToString("X8"),instruction.Offset,"",value);strings++;
                            if(value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||value.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))Enqueue(value);
                        }
                        foreach(var resource in module.Resources.OfType<EmbeddedResource>().Where(r=>r.Name.String.EndsWith(".resources",StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                using var reader=SafeResourceStrings.Open(resource.CreateReader().ToArray());
                                var entries=reader.GetEnumerator();while(entries.MoveNext())
                                {
                                    string key=(string)entries.Key;reader.GetResourceData(key,out string type,out byte[] data);
                                    if(type!="ResourceTypeCode.String")continue;
                                    using var binary=new BinaryReader(new MemoryStream(data));
                                    Add(item.Origin,analysis,"resource",resource.Name.String,"",0,key,binary.ReadString());resources++;
                                }
                            }catch(Exception ex){errors.Add("Resource index: "+item.Origin+" / "+resource.Name+": "+ex.Message);}
                        }
                    }
                    bool unchanged=before==Hash(item.Origin);
                    if(!unchanged)throw new IOException("Input changed during index: "+item.Origin);
                    modules.Add(new{Source=item.Origin,AnalysisAssembly=analysis,Status=status,Strings=strings,ResourceStrings=resources,RemainingDecoderCalls=unresolved,RemainingDecoderSites=remainingSites,InputSha256=before,InputUnchanged=unchanged});
                    remainingCalls+=unresolved;
                }
                catch(Exception ex){errors.Add(item.Origin+": "+ex.Message);}
            }
            void Enqueue(string name)
            {
                if(string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return;
                bool fileName=name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase);
                string[] names=fileName?new[]{name}:new[]{name+".dll",name+".exe"};
                foreach(string path in paths.SelectMany(p=>names.Select(n=>Path.Combine(p,n))))
                {
                    if(!File.Exists(path))continue;
                    if(!seen.Contains(Path.GetFullPath(path)))queue.Enqueue((path,path));
                    break;
                }
            }
        }
        if(queue.Count>0)errors.Add("Index budget reached; queued modules: "+queue.Count);
        File.WriteAllText(Path.Combine(directory,"index.json"),JsonConvert.SerializeObject(new{Status=errors.Count==0?"complete":"partial",StringRecoveryStatus=remainingCalls==0?"no-detected-decoder-calls":"partial-retained-calls",RemainingDecoderCalls=remainingCalls,Strings=rows,Modules=modules,Errors=errors,Note="Index completion is not proof of complete deobfuscation. Search-only dependency copies, original files unchanged. Remaining decoder calls are reported, not guessed. No license/command changes."},Formatting.Indented));
        ProgressEvents.Send(6,errors.Count==0?"success":"warning","字符串索引："+rows+" 项；"+modules.Count+" 个模块；保留 "+remainingCalls+" 个解码调用；"+errors.Count+" 个索引错误；"+Path.Combine(directory,"strings.txt"));
        return errors.Count==0?0:2;
    }
    private static string Hash(string path){using var sha=SHA256.Create();using var file=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
}
