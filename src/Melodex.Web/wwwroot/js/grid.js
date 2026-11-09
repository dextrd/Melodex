// Measures a grid of tiles (see TileGrid.razor) for .NET: how many tiles fit in a row, and how tall a row is. It's
// measured again as the grid is resized. The rows not drawn are slots in the shape of the tiles drawn, their cover
// and lines of text, kept as CSS variables: images of one tile and the gap beside and below it, repeated.
export function observe(element, target) {
    let columns = 0, height = 0, tries = 0, timer = 0, shapes = '';
    const measure = () => {
        const probe = element.querySelector('.tile-probe');
        if (!probe)
            return;
        const style = getComputedStyle(probe);
        const tracks = style.gridTemplateColumns.split(' ').filter(t => t.endsWith('px')).map(parseFloat);
        const rowGap = parseFloat(style.rowGap) || 0;
        const columnGap = parseFloat(style.columnGap) || 0;
        const row = element.querySelector('.tile-row');
        const rowHeight = row ? row.getBoundingClientRect().height + rowGap : 0;
        const tile = row?.querySelector('.tile');
        if (tile && tracks.length) {
            const drawn = slots(tile, tracks[0] + columnGap, rowHeight);
            if (drawn.key !== shapes) {
                shapes = drawn.key;
                for (const [name, value] of Object.entries(drawn.vars))
                    element.style.setProperty(name, value);
            }
        }
        if (tracks.length !== columns || Math.abs(rowHeight - height) >= 1) {
            columns = tracks.length;
            height = rowHeight;
            target.invokeMethodAsync('Measured', columns, rowHeight);
        }
        // The first rows are drawn once .NET knows how many fit, so their height is looked for again shortly.
        clearTimeout(timer);
        if (!row && tries++ < 20)
            timer = setTimeout(measure, 100);
    };
    const resized = new ResizeObserver(measure);
    resized.observe(element);
    measure();
    return {
        dispose: () => {
            resized.disconnect();
            clearTimeout(timer);
        },
    };
}

// A tile, and what's in it: its cover, a bar for each line of text (of varying length, as names are), and its meter as
// it is. Each is an SVG of one cell, a tile and the gaps after it.
function slots(tile, cellWidth, cellHeight) {
    const box = tile.getBoundingClientRect();
    const radius = parseFloat(getComputedStyle(tile).borderTopLeftRadius) || 0;
    const w = round(box.width), h = round(box.height);
    const lengths = [.85, .6, 1, .7];
    const parts = [];
    let line = 0;
    for (const child of tile.children) {
        const r = child.getBoundingClientRect();
        if (!r.width || !r.height)
            continue;
        const x = round(r.left - box.left), y = round(r.top - box.top);
        if (child.matches('.cover'))
            parts.push(rect(x, y, r.width, r.height, parseFloat(getComputedStyle(child).borderTopLeftRadius) || 6));
        else if (!child.textContent.trim())
            parts.push(rect(x, y, r.width, r.height, r.height / 2));
        else {
            const bar = Math.min(r.height, round(parseFloat(getComputedStyle(child).fontSize) * .7));
            parts.push(rect(x, y + (r.height - bar) / 2, r.width * lengths[line++ % lengths.length], bar, 4));
        }
    }
    const svg = body => `url("data:image/svg+xml,${encodeURIComponent(
        `<svg xmlns='http://www.w3.org/2000/svg' width='${round(cellWidth)}' height='${round(cellHeight)}'>${body}</svg>`)}")`;
    const vars = {
        '--slot-cell': `${cellWidth}px ${cellHeight}px`,
        '--slot-tile': svg(rect(0, 0, w, h, radius)),
        '--slot-parts': svg(parts.join('')),
    };
    return { key: Object.values(vars).join(), vars };
}

const round = n => Math.round(n * 100) / 100;
const rect = (x, y, w, h, r) => `<rect x='${round(x)}' y='${round(y)}' width='${round(w)}' height='${round(h)}' rx='${round(r)}'/>`;
