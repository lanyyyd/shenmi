using System.Text.Json.Serialization.Metadata;
using System.Buffers.Binary;
using System.Text.Json;
using SteamEyaWinUI.Models;

namespace SteamEyaWinUI.Services.RemoteAssist;

/// <summary>
/// 远程协助私有协议的帧封装。
///
/// 帧头 12 字节：魔术 "EYA1" + 类型 + 标志 + 保留 + 长度，控制消息的载荷是 UTF-8 JSON，
/// 视频/输入是定长二进制。一条 TCP 连接上多路复用，顺序即语义（先邀请后画面）。
/// </summary>
internal static class AssistFraming
{
    public const int HeaderSize = 12;
    public const int MaxPayload = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "EYA1"u8;

    /// <summary>写一帧。gate 保证多线程（心跳/视频/输入）不会把帧头撕裂。</summary>
    public static async ValueTask WriteAsync(Stream stream, AssistMessageType type, byte flags,
        ReadOnlyMemory<byte> payload, SemaphoreSlim gate, CancellationToken ct)
    {
        if (payload.Length > MaxPayload)
        {
            throw new InvalidOperationException($"载荷过大：{payload.Length}");
        }

        var frame = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(frame);
        frame[4] = (byte)type;
        frame[5] = flags;
        frame[6] = 0;
        frame[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8, 4), payload.Length);
        payload.Span.CopyTo(frame.AsSpan(HeaderSize));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static ValueTask WriteJsonAsync<T>(Stream stream, AssistMessageType type, T value,
        JsonTypeInfo<T> info, SemaphoreSlim gate, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, info);
        return WriteAsync(stream, type, 0, payload, gate, ct);
    }

    /// <summary>读取一帧；连接正常关闭返回 null。</summary>
    public static async ValueTask<AssistFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        if (header[0] != 'E' || header[1] != 'Y' || header[2] != 'A' || header[3] != '1')
        {
            throw new InvalidDataException("帧魔术不匹配，可能不是远程协助端口。");
        }

        var type = (AssistMessageType)header[4];
        var flags = header[5];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (length < 0 || length > MaxPayload)
        {
            throw new InvalidDataException($"帧长度非法：{length}");
        }

        var payload = length == 0 ? [] : new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return null;
        }

        return new AssistFrame(type, flags, payload);
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    public static T? ReadJson<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, info);
        }
        catch
        {
            return default;
        }
    }
}

internal readonly record struct AssistFrame(AssistMessageType Type, byte Flags, byte[] Payload);

/// <summary>视频帧头（20 字节）+ Annex-B 码流。</summary>
internal readonly struct AssistVideoHeader
{
    public const int Size = 32;

    public long PtsMs { get; init; }
    public bool KeyFrame { get; init; }
    public ushort Width { get; init; }
    public ushort Height { get; init; }
    public int DataLength { get; init; }

    /// <summary>被控端光标位置（归一化 0..1）。DXGI 不把光标画进画面，这里单独带过去由主控端绘制。</summary>
    public bool CursorVisible { get; init; }
    public float CursorX { get; init; }
    public float CursorY { get; init; }

    public static byte[] Build(long ptsMs, bool keyFrame, int width, int height, int dataLength,
        bool cursorVisible = false, float cursorX = 0, float cursorY = 0)
    {
        var header = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, 8), ptsMs);
        header[8] = keyFrame ? (byte)1 : (byte)0;
        header[9] = 0; // 编码：0 = H.264
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), (ushort)height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14, 4), dataLength);
        header[18] = cursorVisible ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20, 4), cursorX);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(24, 4), cursorY);
        return header;
    }

    public static AssistVideoHeader Parse(ReadOnlySpan<byte> span)
    {
        return new AssistVideoHeader
        {
            PtsMs = BinaryPrimitives.ReadInt64LittleEndian(span[..8]),
            KeyFrame = span[8] == 1,
            Width = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10, 2)),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12, 2)),
            DataLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(14, 4)),
            CursorVisible = span[18] == 1,
            CursorX = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(20, 4)),
            CursorY = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(24, 4)),
        };
    }
}

/// <summary>输入事件（16 字节定长，避免高频小包反复序列化）。</summary>
internal readonly struct AssistInputEvent
{
    public const int Size = 16;

    public const byte KindMouseMove = 1;   // x,y = 归一化 0..1
    public const byte KindMouseButton = 2; // button = 0 左 1 右 2 中, down = 1/0
    public const byte KindMouseWheel = 3;  // delta
    public const byte KindKey = 4;         // button = 扫描码, down, flags bit0 = 扩展键

    public byte Kind { get; init; }
    public byte Button { get; init; }
    public byte Down { get; init; }
    public byte Flags { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public int Delta { get; init; }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        buffer[0] = Kind;
        buffer[1] = Button;
        buffer[2] = Down;
        buffer[3] = Flags;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4, 4), X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8, 4), Y);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), Delta);
        return buffer;
    }

    public static AssistInputEvent Parse(ReadOnlySpan<byte> span)
    {
        if (span.Length < Size)
        {
            return new AssistInputEvent { Kind = 0 };
        }
        return new AssistInputEvent
        {
            Kind = span[0],
            Button = span[1],
            Down = span[2],
            Flags = span[3],
            X = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, 4)),
            Y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, 4)),
            Delta = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4)),
        };
    }
}
