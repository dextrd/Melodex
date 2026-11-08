// Tells .NET when an element nears the screen, so a long list can draw more of itself as it's scrolled. The element
// takes up the room the rest of the list will, so the scrollbar is the right size from the start.
export function observe(element, target) {
    const observer = new IntersectionObserver(async entries => {
        if (!entries.some(e => e.isIntersecting))
            return;
        await target.invokeMethodAsync('MoreAsync');
        // Look again: if it's still near the screen after drawing more, draw more again.
        observer.unobserve(element);
        observer.observe(element);
    }, { root: scroller(), rootMargin: '1000px 0px' });
    observer.observe(element);
    const resized = new ResizeObserver(() => size(element));
    resized.observe(element.parentElement);
    size(element);
    return {
        size: () => size(element),
        dispose: () => {
            observer.disconnect();
            resized.disconnect();
        },
    };
}

// Worked out from what's drawn so far, the list just before: how tall a row is, and for tiles, how many to a row.
function size(element) {
    const list = element.previousElementSibling;
    const items = list?.matches('table') ? list.tBodies[0]?.children : list?.children;
    const remaining = Number(element.dataset.remaining || 0);
    if (!items?.length || remaining <= 0) {
        element.style.height = '';
        return;
    }
    const top = items[0].offsetTop;
    let perRow = 1;
    while (perRow < items.length && items[perRow].offsetTop === top)
        perRow++;
    const rowHeight = list.getBoundingClientRect().height / Math.ceil(items.length / perRow);
    element.style.height = `${Math.ceil(remaining / perRow) * rowHeight}px`;
}

// What the list scrolls in: the page's own box on a computer, else the screen. Asked of the box itself, as a margin
// reaches past what it clips only when it's the root.
function scroller() {
    const scrolling = window.melodexScroll?.scroller();
    return scrolling && scrolling !== document.scrollingElement ? scrolling : null;
}
