"""Create the offline Inno Setup input kit from the verified published application."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

BASE = Path(__file__).resolve().parents[1]

def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()

def safe_path(root, name):
    parts = PurePosixPath(name.replace('\\', '/')).parts
    if not parts or any(p in ('.', '..') or ':' in p for p in parts) or PurePosixPath(name.replace('\\', '/')).is_absolute():
        raise ValueError('Unsafe managed path: ' + name)
    root = root.absolute()
    target = root.joinpath(*parts)
    if not target.resolve().is_relative_to(root.resolve()):
        raise ValueError('Managed path escapes its directory')
    for parent in [target, *target.parents]:
        if parent.is_symlink() or (hasattr(parent, 'is_junction') and parent.is_junction()):
            raise ValueError('Links are not allowed in the installation kit')
        if parent == root: break
    return target

def sync_owned(destination, files, metadata):
    """Replace only manifest-owned files. User scripts outside the manifest are preserved."""
    manifest = safe_path(destination, 'kit-manifest.json')
    old = json.loads(manifest.read_text(encoding='utf-8'))['files'] if manifest.exists() else {}
    for name in set(old) | set(files): safe_path(destination, name)
    for name, source in files.items():
        target = safe_path(destination, name)
        if target.exists() and name not in old and digest(target) != digest(source):
            raise ValueError('Unmanaged file would be overwritten: ' + name)
    destination.mkdir(parents=True, exist_ok=True)
    for name, source in files.items():
        target = safe_path(destination, name)
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or digest(target) != digest(source): shutil.copy2(source, target)
    for name in old.keys() - files.keys():
        target = safe_path(destination, name)
        if target.is_file(): target.unlink()
    result = dict(metadata, files={name: digest(safe_path(destination, name)) for name in sorted(files)})
    temporary = manifest.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(manifest)
    return result

def build(base=BASE):
    app = base/'Aplicativo'
    installed = json.loads((app/'installation-manifest.json').read_text(encoding='utf-8'))
    version = ET.parse(base/'src/Teclas.App/Teclas.App.csproj').findtext('.//Version')
    if installed['product'] != 'TeclaScore' or installed['version'] != version:
        raise ValueError('Publish and prepare the current application before updating debug')
    for name, expected in installed['files'].items():
        if digest(safe_path(app, name)) != expected: raise ValueError('Payload hash mismatch: ' + name)
    if 'runtime/python.exe' not in installed['files']:
        raise ValueError('Prepare the bundled Python/Pillow before generating debug')
    files = {'app/'+name: safe_path(app, name) for name in installed['files']}
    for name in ['installation-manifest.json', 'LEIA-ME.txt', 'VALIDACAO.txt', 'ROTEIRO-DE-TESTES.txt']:
        files['app/'+name] = app/name
    dependencies = json.loads((base/'tools/offline-dependencies.json').read_text(encoding='utf-8'))
    for dependency in dependencies.values():
        installer = base/'qa/offline-dependencies'/dependency['file']
        if digest(installer) != dependency['sha256']: raise ValueError('Official dependency missing or changed: '+dependency['file'])
        files['prerequisitos/'+dependency['file']] = installer
    files['LEIA-ME-INNO.txt'] = base/'tools/debug-kit/LEIA-ME-INNO.txt'
    msi = [dependency['file'] for dependency in dependencies.values() if dependency['file'].lower().endswith('.msi')]
    if len(msi) != 1: raise ValueError('The debug kit needs exactly one official MuseScore MSI')
    # The complete-installer script is regenerated with the current version on every delivery.
    script = base/'qa/debug-kit/TeclaScore-Completo.iss'
    script.parent.mkdir(parents=True, exist_ok=True)
    template = (base/'tools/debug-kit/TeclaScore-Completo.iss.template').read_text(encoding='utf-8')
    script.write_text(template.replace('@VERSION@', version).replace('@MSI@', msi[0]), encoding='utf-8-sig')
    files['TeclaScore-Completo.iss'] = script
    files['TeclaScore.ico'] = base/'src/Teclas.App/Assets/teclas.ico'
    files['dependencias.json'] = base/'tools/offline-dependencies.json'
    return sync_owned(base/'debug', files, {'product':'TeclaScore debug kit', 'version':version,
        'note':'Generated app/prerequisitos files are managed. Keep custom Inno scripts outside these folders.'})

if __name__ == '__main__':
    result=build(); print(json.dumps({'version':result['version'], 'debug_files':len(result['files'])}))
