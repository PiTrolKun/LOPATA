(() => {
    // Rendered page only; no account storage, tokens, hidden app state or private endpoints.
    const onSite = () => location.hostname === 'chat.deepseek.com';
    const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
    const input = () => [...document.querySelectorAll('textarea')].find(visible);
    const sendControl = () => [...document.querySelectorAll('[role="button"]._52c986b')].find(visible);
    // Observed filled-square stop icon. An unknown icon is never treated as Send.
    const isStop = e => !!e?.querySelector('path[d^="M2 4.88"]');
    const isSend = e => !!e?.querySelector('path[d^="M8.3125 0.980206"]');
    const disabled = e => !e || e.classList.contains('ds-button--disabled') || e.getAttribute('aria-disabled') === 'true';
    let interrupted = false;
    document.addEventListener('click', event => {
        const button = event.target.closest?.('[role="button"]._52c986b');
        if (isStop(button)) interrupted = true;
    }, true);
    const text = root => {
        const walk = e => {
            if (e.nodeType === Node.TEXT_NODE) return e.textContent;
            if (e.nodeType !== Node.ELEMENT_NODE || ['SCRIPT','STYLE','BUTTON','SVG'].includes(e.tagName)) return '';
            if (e.getAttribute('role') === 'button') return '';
            if (e.tagName === 'BR') return '\n';
            const value = [...e.childNodes].map(walk).join('');
            return /^(P|DIV|H[1-6]|LI|UL|OL|PRE|BLOCKQUOTE|TR)$/.test(e.tagName) ? '\n' + value.trim() + '\n' : value;
        };
        return walk(root).replace(/\n[ \t]+/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
    };
    window.__lopataCloud = {
        snapshot() {
            if (!onSite()) return { auth: 'unknown', ready: false, generating: false, answers: [], url: '', problem: 'manual' };
            const field = input(), control = sendControl();
            const login = location.pathname.startsWith('/sign_in') || !!document.querySelector('input[type="password"]');
            const messages = [...document.querySelectorAll('.ds-message')];
            // Only the latest user turn. Virtualized history must not be counted as a new answer.
            const lastUser = messages.map(e => e.classList.contains('d29f3d7d')).lastIndexOf(true);
            const generating = isStop(control) || !!control?.querySelector('.ds-loading');
            const answers = messages.slice(lastUser + 1).filter(e => !e.classList.contains('d29f3d7d')).map(e => {
                const final = e.querySelector('.ds-assistant-message-main-content');
                return { id: e.closest('[data-virtual-list-item-key]')?.getAttribute('data-virtual-list-item-key') || '',
                    text: final ? text(final) : '', copy: !!final && !generating,
                    links: final ? [...new Set([...final.querySelectorAll('a[href]')].map(a => a.href).filter(u => /^https?:\/\//i.test(u)))] : [] };
            });
            const alerts = [...document.querySelectorAll('[role="alert"]')].filter(visible);
            return { auth: login ? 'guest' : field ? 'signed-in' : 'unknown', ready: !!field && !login,
                generating, answers, url: location.href, inputText: field?.value || '',
                sendLabel: isSend(control) ? 'send' : isStop(control) ? 'stop' : '',
                problem: alerts.length ? 'site' : '', interrupted };
        },
        fill(value) {
            if (!onSite()) return false;
            const field = input();
            if (!field || field.disabled || field.readOnly || field.value.length || (field.maxLength > 0 && value.length > field.maxLength)) return false;
            Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(field, value);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
            return field.value === value;
        },
        send(expected) {
            if (!onSite() || input()?.value !== expected) return false;
            const button = sendControl();
            if (disabled(button) || !isSend(button)) return false;
            interrupted = false; button.click(); return true;
        },
        stop() {
            if (!onSite()) return;
            const button = sendControl();
            if (isStop(button)) { interrupted = true; button.click(); }
        }
    };
})();
