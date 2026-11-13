// The audio element behind the player bar. Player.cs drives it and hears back about time, ends and errors. A second
// one waits beside it with the next song loaded, while that runs on from this one with no gap (#1); as this one ends,
// that one starts, and they swap places.
let audio, spare;
let player;
let second = -1;
// The song loaded in the spare, and what's said of it once it plays: { token, meta }.
let prepared = null;
// Watching the last moments of a song, to start the next exactly as it ends; and a start in progress.
let watching = 0, handing = null;
// Bumped by every load and stop, so a start of the next song that's overtaken by one of them is called off when it comes.
let epoch = 0;
// How far ahead of the end the next song is started, to make up for the moment it takes to begin.
const LEAD = 0.04;
// How long each element's song is, as the server measured its file. Safari misjudges files that don't say up front, such
// as Ogg Opus, a second or two in (#93): its own idea is only taken when it agrees, being exact to the hundredth.
const lengths = new WeakMap();

function lengthOf(el) {
    const measured = lengths.get(el), own = el.duration;
    if (Number.isFinite(own) && (!(measured > 0) || Math.abs(own - measured) < 1))
        return own;
    return measured > 0 ? measured : own;
}

function element() {
    const el = new Audio();
    el.preload = "auto";
    el.volume = storedVolume();
    // Only the one playing is heard from: the spare loading or the one just finished say nothing.
    const on = (name, handler) => el.addEventListener(name, e => { if (el === audio) handler(e); });
    on("timeupdate", () => {
        // While seeking, the time can still be the old one, which would put the seek bar back.
        if (audio.seeking)
            return;
        // Once a second is plenty for a seek bar, and keeps calls into .NET down.
        const now = Math.floor(audio.currentTime);
        if (now !== second) {
            second = now;
            player.invokeMethodAsync("OnTime", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0);
        }
        if (prepared && !watching && Number.isFinite(lengthOf(audio)) && lengthOf(audio) - audio.currentTime < 1.5)
            watch();
    });
    // Where a seek landed, at once, which may not be quite where it was asked for; the lyrics jump there too.
    on("seeked", () => {
        second = Math.floor(audio.currentTime);
        player.invokeMethodAsync("OnTime", audio.currentTime, Number.isFinite(audio.duration) ? audio.duration : 0);
        if (lyrics?.isConnected)
            light(true);
    });
    on("ended", () => {
        unwatch();
        // A start of the next one still going decides: on its way, it's played; refused, it's as if there were none.
        if (handing)
            handing.then(started => { if (started === false) player.invokeMethodAsync("OnEnded"); });
        else
            player.invokeMethodAsync("OnEnded");
    });
    on("playing", () => player.invokeMethodAsync("OnPlaying", true));
    on("pause", () => { if (!audio.ended) player.invokeMethodAsync("OnPlaying", false); });
    on("error", () => {
        if (audio.getAttribute("src"))
            player.invokeMethodAsync("OnError", audio.error?.message || "The file couldn't be played.");
    });
    return el;
}

export function init(dotnet) {
    player = dotnet;
    if (audio)
        return;
    document.addEventListener("keydown", shortcut);
    audio = element();
    spare = element();
    if ("mediaSession" in navigator) {
        // Media keys, and the lock screen on a phone.
        navigator.mediaSession.setActionHandler("play", () => audio.play());
        navigator.mediaSession.setActionHandler("pause", () => audio.pause());
        navigator.mediaSession.setActionHandler("previoustrack", () => player.invokeMethodAsync("OnRemote", "previous"));
        navigator.mediaSession.setActionHandler("nexttrack", () => player.invokeMethodAsync("OnRemote", "next"));
        // Whether it's playing is left to the phone, which follows the music itself: told outright, an iPhone showed it
        // paused as it started, and held on to it after stop (#94).
    }
}

function setMetadata(meta) {
    if ("mediaSession" in navigator && "MediaMetadata" in window)
        navigator.mediaSession.metadata = meta ? new MediaMetadata({
            title: meta.title, artist: meta.artist, album: meta.album ?? "",
            artwork: meta.cover ? [{ src: new URL(meta.cover, document.baseURI).href }] : [],
        }) : null;
}

/** Loads the next song into the spare, to start as this one ends. */
export function prepare(url, meta, token, length) {
    prepared = { token, meta };
    // Muted until it plays: a phone offers its controls to any player with sound loaded, and an iPhone chose this one,
    // waiting, showing the music paused while the other played (#94).
    spare.muted = true;
    lengths.set(spare, length);
    if (spare.getAttribute("src") !== url) {
        spare.src = url;
        spare.load();
    }
}

/** Forgets the song loaded ahead: the queue has changed, or it's no longer to run on from this one. */
export function unprepare() {
    prepared = null;
    unwatch();
    if (spare.getAttribute("src")) {
        spare.removeAttribute("src");
        spare.load();
    }
}

// Close to the end, the time is checked often enough to start the next song within a few hundredths of a second.
function watch() {
    watching = setInterval(() => {
        if (!prepared || audio.paused || audio.seeking)
            return;
        const left = lengthOf(audio) - audio.currentTime;
        // Sought back from the end: watched again when it gets there.
        if (left > 2)
            unwatch();
        else if (left <= LEAD)
            handOff();
    }, 10);
}

function unwatch() {
    clearInterval(watching);
    watching = 0;
}

function handOff() {
    unwatch();
    const from = audio, to = spare, next = prepared, at = epoch;
    prepared = null;
    to.volume = from.volume;
    to.muted = false;
    handing = to.play().then(() => {
        // Another song was chosen, or the music stopped, while this one was starting (a phone can take a while): it's
        // stopped again, or both would play.
        if (at !== epoch) {
            to.pause();
            to.removeAttribute("src");
            to.load();
            return null;
        }
        audio = to;
        spare = from;
        second = -1;
        // What's left of the last one is a moment, and the next has it covered.
        from.pause();
        from.removeAttribute("src");
        from.load();
        setMetadata(next.meta);
        player.invokeMethodAsync("OnPlaying", true);
        player.invokeMethodAsync("OnAdvanced", next.token);
        return true;
    }, () => {
        if (at !== epoch)
            return null;
        // Not allowed to start it without a tap, as some phones won't: the song ends, and the next loads as it always has.
        to.removeAttribute("src");
        to.load();
        return false;
    }).finally(() => { handing = null; });
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

export function load(url, meta, length) {
    epoch++;
    lengths.set(audio, length);
    second = -1;
    unwatch();
    audio.src = url;
    setMetadata(meta);
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
    epoch++;
    unprepare();
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    setMetadata(null);
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
    if (audio) {
        audio.volume = v;
        spare.volume = v;
    }
    try {
        localStorage.setItem("melodex.volume", String(v));
    } catch {
    }
}

// ---- The visualiser: bars that move with the music, drawn under the cover in the full-screen player ----

// The bars are worked out from each song by the server (#89): how loud each band of pitch is, many times a second, from
// bass up. They're fetched once for each song and played back with it. Nothing else plays: the music sent through Web
// Audio would go quiet when a phone put the browser in the background, and a second, silent copy playing beside it took
// an iPhone's controls with it, showing the music stopped when Safari was left while it carried on (#65).
const HEADER = 8;
let frame = 0, canvas, bars = null, barsSrc = null, loading = null;
// Each bar's height now, 0 to 255, falling away while paused.
let levels = new Float32Array(64);

/** The song at src's bars, from the server: { fps, count, frames }; null until they're here, or if there are none. */
function loadBars(src) {
    if (barsSrc === src || loading?.src === src)
        return;
    bars = null;
    barsSrc = null;
    const controller = new AbortController();
    loading?.controller.abort();
    loading = { src, controller };
    fetch(src.replace("/stream?", "/bars?"), { signal: controller.signal })
        .then(response => response.ok ? response.arrayBuffer() : Promise.reject(response.status))
        .then(buffer => {
            const bytes = new Uint8Array(buffer);
            // MDXB, the version, frames a second, bars a frame.
            if (loading?.src !== src)
                return;
            barsSrc = src;
            if (bytes.length < HEADER || String.fromCharCode(...bytes.subarray(0, 4)) !== "MDXB" || bytes[4] !== 1)
                return;
            bars = { fps: bytes[5], count: bytes[6], frames: bytes.subarray(HEADER) };
            if (levels.length !== bars.count)
                levels = new Float32Array(bars.count);
        })
        // Not asked for again for this song: the bars stay down.
        .catch(() => { if (loading?.src === src) barsSrc = src; })
        .finally(() => { if (loading?.src === src) loading = null; });
}

/** Each bar's height at this moment of the song, between the frames either side of it; with no moment, falling away. */
function spectrum(at) {
    if (at < 0 || !bars || bars.frames.length < bars.count) {
        for (let i = 0; i < levels.length; i++)
            levels[i] *= 0.85;
        return;
    }
    const { fps, count, frames } = bars;
    const last = frames.length / count - 1;
    const position = Math.min(Math.max(at * fps, 0), last);
    const a = Math.floor(position), b = Math.min(a + 1, last), t = position - a;
    for (let i = 0; i < count; i++)
        levels[i] = frames[a * count + i] * (1 - t) + frames[b * count + i] * t;
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
            loadBars(src);
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
        spectrum(bars && barsSrc === src && !audio.paused ? audio.currentTime : -1);
        // Mirrored from the middle out: bass in the centre. Each bar drawn is the loudest of the bands it covers, about
        // 40 Hz to 10 kHz on a logarithmic scale, as we hear pitch.
        const half = Math.max(8, Math.floor(width / 14));
        const bar = width / (half * 2);
        g.fillStyle = getComputedStyle(canvas).color;
        for (let i = 0; i < half; i++) {
            const from = Math.floor(i * levels.length / half);
            const to = Math.max(from + 1, Math.floor((i + 1) * levels.length / half));
            let peak = 0;
            for (let b = from; b < to && b < levels.length; b++)
                peak = Math.max(peak, levels[b]);
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
