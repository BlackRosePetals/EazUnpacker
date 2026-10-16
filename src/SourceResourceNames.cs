using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace Unpacker;

internal static class SourceResourceNames
{
    internal static string Marker(string original,HashSet<string> occupied)
    {
        int length=Encoding.UTF8.GetByteCount(original);
        for(int index=0;index<4096;index++)
        {
            string number=index.ToString("X");
            string candidate=length==1?("@!#$&()*+-.:=?[]^~"[index%17]).ToString():new string('@',Math.Max(1,length-number.Length))+number;
            if(Encoding.UTF8.GetByteCount(candidate)==length && occupied.Add(candidate))return candidate;
        }
        throw new NotSupportedException("Cannot allocate equal-byte-length resource name marker.");
    }

    internal static void Configure(XDocument project,string output,List<object> map)
    {
        if(map.Count==0)return;
        File.WriteAllText(Path.Combine(output,"UnpackResourceNames.json"),JsonConvert.SerializeObject(map,Formatting.Indented));
        File.WriteAllText(Path.Combine(output,"UnpackResourceNames.ps1"),Script,new UTF8Encoding(true));
        project.Root!.Add(new XElement("Target",new XAttribute("Name","RestoreOpaqueResourceNames"),new XAttribute("AfterTargets","CoreCompile"),
            new XAttribute("Condition","'$(DesignTimeBuild)' != 'true'"),
            new XElement("Exec",new XAttribute("Command","powershell.exe -NoProfile -NonInteractive -File &quot;$(MSBuildProjectDirectory)/UnpackResourceNames.ps1&quot; -Assembly &quot;@(IntermediateAssembly->'%(FullPath)')&quot; -Map &quot;$(MSBuildProjectDirectory)/UnpackResourceNames.json&quot;".Replace("&quot;","\"")))));
    }

    // Roslyn treats an all-whitespace LogicalName as empty. The build emits a
    // unique equal-byte-length marker, then restores ONLY that #Strings entry.
    // No method, metadata row, token, layout, resource payload or PDB is rebuilt.
    private const string Script="""
param([Parameter(Mandatory=$true)][string]$Assembly,[Parameter(Mandatory=$true)][string]$Map)
$ErrorActionPreference='Stop'
$bytes=[IO.File]::ReadAllBytes($Assembly)
function U16([int]$offset){[BitConverter]::ToUInt16($bytes,$offset)}
function U32([int]$offset){[BitConverter]::ToUInt32($bytes,$offset)}
$pe=[int](U32 60)
if((U32 $pe) -ne 17744){throw 'Invalid PE signature'}
$sections=U16 ($pe+6);$optional=$pe+24;$magic=U16 $optional
$directory=if($magic -eq 267){$optional+96}elseif($magic -eq 523){$optional+112}else{throw 'Unsupported PE optional header'}
$table=$optional+(U16 ($pe+20))
function FileOffset([uint32]$rva){
    for($i=0;$i -lt $sections;$i++){
        $section=$table+$i*40;$start=U32 ($section+12)
        $size=[Math]::Max((U32 ($section+8)),(U32 ($section+16)))
        if($rva -ge $start -and $rva -lt ([long]$start+$size)){return [int]((U32 ($section+20))+$rva-$start)}
    }
    throw 'RVA outside PE sections'
}
$clr=FileOffset (U32 ($directory+112));$metadata=FileOffset (U32 ($clr+8))
if((U32 $metadata) -ne 1112167234){throw 'Invalid CLR metadata signature'}
$cursor=[int](($metadata+16+(U32 ($metadata+12))+3) -band -4)
$count=U16 ($cursor+2);$cursor+=4;$strings=-1;$size=0
for($i=0;$i -lt $count;$i++){
    $offset=U32 $cursor;$length=U32 ($cursor+4);$name=$cursor+8;$end=$name
    while($bytes[$end] -ne 0){$end++}
    if([Text.Encoding]::ASCII.GetString($bytes,$name,$end-$name) -eq '#Strings'){$strings=$metadata+$offset;$size=$length}
    $cursor=[int](($end+4) -band -4)
}
if($strings -lt 0){throw 'Missing #Strings heap'}
$text=[Text.Encoding]::UTF8.GetString($bytes,$strings,$size)
foreach($entry in @(Get-Content -LiteralPath $Map -Raw -Encoding UTF8|ConvertFrom-Json)){
    $needle=[char]0+$entry.Placeholder+[char]0
    $position=$text.IndexOf($needle,[StringComparison]::Ordinal)
    if($position -lt 0){
        if($text.IndexOf(([char]0+$entry.Original+[char]0),[StringComparison]::Ordinal) -lt 0){throw 'Expected resource name is missing'}
        continue
    }
    if($text.IndexOf($needle,$position+1,[StringComparison]::Ordinal) -ge 0){throw 'Ambiguous resource name marker'}
    $old=[Text.Encoding]::UTF8.GetBytes($entry.Placeholder);$value=[Text.Encoding]::UTF8.GetBytes($entry.Original)
    if($old.Length -ne $value.Length){throw 'Resource name length differs'}
    $offset=$strings+[Text.Encoding]::UTF8.GetByteCount($text.Substring(0,$position+1))
    [Buffer]::BlockCopy($value,0,$bytes,$offset,$value.Length)
}
[IO.File]::WriteAllBytes($Assembly,$bytes)
""";
}
