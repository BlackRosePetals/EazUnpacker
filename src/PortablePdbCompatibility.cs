using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Unpacker;

/// <summary>Publishes legal portable-symbol external table counts without renumbering IL tokens.</summary>
internal static class PortablePdbCompatibility
{
    internal static int Normalize(string assemblyPath, string pdbPath)
    {
        byte[] pdb=File.ReadAllBytes(pdbPath);
        if(pdb.Length<20 || BitConverter.ToUInt32(pdb,0)!=0x424a5342)return 0;
        int position=checked(16+BitConverter.ToInt32(pdb,12));
        position=checked((position+3)&~3);
        if(position<16 || position+4>pdb.Length)throw new InvalidDataException("Invalid portable PDB root.");
        int streams=BitConverter.ToUInt16(pdb,position+2);position+=4;int removed=0;
        for(int stream=0;stream<streams;stream++)
        {
            int header=position;
            if(position+8>pdb.Length)throw new InvalidDataException("Invalid portable PDB stream header.");
            int offset=BitConverter.ToInt32(pdb,position),size=BitConverter.ToInt32(pdb,position+4);position+=8;
            int start=position;while(position<pdb.Length && pdb[position]!=0)position++;
            if(position==pdb.Length)throw new InvalidDataException("Unterminated portable PDB stream name.");
            string name=Encoding.ASCII.GetString(pdb,start,position-start);position=checked((position+4)&~3);
            if(name!="#Pdb")continue;
            if(offset<0 || size<32 || (long)offset+size>pdb.Length)throw new InvalidDataException("Invalid #Pdb stream.");
            ulong mask=BitConverter.ToUInt64(pdb,offset+24),normalized=mask;
            int read=offset+32,write=read;
            for(int bit=0;bit<64;bit++)
            {
                ulong flag=1UL<<bit;if((mask&flag)==0)continue;
                if(read+4>offset+size)throw new InvalidDataException("Truncated external PDB table counts.");
                // CLI pointer tables are legal in PE #- metadata, but must not
                // be listed as external tables in a portable PDB's #Pdb stream.
                if(bit is 3 or 5 or 7 or 19 or 22){normalized&=~flag;removed++;}
                else {Array.Copy(pdb,read,pdb,write,4);write+=4;}
                read+=4;
            }
            if(removed==0)return 0;
            if(read!=offset+size)throw new InvalidDataException("Unexpected #Pdb trailing data.");
            Array.Copy(BitConverter.GetBytes(normalized),0,pdb,offset+24,8);
            Array.Copy(BitConverter.GetBytes(size-removed*4),0,pdb,header+4,4);
            Array.Clear(pdb,write,read-write);
            break;
        }
        if(removed==0)return 0;
        // ID and MethodDef row coordinates remain unchanged: the scopes/locals
        // are the same symbols. Refresh the PE's checksum for the corrected bytes.
        byte[] image=File.ReadAllBytes(assemblyPath);
        using(var pe=new PEReader(new MemoryStream(image,false)))
        foreach(var entry in pe.ReadDebugDirectory().Where(e=>e.Type==DebugDirectoryEntryType.PdbChecksum))
        {
            int at=entry.DataPointer,end=checked(at+entry.DataSize),separator=at;
            if(at<0 || end>image.Length)throw new InvalidDataException("Invalid PDB checksum directory.");
            while(separator<end && image[separator]!=0)separator++;
            string algorithm=Encoding.ASCII.GetString(image,at,separator-at);
            using HashAlgorithm hash=algorithm=="SHA256"?SHA256.Create():algorithm=="SHA1"?SHA1.Create():throw new InvalidDataException("Unsupported PDB checksum: "+algorithm);
            byte[] digest=hash.ComputeHash(pdb);
            if(separator+1+digest.Length!=end)throw new InvalidDataException("Invalid PDB checksum length.");
            Array.Copy(digest,0,image,separator+1,digest.Length);
        }
        File.WriteAllBytes(pdbPath,pdb);File.WriteAllBytes(assemblyPath,image);
        return removed;
    }

    internal static void PublishSidecar(string assemblyPath,string pdbPath)
    {
        if(!File.Exists(pdbPath))return;
        byte[] image=File.ReadAllBytes(assemblyPath);var obsolete=new List<string>();bool changed=false;
        using(var pe=new PEReader(new MemoryStream(image,false)))
        {
            var entries=pe.ReadDebugDirectory();
            int rva=pe.PEHeaders.PEHeader!.DebugTableDirectory.RelativeVirtualAddress;
            var section=pe.PEHeaders.SectionHeaders.FirstOrDefault(s=>rva>=s.VirtualAddress&&(long)rva+entries.Length*28<=s.VirtualAddress+(long)s.SizeOfRawData);
            if(entries.Length==0)return;
            if(string.IsNullOrEmpty(section.Name))throw new InvalidDataException("Debug directory is outside PE raw sections.");
            int directory=checked(section.PointerToRawData+rva-section.VirtualAddress);
            string desired=Path.GetFileName(pdbPath);byte[] filename=Encoding.UTF8.GetBytes(desired+"\0");
            for(int i=0;i<entries.Length;i++)
            {
                var entry=entries[i];if(entry.Type!=DebugDirectoryEntryType.CodeView)continue;
                string oldPath=pe.ReadCodeViewDebugDirectoryData(entry).Path,name=Path.GetFileName(oldPath);
                if(string.IsNullOrEmpty(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidDataException("Unsafe CodeView symbol filename.");
                if(name.EndsWith("-recovered.pdb",StringComparison.OrdinalIgnoreCase))obsolete.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!,name));
                if(oldPath==desired)continue;
                int at=entry.DataPointer;
                if(at<0||entry.DataSize<25||(long)at+entry.DataSize>image.Length||BitConverter.ToUInt32(image,at)!=0x53445352)throw new InvalidDataException("Invalid RSDS symbol directory.");
                if(filename.Length<=entry.DataSize-24)
                {
                    Array.Clear(image,at+24,entry.DataSize-24);Array.Copy(filename,0,image,at+24,filename.Length);
                }
                else
                {
                    // Longer/Unicode output names need more than the old path's
                    // space. CodeView data may live in a file-only debug overlay;
                    // preserve the RSDS GUID/age and all IL/metadata coordinates.
                    int appended=checked((image.Length+3)&~3),size=checked(24+filename.Length);
                    byte[] grown=new byte[checked(appended+size)];Array.Copy(image,grown,image.Length);
                    Array.Copy(image,at,grown,appended,24);Array.Copy(filename,0,grown,appended+24,filename.Length);image=grown;
                    Array.Copy(BitConverter.GetBytes(size),0,image,directory+i*28+16,4);
                    Array.Clear(image,directory+i*28+20,4);
                    Array.Copy(BitConverter.GetBytes(appended),0,image,directory+i*28+24,4);
                }
                changed=true;
            }
        }
        if(changed)File.WriteAllBytes(assemblyPath,image);
        // Do not remove unrelated PDBs: only a previous declared recovered-name
        // alias whose bytes exactly match this assembly's supplied symbols.
        foreach(string alias in obsolete.Distinct(StringComparer.OrdinalIgnoreCase))
            if(!string.Equals(alias,Path.GetFullPath(pdbPath),StringComparison.OrdinalIgnoreCase)&&File.Exists(alias)&&Enumerable.SequenceEqual(File.ReadAllBytes(alias),File.ReadAllBytes(pdbPath)))File.Delete(alias);
    }
}
