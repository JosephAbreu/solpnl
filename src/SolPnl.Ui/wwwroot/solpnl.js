// Small helpers for SolPnl.Ui components. Bundled locally (no CDN).

const observers = new WeakMap();

/** Reports the element's content width to .NET now and whenever it changes. */
export function observeWidth(element, dotNetRef) {
    if (!element) return;
    let last = -1;
    const report = () => {
        const width = Math.round(element.getBoundingClientRect().width);
        if (width !== last) {
            last = width;
            dotNetRef.invokeMethodAsync("OnWidthChanged", width);
        }
    };
    const observer = new ResizeObserver(report);
    observer.observe(element);
    observers.set(element, observer);
    report();
}

export function unobserve(element) {
    const observer = element && observers.get(element);
    if (observer) {
        observer.disconnect();
        observers.delete(element);
    }
}
