// Подключает hls.js (или нативный HLS в Safari) к <video> и сам восстанавливается после обрывов.
window.intercomPlayer = {
    _hls: null,
    _timer: null,

    start(videoId, src) {
        this.stop();
        const video = document.getElementById(videoId);
        if (!video) return;

        const restart = (delayMs) => {
            this.stop();
            this._timer = setTimeout(() => this.start(videoId, src), delayMs);
        };

        if (window.Hls && Hls.isSupported()) {
            const hls = new Hls({
                liveSyncDurationCount: 2,
                liveMaxLatencyDurationCount: 6,
                manifestLoadingMaxRetry: 60,
                manifestLoadingRetryDelay: 1000,
                manifestLoadingMaxRetryTimeout: 2000,
            });
            this._hls = hls;
            hls.on(Hls.Events.ERROR, (_, data) => {
                if (data.fatal) restart(2000);
            });
            hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => { }));
            hls.loadSource(src);
            hls.attachMedia(video);
        } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
            video.src = src;
            video.play().catch(() => { });
        } else {
            video.insertAdjacentText('afterend', 'Браузер не поддерживает HLS.');
        }
    },

    stop() {
        if (this._timer) { clearTimeout(this._timer); this._timer = null; }
        if (this._hls) { this._hls.destroy(); this._hls = null; }
    },
};
