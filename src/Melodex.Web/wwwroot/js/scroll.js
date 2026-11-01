// Where the page is scrolled to, so a page can be shown again as it was left. The page may still be filling in (covers,
// sections drawn after the first), so going back keeps trying for a moment until it's long enough to get there.
window.melodexScroll = {
    get: () => window.scrollY,
    to: y => {
        let tries = 0;
        const go = () => {
            window.scrollTo({ top: y, behavior: 'instant' });
            if (Math.abs(window.scrollY - y) > 2 && ++tries < 20)
                setTimeout(go, 50);
        };
        go();
    },
};

// A scan's files on the Activity page: opened where the scan is, and following it along unless someone's scrolled or
// clicked in the list in the last little while.
window.melodexScanList = {
    show: (list, index, opening) => {
        if (!list)
            return;
        if (list.melodexTouched === undefined) {
            list.melodexTouched = 0;
            for (const name of ['wheel', 'touchstart', 'pointerdown', 'keydown'])
                list.addEventListener(name, () => list.melodexTouched = Date.now(), { passive: true });
        }
        if (!opening && Date.now() - list.melodexTouched < 8000)
            return;
        // Rows are one height (.scan-file); the current one goes in the middle.
        const row = 52;
        const top = Math.max(0, index * row - (list.clientHeight - row) / 2);
        if (Math.abs(list.scrollTop - top) > 2)
            list.scrollTo({ top, behavior: opening ? 'instant' : 'smooth' });
    },
};

// Back and forward put each page where it was scrolled to this session. Where a page was is noted as it's left, by a
// link or by back and forward; the browser's own attempt comes before the page is drawn, so it's left to this.
(() => {
    const positions = new Map();
    let here = location.href;
    const leave = () => positions.set(here, window.scrollY);
    history.scrollRestoration = 'manual';

    const push = history.pushState.bind(history);
    history.pushState = (...args) => {
        leave();
        push(...args);
        here = location.href;
    };
    const replace = history.replaceState.bind(history);
    history.replaceState = (...args) => {
        replace(...args);
        here = location.href;
    };
    addEventListener('popstate', () => {
        leave();
        here = location.href;
        const y = positions.get(here) ?? 0;
        setTimeout(() => window.melodexScroll.to(y), 0);
    });
})();
