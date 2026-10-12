"""Embed vector keyboard diagrams into the approved native MuseScore score."""
from pathlib import Path
import xml.etree.ElementTree as E
import zipfile, struct, json, hashlib, subprocess

WORK=Path(__file__).resolve().parent
OUT=WORK/'garoto_diagramas'
OUT.mkdir(exist_ok=True)
SOURCE=WORK.parents[1]/'partituras'/'Garoto de Aluguel - melodia e cifras.mscz'
TITLE='Garoto de Aluguel - melodia cifras e diagramas'

def md4(data):
    """RFC 1320 MD4; required by MuseScore's embedded-image store."""
    bits=len(data)*8
    data+=b'\x80'
    data+=b'\0'*((56-len(data)%64)%64)+struct.pack('<Q',bits)
    state=[0x67452301,0xefcdab89,0x98badcfe,0x10325476]
    def rol(x,n):x&=0xffffffff;return ((x<<n)|(x>>(32-n)))&0xffffffff
    for base in range(0,len(data),64):
        x=struct.unpack('<16I',data[base:base+64]);v=state.copy()
        for round_,order,shifts,constant in [
            (0,list(range(16)),[3,7,11,19],0),
            (1,[0,4,8,12,1,5,9,13,2,6,10,14,3,7,11,15],[3,5,9,13],0x5a827999),
            (2,[0,8,4,12,2,10,6,14,1,9,5,13,3,11,7,15],[3,9,11,15],0x6ed9eba1)]:
            for i,k in enumerate(order):
                a=(-i)%4;b=v[(a+1)%4];c=v[(a+2)%4];d=v[(a+3)%4]
                f=(b&c)|(~b&d) if round_==0 else ((b&c)|(b&d)|(c&d) if round_==1 else b^c^d)
                v[a]=rol(v[a]+f+x[k]+constant,shifts[i%4])
        state=[(a+b)&0xffffffff for a,b in zip(state,v)]
    return struct.pack('<4I',*state).hex()
assert md4(b'')=='31d6cfe0d16ae931b73c59d7e0c089c0'
assert md4(b'abc')=='a448017aaf21d8525fc10ae87aa6729d'

DIAGRAMS={
    (20,'m'):('F#m',[54,57,61],['F#3','A3','C#4']),
    (21,'m'):('C#m',[49,52,56],['C#3','E3','G#3']),
    (19,'m'):('Bm',[59,62,66],['B3','D4','F#4']),
    (21,''):('C#',[49,53,56],['C#3','E#3','G#3']),
}

def svg_keyboard(label,pitches):
    root=E.Element('svg',xmlns='http://www.w3.org/2000/svg',width='282',height='84',viewBox='0 0 282 84')
    E.SubElement(root,'title').text=label+' — posição fundamental'
    E.SubElement(root,'desc').text='Teclado de C3 a B4; pontos indicam as três teclas do acorde.'
    natural=[0,2,4,5,7,9,11]
    white=[p for p in range(48,72) if p%12 in natural]
    for i,p in enumerate(white):
        E.SubElement(root,'rect',x=str(1+i*20),y='1',width='20',height='60',fill='white',stroke='#111111',**{'stroke-width':'1'})
    for p in range(48,72):
        if p%12 in natural:continue
        left=white.index(p-1);x=1+(left+1)*20-6
        E.SubElement(root,'rect',x=str(x),y='1',width='12',height='37',fill='#111111')
    for p in pitches:
        if p in white:
            x=11+white.index(p)*20;y=49;fill='#111111'
        else:
            x=1+(white.index(p-1)+1)*20;y=29;fill='white'
        E.SubElement(root,'circle',cx=str(x),cy=str(y),r='4.2',fill=fill,**{'data-midi':str(p)})
    for x in [11,151]:
        E.SubElement(root,'text',x=str(x),y='79',fill='#333333',**{'font-family':'Arial','font-size':'16','text-anchor':'middle'}).text='C'
    return E.tostring(root,encoding='utf-8',xml_declaration=True)

assets={};filenames={};manifest=[]
for key,(label,pitches,names) in DIAGRAMS.items():
    data=svg_keyboard(label,pitches);name=md4(data)+'.svg';assets['Pictures/'+name]=data;filenames[key]=name
    (OUT/(label.replace('#','s')+'.svg')).write_bytes(data)
    manifest.append({'chord':label,'pitches_midi':pitches,'note_names':names,'embedded_path':'Pictures/'+name})

with zipfile.ZipFile(SOURCE) as z:contents={name:z.read(name) for name in z.namelist()}
score_name=next(n for n in contents if n.endswith('.mscx'))
root=E.fromstring(contents[score_name]);staff=root.find('Score/Staff')
assert len(root.findall('Score/Staff'))==1
before_chords=[E.tostring(x) for x in root.findall('.//Chord')]
before_harmonies=[E.tostring(x) for x in root.findall('.//Harmony')]
image_count=0
for number,measure in enumerate(staff.findall('Measure'),1):
    # Seven systems per page produces five evenly filled pages with the diagrams.
    breaks=measure.findall('LayoutBreak')
    if number%6==0 and number<210:
        if not breaks:breaks=[E.SubElement(measure,'LayoutBreak')]
        for br in breaks:
            subtype=br.find('subtype')
            if subtype is None:subtype=E.SubElement(br,'subtype')
            subtype.text='page' if number%42==0 else 'line'
    for voice in measure.findall('voice'):
        for harmony in list(voice.findall('Harmony')):
            info=harmony.find('harmonyInfo')
            key=(int(info.findtext('root')),info.findtext('name',''))
            assert key in filenames,key
            im=E.Element('Image')
            E.SubElement(im,'offset',x='4.5',y='-6.7')
            E.SubElement(im,'path').text=filenames[key]
            E.SubElement(im,'autoScale').text='0'
            E.SubElement(im,'size',w='14',h=str(14*84/282))
            E.SubElement(im,'lockAspectRatio').text='1'
            E.SubElement(im,'sizeIsSpatium').text='1'
            voice.insert(list(voice).index(harmony)+1,im)
            image_count+=1
        if voice.find('Image') is not None:
            for heading in voice.findall('StaffText'):
                offset=heading.find('offset')
                if offset is None:offset=E.SubElement(heading,'offset')
                offset.set('x','0');offset.set('y','-8.5')
                E.SubElement(heading,'autoplace').text='0'
        if number==1:
            for tempo in voice.findall('Tempo'):
                offset=tempo.find('offset')
                if offset is None:offset=E.SubElement(tempo,'offset')
                offset.set('x','13');offset.set('y','-8.5')
                E.SubElement(tempo,'autoplace').text='0'
assert image_count==70
assert before_chords==[E.tostring(x) for x in root.findall('.//Chord')]
assert before_harmonies==[E.tostring(x) for x in root.findall('.//Harmony')]
subtit=next(t for t in staff.findall('VBox/Text') if t.findtext('style')=='subtitle')
vbox=staff.find('VBox')
gap=vbox.find('bottomGap')
if gap is None:gap=E.SubElement(vbox,'bottomGap')
gap.text='11'
text=subtit.find('text')
for c in list(text):text.remove(c)
text.text='Melodia, cifras e diagramas de teclado • Versão Calmon do Forró'
E.indent(root);contents[score_name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
container=E.fromstring(contents['META-INF/container.xml'])
for name in assets:E.SubElement(container.find('rootfiles'),'rootfile',{'full-path':name})
E.indent(container);contents['META-INF/container.xml']=E.tostring(container,encoding='utf-8',xml_declaration=True)
contents.update(assets)
initial=OUT/'diagramas_preparado.mscz'
with zipfile.ZipFile(initial,'w',zipfile.ZIP_DEFLATED) as z:
    for name,data in contents.items():z.writestr(name,data)

jobs=[{'in':str(initial),'out':[str(OUT/(TITLE+'.mscz')),str(OUT/(TITLE+'.png'))]}]
(OUT/'converter.json').write_text(json.dumps(jobs),encoding='utf8')
exe=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
p=subprocess.run([exe,'-r','140','-j',str(OUT/'converter.json')],capture_output=True,text=True,errors='replace',timeout=90)
(OUT/'conversao.log').write_text(p.stdout+'\n'+p.stderr,encoding='utf8')
assert p.returncode==0,(p.returncode,p.stderr)
final=OUT/(TITLE+'.mscz')
with zipfile.ZipFile(final) as z:
    parsed=E.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.mscx'))))
    assert len(parsed.findall('.//Image'))==70
    embedded=[n for n in z.namelist() if n.startswith('Pictures/')]
    assert len(embedded)==4,embedded
    for im in parsed.findall('.//Image'):
        path='Pictures/'+im.findtext('path').split('/')[-1]
        assert path in z.namelist(),path
        data=z.read(path)
        assert Path(path).stem==md4(data)
        svg=E.fromstring(data)
        assert len(svg.findall('{http://www.w3.org/2000/svg}circle'))==3
report={'source_approved_score':str(SOURCE),'source_sha256':hashlib.sha256(SOURCE.read_bytes()).hexdigest(),'image_instances':70,'embedded_vectors':4,'external_image_paths_required':False,'diagram_keyboard_range':'C3-B4','diagram_note_sets':manifest,'pages':len(list(OUT.glob(TITLE+'-*.png')))}
(OUT/'validacao_diagramas.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps(report,ensure_ascii=True,indent=2))
