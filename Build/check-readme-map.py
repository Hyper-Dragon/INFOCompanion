#!/usr/bin/env python3
"""Check that the README's map of the project is the project.

Two lists in the README describe things that grow: the document set — a range in the directory map
and a row per document in the table — and the container suite — a count and a name per test.
Adding to either does not update them, and nothing noticed. The range read `00–15` while
`16_appendix_a.md` sat in the same file's own table; the container section said *these eight* while
`check.sh` ran ten, with `turn-number-per-game` and `player-commands` absent from the prose
altogether. Neither is a copy of anything, so no differ can see them, and `markdownlint` is happy
with both: these are claims that stopped counting.

The rule this holds to: **a claim the README makes about the tree must match the tree, and a claim
it stops making is a finding rather than a silence.** So the range and the count are both required,
not merely checked when present.

What it holds together:

  * every document in `Documents/` has a row in `Documents/README.md`'s table;
  * every `Documents/*.md` path in that table exists;
  * the range that file states matches the highest numbered document;
  * every name in `check.sh`'s `all_tests` is named in `README-private.md` as `<name>.sh`;
  * that file's "runs these <n>" agrees with how many there are.

Both halves are maintainer-only and both are left out of the release tree (`Documents/` and
`README-private.md`, each `export-ignore`), so a public checkout carries neither. That is not a
fault: those claims are not the public repository's to make. It says so and checks what it was
given, because failing on an absent file would mean the public repository could never run the gate.

The short public `README.md` makes neither claim, and this does not read it.

A file is a document unless `git check-ignore` says otherwise — the rule `Build/docs-pdf.sh` uses,
so the working plan beside the set is not one, while a document that is written but not yet
committed still is.

    python3 Build/check-readme-map.py    # exit 1, listing what disagrees
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCUMENTS = ROOT / "Documents"
# The set's own map. It is not published, so its absence is a public checkout rather than a fault.
MAP = DOCUMENTS / "README.md"
# The container suite is described here, in the maintainer's half of the README. Neither file is
# published, so a release checkout carries neither and makes neither claim.
SUITE_DOC = ROOT / "README-private.md"
CHECK_SH = ROOT / "Build" / "check.sh"

# A row of `Documents/README.md`'s document table: the path is the first backticked cell.
ROW = re.compile(r"^\|\s*`(Documents/[^`]+\.md)`")
# "the specification, 00–16, and the research paper" — the set's own statement of how far it runs.
RANGE = re.compile(r"the specification,\s*00[–-](\d+)")
# "Build/check.sh runs these ten in the order that fails fastest."
SUITE = re.compile(r"runs these (\w+)")

NUMBERS = {
    "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
    "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12,
}


def ignored(path: Path) -> bool:
    """True when git's ignore rules say this file is not part of the repository."""
    try:
        done = subprocess.run(
            ["git", "check-ignore", "-q", str(path.relative_to(ROOT))],
            cwd=ROOT,
            capture_output=True,
        )
    except OSError:
        return False
    return done.returncode == 0


def documents() -> list[Path]:
    """The document set: the markdown in `Documents/` that the repository holds.

    The map is not one of its own rows.
    """
    return sorted(
        path
        for path in DOCUMENTS.glob("*.md")
        if path.name != MAP.name and not ignored(path)
    )


def listed(mapped: str) -> list[str]:
    """The document paths the map's table names."""
    found = []
    for line in mapped.splitlines():
        row = ROW.match(line)
        if row is not None:
            found.append(row.group(1))
    return found


def suite(check_sh: str) -> list[str]:
    """The container tests `check.sh` runs, in its own order."""
    found = re.search(r"all_tests=\((.*?)\)", check_sh, re.DOTALL)
    if found is None:
        raise SystemExit("readme-map: no all_tests array in Build/check.sh")
    return found.group(1).split()


def main() -> int:
    tests = suite(CHECK_SH.read_text(encoding="utf-8"))
    problems: list[str] = []
    present: list[Path] = []
    highest = 0
    suite_doc = SUITE_DOC.read_text(encoding="utf-8") if SUITE_DOC.exists() else None

    if MAP.exists():
        mapped = MAP.read_text(encoding="utf-8")
        present = documents()
        named = listed(mapped)

        for path in present:
            if f"Documents/{path.name}" not in named:
                problems.append(
                    f"`Documents/{path.name}` is a document with no row in Documents/README.md"
                )

        for path in named:
            if not (ROOT / path).exists():
                problems.append(f"Documents/README.md lists `{path}`, which does not exist")

        numbered = [int(path.name[:2]) for path in present if path.name[:2].isdigit()]
        highest = max(numbered) if numbered else 0
        claimed = RANGE.search(mapped)
        if claimed is None:
            problems.append(
                "Documents/README.md no longer says how far the set runs — it should read "
                f"`the specification, 00–{highest:02d}`"
            )
        elif int(claimed.group(1)) != highest:
            problems.append(
                f"Documents/README.md says the specification is 00–{claimed.group(1)}; "
                f"the highest document is 00–{highest:02d}"
            )

    if suite_doc is not None:
        for name in tests:
            if f"{name}.sh" not in suite_doc:
                problems.append(
                    f"`check.sh` runs {name}.sh, which README-private.md does not name"
                )

        said = SUITE.search(suite_doc)
        if said is None:
            problems.append(
                "README-private.md no longer says how many the suite runs — it should read "
                f"`runs these {len(tests)}`"
            )
        else:
            word = said.group(1).lower()
            number = NUMBERS.get(word, int(word) if word.isdigit() else None)
            if number is not None and number != len(tests):
                problems.append(
                    f'README-private.md says the container suite is "these {word}"; '
                    f"`check.sh` runs {len(tests)}"
                )

    if not problems:
        if MAP.exists():
            print(
                f"readme-map: the set's {len(present)} documents (00–{highest:02d}) and "
                f"{len(tests)} container tests agree with the tree"
            )
        elif suite_doc is None:
            print("readme-map: neither the set nor the maintainer's README is in this checkout")
        else:
            print(
                f"readme-map: the set is not in this checkout; "
                f"{len(tests)} container tests agree with the tree"
            )
        return 0

    print("readme-map: the README's map of the project disagrees with the project:")
    for problem in problems:
        print(f"  {problem}")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
