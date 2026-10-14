(() => {
    // Public rendered page only. No cookies, private state, endpoints or clipboard access.
    const onAlice = () => location.hostname === 'alice.yandex.ru';
    const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
    const input = () => [...document.querySelectorAll('textarea')].find(visible);
    const buttons = () => [...document.querySelectorAll('button,[role="button"]')].filter(visible);
    const label = e => (e.getAttribute('aria-label') || e.getAttribute('title') || '').trim();
    const markdownText = root => {
        const walk = e => {
            if (e.nodeType === Node.TEXT_NODE) return e.textContent;
            if (e.nodeType !== Node.ELEMENT_NODE || ['SCRIPT','STYLE','BUTTON','SVG'].includes(e.tagName)) return '';
            if (e.tagName === 'BR') return '\n';
            const text = [...e.childNodes].map(walk).join('');
            if (e.tagName === 'LI') return '\n• ' + text.trim() + '\n';
            return /^(P|H[1-6]|UL|OL|PRE|BLOCKQUOTE|TR)$/.test(e.tagName) ? '\n' + text.trim() + '\n' : text;
        };
        return walk(root).replace(/\n[ \t]+/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
    };
    window.__lopataAlice = {
        snapshot() {
            if (!onAlice()) return { auth: 'unknown', ready: false, generating: false, answers: [], url: '',
                problem: /captcha|showcaptcha/i.test(location.pathname) ? 'challenge' : '' };
            const field = input();
            const controls = buttons();
            const profile = document.querySelector('.ChatSidebar-B2BFooter,[aria-label^="Профиль"],[aria-label^="Profile"]');
            const login = controls.some(e => /^(Войти|Log in|Sign in)$/i.test(e.innerText.trim()));
            const answers = [...document.querySelectorAll('.FuturisMarkdown')].filter(visible).map(md => {
                let container = md.closest('.Message,.FuturisChat-Message,.FuturisMessage');
                if (!container) {
                    container = md.parentElement;
                    for (let i = 0; i < 6 && container && ![...container.querySelectorAll('button')].some(b => /Копировать|Copy/i.test(label(b))); i++)
                        container = container.parentElement;
                }
                if (!container) return null;
                const speaker = container.querySelector('.AliceTextBubble_from_alice') ||
                    [...container.querySelectorAll('h1,h2,h3,[role="heading"]')].some(h => /^(Алиса|Alice)$/.test(h.innerText.trim()));
                if (!speaker) return null;
                const idElement = container.id ? container : container.closest('[id]');
                const copy = [...container.querySelectorAll('button')].some(b => /^(Копировать|Copy)$/i.test(label(b)));
                return { id: idElement?.id || '', text: markdownText(md), copy,
                    links: [...new Set([...md.querySelectorAll('a[href]')].map(a => a.href).filter(u => /^https?:\/\//i.test(u)))] };
            }).filter(Boolean);
            const stop = controls.some(e => /^(Остановить|Остановить генерацию|Остановить ответ|Stop|Stop generating)$/i.test(label(e)));
            const generating = stop || !!document.querySelector('[aria-busy="true"],.FuturisAnswer_loading,.AliceTextBubble_loading');
            const alerts = [...document.querySelectorAll('[role="alert"]')].filter(visible).map(e => e.innerText);
            return { auth: profile ? 'signed-in' : login ? 'guest' : 'unknown', ready: !!field, generating, answers,
                url: location.href, inputText: field?.value || '', sendLabel: label(document.getElementById('oknyx-button') || document.createElement('button')),
                problem: alerts.length ? 'site' : /captcha|showcaptcha/i.test(location.pathname) ? 'challenge' : '' };
        },
        fill(text) {
            if (!onAlice()) return false;
            const field = input();
            if (!field || field.disabled || field.readOnly || (field.maxLength > 0 && text.length > field.maxLength)) return false;
            Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(field, text);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
            return field.value === text;
        },
        send(expected) {
            if (!onAlice() || input()?.value !== expected) return false;
            const button = document.getElementById('oknyx-button') || buttons().find(e => /^(Отправить|Отправить сообщение|Send|Send message)$/i.test(label(e)));
            if (!button || button.disabled || /слуш|listen|stop|останов/i.test(label(button))) return false;
            button.click();
            return true;
        },
        stop() {
            if (!onAlice()) return;
            const button = buttons().find(e => /^(Остановить|Остановить генерацию|Остановить ответ|Stop|Stop generating)$/i.test(label(e)));
            button?.click();
        }
    };
})();
