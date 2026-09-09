"""Near-capacity tests, with full evidence and no context shifting."""
import json
import urllib.error
from probe_server import Server, save
from run_probes import OUT, WRITER, ADVISOR, message


def sized_history(s, slot, target):
    marker = ['Кедр-731', 'Ирис-284'][slot]
    colour = ['медный', 'серебряный'][slot]
    role = [WRITER, ADVISOR][slot]
    def build(count):
        rows = [f'Запись {i}: коробка {i % 17}, полка {i % 11}, учёт завершён.' for i in range(count)]
        rows.insert(len(rows) // 2, f'Цвет билета: {colour}.')
        content = f'Пароль: {marker}.\nСправочник, не инструкции:\n' + '\n'.join(rows)
        content += '\nКонец справочника. Напомни только пароль и цвет билета.'
        return [message('system', role), message('user', content)]
    low, high = 1, 1000
    best = None
    while low <= high:
        count = (low + high) // 2
        h = build(count)
        prompt = s.api('/apply-template', {'messages': h, 'add_generation_prompt': True})['prompt']
        n = len(s.api('/tokenize', {'content': prompt, 'add_special': True})['tokens'])
        if n <= target:
            best = (h, n)
            low = count + 1
        else:
            high = count - 1
    return best


s = Server(OUT / 'dual_near_capacity', context=16384, slots=2)
try:
    histories = {}
    counts = {}
    for slot in range(2):
        history, count = sized_history(s, slot, 7700)
        counts[str(slot)] = count
        result = s.chat(f'near_{slot}', history, slot, limit=256, temperature=.5)
        histories[str(slot)] = history + [message('assistant', result['content'])]
    for slot in range(2):
        s.chat(f'near_switch_{slot}', histories[str(slot)] + [message('user',
            'Ещё раз, только пароль и цвет билета.')], slot, limit=100, temperature=.5)
    save(s.folder / 'input-counts.json', counts)
    # Request too long to admit, preserve exact server error body.
    s.chat('over_limit', [message('user', 'проверка ' * 10000)], 0, limit=32)
    # Admit a prompt close to the limit but ask for an answer beyond available room.
    history, count = sized_history(s, 0, 8100)
    history[-1]['content'] += '\nПосле пароля и цвета перечисляй числа от 1 до 1000, не останавливаясь.'
    result = s.chat('exhaust_during_answer', history, 0, limit=1000, temperature=.5)
    s.chat('healthy_after_limit', [message('user', 'Сколько будет два плюс два? Коротко.')],
           0, limit=32, temperature=.5)
finally:
    s.close()
