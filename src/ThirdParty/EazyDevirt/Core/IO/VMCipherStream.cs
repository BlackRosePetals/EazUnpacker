using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace EazyDevirt.Core.IO;

/// <summary>Seekable, cached raw or PKCS1 RSA resource stream with logical block boundaries.</summary>
internal sealed class VMCipherStream : Stream
{
    private readonly int InputBlockSize;
    private readonly int OutputBlockSize;
    private readonly MemoryStream _resource;
    private readonly Org.BouncyCastle.Crypto.IAsymmetricBlockCipher _rsa;
    private readonly Dictionary<int, byte[]> _blocks = new();
    private long _position;
    private int? _length;

    public VMCipherStream(byte[] buffer, BigInteger mod, BigInteger exp, int? knownLength = null, bool raw = false)
    {
        _resource = new MemoryStream(buffer, false);
        InputBlockSize = (mod.BitLength + 7) / 8;
        OutputBlockSize = raw ? (mod.BitLength - 1) / 8 : InputBlockSize - 11;
        if (OutputBlockSize <= 0) throw new InvalidDataException("Invalid VM RSA block size.");
        _rsa = raw ? new RsaEngine() : new Pkcs1Encoding(new RsaEngine());
        // Eaz uses type-2 PKCS1 padding with a public exponent; the decoder expects the private flag.
        _rsa.Init(false, new RsaKeyParameters(true, mod, exp));
        _length = knownLength;
    }

    private byte[] GetBlock(int id)
    {
        if (_blocks.TryGetValue(id, out var cached)) return cached;
        long physical = 4L + (long)id * InputBlockSize;
        if (physical < 4 || physical + InputBlockSize > _resource.Length)
            throw new EndOfStreamException("Truncated RSA resource block.");
        _resource.Position = physical;
        byte[] encoded = new byte[InputBlockSize];
        int total = 0;
        while (total < encoded.Length)
        {
            int read = _resource.Read(encoded, total, encoded.Length - total);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
        byte[] decoded = _rsa.ProcessBlock(encoded, 0, encoded.Length);
        if (_rsa is RsaEngine && decoded.Length < OutputBlockSize)
        {
            var padded = new byte[OutputBlockSize];
            Buffer.BlockCopy(decoded, 0, padded, padded.Length - decoded.Length, decoded.Length);
            decoded = padded;
        }
        int required = (int)Math.Min(OutputBlockSize, Length - (long)id * OutputBlockSize);
        if (required <= 0 || decoded.Length < required || decoded.Length > OutputBlockSize)
            throw new InvalidDataException("Invalid RSA output block length.");
        _blocks.Add(id, decoded);
        return decoded;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        int remaining = (int)Math.Min(count, Math.Max(0, Length - _position));
        int readTotal = 0;
        while (remaining > 0)
        {
            int id = checked((int)(_position / OutputBlockSize));
            int inBlock = (int)(_position % OutputBlockSize);
            byte[] block = GetBlock(id);
            int take = Math.Min(remaining, Math.Min(OutputBlockSize, block.Length) - inBlock);
            if (take <= 0) throw new InvalidDataException("Invalid RSA block position.");
            Buffer.BlockCopy(block, inBlock, buffer, offset + readTotal, take);
            _position += take;
            readTotal += take;
            remaining -= take;
        }
        return readTotal;
    }

    public override long Length
    {
        get
        {
            if (!_length.HasValue)
            {
                _resource.Position = 0;
                using var crypto = new CryptoStreamV3(_resource, 0, true);
                using var reader = new VMBinaryReader(crypto);
                int length = reader.ReadInt32Special();
                if (length < 0 || length > ((_resource.Length - 4) / InputBlockSize) * OutputBlockSize)
                    throw new InvalidDataException("Invalid VM resource length.");
                _length = length;
            }
            return _length.Value;
        }
    }

    public override long Position
    {
        get => _position;
        set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _resource.Dispose(); base.Dispose(disposing); }
}
