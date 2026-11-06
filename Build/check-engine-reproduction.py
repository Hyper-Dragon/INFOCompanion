#!/usr/bin/env python3
"""Check that `16_appendix_a.md` §1 still reproduces `Assets/prompts/engine.txt` verbatim.

The appendix exists to be readable without the source, so it carries the engine text in full — which
makes it a copy, and a copy drifts. It did: Block 12 rewrote the notebook section and the `saves`
bullet, and §1 kept the old words while one bullet was updated and a whole eleven-line block was never
added at all (§21 item 15). The four-of-a-kind check that would have caught it is a diff.

    python3 Build/check-engine-reproduction.py    # exit 1 if §1 and the file disagree
"""

from __future__ import annotations

import difflib
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APPENDIX = ROOT / "Documents" / "16_appendix_a.md"
ENGINE = ROOT / "Assets" / "prompts" / "engine.txt"


def reproduced(appendix: str) -> list[str]:
    """The fenced block under §1, as lines."""
    section = re.search(r"^## 1\..*?(?=^## 2\.)", appendix, re.DOTALL | re.MULTILINE)
    if section is None:
        raise SystemExit("engine-reproduction: no section 1 in 16_appendix_a.md")

    block = re.search(r"```[a-z]*\n(.*?)```", section.group(0), re.DOTALL)
    if block is None:
        raise SystemExit("engine-reproduction: no fenced block in section 1")

    return [line.rstrip() for line in block.group(1).strip().splitlines()]


def main() -> int:
    document = reproduced(APPENDIX.read_text(encoding="utf-8"))
    source = [line.rstrip() for line in ENGINE.read_text(encoding="utf-8").strip().splitlines()]

    if document == source:
        print(f"engine-reproduction: §1 and {ENGINE.name} agree ({len(source)} lines)")
        return 0

    print(f"engine-reproduction: §1 has {len(document)} lines, {ENGINE.name} has {len(source)}")
    print("  `-` is the appendix, `+` is the prompt file. Copy the file's lines into §1.")
    for line in difflib.unified_diff(document, source, "§1", ENGINE.name, lineterm="", n=1):
        print("  " + line)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
