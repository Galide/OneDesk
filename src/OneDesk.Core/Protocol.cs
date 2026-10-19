using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OneDesk.Core;

public enum MessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    Take = 3,
    Release = 4,
    ReleaseAck = 5,
    Key = 6,
    MouseMove = 7,
    MouseButton = 8,
    Wheel = 9,
    Clipboard = 10,
    Ping = 11,
    Pong = 12,
    Error = 13
}

[Flags]
public enum KeyFlags
{
    None = 0,
    Down = 1,
    Extended = 2,
    Repeat = 4
}

public sealed class Message
{
    public MessageType Type { get; set; }
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public int Vk { get; set; }
    public int Scan { get; set; }
    public KeyFlags Flags { get; set; }
    public float Nx { get; set; }
    public float Ny { get; set; }
    public int Button { get; set; }
    public bool Down { get; set; }
    public int Delta { get; set; }
    public int HDelta { get; set; }
    public string? Text { get; set; }

    public static Message Hello(string name) => new()
    {
        Type = MessageType.Hello, Version = Protocol.Version, Name = name
    };

    public static Message Key(int vk, int scan, KeyFlags flags) => new()
    {
        Type = MessageType.Key, Vk = vk, Scan = scan, Flags = flags
    };

    public static Message MouseMove(float nx, float ny) => new()
    {
        Type = MessageType.MouseMove, Nx = nx, Ny = ny
    };

    public static Message MouseButton(int button, bool down, float nx, float ny) => new()
    {
        Type = MessageType.MouseButton, Button = button, Down = down, Nx = nx, Ny = ny
    };

    public static Message Wheel(int delta, int hDelta, float nx, float ny) => new()
    {
        Type = MessageType.Wheel, Delta = delta, HDelta = hDelta, Nx = nx, Ny = ny
    };

    public static Message Clipboard(string text) => new()
    {
        Type = MessageType.Clipboard, Text = text
    };
}

public static class Protocol
{
    public const int Version = 1;
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    public const int MaxTextBytes = 2 * 1024 * 1024;

    public static byte[] Encode(Message m)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, true);
        w.Write((byte)m.Type);

        switch (m.Type)
        {
            case MessageType.Hello:
                w.Write(m.Version);
                WriteString(w, m.Name ?? "");
                break;
            case MessageType.HelloAck:
                w.Write(m.Version);
                WriteString(w, m.Name ?? "");
                break;
            case MessageType.Key:
                w.Write(m.Vk);
                w.Write(m.Scan);
                w.Write((byte)m.Flags);
                break;
            case MessageType.MouseMove:
                w.Write(m.Nx);
                w.Write(m.Ny);
                break;
            case MessageType.MouseButton:
                w.Write((byte)m.Button);
                w.Write(m.Down);
                w.Write(m.Nx);
                w.Write(m.Ny);
                break;
            case MessageType.Wheel:
                w.Write(m.Delta);
                w.Write(m.HDelta);
                w.Write(m.Nx);
                w.Write(m.Ny);
                break;
            case MessageType.Clipboard:
                WriteString(w, m.Text ?? "");
                break;
            case MessageType.Error:
                WriteString(w, m.Text ?? "");
                break;
            case MessageType.Take:
            case MessageType.Release:
            case MessageType.ReleaseAck:
            case MessageType.Ping:
            case MessageType.Pong:
                break;
            default:
                throw new InvalidDataException("Unknown message type " + m.Type);
        }

        w.Flush();
        if (ms.Length > MaxPayloadBytes) throw new InvalidDataException("Message too large");
        return ms.ToArray();
    }

    public static Message Decode(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray(), false);
        using var r = new BinaryReader(ms, Encoding.UTF8, true);
        var type = (MessageType)r.ReadByte();
        var m = new Message { Type = type };

        switch (type)
        {
            case MessageType.Hello:
            case MessageType.HelloAck:
                m.Version = r.ReadInt32();
                m.Name = ReadString(r);
                break;
            case MessageType.Key:
                m.Vk = r.ReadInt32();
                m.Scan = r.ReadInt32();
                m.Flags = (KeyFlags)r.ReadByte();
                break;
            case MessageType.MouseMove:
                m.Nx = r.ReadSingle();
                m.Ny = r.ReadSingle();
                break;
            case MessageType.MouseButton:
                m.Button = r.ReadByte();
                m.Down = r.ReadBoolean();
                m.Nx = r.ReadSingle();
                m.Ny = r.ReadSingle();
                break;
            case MessageType.Wheel:
                m.Delta = r.ReadInt32();
                m.HDelta = r.ReadInt32();
                m.Nx = r.ReadSingle();
                m.Ny = r.ReadSingle();
                break;
            case MessageType.Clipboard:
            case MessageType.Error:
                m.Text = ReadString(r);
                break;
            case MessageType.Take:
            case MessageType.Release:
            case MessageType.ReleaseAck:
            case MessageType.Ping:
            case MessageType.Pong:
                break;
            default:
                throw new InvalidDataException("Unknown message type " + type);
        }

        return m;
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxTextBytes) throw new InvalidDataException("Text too large");
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length < 0 || length > MaxTextBytes) throw new InvalidDataException("Invalid string length");
        if (length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Truncated string");
        var bytes = r.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// Length-prefixed, AES-256-GCM protected message stream. The client sends a random salt
/// before the first frame; both sides derive the key with PBKDF2-SHA256.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SequenceSize = 8;

    private readonly Stream _stream;
    private readonly AesGcm _encrypt;
    private readonly AesGcm _decrypt;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _sendSequence;
    private long _receiveSequence;
    private bool _disposed;

    public SecureChannel(Stream stream, byte[] key)
    {
        _stream = stream;
        _encrypt = new AesGcm(key, TagSize);
        _decrypt = new AesGcm(key, TagSize);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] DeriveKey(string password, byte[] salt)
    {
        var canonical = AppConfig.CanonicalizePassword(password);
        if (canonical.Length == 0) throw new ArgumentException("Password must not be empty", nameof(password));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(canonical), salt, 120_000,
            HashAlgorithmName.SHA256, 32);
    }

    public static async Task WriteSaltAsync(Stream stream, byte[] salt, CancellationToken ct)
    {
        await stream.WriteAsync(salt, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadSaltAsync(Stream stream, CancellationToken ct)
    {
        var salt = new byte[SaltSize];
        await stream.ReadExactlyAsync(salt, ct).ConfigureAwait(false);
        return salt;
    }

    public async Task SendAsync(Message message, CancellationToken ct)
    {
        var payload = Protocol.Encode(message);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plaintext = new byte[SequenceSize + payload.Length];
            BinaryPrimitives.WriteInt64BigEndian(plaintext, _sendSequence);
            payload.CopyTo(plaintext, SequenceSize);

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            _encrypt.Encrypt(nonce, plaintext, ciphertext, tag);

            var frame = new byte[4 + NonceSize + ciphertext.Length + TagSize];
            BinaryPrimitives.WriteInt32BigEndian(frame, NonceSize + ciphertext.Length + TagSize);
            nonce.CopyTo(frame, 4);
            ciphertext.CopyTo(frame, 4 + NonceSize);
            tag.CopyTo(frame, 4 + NonceSize + ciphertext.Length);

            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            _sendSequence++;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns null when the peer closed the stream cleanly.</summary>
    public async Task<Message?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var got = await ReadAtLeastOneAsync(header, ct).ConfigureAwait(false);
        if (!got) return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < NonceSize + SequenceSize + TagSize || length > Protocol.MaxPayloadBytes + NonceSize + SequenceSize + TagSize)
        {
            throw new InvalidDataException("Invalid frame length " + length);
        }

        var rest = new byte[length];
        await _stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[length - NonceSize - TagSize];
        Buffer.BlockCopy(rest, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(rest, NonceSize, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(rest, length - TagSize, tag, 0, TagSize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            _decrypt.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Authentication failed (wrong password or tampered frame)");
        }

        if (plaintext.Length < SequenceSize) throw new InvalidDataException("Frame too short");
        var seq = BinaryPrimitives.ReadInt64BigEndian(plaintext);
        if (seq != _receiveSequence)
        {
            throw new InvalidDataException($"Unexpected sequence {seq}, expected {_receiveSequence}");
        }

        _receiveSequence++;
        return Protocol.Decode(plaintext.AsSpan(SequenceSize));
    }

    private async Task<bool> ReadAtLeastOneAsync(byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) return total > 0 ? throw new EndOfStreamException("Truncated frame header") : false;
            total += read;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encrypt.Dispose();
        _decrypt.Dispose();
        _writeLock.Dispose();
        _stream.Dispose();
    }
}
