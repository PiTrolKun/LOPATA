import argparse
import json
from pathlib import Path
from probe_server import ROOT, Server, save

WRITER = Path(__file__).with_name('writer.txt').read_text(encoding='utf-8').strip()
ADVISOR = 'Ты редактор-советник. Проверяй текст по заданию автора. Отличай противоречия от допустимых трактовок. Не выдумывай цитаты и факты. Если ошибки нет, не придумывай её.'
OUT = ROOT / 'Тесты/Runeweaver/suite_20260909'


def message(role, text):
    return {'role': role, 'content': text}


def baseline(context):
    s = Server(OUT / f'single_{context}', context=context)
    try:
        if context == 8192:
            old = ROOT / 'Тесты/Literary_issue_20260909/LiteraryWriterCPU.jsonl'
            events = [json.loads(x) for x in old.read_text(encoding='utf-8-sig').splitlines()]
            body = json.loads(next(e['data']['json'] for e in events if e['kind'] == 'request'))
            history = [message('system', WRITER), *body['messages']]
            result = s.chat('writer_scene', history, limit=3072)
            history += [message('assistant', result['content']), message('user',
                'Продолжи эту сцену на 150–200 слов, сохраняя факты предыдущего текста. Не повторяй уже описанные события. Заверши новым поступком Леры.')]
            s.chat('writer_continue', history, limit=900)
            bad = ('Проверь только внутреннюю согласованность фактов. Приведи короткие точные цитаты для каждой ошибки; не оценивай стиль.\n'
                   'В начале сцены Лера заперла единственный ключ от шкафа внутри шкафа. Запасных ключей не было. '
                   'Антон всё время оставался снаружи дома и ни разу не заходил внутрь. '
                   'Через минуту Антон, стоя внутри дома, молча передал Лере тот самый ключ. '
                   'На столе лежали три письма. Лера сожгла два письма, не добавляя новых. На столе осталось два письма.')
            s.chat('advisor_errors', [message('system', ADVISOR), message('user', bad)],
                   temperature=.5, limit=1000)
            clean = ('Проверь только внутреннюю согласованность фактов. Если противоречий нет, так и скажи; не придумывай скрытых событий.\n'
                     'В 15:17 Лера вошла в комнату. На остановившихся настенных часах было 03:17. '
                     'Она сверила время с телефоном. В комнате было два окна. Антон закрыл одно окно, второе оставил открытым. '
                     'На столе лежали три письма. Лера убрала два в ящик, одно оставила на столе. '
                     'Ключ от ящика всё это время лежал у неё в кармане.')
            s.chat('advisor_clean', [message('system', ADVISOR), message('user', clean)],
                   temperature=.5, limit=800)
        else:
            s.chat('short', [message('system', WRITER), message('user',
                'Напиши короткий абзац о пустом вокзале ночью.')], limit=256)
        s.idle('final_idle')
    finally:
        s.close()


def dual():
    s = Server(OUT / 'dual_8192_each', context=16384, slots=2)
    histories = {}
    try:
        for slot, (role, marker, colour) in enumerate([(WRITER, 'Кедр-731', 'медный'),
                                                      (ADVISOR, 'Ирис-284', 'серебряный')]):
            h = [message('system', role), message('user',
                f'Для этого диалога запомни: пароль — {marker}, цвет билета — {colour}. Подтверди одной фразой.')]
            r = s.chat(f'init_{slot}', h, slot, limit=100, temperature=.5)
            h.append(message('assistant', r['content']))
            histories[str(slot)] = h
        for cycle in range(3):
            for slot in range(2):
                h = histories[str(slot)]
                h.append(message('user', 'Какой пароль и цвет билета я назвал? Ответь только этими двумя значениями.'))
                r = s.chat(f'recall_{cycle}_{slot}', h, slot, limit=100, temperature=.5)
                h.append(message('assistant', r['content']))
        save(OUT / 'histories.json', histories)
        # No previous history: KV must not leak secrets into an unrelated request.
        s.chat('no_history_privacy', [message('system', ADVISOR), message('user',
            'Какой пароль я называл? Если в этом диалоге я его не называл, ответь НЕ ЗНАЮ.')],
            1, limit=100, temperature=.5)
        s.chat('cancel_writer', [message('system', WRITER), message('user',
            'Напиши длинный рассказ о вокзале, 2000 слов.')], 0, limit=3000, cancel_after=30)
        s.chat('after_cancel', [message('system', WRITER), message('user',
            'Ответь одним словом: готов.')], 0, limit=30, temperature=.5)
        # Over-limit input with context shifting disabled must be rejected, not offloaded.
        text = 'Служебная строка для проверки вместимости контекста.\n' * 1500
        tokens = s.api('/tokenize', {'content': text})['tokens']
        save(s.folder / 'oversize-token-count.json', {'tokens': len(tokens)})
        s.chat('oversize', [message('user', text + '\nОтветь: конец.')], 0, limit=32)
        s.chat('after_oversize', histories['1'] + [message('user', 'Повтори пароль и цвет билета.')],
               1, limit=100, temperature=.5)
    finally:
        s.close()


def restore():
    s = Server(OUT / 'dual_restart', context=16384, slots=2)
    try:
        histories = json.loads((OUT / 'histories.json').read_text(encoding='utf-8'))
        for slot in range(2):
            s.chat(f'restored_{slot}', histories[str(slot)] + [message('user',
                'Повтори пароль и цвет билета.')], slot, limit=100, temperature=.5)
    finally:
        s.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('phase', choices=['8k', '16k', 'dual', 'restore'])
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    if args.phase == '8k': baseline(8192)
    elif args.phase == '16k': baseline(16384)
    elif args.phase == 'dual': dual()
    else: restore()
