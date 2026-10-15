"""Conservative, full-size spacing candidates for generated single-staff scores.

MuseScore's per-measure vspacerUp reserves only the system containing that
measure. A candidate never changes horizontal spacing, breaks, text offsets,
font sizes or musical elements, and is accepted only after native rendering.
"""
import re
import xml.etree.ElementTree as E
import batch_svg_qa as geometry

SPACING = {'minSystemDistance':12, 'maxSystemDistance':24,
           'minSystemSpread':12, 'maxSystemSpread':24, 'staffUpperBorder':7}


def _set(parent, tag, value):
    element = parent.find(tag)
    if element is None:element = E.SubElement(parent, tag)
    element.text = str(value)


def prepare(contents):
    """Leave a copy of every musical/typographic element exactly as received."""
    result = contents.copy()
    native_name = next(name for name in result if name.endswith('.mscx'))
    style_name = next((name for name in result if name.endswith('score_style.mss')), None)
    if style_name is None:return None
    root = E.fromstring(result[native_name]);style_root = E.fromstring(result[style_name])
    style = style_root.find('Style')
    if style is None or len(root.findall('Score/Staff')) != 1:return None
    spacers = []
    for index, measure in enumerate(root.findall('Score/Staff/Measure')):
        # Nine staff spaces account for the glyph above its offset and the
        # previous system's below-staff notes/stems; geometry remains the gate.
        top = 0
        for text in measure.findall('voice/StaffText') + measure.findall('voice/Tempo'):
            offset = text.find('offset')
            if offset is not None:top = max(top, -float(offset.get('y', '0')) + 9)
        for image in measure.findall('voice/Image'):
            offset = image.find('offset')
            if offset is not None:top = max(top, -float(offset.get('y', '0')) + 8)
        top = max(top, float(measure.findtext('vspacerUp', '0')))
        if top > 0:
            _set(measure, 'vspacerUp', top)
            spacers.append({'measure':index + 1, 'space':top})
    for tag, value in SPACING.items():_set(style, tag, value)
    result[native_name] = E.tostring(root, encoding='utf-8', xml_declaration=True)
    result[style_name] = E.tostring(style_root, encoding='utf-8', xml_declaration=True)
    return result, spacers


def _canonical(root):
    return E.canonicalize(E.tostring(root, encoding='unicode'), strip_text=True)


def content_preserved(before, after):
    """Exact native equality except the five spacing values and local spacers.

    This includes note lengths, rests, ties, labels and their musical positions,
    diagram choices, fonts, offsets, all metadata, instrument programs and breaks.
    Embedded diagram assets must also remain byte-identical.
    """
    def fingerprint(contents):
        root = E.fromstring(contents[next(n for n in contents if n.endswith('.mscx'))])
        for measure in root.findall('Score/Staff/Measure'):
            for spacer in measure.findall('vspacerUp'):measure.remove(spacer)
        style = E.fromstring(contents[next(n for n in contents if n.endswith('score_style.mss'))])
        for tag in SPACING:
            for element in style.findall('Style/' + tag):style.find('Style').remove(element)
        assets = {name:data for name,data in contents.items() if name.startswith('Pictures/')}
        return _canonical(root), _canonical(style), assets
    return fingerprint(before) == fingerprint(after)


def page_quality(path):
    """Check decorations against all musical ink and notes across systems.

    Ordinary note-to-stem and tied-note intersections within one system are
    expected. Content from different systems must never intersect. Diagrams
    reserve complete keyboard rectangles, including transparent key gaps.
    """
    root = E.parse(path).getroot();records = geometry._records(root)
    rows = geometry._staff_rows(records);items = []
    music = {'Note','NoteDot','Accidental','Rest','Stem','Beam','Hook','LedgerLine',
             'TieSegment','SlurSegment','Tuplet','Clef','TimeSig','KeySig'}
    decorations = {'StaffText','Tempo','Text','Harmony','Image','MeasureNumber'}
    for record in records:
        element = record['element'];kind = element.get('class', '')
        if not kind or kind == 'Image':continue
        if record['tag'] == 'path':points = geometry._path_points(element.get('d', ''))
        elif record['tag'] == 'polyline':
            values = [float(x) for x in re.findall(geometry.NUMBER, element.get('points', ''))]
            points = list(zip(values[::2], values[1::2]))
        else:continue
        box = geometry._bbox(geometry._xy(record['matrix'], point) for point in points)
        if box is None:continue
        if record['tag'] == 'polyline':
            box = [box[0]-.6, box[1]-.6, box[2]+.6, box[3]+.6]
        # A high note belongs to its nearest staff, unlike labels which are
        # intentionally placed above the next staff. Nearest-staff assignment
        # is used only for rejecting cross-system musical collisions.
        center = (box[1]+box[3])/2
        nearest = min(rows, key=lambda row:abs(center-(row['bbox'][1]+row['bbox'][3])/2))['row'] if rows else -1
        items.append({'kind':kind,'bbox':box,'row':nearest})
    diagrams = geometry.analyze(path)
    items.extend({'kind':'Image','bbox':d['bbox'],'row':d['row']} for d in diagrams['diagrams'])
    overlaps = []
    for index, left in enumerate(items):
        for right in items[index+1:]:
            a,b = left['kind'],right['kind']
            decoration = (a in decorations or b in decorations) and (a != b or a in {'StaffText','Text'})
            cross_system = a in music and b in music and left['row'] != right['row']
            note_collision = a in {'Note','Rest','Accidental'} and b in {'Note','Rest','Accidental'}
            if not decoration and not cross_system and not note_collision:continue
            # Staff lines and measure boundaries intentionally touch notation.
            if a in {'StaffLines','BarLine'} or b in {'StaffLines','BarLine'}:continue
            hit = geometry._intersection(left['bbox'],right['bbox'])
            if hit:overlaps.append({'kinds':[a,b],'bbox':hit,'cross_system':cross_system})
    view = [float(value) for value in root.get('viewBox','0 0 0 0').split()]
    page = [view[0],view[1],view[0]+view[2],view[1]+view[3]]
    clipped = [item for item in items if item['bbox'][0]<page[0]-.5 or item['bbox'][1]<page[1]-.5
               or item['bbox'][2]>page[2]+.5 or item['bbox'][3]>page[3]+.5]
    return {'path':str(path),'overlaps':overlaps,'clipped':clipped,
            'staff_rows':len(rows),'passed':bool(rows) and not overlaps and not clipped}
