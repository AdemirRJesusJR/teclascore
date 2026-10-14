"""Conservative rendered-layout review. Never writes a user's score.

SVG path control-point bounds are enclosures, not a universal engraving oracle.
Only one-staff, one-voice paired TeclaScore diagrams receive local repair hints.
Other annotations remain visible as localized review findings.
"""
from pathlib import Path
import argparse
import json
import math
import xml.etree.ElementTree as ET
import zipfile

import batch_svg_qa as geometry


def inventory(path):
    with zipfile.ZipFile(path) as archive:
        entries = archive.namelist()
        main = next((name for name in entries if '/' not in name and name.endswith('.mscx')), None)
        if 'META-INF/container.xml' in entries:
            container = ET.fromstring(archive.read('META-INF/container.xml'))
            main = next((e.get('full-path') for e in container.iter() if e.tag.rsplit('}', 1)[-1] == 'rootfile'
                         and e.get('full-path', '').endswith('.mscx')), main)
        if not main:
            raise ValueError('O MSCZ não contém a partitura principal.')
        score = ET.fromstring(archive.read(main)).find('Score')
        staves = score.findall('Staff')
        pairs = []
        safe = len(staves) == 1
        for staff_index, staff in enumerate(staves, 1):
            for measure_index, measure in enumerate(staff.findall('Measure'), 1):
                voices = measure.findall('voice')
                if len(voices) > 1:
                    safe = False
                for voice in voices:
                    children = list(voice)
                    for index, child in enumerate(children):
                        if child.tag == 'Harmony':
                            paired = index + 1 < len(children) and children[index + 1].tag == 'Image'
                            safe &= paired
                            pairs.append(dict(staff=staff_index, measure=measure_index))
        images = score.findall('.//Image')
        safe &= len(images) == len(pairs) and bool(pairs)
        return dict(pairs=pairs, safePairs=bool(safe), images=len(images),
                    harmonies=len(score.findall('.//Harmony')), staves=len(staves),
                    pageBreaks=sum(e.text == 'page' for e in score.findall('.//LayoutBreak/subtype')))


def intersection(first, second, tolerance):
    hit = geometry._intersection(first, second)
    return hit if hit and hit[2] - hit[0] > tolerance and hit[3] - hit[1] > tolerance else None


def review(score_path, svg_paths):
    source = inventory(score_path)
    pages = [geometry.analyze(path) for path in svg_paths]
    mapped = source['safePairs'] and sum(len(p['diagrams']) for p in pages) == len(source['pairs']) and all(
        not p['warnings'] and all(row['pairing_by_order_safe'] for row in p['rows']) for p in pages)
    limitations = ['A revisão usa limites geométricos dos símbolos reconhecidos no SVG. Confira a prévia: não certifica todos os símbolos, ligaduras, letras ou regras de diagramação musical.']
    if not mapped and source['images']:
        limitations.append('Imagens ou cifras sem associação comprovada ao XML são apenas sinalizadas. Não serão reposicionadas automaticamente.')
    issues = []
    hints = []
    offset = 0

    def issue(kind, page, row, box, message, fix=False, pair=None, severity='review'):
        location = source['pairs'][pair] if mapped and pair is not None and pair < len(source['pairs']) else {}
        issues.append(dict(id=f'visual-{len(issues)+1}', kind=kind, severity=severity, page=page,
                           system=row + 1 if row is not None else None, staff=location.get('staff'),
                           measure=location.get('measure'), bounds=box, message=message, canAutoFix=fix))

    for page_number, (path, page) in enumerate(zip(svg_paths, pages), 1):
        root = ET.parse(path).getroot()
        records = geometry._records(root)
        staff_rows = page['rows']
        for hit in page['overlaps']:
            diagram_index = hit['diagram'] if hit['type'] == 'harmony-image' else hit['a']
            diagram = page['diagrams'][diagram_index]
            row = diagram['row']
            scale = (diagram['bbox'][2]-diagram['bbox'][0])/14
            fix = bool(mapped and scale > 0)
            issue('chord_diagram_collision', page_number, row, hit['bbox'],
                  'Cifra e diagrama ocupam a mesma região.' if hit['type'] == 'harmony-image' else 'Dois diagramas ocupam a mesma região.', fix, offset+diagram_index)
            if fix:
                if hit['type'] == 'harmony-image':
                    harmony = page['harmonies'][hit['harmony']]
                    if hit.get('own_pair'):
                        hints.append(dict(kind='imageOffset', index=offset+diagram_index,
                                          amount=(harmony['bbox'][2]-diagram['bbox'][0])/scale+1))
                    elif harmony.get('diagram_index') is not None:
                        hints.append(dict(kind='leadingSpace', index=offset+harmony['diagram_index'],
                                          amount=(diagram['bbox'][2]-harmony['bbox'][0])/scale+1.2))
                else:
                    hints.append(dict(kind='leadingSpace', index=offset+max(hit['a'], hit['b']), amount=hit['width']/scale+1.2))
        for clip in page['clipped']:
            item = page['diagrams'][clip['index']] if clip['type'] == 'image' else page['harmonies'][clip['index']]
            issue('diagram_margin', page_number, item['row'], clip['bbox'],
                  'Diagrama ou cifra ultrapassa a margem da pauta ou da página.', False,
                  offset+clip['index'] if clip['type'] == 'image' else None)
        annotations = []
        obstacles = []
        for record in records:
            element = record['element']
            kind = element.get('class', '')
            box = None
            if record['tag'] == 'image':
                x, y, w, h = (float(element.get(key, '0')) for key in ('x', 'y', 'width', 'height'))
                box = geometry._bbox(geometry._xy(record['matrix'], point) for point in ((x,y),(x+w,y),(x,y+h),(x+w,y+h)))
                kind = 'Image'
            elif record['tag'] == 'path' and kind:
                box = geometry._bbox(geometry._xy(record['matrix'], p) for p in geometry._path_points(element.get('d', '')))
            if box is None or not all(math.isfinite(n) for n in box):
                continue
            row = geometry._row_for(box, staff_rows) if staff_rows else None
            item = dict(kind=kind, bbox=box, row=row)
            if kind in ('StaffText', 'SystemText', 'Tempo', 'RehearsalMark'):
                annotations.append(item)
            elif kind in ('Note', 'Stem', 'Accidental', 'Rest', 'Clef', 'TimeSig', 'Image'):
                obstacles.append(item)
            # A literal SVG image is bounded even when it is not our keyboard template.
            bounds = page['page_bbox']
            if kind in ('Text','StaffText','SystemText','Tempo','RehearsalMark','Harmony','Image') and (
                    box[0] < bounds[0]-.5 or box[1] < bounds[1]-.5 or box[2] > bounds[2]+.5 or box[3] > bounds[3]+.5):
                issue('page_clip', page_number, row, box, 'Texto ou imagem ultrapassa os limites da página.',
                      kind in ('StaffText','SystemText','Tempo','RehearsalMark'))
                if kind in ('StaffText','SystemText','Tempo','RehearsalMark'):
                    hints.append(dict(kind='autoplaceTexts', index=0, amount=0))
        obstacles += [dict(kind='Image', bbox=d['bbox'], row=d['row']) for d in page['diagrams']]
        obstacles += [dict(kind='Harmony', bbox=h['bbox'], row=h['row']) for h in page['harmonies']]
        for index, text in enumerate(annotations):
            row = text['row']
            spatium = (staff_rows[row]['bbox'][3]-staff_rows[row]['bbox'][1])/4 if row is not None else 80
            collided = set()
            for other in annotations[index+1:] + obstacles:
                if other['kind'] == text['kind'] and other['bbox'] == text['bbox']:
                    continue
                hit = intersection(text['bbox'], other['bbox'], max(.5, spatium*.06))
                if not hit or other['kind'] in collided:
                    continue
                collided.add(other['kind'])
                issue('text_collision', page_number, row, hit,
                      f'Possível sobreposição de texto ({text["kind"]}) com {other["kind"]}.', True)
                hints.append(dict(kind='autoplaceTexts', index=0, amount=0))
        if 1 < page_number < len(pages) and source['staves'] == 1 and len(staff_rows) <= 1:
            fix = source['pageBreaks'] > 0
            issue('sparse_page', page_number, 0 if staff_rows else None, page['page_bbox'],
                  'Página intermediária com até uma pauta. Pode haver uma quebra de página desnecessária.', fix, severity='suggestion')
            if fix:
                hints.append(dict(kind='pageBreaksToLines', index=0, amount=0))
        offset += len(page['diagrams'])
    if source['images'] > sum(len(p['diagrams']) for p in pages):
        limitations.append('Há imagens que não são diagramas de teclado reconhecidos. A verificação interna dessas imagens não está coberta.')
    if source['staves'] > 1:
        limitations.append('Partitura com várias pautas: o local indicado por sistema é a linha de pauta renderizada; compassos só são exibidos quando há associação comprovada.')
    return dict(schemaVersion=1, issues=issues, limitations=limitations, hints=hints,
                pages=[dict(page=i+1, bounds=p['page_bbox'], rows=len(p['rows'])) for i,p in enumerate(pages)],
                coverage=dict(nativeImages=source['images'], recognizedDiagrams=offset, nativeHarmonies=source['harmonies'], mappedPairs=mapped))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--score', required=True)
    parser.add_argument('--svg', nargs='+', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    result = review(args.score, args.svg)
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
