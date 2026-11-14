namespace IntercomPlayer.Services;

public sealed class IntercomOptions
{
    public const string Section = "Intercom";

    /// <summary>Хост регистратора.</summary>
    public string Host { get; set; } = "192.168.31.150";

    public int Port { get; set; } = 9000;

    /// <summary>Файл с запросом, отправляемым после подключения (путь относительно каталога приложения).</summary>
    public string RequestFile { get; set; } = "toServer720.bin";

    /// <summary>Формат аудио в потоке: alaw, mulaw или s16le (сырой PCM).</summary>
    public string AudioFormat { get; set; } = "alaw";

    public int AudioSampleRate { get; set; } = 8000;

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Каталог для HLS-сегментов (относительно каталога приложения).</summary>
    public string HlsDirectory { get; set; } = "hls";

    public int ReconnectDelaySeconds { get; set; } = 3;
}
