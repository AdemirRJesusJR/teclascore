"""Prepare a published application for package_app.py (no network or installation)."""
from pathlib import Path
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

BASE = Path(__file__).resolve().parents[1]
APP = BASE / 'Aplicativo'


def prepare():
    version = ET.parse(BASE / 'src/Teclas.App/Teclas.App.csproj').findtext('.//Version')
    if not (APP / 'Teclas.Partituras.exe').is_file():
        raise RuntimeError('Publique o aplicativo antes de preparar o instalador.')
    for name in ('Install.ps1', 'Instalar.cmd', 'RuntimePrerequisite.ps1'):
        shutil.copy2(BASE / 'tools/installer' / name, APP / name)
    # Installer verifies the executable/runtime content, not mutable readme copies.
    files = {}
    for path in sorted(APP.rglob('*')):
        if (not path.is_file() or '__pycache__' in path.parts
                or path.suffix in ('.pdb', '.pyc')
                or path.name in ('installation-manifest.json', 'LEIA-ME.txt', 'VALIDACAO.txt', 'ROTEIRO-DE-TESTES.txt')):
            continue
        files[path.relative_to(APP).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    (APP / 'installation-manifest.json').write_text(json.dumps(
        {'product': 'TeclaScore', 'version': version, 'files': files}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'installer_version': version, 'verified_files': len(files)}))


if __name__ == '__main__':
    prepare()
