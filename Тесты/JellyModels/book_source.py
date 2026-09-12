"""Read the stand EPUB in spine order; preserve normalized source offsets."""
import hashlib
import json
from pathlib import Path
from html.parser import HTMLParser
import posixpath
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]

def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)

class BodyText(HTMLParser):
    def __init__(self):
        super().__init__()
        self.active = False
        self.parts = []
    def handle_starttag(self, tag, attrs):
        if tag == 'body': self.active = True
        if self.active and tag in ('p', 'div', 'br'): self.parts.append('\n')
    def handle_endtag(self, tag):
        if tag == 'body': self.active = False
        if self.active and tag in ('p', 'div'): self.parts.append('\n')
    def handle_data(self, data):
        if self.active: self.parts.append(data)

def prepare(destination, limit=2600):
    book = ROOT / 'Тесты/LiteraryReading/books-local/Пиковая_дама.epub'
    chapters, full, chunks = [], '', []
    with zipfile.ZipFile(book) as archive:
        container = ET.fromstring(archive.read('META-INF/container.xml'))
        opf = next(x for x in container.iter() if x.tag.endswith('rootfile')).attrib['full-path']
        package = ET.fromstring(archive.read(opf))
        manifest = {x.attrib['id']: x.attrib['href'] for x in package.iter() if x.tag.endswith('}item')}
        for item in package.iter():
            if not item.tag.endswith('}itemref'): continue
            href = manifest[item.attrib['idref']]
            # This fixed fixture has seven narrative spine items; cover/annotation are not story.
            if not Path(href).name.startswith('chapter'): continue
            parser = BodyText()
            parser.feed(archive.read(posixpath.join(posixpath.dirname(opf), href)).decode('utf-8'))
            paragraphs = [' '.join(x.split()) for x in ''.join(parser.parts).splitlines() if x.strip()]
            text = '\n\n'.join(paragraphs)
            start = len(full)
            full += text + '\n\n'
            chapters.append(dict(path=href, start=start, end=start+len(text), title=paragraphs[0]))
            local = 0
            while local < len(text):
                end = min(local+limit, len(text))
                if end < len(text):
                    boundary = text.rfind('\n\n', local+limit//2, end)
                    if boundary != -1: end = boundary+2
                    else:
                        boundary = text.rfind(' ', local+limit//2, end)
                        if boundary != -1: end = boundary+1
                chunks.append(dict(id=len(chunks)+1, chapter=paragraphs[0], start=start+local,
                                   end=start+end, text=text[local:end]))
                local = end
    value = dict(title='Пиковая дама', corpus='reference', source_path=str(book),
                 epub_sha256=hashlib.sha256(book.read_bytes()).hexdigest(),
                 sha256=hashlib.sha256(full.encode()).hexdigest(), text=full,
                 chapters=chapters, chunks=chunks, chunk_limit=limit)
    save(destination / 'source.json', value)
    (destination / 'source.txt').write_text(full, encoding='utf-8')
    return value

if __name__ == '__main__':
    result = prepare(HERE / 'book')
    print(len(result['text']), 'characters;', len(result['chunks']), 'chunks;', len(result['chapters']), 'chapters')
