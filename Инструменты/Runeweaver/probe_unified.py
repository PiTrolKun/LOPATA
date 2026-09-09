"""Verify the app's asymmetric logical budgets on one unified KV pool."""
from pathlib import Path
from probe_server import Server, save

s = Server(Path(__file__).resolve().parents[2] / 'Тесты/Runeweaver/app_unified_20260909',
           context=24576, slots=2, extra_args=('-kvu', '--no-cache-idle-slots'))
try:
    histories = []
    for slot, target in enumerate((7000, 14500)):
        def build(count):
            return [{'role': 'system', 'content': 'Запомни данные и отвечай кратко.'},
                    {'role': 'user', 'content': f'Пароль: {"Кедр-731" if slot == 0 else "Ирис-284"}.\n' +
                     '\n'.join(f'Запись {i}: коробка на полке, учёт завершён.' for i in range(count)) + '\nНазови пароль.'}]
        low, high, history = 1, 1600, None
        while low <= high:
            mid = (low + high) // 2
            h = build(mid)
            prompt = s.api('/apply-template', {'messages': h, 'add_generation_prompt': True})['prompt']
            n = len(s.api('/tokenize', {'content': prompt, 'add_special': True})['tokens'])
            if n <= target:
                history, low = h, mid + 1
            else:
                high = mid - 1
        result = s.chat(f'fill_{slot}', history, slot, limit=64, temperature=.5)
        assert result.get('finish') == 'stop', result
        histories.append(history + [{'role': 'assistant', 'content': result['content']}])
    for slot in (0, 1):
        result = s.chat(f'recall_{slot}', histories[slot] + [{'role': 'user', 'content': 'Ещё раз назови пароль.'}],
                        slot, limit=64, temperature=.5)
        assert ('Кедр-731' if slot == 0 else 'Ирис-284') in result['content'], result
    save(s.folder / 'verified.json', {'shared': 24576, 'writer': 8192, 'advisor': 16384, 'pid': s.process.pid})
finally:
    s.close()
