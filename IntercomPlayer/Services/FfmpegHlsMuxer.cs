using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace IntercomPlayer.Services;

/// <summary>
/// Запускает ffmpeg, кормит его H.264 и аудио через два локальных TCP-порта
/// и получает HLS (видео копируется без перекодирования, звук кодируется в AAC).
/// Если реального звука нет, в аудиовход подаётся тишина, чтобы у плеера всегда была аудиодорожка.
/// </summary>
public sealed class FfmpegHlsMuxer : IAsyncDisposable
{
    private readonly IntercomOptions _opt;
    private readonly string _hlsDir;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _video = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentQueue<byte[]> _audio = new();
    private int _audioQueued;
    private Process? _proc;
    private Task[] _tasks = [];

    public FfmpegHlsMuxer(IntercomOptions opt, string hlsDir, ILogger log)
    {
        _opt = opt;
        _hlsDir = hlsDir;
        _log = log;
    }

    public bool IsRunning => _proc is { HasExited: false };

    public void Start()
    {
        Directory.CreateDirectory(_hlsDir);
        foreach (var f in Directory.EnumerateFiles(_hlsDir))
            if (f.EndsWith(".ts") || f.EndsWith(".m3u8") || f.EndsWith(".tmp"))
                File.Delete(f);

        int videoPort = FreePort(), audioPort = FreePort();
        var fmt = _opt.AudioFormat.ToLowerInvariant();
        var psi = new ProcessStartInfo(_opt.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-fflags", "+nobuffer",
            "-probesize", "100000", "-analyzeduration", "500000",
            "-f", "ivf", "-i", $"tcp://127.0.0.1:{videoPort}?listen=1",
            "-f", fmt, "-ar", _opt.AudioSampleRate.ToString(), "-ac", "1", "-i", $"tcp://127.0.0.1:{audioPort}?listen=1",
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "32k", "-ar", "16000",
            "-f", "hls", "-hls_time", "1", "-hls_list_size", "8",
            "-hls_flags", "delete_segments+omit_endlist+temp_file+independent_segments",
            "-hls_segment_filename", Path.Combine(_hlsDir, "seg_%05d.ts"),
            Path.Combine(_hlsDir, "stream.m3u8"),
        })
            psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg");
        _log.LogInformation("ffmpeg запущен (pid {Pid}), порты видео {V}, аудио {A}", _proc.Id, videoPort, audioPort);

        var ct = _cts.Token;
        _tasks =
        [
            Task.Run(() => ReadStderr(_proc, ct)),
            Task.Run(() => VideoPump(videoPort, ct)),
            Task.Run(() => AudioPump(audioPort, ct)),
        ];
    }

    private long _firstTs = -1, _lastPts = -1;

    /// <summary>
    /// Передаёт кадр H.264 (Annex B) в ffmpeg, упаковав в IVF с меткой времени устройства (мкс).
    /// Сырой H.264 без меток ffmpeg разбирает с потерей кадров, поэтому метки задаём сами.
    /// </summary>
    public void WriteVideo(byte[] annexB, long deviceTimestampUs)
    {
        if (_firstTs < 0) _firstTs = deviceTimestampUs;
        var pts = deviceTimestampUs - _firstTs;
        // Метки должны строго расти; после обрыва связи или сброса часов устройства не даём им скакать.
        if (_lastPts >= 0 && (pts <= _lastPts || pts - _lastPts > 2_000_000))
            pts = _lastPts + FrameIntervalUs;
        _lastPts = pts;

        var frame = new byte[12 + annexB.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)annexB.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4), (ulong)pts);
        annexB.CopyTo(frame, 12);
        _video.Writer.TryWrite(frame);
    }

    private const long FrameIntervalUs = 40_000;

    public void WriteAudio(byte[] data)
    {
        _audio.Enqueue(data);
        // Не накапливаем больше ~2 секунд, чтобы задержка звука не росла.
        var limit = _opt.AudioSampleRate * BytesPerSample * 2;
        if (Interlocked.Add(ref _audioQueued, data.Length) > limit)
            while (_audioQueued > limit / 2 && _audio.TryDequeue(out var old))
                Interlocked.Add(ref _audioQueued, -old.Length);
    }

    private int BytesPerSample => _opt.AudioFormat.Equals("s16le", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private byte SilenceByte => _opt.AudioFormat.ToLowerInvariant() switch
    {
        "alaw" => 0xD5,
        "mulaw" => 0xFF,
        _ => 0x00,
    };

    private async Task ReadStderr(Process p, CancellationToken ct)
    {
        try
        {
            while (await p.StandardError.ReadLineAsync(ct) is { } line)
                _log.LogWarning("ffmpeg: {Line}", line);
        }
        catch (OperationCanceledException) { }
    }

    private async Task VideoPump(int port, CancellationToken ct)
    {
        try
        {
            using var sock = await ConnectLocal(port, ct);
            var ns = sock.GetStream();
            await ns.WriteAsync(IvfHeader(), ct);
            await foreach (var frame in _video.Reader.ReadAllAsync(ct))
                await ns.WriteAsync(frame, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Ошибка записи видео в ffmpeg"); }
    }

    private static byte[] IvfHeader()
    {
        var h = new byte[32];
        "DKIF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 32);   // длина заголовка
        "H264"u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), 704); // уточняется по SPS
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14), 576);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 1_000_000); // метки в микросекундах
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), 1);
        return h;
    }

    private async Task AudioPump(int port, CancellationToken ct)
    {
        try
        {
            using var sock = await ConnectLocal(port, ct);
            var ns = sock.GetStream();
            var bps = BytesPerSample;
            var silence = SilenceByte;
            byte[]? rest = null;
            var restPos = 0;
            long sent = 0; // байт отправлено
            var clock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * _opt.AudioSampleRate) * bps - sent;
                due -= due % bps;
                if (due < 160 * bps)
                {
                    await Task.Delay(10, ct);
                    continue;
                }
                var block = new byte[due];
                var filled = 0;
                while (filled < block.Length)
                {
                    if (rest is null || restPos >= rest.Length)
                    {
                        if (!_audio.TryDequeue(out rest)) { rest = null; break; }
                        Interlocked.Add(ref _audioQueued, -rest.Length);
                        restPos = 0;
                    }
                    var n = Math.Min(block.Length - filled, rest.Length - restPos);
                    Buffer.BlockCopy(rest, restPos, block, filled, n);
                    restPos += n;
                    filled += n;
                }
                if (filled < block.Length)
                    Array.Fill(block, silence, filled, block.Length - filled);
                await ns.WriteAsync(block, ct);
                sent += block.Length;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Ошибка записи аудио в ffmpeg"); }
    }

    // ffmpeg открывает вторую точку прослушивания только после анализа первого входа, поэтому подключаемся с повторами.
    private static async Task<TcpClient> ConnectLocal(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var c = new TcpClient { NoDelay = true };
            try
            {
                await c.ConnectAsync(IPAddress.Loopback, port, ct);
                return c;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                c.Dispose();
                await Task.Delay(200, ct);
            }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_proc is { HasExited: false }) { _proc.Kill(true); await _proc.WaitForExitAsync(); } } catch { }
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _proc?.Dispose();
        _cts.Dispose();
    }
}
