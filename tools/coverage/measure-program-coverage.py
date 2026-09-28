#!/usr/bin/env python
"""SPEC-3.1.0.md F7-5: line coverage for Oom.Program, summed across every partial
class file (src/Oom/Program.cs + src/Oom/Cli/Program.<Command>.cs), never a single
file, per the review note under "F7. ... acceptance risk": "Program.cs icin %65
kapsama hedefi dosya bolunerek oyunlanabilir. -> Kapsama assembly duzeyinde ya da
adiyla listelenen siniflar icin olcusun."

Cobertura's <class> element is one per (type, source file) pair, so a partial type
shows up as several <class name="Oom.Program" filename="..."> rows, one per file
that contributes IL to it. This script sums <line hits="..."> across every such row
(not the per-file line-rate attribute, which would let one well-covered file mask a
poorly covered one) and fails loud below the threshold.

Usage: python measure-program-coverage.py <coverage.cobertura.xml> [--threshold 65] [--class Oom.Program]
Exit 0 and prints the measured percentage when it meets the threshold; exit 1 and
prints the same breakdown when it does not. Never edits the threshold or the
counting method from the command line in a way that changes what F7-5 checks;
--threshold exists only so a caller can never silently forget to pass 65.
"""
from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cobertura_xml", help="Path to a coverage.cobertura.xml produced by --collect:\"XPlat Code Coverage\"")
    parser.add_argument("--threshold", type=float, required=True, help="Minimum percent (e.g. 65) F7-5 requires")
    parser.add_argument("--class-name", default="Oom.Program", help="Fully qualified type name to sum (default: Oom.Program)")
    args = parser.parse_args()

    tree = ET.parse(args.cobertura_xml)
    root = tree.getroot()
    rows = [c for c in root.findall(".//class") if c.get("name") == args.class_name]
    if not rows:
        print(f"kapsama: '{args.class_name}' adinda hic <class> bulunamadi ({args.cobertura_xml})", file=sys.stderr)
        return 1

    total = 0
    covered = 0
    per_file: list[tuple[str, int, int]] = []
    for row in rows:
        lines = row.findall("./lines/line")
        file_total = len(lines)
        file_covered = sum(1 for line in lines if int(line.get("hits", "0")) > 0)
        total += file_total
        covered += file_covered
        per_file.append((row.get("filename", "?"), file_covered, file_total))

    percent = (100.0 * covered / total) if total else 0.0

    print(f"kapsama ({args.class_name}, {len(rows)} dosya):")
    for filename, file_covered, file_total in sorted(per_file):
        file_percent = (100.0 * file_covered / file_total) if file_total else 0.0
        print(f"  {filename}: {file_covered}/{file_total} ({file_percent:.1f}%)")
    print(f"toplam: {covered}/{total} satir = %{percent:.2f} (esik: %{args.threshold:.2f})")

    if percent < args.threshold:
        print(f"F7-5 basarisiz: %{percent:.2f} < %{args.threshold:.2f}", file=sys.stderr)
        return 1

    print(f"F7-5 gecti: %{percent:.2f} >= %{args.threshold:.2f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
