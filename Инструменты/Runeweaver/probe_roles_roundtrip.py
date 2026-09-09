"""One real Writer -> Advisor -> Writer transfer on shared weights."""
import json
from probe_server import Server, save
from run_probes import OUT, WRITER, ADVISOR, message

s = Server(OUT / 'dual_roles_roundtrip', context=16384, slots=2)
try:
    writer = [message('system', WRITER), message('user',
        'Напиши сцену на 180–220 слов: Лера возвращает Антону найденный билет на вокзале. '
        'Бытовая история, без мистики. Антон говорит коротко, Лера задаёт вопросы. '
        'Билет синий. Закончи одной короткой репликой Леры.')]
    r = s.chat('writer_draft', writer, 0, limit=1100)
    writer.append(message('assistant', r['content']))
    advisor = [message('system', ADVISOR), message('user',
        'Проверь текст по условиям: бытовая сцена без мистики; билет синий; Антон говорит коротко; '
        'Лера задаёт вопросы; последняя строка — короткая реплика Леры. '
        'Назови только реальные нарушения, затем до двух предложений по улучшению. Текст:\n' + r['content'])]
    a = s.chat('advisor_review', advisor, 1, limit=700, temperature=.5)
    writer.append(message('user', 'Вот замечания редактора. Исправь только то, что действительно нужно. '
        'Сохрани синий билет и закончи одной короткой репликой Леры. Текст замечаний:\n' + a['content']))
    s.chat('writer_revision', writer, 0, limit=1100)
finally:
    s.close()
