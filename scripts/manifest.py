#!/usr/bin/env python3
"""Adds one build to manifest.json, the plugin repository file Jellyfin reads.

Run by CI on a release tag, once per build (Jellyfin 10.11 and 12):

    manifest.py add manifest.json <version> <targetAbi> <sourceUrl> <zip> <changelog-file>

Computes the zip's MD5 (Jellyfin checks every download against it) and a UTC
timestamp, and puts the entry first, newest on top. Adding a version that is
already there replaces it, so re-running a release job is safe.

    manifest.py selftest     # checks the above against a temporary file
"""
import hashlib
import json
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

GUID = 'a8b9c0d1-e2f3-4a5b-6c7d-8e9f0a1b2c3d'


def add(manifest: Path, version: str, abi: str, url: str, zip_path: Path, changelog: str, now=None) -> None:
    data = json.loads(manifest.read_text())
    plugin = next(p for p in data if p['guid'] == GUID)
    entry = {
        'version': version,
        'changelog': changelog.strip(),
        'targetAbi': abi,
        'sourceUrl': url,
        'checksum': hashlib.md5(zip_path.read_bytes()).hexdigest(),
        'timestamp': (now or datetime.now(timezone.utc)).strftime('%Y-%m-%dT%H:%M:%SZ'),
    }
    plugin['versions'] = [entry] + [v for v in plugin.get('versions', []) if v['version'] != version]
    manifest.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n')


def selftest() -> None:
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        m = d / 'manifest.json'
        m.write_text(json.dumps([{'guid': GUID, 'name': 'Cascade Server', 'versions': []}]))
        z = d / 'a.zip'
        z.write_bytes(b'dll bytes')
        t = datetime(2026, 9, 24, 12, 0, tzinfo=timezone.utc)
        add(m, '1.0.0.10', '10.11.0.0', 'https://x/a.zip', z, ' First release \n', t)
        add(m, '1.0.0.12', '12.0.0.0', 'https://x/b.zip', z, 'First release', t)
        v = json.loads(m.read_text())[0]['versions']
        assert [e['version'] for e in v] == ['1.0.0.12', '1.0.0.10'], v
        assert v[1] == {'version': '1.0.0.10', 'changelog': 'First release', 'targetAbi': '10.11.0.0',
                        'sourceUrl': 'https://x/a.zip', 'checksum': hashlib.md5(b'dll bytes').hexdigest(),
                        'timestamp': '2026-09-24T12:00:00Z'}, v[1]
        add(m, '1.0.0.10', '10.11.0.0', 'https://x/a2.zip', z, 'again', t)   # re-run: replaced, not duplicated
        v = json.loads(m.read_text())[0]['versions']
        assert [e['version'] for e in v] == ['1.0.0.10', '1.0.0.12'] and v[0]['sourceUrl'] == 'https://x/a2.zip', v
    print('manifest.py selftest passed')


if __name__ == '__main__':
    if sys.argv[1:2] == ['selftest']:
        selftest()
    elif sys.argv[1:2] == ['add'] and len(sys.argv) == 8:
        _, _, m, ver, abi, url, zp, notes = sys.argv
        add(Path(m), ver, abi, url, Path(zp), Path(notes).read_text())
    else:
        sys.exit(__doc__)
