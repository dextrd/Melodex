// The top bar keeps to its lines (one, or two on a phone): when the pages' names would push something onto another
// line, they're shown as icons instead. Looked at again whenever the bar changes size or what's in it changes.
window.melodexTopbar = {
    watch: bar => {
        if (!bar || bar.melodexWatched)
            return;
        bar.melodexWatched = true;
        const phone = matchMedia('(max-width: 640px)');
        // The lines the bar's items are on, by their middles: items on a line are centred on it.
        const lines = () => {
            const middles = [];
            for (const item of bar.children) {
                const box = item.getBoundingClientRect();
                if (box.height === 0)
                    continue;
                const middle = (box.top + box.bottom) / 2;
                if (!middles.some(m => Math.abs(m - middle) < 6))
                    middles.push(middle);
            }
            return middles.length;
        };
        let queued = false;
        const fit = () => {
            queued = false;
            bar.classList.remove('compact');
            if (lines() > (phone.matches ? 2 : 1))
                bar.classList.add('compact');
        };
        const later = () => {
            if (!queued) {
                queued = true;
                requestAnimationFrame(fit);
            }
        };
        new ResizeObserver(later).observe(bar);
        new MutationObserver(later).observe(bar, { childList: true, subtree: true, characterData: true });
        phone.addEventListener('change', later);
        fit();
    },
};
