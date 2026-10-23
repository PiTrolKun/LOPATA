// Trusted application controls. The SVG is an inert image; CSP rejects scripts supplied by documents.
(() => {
    const image = document.getElementById('vector');
    let width = innerWidth, height = innerHeight, aspect = 1, scale = 1, x = 0, y = 0, drag = null;
    const draw = () => {
        const w = Math.min(width, height * aspect), h = w / aspect;
        Object.assign(image.style, {
            left: ((width - w) / 2 * scale + x) + 'px', top: ((height - h) / 2 * scale + y) + 'px',
            width: (w * scale) + 'px', height: (h * scale) + 'px'
        });
    };
    const report = () => chrome.webview.postMessage([scale, x, y]);
    document.addEventListener('wheel', e => {
        e.preventDefault();
        const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? height : 1;
        const next = Math.max(.25, Math.min(32, scale * Math.pow(1.2, -e.deltaY * unit / 100)));
        const factor = next / scale;
        x = e.clientX + (x - e.clientX) * factor; y = e.clientY + (y - e.clientY) * factor;
        scale = next; draw(); report();
    }, { passive: false });
    document.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        drag = [e.clientX, e.clientY]; document.body.setPointerCapture(e.pointerId); document.body.style.cursor = 'grabbing';
    });
    document.addEventListener('pointermove', e => {
        if (!drag) return;
        x += e.clientX - drag[0]; y += e.clientY - drag[1]; drag = [e.clientX, e.clientY]; draw(); report();
    });
    const release = () => { drag = null; document.body.style.cursor = ''; };
    document.addEventListener('pointerup', release); document.addEventListener('lostpointercapture', release);
    document.addEventListener('dblclick', () => { scale = 1; x = y = 0; draw(); report(); });
    window.__lopataVector = Object.freeze({ snapshot: () => [scale, x, y], set(v) {
        [width, height, aspect, scale, x, y] = v; draw(); return true;
    }});
})()
