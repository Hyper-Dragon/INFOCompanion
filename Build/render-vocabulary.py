#!/usr/bin/env python3
"""Render the phrase tables in `Documents/16_appendix_a.md` §4 from `Vocabulary.cs`.

The tables were written out by hand, twice, and drifted: the delegation forms and `what about` were
missing from the appendix while the code had them, and the brief table had no entry at all (§21 item
13). A table that is a copy is a table that will be wrong, so the document no longer holds them — this
script reads the arrays out of the code and rewrites the marked regions between them.

    python3 Build/render-vocabulary.py            # rewrite the document
    python3 Build/render-vocabulary.py --check    # exit 1 if it is out of step

The markers are in the document, one pair per region:

    <!-- generated: classify — from Vocabulary.cs, by Build/render-vocabulary.py -->
    ...
    <!-- /generated -->

Everything between them is this script's to write. Everything outside them is prose a person wrote.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VOCABULARY = ROOT / "Source" / "Agent" / "src" / "Agent.Core" / "Loop" / "Vocabulary.cs"
CLASSIFIER = ROOT / "Source" / "Agent" / "src" / "Agent.Core" / "Loop" / "UtteranceClassifier.cs"
APPENDIX = ROOT / "Documents" / "16_appendix_a.md"

# The regions in the appendix, and the tables each one carries: (label, array name). The **labels** are
# the document's own words, because they carry a gloss a field name cannot; the **contents** are the
# code's, and nothing here may paraphrase them.
REGIONS: dict[str, list[tuple[str, str]]] = {
    "classify": [
        ("Grant", "GrantPhrases"),
        ("Veto", "VetoPhrases"),
        ("Steer (a plan rather than a command)", "SteerPhrases"),
        (
            "Delegation (an order phrased as a request, checked before the question mark decides)",
            "DelegationPhrases",
        ),
        ("Question", "QuestionPhrases"),
    ],
    "anywhere": [
        ("Errand", "ErrandPhrases"),
        (
            "Unmistakable errand (delegation even when the line opens with a word the game knows)",
            "UnmistakableErrandPhrases",
        ),
        ("Suggestion (\"have you considered…\")", "SuggestionPhrases"),
        (
            "Brief (too long to be one command, and carried by phrases a remark does not use)",
            "BriefPhrases",
        ),
    ],
}


def arrays(*sources: str) -> dict[str, list[str]]:
    """Every `static readonly string[] Name = [ ... ];` in the files, in order.

    Two files, because the tables are not all in one place: the classifier's own errand, unmistakable
    errand and suggestion lists are declared beside the rules that use them, and are private because
    nothing outside the classifier reads them.
    """
    found: dict[str, list[str]] = {}
    for source in sources:
        for match in re.finditer(
            r"static readonly string\[\] (\w+)\s*=\s*\[(.*?)\];", source, re.DOTALL
        ):
            found[match.group(1)] = re.findall(r'"([^"]*)"', match.group(2))
    return found


def table(names: list[tuple[str, str]], found: dict[str, list[str]]) -> str:
    lines = ["| Table | Phrases |", "|---|---|"]
    for label, name in names:
        if name not in found:
            raise SystemExit(f"render-vocabulary: {name} is not in the sources")
        phrases = ", ".join(f"`{phrase}`" for phrase in found[name])
        lines.append(f"| **{label}** | {phrases} |")
    return "\n".join(lines)


def render(source: str, classifier: str, document: str) -> str:
    found = arrays(source, classifier)
    for region, names in REGIONS.items():
        pattern = re.compile(
            r"(<!-- generated: " + region + r"[^>]*-->)(.*?)(<!-- /generated -->)", re.DOTALL
        )
        if not pattern.search(document):
            raise SystemExit(f"render-vocabulary: no `{region}` region in {APPENDIX.name}")
        document = pattern.sub(
            lambda match: match.group(1) + "\n\n" + table(names, found) + "\n\n" + match.group(3),
            document,
            count=1,
        )
    return document


def main() -> int:
    check = "--check" in sys.argv
    after = render(
        VOCABULARY.read_text(encoding="utf-8"),
        CLASSIFIER.read_text(encoding="utf-8"),
        APPENDIX.read_text(encoding="utf-8"),
    )
    before = APPENDIX.read_text(encoding="utf-8")

    if before == after:
        print("vocabulary-tables: the appendix and Vocabulary.cs agree")
        return 0

    if check:
        print("vocabulary-tables: Documents/16_appendix_a.md is out of step with Vocabulary.cs")
        print("                   run `python3 Build/render-vocabulary.py`")
        return 1

    APPENDIX.write_text(after, encoding="utf-8")
    print("vocabulary-tables: Documents/16_appendix_a.md rewritten from the code")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
