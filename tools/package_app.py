from pathlib import Path
import argparse,json,hashlib,zipfile,shutil
import xml.etree.ElementTree as ET
B=Path(__file__).resolve().parents[1]
APP=B/'Aplicativo'
version=ET.parse(B/'src/Teclas.App/Teclas.App.csproj').findtext('.//Version')
archive_name=f'TeclaScore-{version}-Windows.zip'
installer_name=f'TeclaScore-{version}-Setup-x64.exe'
parser=argparse.ArgumentParser(description='Empacota o TeclaScore publicado e gera os checksums da release.')
parser.add_argument('--installer',action='store_true',help='Inclui o instalador EXE já compilado nos checksums e na entrega.')
options=parser.parse_args()
if options.installer and not (B/installer_name).is_file():
    raise RuntimeError('Compile o instalador desta versão antes de finalizar o pacote.')
# Refresh text/runtime assets after the final, validated worker edit.
shutil.copy2(B/'engine/worker.py',APP/'engine/worker.py')
shutil.copy2(B/'LEIA-ME.txt',APP/'LEIA-ME.txt')
shutil.copy2(B/'VALIDACAO.txt',APP/'VALIDACAO.txt')
shutil.copy2(B/'ROTEIRO-DE-TESTES.txt',APP/'ROTEIRO-DE-TESTES.txt')
shutil.copy2(B/'ui-preview.png',B/'previa-do-aplicativo.png')
with zipfile.ZipFile(B/archive_name,'w',zipfile.ZIP_DEFLATED) as z:
    for p in APP.rglob('*'):
        if p.is_file() and '__pycache__' not in p.parts and p.suffix not in ('.pdb','.pyc'):z.write(p,p.relative_to(APP))
with zipfile.ZipFile(B/archive_name) as z:
    assert z.testzip() is None
    assert {'Teclas.Partituras.exe','Teclas.Partituras.dll','Teclas.Core.dll','engine/worker.py','engine/profiles.json','engine/gm.json'}<=set(z.namelist())
artifacts=[archive_name]+([installer_name] if options.installer else [])
(B/'SHA256SUMS').write_text(''.join(hashlib.sha256((B/name).read_bytes()).hexdigest()+'  '+name+'\n' for name in artifacts),encoding='ascii')
def included(p):
    rel=p.relative_to(B)
    if p.suffix=='.zip' and p.name!=archive_name:return False
    if len(rel.parts)==1 and p.suffix=='.exe' and (not options.installer or p.name!=installer_name):return False
    return not any(part in ('bin','obj','.git','.vs','.idea','.dotnet','.nuget','__pycache__','qa') for part in rel.parts) and p.suffix not in ('.pyc','.pdb') and not p.name.endswith(('.diagnostics.json','.error.txt')) and rel.as_posix() not in ('ui-preview.png','ui-deployed.png','delivery-manifest.json')
files=[p for p in B.rglob('*') if p.is_file() and included(p)]
manifest={str(p.relative_to(B)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
(B/'delivery-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({'files':len(files),'zip_bytes':(B/archive_name).stat().st_size,'ready':True,'version':version}))
