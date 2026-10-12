from pathlib import Path
import json,shutil,re
import sys
root=Path(__file__).resolve().parents[1]
source=Path(sys.argv[1]) if len(sys.argv)>1 else root.parent/'trabalho/lote-2026-10-12/ferramentas'
target=root/'engine';target.mkdir(exist_ok=True)
for name in ('batch_score.py','batch_harmony.py','batch_svg_qa.py','inspect_midi.py','build_keyboard_diagrams.py'):
    shutil.copy2(source/name,target/name)
shutil.copytree(source/'garoto_melodia_cifras',target/'garoto_melodia_cifras',dirs_exist_ok=True)
p=target/'batch_score.py'
s=p.read_text(encoding='utf8')
s=s.replace('import ast, struct, math, json, zipfile, subprocess, hashlib, re, copy','import ast, struct, math, json, zipfile, subprocess, hashlib, re, copy, os, tempfile')
s=s.replace("BATCH=WORK/'lote_2026_10_12';BATCH.mkdir(exist_ok=True)","BATCH=Path(os.environ.get('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclasPartituras')));BATCH.mkdir(parents=True,exist_ok=True)")
s=re.sub(r"    parsed=parse_midi\(config\.get\('source_path'\).*", "    parsed=parse_midi(config['source_path'])",s)
s=s.replace("title=re.sub(r'[_]+',' ',Path(config['filename']).stem);title=re.sub(r'\\s+',' ',title).strip()","title=re.sub(r'[_]+',' ',Path(config['filename']).stem);title=re.sub(r'\\s+',' ',title).strip()")
# Expose phase progress without a GUI or changing the musical algorithm.
s=s.replace("p=subprocess.run([EXE]+args", "print(json.dumps({'kind':'progress','message':'MuseScore: '+log.stem.replace('_',' ')+'…'},ensure_ascii=False),flush=True)\n    p=subprocess.run([EXE]+args")
p.write_text(s,encoding='utf8')
sys.path.insert(0,str(target))
from inspect_midi import GM
(target/'gm.json').write_text(json.dumps(GM,ensure_ascii=False),encoding='utf8')
configs=json.loads((source/'batch_configs.json').read_text(encoding='utf8'))
inventory=json.loads((source.parents[1]/'inventario'/'inventario_midis.json').read_text(encoding='utf8'))
hashes={Path(x['path']).name:x['sha256'] for x in inventory['files']}
overrides={}
for p in source.glob('batch_harmony_overrides*.json'):
    data=json.loads(p.read_text(encoding='utf8'));entries=data.get('entries',list(data.values())) if isinstance(data,dict) else data
    overrides.update({v['index']:v for v in entries})
keep=('index','filename','source_sha256','status','reason','melody_sources','solo_sources','onset_cluster_quarters','velocity_filter','grace_notes','max_chords_per_system','melody_transpose_semitones','register_adaptation','reserve_high_melody','tuplet_bracket_measures')
profiles={}
for c in configs:
    compact={k:c[k] for k in keep if k in c}
    profiles[c.get('source_sha256') or hashes[c['filename']]]={'config':compact,'harmony':overrides.get(c['index'])}
(target/'profiles.json').write_text(json.dumps(profiles,ensure_ascii=False,separators=(',',':')),encoding='utf8')
print('Engine prepared:',len(profiles),'known MIDI profiles;18 reviewed.')
