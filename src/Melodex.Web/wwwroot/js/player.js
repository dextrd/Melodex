// The one audio element behind the player bar. Player.cs drives it and hears back about time, ends and errors.
let audio;
let player;
let second = -1;

export function init(dotnet) {
    player = dotnet;
    if (audio)
        return;
    document.addEventListener("keydown", shortcut);
    audio = new Audio();
    audio.preload = "auto";
    audio.volume = storedVolume();
    audio.addEventListener("timeupdate", () => {
        // While seeking, the time can still be the old one, which would put the seek bar back.
        if (audio.seeking)
            return;
        // Once a second is plenty for a seek bar, and keeps calls into .NET down.
        const now = Math.floor(audio.currentTime);
        if (now !== second) {
            second = now;
            player.invokeMethodAsync("OnTime", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0);
        }
    });
    // Where a seek landed, at once, which may not be quite where it was asked for; the lyrics jump there too.
    audio.addEventListener("seeked", () => {
        second = Math.floor(audio.currentTime);
        player.invokeMethodAsync("OnTime", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0);
        if (lyrics?.isConnected)
            light(true);
    });
    audio.addEventListener("ended", () => player.invokeMethodAsync("OnEnded"));
    audio.addEventListener("playing", () => player.invokeMethodAsync("OnPlaying", true));
    audio.addEventListener("pause", () => player.invokeMethodAsync("OnPlaying", false));
    audio.addEventListener("error", () => {
        if (audio.getAttribute("src"))
            player.invokeMethodAsync("OnError", audio.error?.message || "The file couldn't be played.");
    });
    if ("mediaSession" in navigator) {
        // Media keys, and the lock screen on a phone.
        navigator.mediaSession.setActionHandler("play", () => audio.play());
        navigator.mediaSession.setActionHandler("pause", () => audio.pause());
        navigator.mediaSession.setActionHandler("previoustrack", () => player.invokeMethodAsync("OnRemote", "previous"));
        navigator.mediaSession.setActionHandler("nexttrack", () => player.invokeMethodAsync("OnRemote", "next"));
        // Said outright, so the phone's controls show the music as it is.
        audio.addEventListener("playing", () => navigator.mediaSession.playbackState = "playing");
        audio.addEventListener("pause", () => navigator.mediaSession.playbackState = "paused");
    }
}

// Keyboard shortcuts, anywhere but a text box. The player's need something in the queue; "/" opens Search.
const keys = {
    " ": "toggle", k: "toggle", K: "toggle",
    ArrowRight: "forward", ArrowLeft: "back",
    n: "next", N: "next", p: "previous", P: "previous",
    s: "shuffle", S: "shuffle", r: "repeat", R: "repeat",
};

function shortcut(e) {
    if (e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || e.isComposing)
        return;
    const target = e.target instanceof Element ? e.target : document.body;
    if (target.closest("input, textarea, select, [contenteditable]:not([contenteditable=false])"))
        return;
    // Nothing behind an open dialog.
    if (document.querySelector(".dialog-backdrop, dialog[open]"))
        return;
    if (e.key === "/") {
        e.preventDefault();
        Blazor.navigateTo("search");
        return;
    }
    let action = keys[e.key];
    if (!action || !document.querySelector(".player-bar"))
        return;
    // Shift with the arrows goes from song to song rather than through one.
    if (e.shiftKey && (e.key === "ArrowRight" || e.key === "ArrowLeft"))
        action = e.key === "ArrowRight" ? "next" : "previous";
    // Space on a focused button or link presses it, as it always does.
    if (e.key === " " && target.closest("button, a, [role=slider]"))
        return;
    e.preventDefault();
    player.invokeMethodAsync("OnKey", action);
}

/** Whether the browser expects to play this type; an empty type is given the benefit of the doubt. */
export function canPlay(type) {
    return !type || (audio ?? new Audio()).canPlayType(type) !== "";
}

export function load(url, meta) {
    second = -1;
    audio.src = url;
    if ("mediaSession" in navigator && "MediaMetadata" in window)
        navigator.mediaSession.metadata = new MediaMetadata({
            title: meta.title, artist: meta.artist, album: meta.album ?? "",
            artwork: meta.cover ? [{ src: new URL(meta.cover, document.baseURI).href }] : [],
        });
    play();
}

export function play() {
    audio.play().catch(e => {
        // Loading another track first isn't a failure; the browser refusing to start without a click is shown as paused.
        if (e.name !== "AbortError")
            player.invokeMethodAsync("OnPlaying", false);
    });
}

export function pause() {
    audio.pause();
}

export function seek(seconds) {
    audio.currentTime = seconds;
}

export function stop() {
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    if ("mediaSession" in navigator)
        navigator.mediaSession.metadata = null;
}

// ---- Volume, kept per browser ----

function storedVolume() {
    try {
        const v = Number(localStorage.getItem("melodex.volume"));
        return localStorage.getItem("melodex.volume") !== null && v >= 0 && v <= 1 ? v : 1;
    } catch {
        return 1;
    }
}

export function getVolume() {
    return audio ? audio.volume : storedVolume();
}

export function setVolume(value) {
    const v = Math.min(1, Math.max(0, value));
    if (audio)
        audio.volume = v;
    try {
        localStorage.setItem("melodex.volume", String(v));
    } catch {
    }
}

// ---- The visualiser: bars that move with the music, drawn under the cover in the full-screen player ----

// The bars are worked out from the song itself, downloaded and decoded once into memory, at the moment the music is
// at. Nothing else plays: the music sent through Web Audio would go quiet when a phone put the browser in the
// background, and a second, silent copy playing beside it took an iPhone's controls with it, showing the music stopped
// when Safari was left while it carried on (#65).
const SIZE = 2048, RATE = 22050, SMOOTHING = 0.8, MIN_DB = -100, MAX_DB = -30;
let frame = 0, canvas, decoded = null, decodedSrc = null, decoding = null;
const window_ = new Float32Array(SIZE), smoothed = new Float32Array(SIZE / 2), data = new Uint8Array(SIZE / 2);
const re = new Float32Array(SIZE), im = new Float32Array(SIZE);
for (let i = 0; i < SIZE; i++)
    window_[i] = 0.42 - 0.5 * Math.cos(2 * Math.PI * i / SIZE) + 0.08 * Math.cos(4 * Math.PI * i / SIZE);

/** The song at src, as one channel of samples at RATE; null if it can't be. One at a time, the latest wanted. */
function decode(src) {
    if (decodedSrc === src || decoding?.src === src)
        return;
    decoded = null;
    decodedSrc = null;
    const controller = new AbortController();
    decoding?.controller.abort();
    decoding = { src, controller };
    fetch(src, { signal: controller.signal })
        .then(response => response.ok ? response.arrayBuffer() : Promise.reject(response.status))
        .then(bytes => new OfflineAudioContext(1, 1, RATE).decodeAudioData(bytes))
        .then(buffer => {
            if (decoding?.src !== src)
                return;
            // One channel: the others averaged in.
            const mono = new Float32Array(buffer.length);
            for (let c = 0; c < buffer.numberOfChannels; c++) {
                const channel = buffer.getChannelData(c);
                for (let i = 0; i < mono.length; i++)
                    mono[i] += channel[i] / buffer.numberOfChannels;
            }
            decoded = mono;
            decodedSrc = src;
            smoothed.fill(0);
        })
        .catch(() => {})
        .finally(() => { if (decoding?.src === src) decoding = null; });
}

/** How loud each frequency is at this moment of the song, 0 to 255, as a Web Audio analyser would say. */
function spectrum(at) {
    const start = Math.floor(at * RATE) - SIZE;
    for (let i = 0; i < SIZE; i++) {
        const j = start + i;
        re[i] = j >= 0 && j < decoded.length ? decoded[j] * window_[i] : 0;
        im[i] = 0;
    }
    // A radix-2 FFT, in place.
    for (let i = 1, j = 0; i < SIZE; i++) {
        let bit = SIZE >> 1;
        for (; j & bit; bit >>= 1)
            j ^= bit;
        j ^= bit;
        if (i < j) {
            [re[i], re[j]] = [re[j], re[i]];
            [im[i], im[j]] = [im[j], im[i]];
        }
    }
    for (let length = 2; length <= SIZE; length <<= 1) {
        const angle = -2 * Math.PI / length;
        for (let i = 0; i < SIZE; i += length)
            for (let k = 0; k < length / 2; k++) {
                const wr = Math.cos(angle * k), wi = Math.sin(angle * k);
                const a = i + k, b = a + length / 2;
                const tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                re[b] = re[a] - tr; im[b] = im[a] - ti;
                re[a] += tr; im[a] += ti;
            }
    }
    for (let k = 0; k < SIZE / 2; k++) {
        smoothed[k] = SMOOTHING * smoothed[k] + (1 - SMOOTHING) * Math.hypot(re[k], im[k]) / SIZE;
        const db = 20 * Math.log10(smoothed[k] || 1e-12);
        data[k] = Math.max(0, Math.min(255, 255 * (db - MIN_DB) / (MAX_DB - MIN_DB)));
    }
}

export function startVisualiser(target) {
    canvas = target;
    if (!audio || !canvas)
        return;
    cancelAnimationFrame(frame);
    const draw = () => {
        frame = requestAnimationFrame(draw);
        if (!canvas.isConnected) {
            stopVisualiser();
            return;
        }
        const src = audio.getAttribute("src");
        if (src)
            decode(src);
        const ratio = window.devicePixelRatio || 1;
        const width = canvas.clientWidth, height = canvas.clientHeight;
        if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
            canvas.width = Math.round(width * ratio);
            canvas.height = Math.round(height * ratio);
        }
        const g = canvas.getContext("2d");
        g.setTransform(ratio, 0, 0, ratio, 0, 0);
        g.clearRect(0, 0, width, height);
        // Still while paused, falling away as they would.
        if (decoded && decodedSrc === src)
            spectrum(audio.paused ? -1 : audio.currentTime);
        else
            data.fill(0);
        // Bars over a logarithmic scale, as we hear pitch, from about 40 Hz to 10 kHz, mirrored from the middle out:
        // bass in the centre. Each bar is the loudest of the frequencies it covers.
        const hz = RATE / 2 / data.length;
        const low = Math.max(1, Math.floor(40 / hz)), high = Math.min(data.length - 1, Math.floor(10000 / hz));
        const half = Math.max(8, Math.floor(width / 14));
        const bar = width / (half * 2);
        g.fillStyle = getComputedStyle(canvas).color;
        for (let i = 0; i < half; i++) {
            const from = Math.floor(low * Math.pow(high / low, i / half));
            const to = Math.max(from + 1, Math.floor(low * Math.pow(high / low, (i + 1) / half)));
            let peak = 0;
            for (let b = from; b < to; b++)
                peak = Math.max(peak, data[b]);
            const h = Math.max(2, Math.pow(peak / 255, 1.6) * height);
            const r = Math.min(bar * 0.35, 3);
            for (const x of [width / 2 + i * bar, width / 2 - (i + 1) * bar]) {
                g.beginPath();
                g.roundRect(x + bar * 0.15, height - h, bar * 0.7, h, [r, r, 0, 0]);
                g.fill();
            }
        }
    };
    draw();
}

export function stopVisualiser() {
    cancelAnimationFrame(frame);
    frame = 0;
}

// ---- Lyrics that follow along: the line being sung is lit and kept in view ----

let lyrics, lyricsTimer = 0, userScrolledAt = 0, lastActive = -1;

export function followLyrics(container) {
    unfollowLyrics();
    lyrics = container;
    lastActive = -1;
    if (!lyrics)
        return;
    if (!lyrics.dataset.followed) {
        lyrics.dataset.followed = "1";
        // Scrolling them yourself stops them being moved for a few seconds.
        const held = () => userScrolledAt = Date.now();
        lyrics.addEventListener("wheel", held, { passive: true });
        lyrics.addEventListener("touchmove", held, { passive: true });
        lyrics.addEventListener("click", seekToLine);
    }
    const tick = () => {
        lyricsTimer = requestAnimationFrame(tick);
        if (!lyrics?.isConnected) {
            unfollowLyrics();
            return;
        }
        light(false);
    };
    light(true);
    tick();
}

// Clicking a line plays from it.
function seekToLine(e) {
    const line = e.target.closest("[data-time]");
    if (line && audio) {
        audio.currentTime = Number(line.dataset.time);
        userScrolledAt = 0;
        lastActive = -2;
        if (audio.paused)
            play();
    }
}

function light(jump) {
    const lines = lyrics.querySelectorAll("[data-time]");
    if (lines.length === 0 || !audio)
        return;
    // A little early, as people expect to see a line just before it's sung.
    const now = audio.currentTime + 0.2;
    let active = -1;
    for (let i = 0; i < lines.length && Number(lines[i].dataset.time) <= now; i++)
        active = i;
    if (active === lastActive && !jump)
        return;
    lastActive = active;
    lines.forEach((line, i) => {
        line.classList.toggle("active", i === active);
        line.classList.toggle("past", i < active);
    });
    if (Date.now() - userScrolledAt > 3000) {
        const target = lines[Math.max(active, 0)];
        const top = target.offsetTop - lyrics.clientHeight * 0.35;
        lyrics.scrollTo({ top, behavior: jump ? "auto" : "smooth" });
    }
}

export function unfollowLyrics() {
    cancelAnimationFrame(lyricsTimer);
    lyricsTimer = 0;
    lyrics = null;
}

// ---- The browser's own full screen, for the full-screen player ----

export async function toggleFullscreen(element) {
    try {
        if (document.fullscreenElement)
            await document.exitFullscreen();
        else
            await element.requestFullscreen();
    } catch {
    }
    return !!document.fullscreenElement;
}

export function isFullscreen() {
    return !!document.fullscreenElement;
}

export function exitFullscreen() {
    if (document.fullscreenElement)
        document.exitFullscreen().catch(() => { });
}
