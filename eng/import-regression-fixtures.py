#!/usr/bin/env python3
"""One-time source import of finite JSON regression witnesses, not binary evidence."""
from pathlib import Path
import argparse, hashlib, json, shutil
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('json_source_directory', type=Path)
args = parser.parse_args()
rows = []
for family in ['MemberConformance', 'Conformance']:
    for source in sorted((args.json_source_directory / family).rglob('*')):
        if not source.is_file() or source.suffix not in ['.cs', '.fs', '.md']:
            continue
        relative = source.relative_to(args.json_source_directory)
        target = root / 'tests/RegressionSources' / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with source.open('rb') as input_stream, target.open('xb') as output:
            shutil.copyfileobj(input_stream, output)
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        assert hashlib.sha256(target.read_bytes()).hexdigest() == digest
        rows.append({'path': str(target.relative_to(root)), 'originRelativePath': str(relative), 'sha256': digest})
(root / 'provenance/regression-source-import.json').write_text(json.dumps({
    'scope': 'Source-only port of existing finite inference/attribute regression witnesses; original cross-project execution does not establish execution here.',
    'originProject': 'Neemle DBAL runtime/Json, MIT; source snapshot SHA per file',
    'nativeMetadataBaseline': 'Tool50', 'executedInThisCheckout': False, 'files': rows}, indent=2) + '\n')
print('Imported', len(rows), 'source regression files; no execution claim.')
