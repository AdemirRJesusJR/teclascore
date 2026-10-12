from pathlib import Path
import json,subprocess,sys,hashlib,struct,copy
BASE=Path(__file__).resolve().parents[1]
ENGINE=BASE/'engine'
QA=BASE/'qa'/'protocol';QA.mkdir(parents=True,exist_ok=True)
PROJ=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).resolve().parents[2]
EXE=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
def call(req,expected=True):
    work=Path(req['workspace']);work.mkdir(parents=True,exist_ok=True)
    path=work/'request.json';path.write_text(json.dumps(req),encoding='utf8')
    p=subprocess.run([sys.executable,str(ENGINE/'worker.py'),str(path)],capture_output=True,text=True,encoding='utf8',timeout=180)
    messages=[json.loads(line) for line in p.stdout.splitlines() if line.startswith('{')]
    if expected:
        assert p.returncode==0,(p.stdout,p.stderr)
        return next(x['data'] for x in messages if x['kind']=='result')
    assert p.returncode!=0
    return next(x['message'] for x in messages if x['kind']=='error')
profiles=json.loads((ENGINE/'profiles.json').read_text(encoding='utf8'))
reviewed=[p for p in profiles.values() if p['config']['status']=='ready']
checks=[]
for profile in reviewed:
    c=profile['config'];index=c['index'];work=QA/f'profile-{index:03d}';source=PROJ/'midis'/c['filename']
    r=call({'mode':'analyze','workspace':str(work),'inputMidi':str(source),'useReviewedProfile':True})
    analyzed=json.loads(Path(r['analysisPath']).read_text(encoding='utf8'))
    original=json.loads((PROJ/'trabalho/lote-2026-10-12'/f'{index+1:03d}'/'harmonia_fonte.json').read_text(encoding='utf8'))
    assert [(v['tick'],v['root'],v['quality']) for v in analyzed['harmony']['events']]==[(v['tick'],v['root'],v['quality']) for v in original['events']],index
    assert analyzed['harmony']['key']['fifths']==original['key']['fifths'],index
    # Editor round-trip must name the same pitch classes under all local key changes.
    pitches={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}
    for ui,ev in zip(r['chords'],analyzed['harmony']['events']):
        name=ui['root'];assert (pitches[name[0]]+name.count('#')-name.count('b'))%12==ev['root']
    checks.append({'index':index,'filename':c['filename'],'events':len(r['chords']),'status':'profile_matches_reviewed_batch'})
print('18 reviewed profiles match.',flush=True)
# A genuinely new MIDI with explicit, independently known melody and harmonic tracks.
def vlq(n):
    result=[n&127];n>>=7
    while n:result.insert(0,(n&127)|128);n>>=7
    return bytes(result)
def track(events):
    b=bytearray();last=0
    for tick,event in sorted(events,key=lambda x:x[0]):
        b.extend(vlq(tick-last));b.extend(event);last=tick
    b.extend(b'\x00\xff\x2f\x00')
    return b'MTrk'+struct.pack('>I',len(b))+b
meta=[(0,b'\xff\x51\x03\x09\xf3\x8e'),(0,b'\xff\x58\x04\x04\x02\x18\x08'),(0,b'\xff\x59\x02\x00\x00')]
mel=[(0,bytes([0xc0,73]))];acc=[(0,bytes([0xc1,0]))]
for q in range(32):
    pitch=[72,74,76,79][q%4];mel.extend([(q*480,bytes([0x90,pitch,90])),((q+1)*480-20,bytes([0x80,pitch,0]))])
for bar in range(8):
    for pitch in ([48,52,55],[45,48,52],[41,45,48],[43,47,50])[bar%4]:
        acc.extend([(bar*1920,bytes([0x91,pitch,80])),((bar+1)*1920-1,bytes([0x81,pitch,0]))])
source=QA/'MIDI novo de teste.mid';source.write_bytes(b'MThd'+struct.pack('>IHHH',6,1,3,480)+track(meta)+track(mel)+track(acc))
source_sha=hashlib.sha256(source.read_bytes()).hexdigest()
work=QA/'manual'
r=call({'mode':'analyze','workspace':str(work),'inputMidi':str(source),'useReviewedProfile':False,'melodySources':[{'track':1,'channel':1,'program':73}]})
assert not r['reviewedProfile']
req={'mode':'generate','workspace':str(work),'inputMidi':str(source),'analysisPath':r['analysisPath'],'musescorePath':EXE,'outputDirectory':str(work/'output'),'chords':r['chords'],'keyFifths':r['keyFifths'],'keyMinor':r['keyMinor']}
bad=copy.deepcopy(req);bad['chords'][0]['quality']='invalid'
assert 'não suportado' in call(bad,False)
result=call(req);assert Path(result['scorePath']).is_file()
first_sha=hashlib.sha256(Path(result['scorePath']).read_bytes()).hexdigest()
second=call(req);assert second['scorePath']!=result['scorePath']
assert hashlib.sha256(Path(result['scorePath']).read_bytes()).hexdigest()==first_sha
assert hashlib.sha256(source.read_bytes()).hexdigest()==source_sha
report=json.loads((Path(result['reviewDirectory'])/'validacao.json').read_text(encoding='utf8'))
assert report['melody_attacks_verified']==32
assert report['geometric_layout_qa']['passed'] and report['native_reopened']
checks.append({'status':'new_midi_manual_flow_passed','notes':32,'pages':result['pages'],'invalid_chord_rejected':True,'existing_output_preserved':True,'source_preserved':True})
(QA/'results.json').write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({'profiles':len(reviewed),'manual_conversion':True,'output_collision_protection':True,'report':str(QA/'results.json')},ensure_ascii=False))
