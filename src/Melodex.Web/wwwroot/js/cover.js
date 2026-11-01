// A picture that didn't load: the server answers 503 while it's still fetching one, and 404 when there's none. An
// <img> can't tell which, so the covers still waiting are asked after together, in one request every couple of
// seconds, which leaves the browser's few connections to the server free for the page itself.
const waiting = new Set();
const every = 2000;
const maxChecks = 150;
let timer = null;

window.melodexCoverFailed = function (img) {
    waiting.add(img);
    timer ??= setTimeout(check, every);
};

const path = img => new URL(img.src).pathname;

function blank(img) {
    img.parentElement?.classList.replace('pending', 'blank');
}

async function check() {
    const covers = [...waiting].filter(img => img.isConnected);
    waiting.clear();
    let states = {};
    if (covers.length > 0) {
        try {
            const response = await fetch('api/v1/artwork/status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify([...new Set(covers.map(path))]),
            });
            if (response.ok)
                states = await response.json();
        } catch {
        }
    }
    for (const img of covers) {
        if (!img.isConnected)
            continue;
        const checks = Number(img.dataset.checks || 0) + 1;
        img.dataset.checks = checks;
        const state = states[path(img)];
        if (state === 'none' || (state !== 'ready' && checks >= maxChecks)) {
            blank(img);
        } else if (state === 'ready') {
            const url = new URL(img.src);
            url.searchParams.set('try', checks);
            img.src = url.toString();
        } else {
            waiting.add(img);
        }
    }
    timer = waiting.size > 0 ? setTimeout(check, every) : null;
}

// A playlist's four covers show together once they're all in (or given up on), not one by one as each arrives.
window.melodexMosaicPart = function (img) {
    const mosaic = img.parentElement;
    if (!mosaic || [...mosaic.querySelectorAll('img')].some(part => !part.complete))
        return;
    mosaic.classList.replace('pending', 'loaded');
};
