"""Geometry QA for native MuseScore SVG exports; standard library only.

analyze(path) returns diagram and Harmony bounding boxes ordered by staff row,
then x, independently of the exporter's arbitrary element order. Coordinates
are in SVG viewBox units. Glyph bounds conservatively include Bezier controls.
The keyboard image template is 14 white keys, 20x60 each, in a 282x84 viewBox.
No score or source SVG is modified. CLI: python batch_svg_qa.py page.svg --out qa.json
"""
from __future__ import annotations
import argparse
import json
import math
import re
import statistics
import xml.etree.ElementTree as ET
from pathlib import Path

NUMBER = r'[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?'
TOKEN = re.compile(r'[AaCcHhLlMmQqSsTtVvZz]|' + NUMBER)
IDENTITY = (1., 0., 0., 1., 0., 0.)

def _mul(a, b):
    return (a[0]*b[0]+a[2]*b[1], a[1]*b[0]+a[3]*b[1],
            a[0]*b[2]+a[2]*b[3], a[1]*b[2]+a[3]*b[3],
            a[0]*b[4]+a[2]*b[5]+a[4], a[1]*b[4]+a[3]*b[5]+a[5])

def _transform(value):
    out = IDENTITY
    for name, body in re.findall(r'([A-Za-z]+)\s*\(([^)]*)\)', value or ''):
        v = [float(n) for n in re.findall(NUMBER, body)]
        if name == 'matrix' and len(v) == 6: m = tuple(v)
        elif name == 'translate': m = (1,0,0,1,v[0],v[1] if len(v)>1 else 0)
        elif name == 'scale': m = (v[0],0,0,v[1] if len(v)>1 else v[0],0,0)
        elif name == 'rotate':
            a = math.radians(v[0]); c,s = math.cos(a),math.sin(a)
            m = (c,s,-s,c,0,0)
            if len(v)>2: m = _mul(_mul((1,0,0,1,v[1],v[2]),m),(1,0,0,1,-v[1],-v[2]))
        elif name == 'skewX': m = (1,0,math.tan(math.radians(v[0])),1,0,0)
        elif name == 'skewY': m = (1,math.tan(math.radians(v[0])),0,1,0,0)
        else: raise ValueError('Unsupported SVG transform: '+name)
        out = _mul(out,m)
    return out

def _xy(m, p):
    return (m[0]*p[0]+m[2]*p[1]+m[4], m[1]*p[0]+m[3]*p[1]+m[5])

def _bbox(points):
    points=list(points)
    return [min(p[0] for p in points),min(p[1] for p in points),
            max(p[0] for p in points),max(p[1] for p in points)] if points else None

def _union(boxes):
    return _bbox((x,y) for b in boxes if b for x,y in ((b[0],b[1]),(b[2],b[3])))

def _path_points(d):
    """Control-point enclosure, with relative commands resolved before transform."""
    tokens=TOKEN.findall(d); i=0;cmd=None;x=y=0.;start=(0.,0.);points=[];last_control=None;previous=None
    arity={'M':2,'L':2,'H':1,'V':1,'C':6,'S':4,'Q':4,'T':2,'A':7}
    while i<len(tokens):
        if tokens[i].isalpha():
            cmd=tokens[i];i+=1
            if cmd.upper()=='Z':
                x,y=start;points.append(start);last_control=None;previous='Z';continue
        if cmd is None or cmd.upper() not in arity: raise ValueError('Unsupported SVG path command')
        op=cmd.upper(); count=arity[op]
        if i+count>len(tokens) or any(v.isalpha() for v in tokens[i:i+count]): raise ValueError('Malformed SVG path')
        a=list(map(float,tokens[i:i+count]));i+=count;ox,oy=x,y
        def pt(px,py): return (px+ox,py+oy) if cmd.islower() else (px,py)
        if op in ('M','L','T'):
            dest=pt(*a)
            if op=='T' and previous in ('Q','T') and last_control is not None:
                c=(2*x-last_control[0],2*y-last_control[1]);points.append(c);last_control=c
            else:last_control=None
            x,y=dest;points.append(dest)
            if op=='M':start=dest;cmd='l' if cmd.islower() else 'L'
        elif op=='H':x=a[0]+x if cmd.islower() else a[0];points.append((x,y));last_control=None
        elif op=='V':y=a[0]+y if cmd.islower() else a[0];points.append((x,y));last_control=None
        elif op in ('C','S','Q'):
            if op=='S' and previous in ('C','S') and last_control is not None:points.append((2*x-last_control[0],2*y-last_control[1]))
            pairs=[pt(a[j],a[j+1]) for j in range(0,len(a),2)]
            points.extend(pairs);x,y=pairs[-1];last_control=pairs[-2]
        elif op=='A':
            # Native MuseScore glyphs are Beziers. A broad enclosure also handles arcs.
            dest=pt(a[5],a[6]);rx,ry=abs(a[0]),abs(a[1])
            if not rx or not ry:points.extend([(x,y),dest])
            else:
                # SVG may enlarge undersized radii. Bound that enlargement using
                # the smaller radius, then enclose the entire possible ellipse.
                radius=2*max(rx,ry)*max(1,math.dist((x,y),dest)/(2*min(rx,ry)))
                cx,cy=(x+dest[0])/2,(y+dest[1])/2
                points.extend([(cx-radius,cy-radius),(cx+radius,cy+radius)])
            x,y=dest;last_control=None
        previous=op
    return points

def _records(root):
    result=[]
    def walk(e,parent):
        matrix=_mul(parent,_transform(e.get('transform')))
        tag=e.tag.rsplit('}',1)[-1]
        rec={'element':e,'tag':tag,'matrix':matrix,'source_index':len(result)}
        result.append(rec)
        for child in e:walk(child,matrix)
    walk(root,IDENTITY)
    return result

def _rect(rec):
    e=rec['element'];d=e.get('d','')
    if rec['tag']!='path' or re.search(r'[CcQqSsTtAa]',d):return None
    pts=_path_points(d);b=_bbox(pts)
    if not b or len(set(pts))!=4:return None
    if not all(p[0] in (b[0],b[2]) and p[1] in (b[1],b[3]) for p in pts):return None
    return b

def _staff_rows(records):
    lines=[]
    for rec in records:
        e=rec['element']
        if e.get('class')!='StaffLines':continue
        ns=[float(n) for n in re.findall(NUMBER,e.get('points',''))]
        pts=[_xy(rec['matrix'],(ns[i],ns[i+1])) for i in range(0,len(ns)-1,2)]
        b=_bbox(pts)
        if b and abs(b[3]-b[1])<1:lines.append(b)
    unique=sorted(set(round(b[1],3) for b in lines))
    gaps=[b-a for a,b in zip(unique,unique[1:]) if b-a>1]
    spacing=min(gaps) if gaps else 82.677
    groups=[]
    for y in unique:
        if not groups or y-groups[-1][-1]>spacing*1.8:groups.append([])
        groups[-1].append(y)
    return [dict(row=i,bbox=_union([b for b in lines if any(abs(b[1]-y)<1 for y in ys)]),line_count=len(ys)) for i,ys in enumerate(groups)]

def _row_for(box,rows):
    cy=(box[1]+box[3])/2
    below=[r for r in rows if r['bbox'][1]>=cy]
    return (below[0] if below else rows[-1])['row'] if rows else 0

def _intersection(a,b,tolerance=.5):
    out=[max(a[0],b[0]),max(a[1],b[1]),min(a[2],b[2]),min(a[3],b[3])]
    return out if out[2]-out[0]>tolerance and out[3]-out[1]>tolerance else None

def analyze(svg_path, staff_right=None):
    path=Path(svg_path);root=ET.parse(path).getroot();records=_records(root);warnings=[]
    vb=[float(x) for x in root.get('viewBox','0 0 9921.26 14031.5').split()]
    page=[vb[0],vb[1],vb[0]+vb[2],vb[1]+vb[3]]
    rows=_staff_rows(records)
    right=float(staff_right) if staff_right is not None else max((r['bbox'][2] for r in rows),default=9259.84)
    diagrams=[];current=None;glyphs=[]
    for rec in records:
        e=rec['element'];cls=e.get('class','')
        if cls not in ('Image','Harmony'):continue
        if rec['tag']!='path':warnings.append('Non-path '+cls+' element not measured');continue
        local=_path_points(e.get('d',''));box=_bbox(_xy(rec['matrix'],p) for p in local)
        if box is None:continue
        if cls=='Harmony':
            glyphs.append(dict(bbox=box,source_index=rec['source_index'],row=_row_for(box,rows)));continue
        rectangle=_rect(rec)
        white=e.get('fill','').lower() in ('#fff','#ffffff','white')
        is_key=white and rectangle and abs((rectangle[3]-rectangle[1])/(rectangle[2]-rectangle[0])-3)<.02
        if is_key:
            w=rectangle[2]-rectangle[0]
            first=current is None or current['white_keys']==14 or rectangle[0]<=current['_last_key_x']+.01
            if first:
                if current is not None and current['white_keys']!=14:warnings.append('Diagram with '+str(current['white_keys'])+' white keys')
                unit=w/20; x=rectangle[0]-unit;y=rectangle[1]-unit
                template=[(x,y),(x+14*w+2*unit,y),(x+14*w+2*unit,y+84*unit),(x,y+84*unit)]
                full=_bbox(_xy(rec['matrix'],p) for p in template)
                current=dict(bbox=full,ink_bbox=box,source_index=rec['source_index'],white_keys=0,path_count=0,
                             key_width=math.dist(_xy(rec['matrix'],(0,0)),_xy(rec['matrix'],(w,0))),
                             scale_x=math.hypot(rec['matrix'][0],rec['matrix'][1]),scale_y=math.hypot(rec['matrix'][2],rec['matrix'][3]),
                             row=_row_for(full,rows),_last_key_x=rectangle[0])
                diagrams.append(current)
            current['white_keys']+=1;current['_last_key_x']=rectangle[0]
        if current is None:warnings.append('Image path before first recognized keyboard key');continue
        current['path_count']+=1;current['ink_bbox']=_union([current['ink_bbox'],box])
    if current is not None and current['white_keys']!=14:warnings.append('Final diagram has '+str(current['white_keys'])+' white keys')
    for d in diagrams:d.pop('_last_key_x',None)
    diagrams.sort(key=lambda d:(d['row'],d['bbox'][0],d['bbox'][1]))
    for i,d in enumerate(diagrams):d['index']=i

    # Spatial connected components keep superscript qualities and accidental glyphs
    # with their root even though the XML order is unrelated to reading order.
    harmonies=[]
    for row in sorted(set(g['row'] for g in glyphs)):
        gs=sorted([g for g in glyphs if g['row']==row],key=lambda g:g['bbox'][0]);components=[]
        median_height=statistics.median(g['bbox'][3]-g['bbox'][1] for g in gs)
        for g in gs:
            target=None
            for c in reversed(components):
                b=c['bbox'];a=g['bbox'];gap=a[0]-b[2]
                vo=min(a[3],b[3])-max(a[1],b[1]);minh=min(a[3]-a[1],b[3]-b[1])
                compatible=vo>minh*.15 or abs(a[3]-b[3])<median_height*.7
                if gap<=median_height*.45 and compatible and a[2]>=b[0]:target=c;break
            if target is None:target=dict(row=row,bbox=g['bbox'][:],glyphs=[]);components.append(target)
            target['glyphs'].append(g);target['bbox']=_union([target['bbox'],g['bbox']])
        harmonies.extend(components)
    harmonies.sort(key=lambda h:(h['row'],h['bbox'][0],h['bbox'][1]))
    for i,h in enumerate(harmonies):h['index']=i;h['glyph_count']=len(h['glyphs'])
    for r in rows:
        ds=[d for d in diagrams if d['row']==r['row']];hs=[h for h in harmonies if h['row']==r['row']]
        r['diagrams']=[d['index'] for d in ds];r['harmonies']=[h['index'] for h in hs]
        r['pairing_by_order_safe']=len(ds)==len(hs)
        if len(ds)==len(hs):
            for d,h in zip(ds,hs):d['harmony_index']=h['index'];h['diagram_index']=d['index']
        elif ds or hs:warnings.append('Row '+str(r['row'])+': '+str(len(ds))+' diagrams vs '+str(len(hs))+' Harmony groups; do not assume a bijection')
    overlaps=[]
    for i,a in enumerate(diagrams):
        for b in diagrams[i+1:]:
            hit=_intersection(a['bbox'],b['bbox'])
            if hit:overlaps.append(dict(type='image-image',a=a['index'],b=b['index'],bbox=hit,width=hit[2]-hit[0],height=hit[3]-hit[1]))
    for h in harmonies:
        for d in diagrams:
            hit=_intersection(h['bbox'],d['bbox'])
            if hit:overlaps.append(dict(type='harmony-image',harmony=h['index'],diagram=d['index'],own_pair=h.get('diagram_index')==d['index'],bbox=hit,width=hit[2]-hit[0],height=hit[3]-hit[1]))
    clipped=[]
    for kind,items in (('image',diagrams),('harmony',harmonies)):
        for item in items:
            b=item['bbox']
            item['right_overflow']=max(0,b[2]-right)
            if item['right_overflow']>.5:clipped.append(dict(type=kind,index=item['index'],edge='staff_right',overflow=item['right_overflow'],bbox=b))
            for edge,amount in [('page_left',page[0]-b[0]),('page_right',b[2]-page[2]),('page_top',page[1]-b[1]),('page_bottom',b[3]-page[3])]:
                if amount>.5:clipped.append(dict(type=kind,index=item['index'],edge=edge,overflow=amount,bbox=b))
    return dict(svg_path=str(path.resolve()),page_bbox=page,staff_right=right,diagrams=diagrams,harmonies=harmonies,rows=rows,
                overlaps=overlaps,clipped=clipped,warnings=warnings,
                counts=dict(diagrams=len(diagrams),harmony_groups=len(harmonies),harmony_glyphs=len(glyphs),image_paths=sum(d['path_count'] for d in diagrams),
                            image_image_overlaps=sum(x['type']=='image-image' for x in overlaps),harmony_image_overlaps=sum(x['type']=='harmony-image' for x in overlaps),clipped=len(clipped)),
                method='Full keyboard template bounds; transformed SVG path control-point enclosures for Harmony. Spatial grouping by staff row and adjacent glyph boxes, never XML glyph order.')

if __name__=='__main__':
    ap=argparse.ArgumentParser(description=__doc__);ap.add_argument('svg',nargs='+');ap.add_argument('--out');ap.add_argument('--staff-right',type=float)
    args=ap.parse_args();results=[analyze(p,args.staff_right) for p in args.svg];output=results[0] if len(results)==1 else results
    text=json.dumps(output,ensure_ascii=False,indent=2)
    if args.out:Path(args.out).write_text(text,encoding='utf-8');print(json.dumps([dict(svg=r['svg_path'],counts=r['counts'],warnings=r['warnings']) for r in results],ensure_ascii=False))
    else:print(text)
