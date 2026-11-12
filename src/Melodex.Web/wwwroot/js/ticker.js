// Names too long for their space scroll like a ticker (#86): across to their end, on past it, and round to the start
// again, rather than being cut off. That's anything marked .ticker, and the title line of the song playing in a track
// list. Names that fit stay still, and nothing moves for people who'd rather it didn't.
//
// The page's own markup isn't touched, as Blazor keeps it: the text is copied into an attribute that a ::after shows
// after a gap (hidden from screen readers, which read the name once), and the line slides along by its width and the
// gap, so the copy ends up where the name started and the loop can't be seen.
(() => {
    const SELECTOR = ".ticker, tr.playing .ellipsis:first-of-type";
    const SPEED = 40;          // pixels a second
    const HOLD = 0.2;          // of each loop, still at the start
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    let ticking = new Set();
    let queued = false;

    function stop(el) {
        el.removeAttribute("data-ticking");
        el.removeAttribute("data-ticker");
        el.style.removeProperty("--ticker-distance");
        el.style.removeProperty("--ticker-time");
    }

    function refresh() {
        queued = false;
        const now = new Set(reduced.matches ? [] : document.querySelectorAll(SELECTOR));
        for (const el of ticking)
            if (!now.has(el))
                stop(el);
        for (const el of now) {
            // The name's own width, without the copy, which leaves a moving name moving: taking the copy away to
            // measure would start it again from the beginning, every time the page changes.
            const range = document.createRange();
            range.selectNodeContents(el);
            const width = Math.ceil(range.getBoundingClientRect().width), room = el.clientWidth;
            if (width <= room + 1) {
                stop(el);
                now.delete(el);
                continue;
            }
            const gap = parseFloat(getComputedStyle(el).fontSize) * 2.5;
            const distance = `${width + gap}px`, time = `${((width + gap) / SPEED / (1 - HOLD)).toFixed(2)}s`;
            const text = el.innerText.replace(/\s+/g, " ").trim();
            if (el.getAttribute("data-ticker") !== text)
                el.setAttribute("data-ticker", text);
            if (el.style.getPropertyValue("--ticker-distance") !== distance)
                el.style.setProperty("--ticker-distance", distance);
            if (el.style.getPropertyValue("--ticker-time") !== time)
                el.style.setProperty("--ticker-time", time);
            if (!el.hasAttribute("data-ticking"))
                el.setAttribute("data-ticking", "");
        }
        ticking = now;
    }

    function queue() {
        if (!queued) {
            queued = true;
            requestAnimationFrame(refresh);
        }
    }

    // Only the class changes Blazor makes are watched, not the attributes set here.
    new MutationObserver(queue).observe(document.documentElement,
        { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["class"] });
    addEventListener("resize", queue);
    reduced.addEventListener("change", queue);
    queue();
})();
