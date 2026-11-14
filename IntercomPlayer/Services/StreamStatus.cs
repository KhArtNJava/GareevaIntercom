namespace IntercomPlayer.Services;

/// <summary>Состояние приёма потока для отображения на странице.</summary>
public sealed class StreamStatus
{
    private long _bytes, _videoFrames, _audioFrames;

    public string State { get; set; } = "Ожидание";
    public string? LastError { get; set; }
    public string? DeviceCodec { get; set; }
    public string? AudioInfo { get; set; }
    public DateTimeOffset? LastFrameAt { get; set; }
    /// <summary>Дата и время устройства из заголовка последнего ключевого кадра (часовой пояс неизвестен).</summary>
    public DateTime? DeviceTime { get; set; }

    public long Bytes => Interlocked.Read(ref _bytes);
    public long VideoFrames => Interlocked.Read(ref _videoFrames);
    public long AudioFrames => Interlocked.Read(ref _audioFrames);

    public void AddBytes(int n) => Interlocked.Add(ref _bytes, n);
    public void AddVideo() => Interlocked.Increment(ref _videoFrames);
    public void AddAudio() => Interlocked.Increment(ref _audioFrames);
}
