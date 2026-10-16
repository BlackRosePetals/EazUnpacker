using EazyDevirt.Core.IO;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace EazyDevirt;

/// <summary>Regression checks for the patched RSA stream implementation.</summary>
public static class CipherStreamTests
{
    public static void Run()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = generator.GenerateKeyPair();
        var key = (RsaKeyParameters)pair.Public;
        var signer = new Pkcs1Encoding(new RsaEngine());
        var privateKey = (RsaKeyParameters)pair.Private;
        signer.Init(true, new RsaKeyParameters(false, privateKey.Modulus, privateKey.Exponent));
        byte[] expected = Enumerable.Range(0, 700).Select(i => (byte)(i * 37)).ToArray();
        using var packed = new MemoryStream();
        packed.Write(new byte[4], 0, 4);
        for (int i = 0; i < expected.Length; i += 245)
        {
            var block = signer.ProcessBlock(expected, i, Math.Min(245, expected.Length - i));
            packed.Write(block, 0, block.Length);
        }
        using var stream = new VMCipherStream(packed.ToArray(), key.Modulus, key.Exponent, expected.Length);
        byte[] actual = new byte[expected.Length + 7];
        int read = stream.Read(actual, 7, expected.Length);
        if (read != expected.Length || !expected.SequenceEqual(actual.Skip(7)))
            throw new Exception("RSA stream bulk/nonzero-offset read failed.");
        if (stream.Read(actual, 0, 1) != 0) throw new Exception("RSA stream EOF failed.");
        stream.Position = 242;
        actual = new byte[30];
        if (stream.Read(actual, 3, 20) != 20 || !expected.Skip(242).Take(20).SequenceEqual(actual.Skip(3).Take(20)))
            throw new Exception("RSA stream cached boundary read failed.");
        using var fresh = new VMCipherStream(packed.ToArray(), key.Modulus, key.Exponent, expected.Length);
        if (fresh.ReadByte() != expected[0]) throw new Exception("RSA stream first byte failed.");
        fresh.Position = 247;
        if (fresh.ReadByte() != expected[247]) throw new Exception("RSA stream seek failed.");
        fresh.Position = 488;
        if (fresh.Read(actual, 0, 20) != 20 || !expected.Skip(488).Take(20).SequenceEqual(actual.Take(20)))
            throw new Exception("RSA stream cached-to-uncached transition failed.");
        Console.WriteLine("PASS: RSA stream bulk reads, offsets, EOF, boundary reads and seeks.");
    }
}
