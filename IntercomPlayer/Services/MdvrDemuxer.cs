using System.Buffers.Binary;

namespace IntercomPlayer.Services;

public enum MdvrChunkKind { Video, Audio }

/// <summary>Один кадр из потока регистратора. Payload — данные без заголовка контейнера.</summary>
public readonly record struct MdvrChunk(MdvrChunkKind Kind, string Codec, bool KeyFrame, long TimestampUs, DateTime? DeviceTime, byte[] Payload);

/// <summary>
/// Разбирает поток регистратора. Формат (определён по захваченному фрагменту):
/// в начале потока идёт служебный заголовок ~528 байт, затем кадры вида
///   "NNdc"|"NNwb" (4) + кодек ASCII (4) + размер полезных данных (4, LE) + флаги (4, 0x10 = ключевой)
///   + метка времени в микросекундах (8, LE, монотонная, 40000 мкс между кадрами при 25 к/с)
///   [+ ещё 16 байт у ключевого видеокадра: дата YY MM DD hh mm ss, fps, ширина, высота] + данные.
/// Кадры выровнены по 8 байт, между ними могут быть нули, поэтому после каждого кадра ищется следующий тег.
/// </summary>
public sealed class MdvrDemuxer
{
    private const int MinHeader = 24;
    private const int KeyFrameExtraHeader = 16;
    private const int MaxPayload = 4 * 1024 * 1024;

    private byte[] _buf = new byte[256 * 1024];
    private int _start;
    private int _end;

    public long SkippedBytes { get; private set; }

    public void Push(ReadOnlySpan<byte> data, Action<MdvrChunk> onChunk)
    {
        Append(data);
        while (TryReadChunk(out var chunk))
            onChunk(chunk);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_start > 0 && _end + data.Length > _buf.Length)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end + data.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _end + data.Length));
        data.CopyTo(_buf.AsSpan(_end));
        _end += data.Length;
    }

    private bool TryReadChunk(out MdvrChunk chunk)
    {
        chunk = default;
        while (true)
        {
            // Ищем тег кадра.
            while (_end - _start >= MinHeader && !IsTag(_start))
            {
                _start++;
                SkippedBytes++;
            }
            if (_end - _start < MinHeader)
                return false;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_start + 8));
            if (size == 0 || size > MaxPayload)
            {
                _start++; // ложное срабатывание тега
                SkippedBytes++;
                continue;
            }

            var isVideo = _buf[_start + 3] == (byte)'c';
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_start + 12));
            var headerLen = MinHeader;
            if (isVideo)
            {
                // У ключевого кадра между заголовком и данными лежит блок описания (дата, fps, размер).
                if (_end - _start < MinHeader + KeyFrameExtraHeader + 4)
                    return false;
                if (!IsStartCode(_start + MinHeader))
                {
                    if (IsStartCode(_start + MinHeader + KeyFrameExtraHeader))
                        headerLen = MinHeader + KeyFrameExtraHeader;
                    else
                    {
                        _start++;
                        SkippedBytes++;
                        continue;
                    }
                }
            }

            if (_end - _start < headerLen + size)
                return false; // ждём остаток кадра

            DateTime? deviceTime = null;
            if (headerLen > MinHeader)
            {
                var d = _buf.AsSpan(_start + MinHeader, 6);
                try { deviceTime = new DateTime(2000 + d[0], d[1], d[2], d[3], d[4], d[5], DateTimeKind.Unspecified); }
                catch (ArgumentOutOfRangeException) { }
            }

            var payload = new byte[size];
            Buffer.BlockCopy(_buf, _start + headerLen, payload, 0, (int)size);
            chunk = new MdvrChunk(
                isVideo ? MdvrChunkKind.Video : MdvrChunkKind.Audio,
                System.Text.Encoding.ASCII.GetString(_buf, _start + 4, 4),
                (flags & 0x10) != 0,
                (long)BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(_start + 16)),
                deviceTime,
                payload);
            _start += headerLen + (int)size;
            return true;
        }
    }

    private bool IsStartCode(int pos) =>
        _buf[pos] == 0 && _buf[pos + 1] == 0 && _buf[pos + 2] == 0 && _buf[pos + 3] == 1;

    private bool IsTag(int p)
    {
        var b = _buf;
        if (!char.IsAsciiDigit((char)b[p]) || !char.IsAsciiDigit((char)b[p + 1])) return false;
        var isDc = b[p + 2] == (byte)'d' && b[p + 3] == (byte)'c';
        var isWb = b[p + 2] == (byte)'w' && b[p + 3] == (byte)'b';
        if (!isDc && !isWb) return false;
        for (var i = 4; i < 8; i++)
            if (!char.IsAsciiLetterOrDigit((char)b[p + i]) && b[p + i] != (byte)'_' && b[p + i] != (byte)' ')
                return false;
        return true;
    }
}
