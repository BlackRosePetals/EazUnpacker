using System.IO.Compression;

namespace Unpacker;

internal static class BundleExtractor
{
    private static readonly byte[] Signature = { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32, 0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae };

    internal static bool IsBundle(byte[] bytes, out long header)
    {
        header = 0;
        // A single-file apphost is native. Managed assemblies can contain the
        // marker as ordinary data (including tools that inspect bundles).
        // Do not interpret that data's preceding bytes as an apphost pointer.
        if (bytes.Length >= 2 && bytes[0] == 0x4d && bytes[1] == 0x5a)
        {
            using var pe = new dnlib.PE.PEImage(bytes);
            var clr = pe.ImageNTHeaders.OptionalHeader.DataDirectories[14];
            if (clr.VirtualAddress != 0 && clr.Size >= 0x48) return false;
        }
        for (int i = 8; i <= bytes.Length - Signature.Length; i++)
        {
            if (bytes[i] != Signature[0]) continue;
            bool match = true;
            for (int j = 1; j < Signature.Length; j++) if (bytes[i + j] != Signature[j]) { match = false; break; }
            if (!match) continue;
            header = BitConverter.ToInt64(bytes, i - 8);
            if (header == 0) continue;
            if (header < 0 || header > bytes.LongLength - 12) throw new InvalidDataException("Invalid bundle header offset.");
            return true;
        }
        header = 0; return false;
    }

    internal static List<string> Extract(byte[] bytes, long header, string output)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position = header;
        uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
        if ((major != 1 && major != 2 && major != 6) || minor != 0) throw new NotSupportedException($"Bundle version {major}.{minor}");
        int count = reader.ReadInt32();
        if (count < 1 || count > 100000) throw new InvalidDataException("Invalid bundle file count.");
        reader.ReadString();
        if (major >= 2) reader.BaseStream.Seek(40, SeekOrigin.Current);
        string root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Path, long Offset, long Size, long Packed)>();
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), packed = major >= 6 ? reader.ReadInt64() : 0;
            reader.ReadByte();
            string name = reader.ReadString().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(name) || name.IndexOf(':') >= 0) throw new InvalidDataException("Unsafe bundle entry.");
            string dest = Path.GetFullPath(Path.Combine(root, name));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(dest)) throw new InvalidDataException("Unsafe or duplicate bundle entry.");
            long stored = packed == 0 ? size : packed;
            if (size < 0 || size > 512 * 1024 * 1024 || packed < 0 || offset < 0 || offset > bytes.LongLength || stored > bytes.LongLength - offset)
                throw new InvalidDataException("Invalid bundle payload bounds.");
            entries.Add((dest, offset, size, packed));
        }
        foreach (var entry in entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
            using var source = new MemoryStream(bytes, (int)entry.Offset, (int)(entry.Packed == 0 ? entry.Size : entry.Packed));
            using Stream payload = entry.Packed == 0 ? source : new DeflateStream(source, CompressionMode.Decompress);
            using var target = new FileStream(entry.Path, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920]; long total = 0; int read;
            while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read; if (total > entry.Size) throw new InvalidDataException("Bundle decompression exceeded declared size.");
                target.Write(buffer, 0, read);
            }
            if (total != entry.Size) throw new InvalidDataException("Bundle payload size mismatch.");
        }
        return entries.Select(e => e.Path).ToList();
    }

    internal static void Repack(string original, string recovered, string extracted, string output)
    {
        byte[] bytes=File.ReadAllBytes(original);
        if(!IsBundle(bytes,out long oldHeader))throw new NotSupportedException("现代 EXE 单文件输出需要原始 bundle 启动器。");
        using var reader=new BinaryReader(new MemoryStream(bytes));
        reader.BaseStream.Position=oldHeader;
        uint major=reader.ReadUInt32(),minor=reader.ReadUInt32();
        int count=reader.ReadInt32();reader.ReadString();
        ulong flags=0;
        if(major>=2){reader.BaseStream.Seek(32,SeekOrigin.Current);flags=reader.ReadUInt64();}
        var entries=new List<(string Name,byte Kind,long Offset,long Size)>();
        var originalEntries=new List<(string Name,byte Kind,long Offset)>();
        for(int index=0;index<count;index++)
        {
            long offset=reader.ReadInt64();reader.ReadInt64();if(major>=6)reader.ReadInt64();
            byte kind=reader.ReadByte();string name=reader.ReadString();
            originalEntries.Add((name,kind,offset));
        }
        using var module=dnlib.DotNet.ModuleDefMD.Load(recovered);
        string main=module.Assembly.Name+".dll";
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        // Keep the native host, replace every payload and the manifest. The
        // original entry name remains bound to the native host's app path.
        writer.Write(bytes,0,checked((int)originalEntries.Min(e=>e.Offset)));
        bool replaced=false;
        foreach(var entry in originalEntries)
        {
            while(stream.Position%16!=0)writer.Write((byte)0);
            long offset=stream.Position;
            string payload=entry.Name==main?recovered:Path.Combine(extracted,entry.Name);
            if(entry.Name==main)replaced=true;
            byte[] data=File.ReadAllBytes(payload);writer.Write(data);
            entries.Add((entry.Name,entry.Kind,offset,data.LongLength));
        }
        if(!replaced)throw new InvalidDataException("Bundle 主程序集名称与恢复产物不符。");
        long header=stream.Position;
        using var digest=System.Security.Cryptography.SHA256.Create();
        string bundleId=Convert.ToBase64String(digest.ComputeHash(stream.ToArray())).Replace('+','-').Replace('/','_').TrimEnd('=');
        writer.Write(major);writer.Write(minor);writer.Write(count);writer.Write(bundleId);
        if(major>=2)
        {
            foreach(byte kind in new byte[]{3,4})
            {var entry=entries.FirstOrDefault(e=>e.Kind==kind);writer.Write(entry.Offset);writer.Write(entry.Size);}
            writer.Write(flags);
        }
        foreach(var entry in entries)
        {writer.Write(entry.Offset);writer.Write(entry.Size);if(major>=6)writer.Write(0L);writer.Write(entry.Kind);writer.Write(entry.Name);}
        var packed=stream.ToArray();
        for(int index=8;index<=packed.Length-Signature.Length;index++)
        {
            if(!Signature.Select((b,i)=>packed[index+i]==b).All(x=>x))continue;
            Buffer.BlockCopy(BitConverter.GetBytes(header),0,packed,index-8,8);File.WriteAllBytes(output,packed);return;
        }
        throw new InvalidDataException("Bundle 启动器签名缺失。");
    }
}
