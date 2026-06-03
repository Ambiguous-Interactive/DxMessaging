#!/usr/bin/env python3
# =============================================================================
# scripts/unity/lib/parse-test-results.py
# =============================================================================
# Tiny NUnit XML summary extractor. The single source of truth for parsing the
# first <test-run> element from a Unity Test Framework results.xml. Used by:
#   - scripts/unity/run-tests.sh        (print_results_summary)
#   - scripts/unity/run-tests.ps1       (Write-ResultsSummary)
# Both callers consume the same one-line "OK ..." format below, so any
# behavioral change here applies uniformly.
#
# Usage:
#   python3 scripts/unity/lib/parse-test-results.py <results.xml>
#
# Output (single line on stdout):
#   OK total=<int> passed=<int> failed=<int> skipped=<int>   on success (exit 0)
#   PARSE_ERROR:<reason>                                     on failure   (exit 2)
#
# Exit codes:
#   0  success
#   2  could not parse / no <test-run> element / file missing / wrong argv
# =============================================================================

import sys
import xml.etree.ElementTree as ET


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        sys.stdout.write("PARSE_ERROR:usage parse-test-results.py <results.xml>\n")
        return 2

    path = argv[1]
    try:
        root = ET.parse(path).getroot()
    except FileNotFoundError as exc:
        sys.stdout.write(f"PARSE_ERROR:file not found: {exc}\n")
        return 2
    except ET.ParseError as exc:
        sys.stdout.write(f"PARSE_ERROR:malformed XML: {exc}\n")
        return 2

    tr = root if root.tag == "test-run" else root.find(".//test-run")
    if tr is None:
        sys.stdout.write("PARSE_ERROR:no <test-run> element\n")
        return 2

    sys.stdout.write(
        "OK total={t} passed={p} failed={f} skipped={s}\n".format(
            t=tr.get("total", "0"),
            p=tr.get("passed", "0"),
            f=tr.get("failed", "0"),
            s=tr.get("skipped", "0"),
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
