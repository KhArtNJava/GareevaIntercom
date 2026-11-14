using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace IntercomPlayer.Services;

/// <summary>
/// Подключается к регистратору, отправляет файл запроса, читает поток, выделяет видео и звук
/// и передаёт их в ffmpeg (HLS). При обрыве соединения переподключается.
/// </summary>
public sealed class StreamRelayService : BackgroundService
{
    private readonly IntercomOptions _opt;
    private readonly StreamStatus _status;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<StreamRelayService> _log;
    private FfmpegHlsMuxer? _muxer;

    public StreamRelayService(IOptions<IntercomOptions> opt, StreamStatus status, IWebHostEnvironment env,
        ILogger<StreamRelayService> log)
    {
        _opt = opt.Value;
        _status = status;
        _env = env;
        _log = log;
    }

    public string HlsPath => Path.Combine(_env.ContentRootPath, _opt.HlsDirectory);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var requestPath = Path.Combine(_env.ContentRootPath, _opt.RequestFile);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await File.ReadAllBytesAsync(requestPath, ct);
                await RunSession(request, ct);
                _status.State = "Соединение закрыто сервером";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка сессии");
                _status.LastError = ex.Message;
                _status.State = "Ошибка, переподключение";
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_opt.ReconnectDelaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
        if (_muxer is not null) await _muxer.DisposeAsync();
    }

    private async Task RunSession(byte[] request, CancellationToken ct)
    {
        _status.State = $"Подключение к {_opt.Host}:{_opt.Port}";
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_opt.Host, _opt.Port, ct);
        var ns = client.GetStream();
        await ns.WriteAsync(request, ct);
        _status.State = "Приём потока";
        _status.LastError = null;
        _log.LogInformation("Подключено к {Host}:{Port}, отправлено {N} байт запроса", _opt.Host, _opt.Port, request.Length);

        var demux = new MdvrDemuxer();
        var seenKey = false; // видео передаём в ffmpeg, начиная с ключевого кадра
        var buf = new byte[64 * 1024];
        var lastLog = DateTime.UtcNow;
        long lastVideo = 0;
        int n;
        while ((n = await ns.ReadAsync(buf, ct)) > 0)
        {
            _status.AddBytes(n);
            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
            {
                _log.LogInformation("Видеокадров за 10 с: {V}, всего аудио: {A}, пропущено байт: {S}",
                    _status.VideoFrames - lastVideo, _status.AudioFrames, demux.SkippedBytes);
                lastVideo = _status.VideoFrames;
                lastLog = DateTime.UtcNow;
            }
            demux.Push(buf.AsSpan(0, n), chunk =>
            {
                _status.LastFrameAt = DateTimeOffset.UtcNow;
                if (chunk.DeviceTime is { } dt)
                    _status.DeviceTime = dt;

                if (chunk.Kind == MdvrChunkKind.Video)
                {
                    _status.AddVideo();
                    _status.DeviceCodec = chunk.Codec;
                    if (chunk.KeyFrame) seenKey = true;
                    if (!seenKey) return;
                    if (_muxer is null || !_muxer.IsRunning) RestartMuxer();
                    _muxer!.WriteVideo(chunk.Payload, chunk.TimestampUs);
                }
                else
                {
                    if (_status.AudioFrames == 0)
                        _log.LogInformation("Первый аудиокадр: кодек {Codec}, {Len} байт, начало {Hex}",
                            chunk.Codec, chunk.Payload.Length, Convert.ToHexString(chunk.Payload.AsSpan(0, Math.Min(16, chunk.Payload.Length))));
                    _status.AddAudio();
                    _status.AudioInfo = $"{chunk.Codec}, {chunk.Payload.Length} байт/кадр";
                    _muxer?.WriteAudio(chunk.Payload);
                }
            });
        }
    }

    private void RestartMuxer()
    {
        _muxer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _muxer = new FfmpegHlsMuxer(_opt, HlsPath, _log);
        _muxer.Start();
    }
}
