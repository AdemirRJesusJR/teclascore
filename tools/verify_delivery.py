"""Verify a staged or delivered build against its manifest (read-only)."""
from pathlib import Path
import hashlib
import json
import sys

base = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
manifest = json.loads((base / 'delivery-manifest.json').read_text(encoding='utf-8-sig'))
failures = []
for name, expected in manifest.items():
    path = base / name
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        failures.append(name)
print(json.dumps({'files': len(manifest), 'failures': failures}, ensure_ascii=False))
sys.exit(bool(failures))
