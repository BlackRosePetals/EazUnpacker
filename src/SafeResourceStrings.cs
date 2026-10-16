using System.Resources;
using System.Text;

namespace Unpacker;

internal static class SafeResourceStrings
{
    internal static ResourceReader Open(byte[] bytes)
    {
        // Extended .resources use the same raw resource table, but ResourceReader
        // rejects their reader-name header. Normalize that header in a copy only;
        // its fixed byte length keeps every table/name/data offset unchanged.
        using(var reader=new BinaryReader(new MemoryStream(bytes),Encoding.UTF8))
        {
            if(reader.ReadUInt32()==0xBEEFCACE && reader.ReadInt32()==1)
            {
                int size=reader.ReadInt32();
                if(size<0 || size>bytes.Length-12)throw new InvalidDataException("Invalid resource header size.");
                string readerName=reader.ReadString(),setName=reader.ReadString();
                if(readerName.StartsWith("System.Resources.Extensions.DeserializingResourceReader",StringComparison.Ordinal))
                {
                    byte[] Header(string resourceSet)
                    {
                        using var data=new MemoryStream();using(var writer=new BinaryWriter(data,Encoding.UTF8,true))
                        {writer.Write(typeof(ResourceReader).AssemblyQualifiedName!);writer.Write(resourceSet);}
                        return data.ToArray();
                    }
                    var header=Header(setName);int gap=size-header.Length;
                    if(gap<0)throw new InvalidDataException("Cannot normalize resource reader without changing table offsets.");
                    // Framework ResourceReader consumes both strings directly rather
                    // than seeking past spare header bytes. Pad the unused set-name
                    // string, including its 7-bit length prefix, to the exact size.
                    for(int padding=Math.Max(0,gap-5);padding<=gap+5 && header.Length!=size;padding++)header=Header(setName+new string(' ',padding));
                    if(header.Length!=size)throw new InvalidDataException("Cannot preserve resource header alignment.");
                    var copy=(byte[])bytes.Clone();Array.Copy(header,0,copy,12,header.Length);bytes=copy;
                }
            }
        }
        return new ResourceReader(new MemoryStream(bytes,false));
    }
}
