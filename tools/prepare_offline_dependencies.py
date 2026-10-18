"""Fetch pinned official packages only with --download, then prepare private Python with Pillow, numpy and onnxruntime."""
from pathlib import Path
import argparse
import json
import subprocess
import shutil
import tempfile
import urllib.request
import zipfile
from build_debug import BASE, digest, safe_path, sync_owned

def prepare(download=False):
    packages=json.loads((BASE/'tools/offline-dependencies.json').read_text(encoding='utf-8'))
    cache=BASE/'qa/offline-dependencies'; cache.mkdir(parents=True,exist_ok=True)
    for item in packages.values():
        target=safe_path(cache,item['file'])
        offline=safe_path(BASE/'debug/prerequisitos',item['file'])
        if not target.exists() and offline.is_file() and digest(offline)==item['sha256']:
            shutil.copy2(offline,target)
        if not target.exists():
            if not download: raise RuntimeError('Run tools/prepare_offline_dependencies.py --download to fetch '+item['file'])
            temporary=target.with_suffix(target.suffix+'.download')
            with urllib.request.urlopen(item['url'],timeout=120) as response, temporary.open('wb') as output:
                while chunk:=response.read(1024*1024): output.write(chunk)
            if digest(temporary)!=item['sha256']: raise ValueError('Download checksum mismatch: '+item['file'])
            temporary.replace(target)
        if digest(target)!=item['sha256']: raise ValueError('Cached checksum mismatch: '+item['file'])
    # Only temporary files created within this project's QA directory are extracted here.
    with tempfile.TemporaryDirectory(prefix='python-build-',dir=cache) as directory:
        scratch=Path(directory)
        for key,folder in [('python',scratch),('pillow',scratch/'Lib/site-packages'),('numpy',scratch/'Lib/site-packages'),('onnxruntime',scratch/'Lib/site-packages')]:
            with zipfile.ZipFile(cache/packages[key]['file']) as archive:
                for entry in archive.infolist():
                    target=safe_path(folder,entry.filename)
                    if entry.is_dir(): target.mkdir(parents=True,exist_ok=True); continue
                    target.parent.mkdir(parents=True,exist_ok=True); target.write_bytes(archive.read(entry))
        (scratch/'python313._pth').write_text('python313.zip\n.\nLib/site-packages\n../engine\n',encoding='ascii')
        files={p.relative_to(scratch).as_posix():p for p in scratch.rglob('*') if p.is_file()}
        sync_owned(BASE/'Aplicativo/runtime',files,{'product':'TeclaScore private Python','python':'3.13.13','pillow':'12.3.0','numpy':'2.5.3','onnxruntime':'1.30.0'})
    python=BASE/'Aplicativo/runtime/python.exe'
    subprocess.run([str(python),'-I','-B','-c',
        "import sys,io,PIL;from PIL import Image;b=io.BytesIO();Image.new('RGB',(2,2)).save(b,format='PNG');b.seek(0);Image.open(b).load();assert PIL.__version__=='12.3.0';"
        "import numpy,onnxruntime;assert numpy.__version__=='2.5.3' and onnxruntime.__version__=='1.30.0';"
        "print(sys.version);print('Pillow',PIL.__version__,'numpy',numpy.__version__,'onnxruntime',onnxruntime.__version__)"],check=True,timeout=60)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--download',action='store_true');prepare(parser.parse_args().download)
