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
    // The visualiser's copy follows the music.
    for (const event of ["play", "pause", "seeked", "ratechange", "loadstart"])
        audio.addEventListener(event, follow);
    document.addEventListener("visibilitychange", follow);
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
        // Said outright, so the controls show the music as it is rather than as the visualiser's copy was.
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

// The bars are drawn from a copy of the music: a second element kept in step with the one you hear, sent through Web
// Audio and out silently. Sent through Web Audio itself, the music you hear would only come out that way, and phones
// stop Web Audio when the browser goes to the background: it would go quiet while the song carried on. The copy plays
// only while the bars are drawn and the page is in view.
let context, analyser, copy, frame = 0, canvas, frames = 0;

export function startVisualiser(target) {
    canvas = target;
    if (!audio || !canvas)
        return;
    if (!context) {
        // Set up when first wanted, by a click, so the copy may play. The stream is the core's own, so the analyser may
        // read it.
        context = new AudioContext();
        copy = new Audio();
        copy.preload = "auto";
        copy.disableRemotePlayback = true;
        const source = context.createMediaElementSource(copy);
        analyser = context.createAnalyser();
        analyser.fftSize = 2048;
        analyser.smoothingTimeConstant = 0.8;
        // Through a silent gain to the speakers, as some browsers only work out a graph that reaches them.
        const silent = context.createGain();
        silent.gain.value = 0;
        source.connect(analyser);
        analyser.connect(silent);
        silent.connect(context.destination);
    }
    context.resume();
    cancelAnimationFrame(frame);
    const data = new Uint8Array(analyser.frequencyBinCount);
    const draw = () => {
        frame = requestAnimationFrame(draw);
        if (!canvas.isConnected) {
            stopVisualiser();
            return;
        }
        // Back in step now and then, as two elements drift.
        if (++frames % 30 === 0)
            follow();
        const ratio = window.devicePixelRatio || 1;
        const width = canvas.clientWidth, height = canvas.clientHeight;
        if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
            canvas.width = Math.round(width * ratio);
            canvas.height = Math.round(height * ratio);
        }
        const g = canvas.getContext("2d");
        g.setTransform(ratio, 0, 0, ratio, 0, 0);
        g.clearRect(0, 0, width, height);
        analyser.getByteFrequencyData(data);
        // Bars over a logarithmic scale, as we hear pitch, from about 40 Hz to 16 kHz, mirrored from the middle out:
        // bass in the centre. Each bar is the loudest of the frequencies it covers.
        const hz = context.sampleRate / 2 / data.length;
        const low = Math.max(1, Math.floor(40 / hz)), high = Math.min(data.length - 1, Math.floor(16000 / hz));
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
    follow();
}

/**
 * Keeps the copy playing what the music is, where it is, while the bars are drawn; otherwise it lets go of the music
 * altogether. Paused but holding it, an iPhone's controls go with the copy, as the last to play: leaving Safari, they
 * showed the music stopped while it carried on.
 */
function follow() {
    if (!copy)
        return;
    const src = audio.getAttribute("src");
    if (!frame || !src || document.hidden) {
        if (copy.getAttribute("src") !== null) {
            copy.pause();
            copy.removeAttribute("src");
            copy.load();
            if ("mediaSession" in navigator)
                navigator.mediaSession.playbackState = audio.paused ? "paused" : "playing";
        }
        return;
    }
    if (copy.getAttribute("src") !== src)
        copy.src = src;
    copy.playbackRate = audio.playbackRate;
    if (Math.abs(copy.currentTime - audio.currentTime) > 0.25)
        copy.currentTime = audio.currentTime;
    if (audio.paused)
        copy.pause();
    else if (copy.paused) {
        context.resume();
        copy.play().catch(() => {});
    }
}

export function stopVisualiser() {
    cancelAnimationFrame(frame);
    frame = 0;
    follow();
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
