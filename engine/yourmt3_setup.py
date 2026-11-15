"""Instala o componente YourMT3+ (o modelo que reconhece as notas da gravação) numa pasta própria do usuário.

O YourMT3+ precisa de PyTorch, que não cabe no Python privado do TeclaScore. Por isso ele vive num ambiente separado,
em %LOCALAPPDATA%\\TeclasPartituras\\componentes\\yourmt3 (ou na pasta de TECLAS_YOURMT3), e o motor o chama como um
processo à parte (yourmt3_engine.py → yourmt3_runner.py). Tudo vem das fontes oficiais, com versão e SHA-256 fixados:

- uv (Astral, GitHub) cria o ambiente com Python 3.11;
- PyTorch 2.5.1 (CUDA 12.1 quando há placa NVIDIA; senão a versão de CPU) e as bibliotecas do modelo;
- o código e o modelo "YPTF.MoE+Multi (noPS)" dos autores, do espaço mimbres/YourMT3 no Hugging Face (Apache 2.0).

Só usa a biblioteca padrão: roda com o Python do TeclaScore. Uso: python yourmt3_setup.py [--pasta PASTA] [--cpu]"""
from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess, sys, urllib.parse, urllib.request, zipfile

UV = {'version': '0.13.8', 'url': 'https://github.com/astral-sh/uv/releases/download/0.13.8/uv-x86_64-pc-windows-msvc.zip',
      'sha256': '529e78a9cec6cd7e3e014f7ecd8bda0c113969d4ed0f2efeedb85c30943325b8'}
SPACE = 'mimbres/YourMT3'
REVISION = '5e66c1ea173a8186e0d20432b841d3180cc015b5'
EXPERIMENT = 'mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops'
CHECKPOINT = {'path': f'amt/logs/2024/{EXPERIMENT}/checkpoints/last.ckpt', 'size': 561544628,
              'sha256': 'ae38e415c79efd5592dcb9b658cdb99ddb11d4c4e1eaa364cab04a052473fc25'}
TORCH = ['torch==2.5.1', 'torchaudio==2.5.1']
PACKAGES = ['lightning==2.6.6', 'pytorch-lightning==2.6.6', 'transformers==4.45.1', 'numpy==1.26.4', 'einops==0.8.2',
            'deprecated==1.3.1', 'mido==1.3.3', 'mir-eval==0.8.2', 'librosa==0.11.0', 'wandb==0.31.0', 'soundfile==0.14.0']
MARKER = 'instalado.json'
COMPONENT = 1          # muda quando a instalação precisa ser refeita


def default_root():
    """A pasta do componente: TECLAS_YOURMT3 ou, por padrão, ao lado dos dados do TeclaScore."""
    chosen = os.environ.get('TECLAS_YOURMT3')
    if chosen: return Path(chosen)
    return Path(os.environ.get('LOCALAPPDATA') or Path.home() / 'AppData/Local') / 'TeclasPartituras' / 'componentes' / 'yourmt3'


def python_of(root): return Path(root) / 'venv' / 'Scripts' / 'python.exe'
def code_of(root): return Path(root) / 'YourMT3'
def checkpoint_of(root): return code_of(root) / CHECKPOINT['path']


def installed(root=None):
    """O que está instalado (o conteúdo de instalado.json) ou None quando falta alguma parte."""
    root = Path(root or default_root())
    try: marker = json.loads((root / MARKER).read_text(encoding='utf-8'))
    except (OSError, ValueError): return None
    if marker.get('component') != COMPONENT or marker.get('revision') != REVISION: return None
    if not python_of(root).is_file() or not checkpoint_of(root).is_file() or not (code_of(root) / 'model_helper.py').is_file(): return None
    return marker


def _digest(path):
    sha = hashlib.sha256()
    with open(path, 'rb') as stream:
        while chunk := stream.read(1 << 20): sha.update(chunk)
    return sha.hexdigest()


def _download(url, target, progress, label, sha256=None, size=None):
    """Baixa para um arquivo temporário e só o põe no lugar depois de conferir o SHA-256."""
    target = Path(target)
    if target.is_file() and (sha256 is None or _digest(target) == sha256): return
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + '.baixando')
    request = urllib.request.Request(url, headers={'User-Agent': 'TeclaScore'})
    with urllib.request.urlopen(request, timeout=120) as response, partial.open('wb') as output:
        total = size or int(response.headers.get('Content-Length') or 0); done = 0; shown = -1
        while chunk := response.read(1 << 20):
            output.write(chunk); done += len(chunk)
            if total and done * 100 // total != shown:
                shown = done * 100 // total
                if shown % 5 == 0: progress(f'{label}… {shown}%')
    if sha256 and _digest(partial) != sha256:
        partial.unlink(); raise ValueError(f'O arquivo baixado não confere com o esperado: {target.name}')
    partial.replace(target)


def _run(command, progress, label, env=None):
    progress(label)
    process = subprocess.run([str(c) for c in command], capture_output=True, text=True, encoding='utf-8', errors='replace', env=env,
                             creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if process.returncode:
        tail = (process.stderr or process.stdout or '').strip().splitlines()[-12:]
        raise RuntimeError(f'{label} falhou:\n' + '\n'.join(tail))
    return process.stdout


def has_nvidia():
    """Há uma placa NVIDIA com driver (nvidia-smi responde)? Sem ela o modelo roda no processador, bem mais devagar."""
    try:
        return subprocess.run(['nvidia-smi', '-L'], capture_output=True, timeout=20,
                              creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)).returncode == 0
    except (OSError, subprocess.SubprocessError): return False


def install(root=None, progress=print, cpu=None):
    """Instala (ou completa) o componente e devolve o conteúdo de instalado.json. Refazer é seguro: o que já está
    baixado e confere não é baixado de novo."""
    root = Path(root or default_root()); root.mkdir(parents=True, exist_ok=True)
    done = installed(root)
    if done: progress('O YourMT3+ já está instalado.'); return done
    if cpu is None: cpu = not has_nvidia()
    # 1. uv, que traz o Python 3.11 e instala os pacotes.
    uv = root / 'uv' / 'uv.exe'
    if not uv.is_file():
        archive = root / 'uv' / 'uv.zip'
        _download(UV['url'], archive, progress, 'Baixando o instalador de pacotes (uv)', UV['sha256'])
        with zipfile.ZipFile(archive) as bundle:
            for name in bundle.namelist():
                if Path(name).name in ('uv.exe', 'uvx.exe'): (root / 'uv' / Path(name).name).write_bytes(bundle.read(name))
        archive.unlink()
    env = dict(os.environ, UV_CACHE_DIR=str(root / 'uv' / 'cache'), UV_PYTHON_INSTALL_DIR=str(root / 'uv' / 'python'),
               UV_NO_PROGRESS='1', PYTHONUTF8='1')
    for name in ('VIRTUAL_ENV', 'PYTHONHOME', 'PYTHONPATH'): env.pop(name, None)
    python = python_of(root)
    if not python.is_file():
        _run([uv, 'venv', '--python', '3.11', '--python-preference', 'only-managed', root / 'venv'], progress, 'Criando o ambiente Python 3.11', env)
    index = 'https://download.pytorch.org/whl/' + ('cpu' if cpu else 'cu121')
    _run([uv, 'pip', 'install', '--python', python, '--index-url', index, *TORCH], progress,
         'Baixando o PyTorch' + (' (processador)' if cpu else ' com CUDA (cerca de 2,5 GB)') + '…', env)
    _run([uv, 'pip', 'install', '--python', python, *PACKAGES], progress, 'Baixando as bibliotecas do modelo…', env)
    # 2. Código dos autores na revisão fixada (só amt/src e o model_helper) e o modelo.
    code = code_of(root)
    listing = code / 'arquivos.json'
    _download(f'https://huggingface.co/api/spaces/{SPACE}/tree/{REVISION}?recursive=1', listing, progress, 'Lendo a lista de arquivos do YourMT3+')
    files = [f['path'] for f in json.loads(listing.read_text(encoding='utf-8'))
             if f.get('type') == 'file' and (f['path'].startswith('amt/src/') or f['path'] == 'model_helper.py')]
    if len(files) < 50: raise RuntimeError('A lista de arquivos do YourMT3+ veio incompleta.')
    base = f'https://huggingface.co/spaces/{SPACE}/resolve/{REVISION}/'
    for number, name in enumerate(files, 1):
        target = (code / name).resolve()
        if code.resolve() not in target.parents: raise ValueError('Caminho inesperado na lista de arquivos: ' + name)
        if not target.is_file(): _download(base + urllib.parse.quote(name), target, lambda message: None, name)
        if number % 20 == 0 or number == len(files): progress(f'Baixando o código do YourMT3+… {number}/{len(files)}')
    _download(base + urllib.parse.quote(CHECKPOINT['path']), checkpoint_of(root), progress, 'Baixando o modelo YourMT3+ (536 MB)',
              CHECKPOINT['sha256'], CHECKPOINT['size'])
    # 3. Confere: o PyTorch importa e enxerga (ou não) a placa de vídeo.
    probe = _run([python, '-c', 'import torch, torchaudio, lightning, transformers, json; print(json.dumps({"torch": torch.__version__, '
                  '"cuda": torch.cuda.is_available(), "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None}))'],
                 progress, 'Conferindo a instalação…', env)
    marker = {'component': COMPONENT, 'revision': REVISION, 'experiment': EXPERIMENT, **json.loads(probe.strip().splitlines()[-1])}
    (root / MARKER).write_text(json.dumps(marker, ensure_ascii=False, indent=1), encoding='utf-8')
    shutil.rmtree(root / 'uv' / 'cache', ignore_errors=True)
    progress('YourMT3+ instalado' + (f' (placa de vídeo: {marker["gpu"]}).' if marker.get('cuda') else ' (sem placa NVIDIA: vai rodar no processador, mais devagar).'))
    return marker


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Instala o componente YourMT3+ do TeclaScore.')
    parser.add_argument('--pasta', default=None); parser.add_argument('--cpu', action='store_true')
    arguments = parser.parse_args()
    try: install(arguments.pasta, lambda message: print(message, flush=True), cpu=True if arguments.cpu else None)
    except Exception as error: print(f'Falhou: {error}', file=sys.stderr); sys.exit(1)
