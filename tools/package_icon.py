"""Package the approved transparent PNG as a Windows multi-resolution icon."""
from pathlib import Path
import hashlib
import json
import shutil
import sys
from PIL import Image

base = Path(__file__).resolve().parents[1]
assets = base / 'src/Teclas.App/Assets'
source = Path(sys.argv[1]) if len(sys.argv) > 1 else assets / 'teclas.png'
assets.mkdir(parents=True, exist_ok=True)
if source.resolve() != (assets / 'teclas.png').resolve():
    shutil.copy2(source, assets / 'teclas.png')
with Image.open(source) as original:
    assert original.mode == 'RGBA' and original.getextrema()[3][0] == 0
    original.save(assets / 'teclas.ico', format='ICO', sizes=[(s,s) for s in (16,24,32,48,64,128,256)])
with Image.open(assets / 'teclas.ico') as icon:
    sizes = sorted(icon.ico.sizes())
    assert sizes == [(s,s) for s in (16,24,32,48,64,128,256)]
assert hashlib.sha256(source.read_bytes()).digest() == hashlib.sha256((assets/'teclas.png').read_bytes()).digest()
print(json.dumps({'sizes': sizes, 'png_bytes': source.stat().st_size, 'ico_bytes': (assets/'teclas.ico').stat().st_size}))
