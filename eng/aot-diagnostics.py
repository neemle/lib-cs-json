#!/usr/bin/env python3
"""Inventory native-publish diagnostics and reject warnings or errors for acceptance."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-clean", action="store_true")
    args = parser.parse_args()
    pattern = re.compile(r"^(.*?)\s*:\s*(?:(?:Trim|AOT) analysis )?(warning|error) (IL\d+): (.*)$")
    other_pattern = re.compile(r"^(.*?)\s*:\s*warning(?:\s+([A-Z]+\d+))?\s*:\s*(.*)$")
    diagnostics = []
    other_diagnostics = []
    compiler_failures = []
    published = True
    for path in args.logs:
        content = path.read_text()
        # A quiet, empty, or interrupted log must not count as a clean publication.
        published = published and bool(re.search(r"^\s*\S+\s*->\s*.+[/\\]\s*$", content, re.MULTILINE))
        published = published and not re.search(r"\berror\s+(?:[A-Z]+\d+|:)", content)
        for line in content.splitlines():
            # Every resilient compiler replacement is a failed method body,
            # regardless of the particular assembly/type/signature error text.
            if "will always throw because:" in line:
                compiler_failures.append({"log": str(path), "message": line.strip()})
            match = pattern.match(line)
            if match:
                location, severity, code, message = match.groups()
                location = re.sub(r"\(\d+(?:,\d+)?\)$", "", location.strip())
                message = re.sub(r"\s+\[[^\]]+\.csproj\]$", "", message)
                diagnostics.append({"source": location, "code": code, "message": message, "severity": severity})
            elif match := other_pattern.match(line):
                location, code, message = match.groups()
                location = re.sub(r"\(\d+(?:,\d+)?\)$", "", location.strip())
                message = re.sub(r"\s+\[[^\]]+\.csproj\]$", "", message)
                other_diagnostics.append({"source": location, "code": code or "warning", "message": message})
    unique = {}
    for row in diagnostics:
        unique.setdefault((row["source"], row["code"], row["message"]), set()).add(row["severity"])
    other_unique = {(row["source"], row["code"], row["message"]) for row in other_diagnostics}
    report = {
        "logs": [str(path) for path in args.logs],
        "diagnostic_occurrences": len(diagnostics),
        "warning_occurrences": sum(row["severity"] == "warning" for row in diagnostics),
        "error_occurrences": sum(row["severity"] == "error" for row in diagnostics),
        "unique_diagnostics": len(unique),
        "codes": dict(sorted(Counter(row["code"] for row in diagnostics).items())),
        "diagnostics": [{"source": source, "code": code, "message": message,
                         "severities": sorted(unique[(source, code, message)])}
                        for source, code, message in sorted(unique)],
        "other_warning_occurrences": len(other_diagnostics),
        "unique_other_diagnostics": len(other_unique),
        "other_codes": dict(sorted(Counter(row["code"] for row in other_diagnostics).items())),
        "other_diagnostics": [{"source": source, "code": code, "message": message} for source, code, message in sorted(other_unique)],
        "compiler_failures": compiler_failures,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(f"{len(diagnostics)} IL diagnostic occurrences "
          f"({report['warning_occurrences']} warnings, {report['error_occurrences']} errors); "
          f"{len(unique)} unique diagnostics. Inventory: {args.output}")
    if other_diagnostics:
        print(f"{len(other_diagnostics)} other warning occurrences; {len(other_unique)} unique diagnostics.")
    if compiler_failures:
        print(f"{len(compiler_failures)} unresolved compiler method bodies; publication is not accepted.")
    if args.require_clean and (diagnostics or other_diagnostics or compiler_failures or not published):
        sys.exit("Release acceptance failed: native publication must complete with zero build, compiler, trim and AOT warnings or errors.")


if __name__ == "__main__":
    main()
