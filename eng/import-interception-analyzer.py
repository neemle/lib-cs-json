#!/usr/bin/env python3
# Copyright (c) Neemle contributors. Licensed under the MIT License.
"""Import exact upstream analyzer sources for an executable-interceptor prototype."""
from pathlib import Path
import concurrent.futures
import hashlib
import json
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
COMMIT = '6aa72daa24f66c01c3d7b6a32519e9d21396deec'
BASE = 'src/tools/illink/src/'


def main():
    destination = ROOT / 'tools/InterceptionAwareAnalyzer/upstream'
    if destination.exists():
        raise FileExistsError(destination)
    inputs = []

    def walk(folder):
        url = f'https://api.github.com/repos/dotnet/runtime/contents/{BASE}{folder}?ref={COMMIT}'
        with urllib.request.urlopen(url, timeout=30) as response:
            entries = json.load(response)
        for entry in entries:
            relative = entry['path'].removeprefix(BASE)
            if entry['type'] == 'dir':
                walk(relative)
            elif entry['name'].endswith(('.cs', '.resx')):
                inputs.append((relative, entry['download_url']))

    for folder in ('ILLink.RoslynAnalyzer', 'ILLink.Shared'):
        walk(folder)

    def fetch(item):
        relative, url = item
        with urllib.request.urlopen(url, timeout=30) as response:
            contents = response.read()
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(contents)
        return {'path': relative, 'url': url, 'sha256': hashlib.sha256(contents).hexdigest()}

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        records = sorted(executor.map(fetch, inputs), key=lambda record: record['path'])
    record = {'repository': 'https://github.com/dotnet/runtime', 'commit': COMMIT,
              'scope': 'Source-only Roslyn analyzer prototype, not SDK binary equivalence or native acceptance',
              'inputs': records}
    (ROOT / 'provenance/interception-analyzer-upstream.json').write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps({'sourceFiles': len(records), 'sourceBytes': sum(path.stat().st_size for path in destination.rglob('*') if path.is_file())}))


if __name__ == '__main__':
    main()
