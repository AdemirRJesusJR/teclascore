"""Full-size keyboard lanes for measures too dense for horizontal spacing alone."""
import xml.etree.ElementTree as E


def music_preserved(before, after):
    def fingerprint(contents):
        root = E.fromstring(contents[next(n for n in contents if n.endswith('.mscx'))])
        voices = []
        for measure in root.findall('Score/Staff/Measure'):
            parts = []
            for voice in measure.findall('voice'):
                events = []
                for element in voice:
                    if element.tag in ('Segment', 'LayoutBreak'):continue
                    clone = E.fromstring(E.tostring(element))
                    # Renderer-owned positions and autoplace can be materialized on save.
                    for parent in clone.iter():
                        for child in list(parent):
                            if child.tag in ('offset', 'pos', 'autoplace'):parent.remove(child)
                    events.append(E.canonicalize(E.tostring(clone,encoding='unicode'),strip_text=True))
                parts.append(events)
            voices.append((dict(measure.attrib),parts))
        assets = {name:data for name,data in contents.items() if name.startswith('Pictures/')}
        return root.findtext('Score/Division'),voices,assets
    return fingerprint(before) == fingerprint(after)


def plan(pages):
    positions = {}; offset = 0; extra = 0
    for page in pages:
        if page['warnings']:return None
        affected = set()
        for problem in page['overlaps']:
            indices = [problem['diagram']] if problem['type'] == 'harmony-image' else [problem['a'], problem['b']]
            affected.update(page['diagrams'][i]['row'] for i in indices)
        affected.update((page['diagrams'] if c['type'] == 'image' else page['harmonies'])[c['index']]['row'] for c in page['clipped'])
        for row in page['rows']:
            if row['row'] not in affected:continue
            if not row['pairing_by_order_safe'] or 'bbox' not in row:return None
            scale = (row['bbox'][3] - row['bbox'][1]) / 4
            if scale <= 0:return None
            pairs = [(i, page['diagrams'][i], page['harmonies'][page['diagrams'][i]['harmony_index']]) for i in row['diagrams']]
            if not pairs:return None
            ceiling = min(h['bbox'][1] for _, _, h in pairs) - 1.5 * scale
            pitch = max((d['bbox'][3] - d['bbox'][1]) / scale for _, d, _ in pairs) + 1.2
            lanes = []
            for index, diagram, harmony in pairs:
                box = diagram['bbox']; width = box[2] - box[0]
                if width > row['bbox'][2] - row['bbox'][0]:return None
                left = max(row['bbox'][0], min(harmony['bbox'][0], row['bbox'][2] - width))
                right = left + width
                lane = next((i for i, intervals in enumerate(lanes) if all(right + scale <= a or left >= b + scale for a, b in intervals)), len(lanes))
                if lane >= 4:return None
                if lane == len(lanes):lanes.append([])
                lanes[lane].append((left, right))
                dy = (ceiling - lane * pitch * scale - box[3]) / scale
                positions[offset + index] = dict(x=(left - box[0]) / scale, y=dy, lane=lane)
                extra = max(extra, -dy)
        offset += len(page['diagrams'])
    return (positions, extra) if positions else None


def prepare(contents, pages):
    planned = plan(pages)
    if planned is None:return None
    positions, extra = planned
    result = dict(contents)
    name = next(n for n in result if n.endswith('.mscx'))
    root = E.fromstring(result[name])
    images = root.findall('Score/Staff/Measure/voice/Image')
    if len(images) != sum(len(p['diagrams']) for p in pages):return None
    for index, image in enumerate(images):
        if index not in positions:continue
        offset = image.find('offset')
        if offset is None:offset = E.SubElement(image, 'offset', x='0', y='0')
        for axis in ('x', 'y'):offset.set(axis, str(float(offset.get(axis, '0')) + positions[index][axis]))
        auto = image.find('autoplace')
        if auto is None:auto = E.SubElement(image, 'autoplace')
        auto.text = '0'
    # Preserve every musical anchor; lift ancillary labels above the new stack.
    for tag in ('StaffText', 'Tempo'):
        for element in root.findall('Score/Staff/Measure/voice/' + tag):
            offset = element.find('offset')
            if offset is None:offset = E.SubElement(element, 'offset', x='0', y='0')
            offset.set('y', str(float(offset.get('y', '0')) - extra - 3))
    maximum_top = 0
    for measure in root.findall('Score/Staff/Measure'):
        moved = [im for im in measure.findall('voice/Image') if im.findtext('autoplace') == '0']
        labels = measure.findall('voice/StaffText') + measure.findall('voice/Tempo')
        if not moved and not labels:continue
        top = max([-float(e.find('offset').get('y', '0')) + 5 for e in moved + labels if e.find('offset') is not None] or [0])
        maximum_top = max(maximum_top, top)
        spacer = measure.find('vspacerUp')
        if spacer is None:spacer = E.SubElement(measure, 'vspacerUp')
        spacer.text = str(max(float(spacer.text or '0'), top))
    for element in root.findall('.//LayoutBreak/subtype'):
        if element.text == 'page':element.text = 'line'
    style_name = next((n for n in result if n.endswith('score_style.mss')), None)
    style_root = E.fromstring(result[style_name]) if style_name else root
    parent = style_root if style_name else root.find('Score')
    style = parent.find('Style')
    if style is None:style = E.SubElement(parent, 'Style')
    for tag, minimum in (('staffUpperBorder', maximum_top),):
        element = style.find(tag)
        if element is None:element = E.SubElement(style, tag)
        element.text = str(max(float(element.text or '0'), minimum))
    if style_name:result[style_name] = E.tostring(style_root, encoding='utf-8', xml_declaration=True)
    result[name] = E.tostring(root, encoding='utf-8', xml_declaration=True)
    return result, dict(diagrams=len(positions), lanes=1 + max(p['lane'] for p in positions.values()), size_preserved=True)
