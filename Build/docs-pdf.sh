#!/usr/bin/env bash
# The documents, as PDFs.
#
#   Build/docs-pdf.sh                      every document in the set  ->  Documents/pdf/*.pdf
#   Build/docs-pdf.sh 02_game_boundary.md  just that one
#
# Why this way round. `pandoc --pdf-engine=...` wants a TeX installation, and there is none on this
# machine; Chrome, on the other hand, is exactly what a reader's eye expects for a page of tables,
# code fences and diagrams. So pandoc does the markdown (GitHub-flavoured, so the tables and the
# footnotes are read the way GitHub reads them), and Chrome prints the page:
#
#   markdown  --pandoc-->  one styled standalone HTML  --chrome-->  one PDF
#
# The mermaid diagrams are *rendered*, not printed as code, by fetching mermaid once into the build
# directory and letting the browser draw them before the page is printed. With no network the
# fetch fails and the diagrams print as code blocks — still readable, and the run says which way it
# went rather than pretending.
#
# The PDFs are a **rendering of the documents, not a second source of truth**: the markdown is what
# the specs are, and these are what you hand somebody who wants to read them on a train. Rerun this
# after editing a document; nothing else keeps them in step.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root" || exit 1

out="$root/Documents/pdf"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# --- what it needs ----------------------------------------------------------------------------
if ! command -v pandoc >/dev/null 2>&1; then
    echo "docs-pdf: pandoc is not installed" >&2
    exit 2
fi

chrome=""
for candidate in google-chrome chromium chromium-browser google-chrome-stable; do
    if command -v "$candidate" >/dev/null 2>&1; then
        chrome="$candidate"
        break
    fi
done
if [ -z "$chrome" ]; then
    echo "docs-pdf: no chrome to print the pages with (google-chrome or chromium)" >&2
    exit 2
fi

# --- the page's own look ----------------------------------------------------------------------
# Print-first: no colour banding, no hover, nothing that only means something with a mouse. The
# prose is a serif face because these are read rather than scanned; headings and tables are the
# sans one, which is what tells a reader at a glance where a section starts.
cat > "$work/doc.css" <<'CSS'
:root { --ink: #1b1c19; --dim: #5d5f57; --rule: #d8d9d2; --code: #f4f4f0; }
html { font-size: 10.5pt; }
body {
  font-family: Georgia, "Times New Roman", serif;
  color: var(--ink);
  line-height: 1.5;
  margin: 0;
  max-width: 47rem;
}
h1, h2, h3, h4, h5 { font-family: "Helvetica Neue", Helvetica, Arial, sans-serif; line-height: 1.2; }
h1 { font-size: 1.9rem; margin: 0 0 0.4rem; letter-spacing: -0.01em; }
h1.title { border-bottom: 2px solid var(--rule); padding-bottom: 0.5rem; }
h2 { font-size: 1.35rem; margin: 1.8rem 0 0.5rem; border-bottom: 1px solid var(--rule); padding-bottom: 0.2rem; }
h3 { font-size: 1.1rem; margin: 1.4rem 0 0.4rem; }
h4 { font-size: 1rem; margin: 1.1rem 0 0.3rem; }
h5 { font-size: 0.95rem; margin: 1rem 0 0.3rem; color: var(--dim); }
code, pre, kbd { font-family: "SFMono-Regular", Menlo, Consolas, monospace; font-size: 0.86em; }
code { background: var(--code); padding: 0.05em 0.25em; border-radius: 2px; }
pre { background: var(--code); padding: 0.6rem 0.7rem; border: 1px solid var(--rule);
      border-radius: 3px; white-space: pre-wrap; overflow-wrap: break-word; }
pre code { background: none; padding: 0; }
pre.mermaid { background: none; border: 0; text-align: center; white-space: pre; }
div.mermaid { margin: 0.8rem 0; text-align: center; }
div.mermaid svg { max-width: 100%; height: auto; }
table { border-collapse: collapse; width: 100%; margin: 0.7rem 0; font-size: 0.9em; }
th, td { border: 1px solid var(--rule); padding: 0.3rem 0.45rem; text-align: left; vertical-align: top; }
th { background: #f0f0eb; font-family: "Helvetica Neue", Helvetica, Arial, sans-serif; }
blockquote { margin: 0.7rem 0; padding-left: 0.8rem; border-left: 3px solid var(--rule); color: var(--dim); }
hr { border: 0; border-top: 1px solid var(--rule); margin: 1.6rem 0; }
a { color: #1a4f8a; text-decoration: none; }
figure, svg { max-width: 100%; }
#TOC { font-size: 0.92em; }
#TOC > ul { padding-left: 1.1rem; }
#TOC a { color: var(--ink); }
@page { margin: 18mm 16mm 20mm; }
pre, table, blockquote, figure { break-inside: avoid; }
h1, h2, h3 { break-after: avoid; }
CSS

# --- mermaid, once, if the network is there ---------------------------------------------------
# Fetched into the build directory and then inlined into the page (see below), so the HTML pandoc
# writes has nothing left to fetch: `--embed-resources` resolves relative URLs against the
# *document's* directory, not this one, and a page whose diagram script failed to arrive prints the
# diagram's source as code instead of the diagram.
mermaid=""
mermaid_script=""
if curl -sS --max-time 30 -o "$work/mermaid.min.js" \
        https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js 2>/dev/null \
        && [ -s "$work/mermaid.min.js" ]; then
    mermaid="yes"
    mermaid_script="$work/mermaid.min.js"
fi

# --- the page's own head: the stylesheet, and the diagram script when there is one -------------- #
{
    echo '<style>'
    cat "$work/doc.css"
    echo '</style>'
    if [ -n "$mermaid_script" ]; then
        echo '<script>'
        cat "$mermaid_script"
        echo '</script>'
        echo '<script>'
        echo "  window.addEventListener('load', function () {"
        echo "    mermaid.initialize({ startOnLoad: true, theme: 'neutral' });"
        echo '  });'
        echo '</script>'
    fi
} > "$work/head.html"

# --- the documents ----------------------------------------------------------------------------
picked=("$@")
if [ ${#picked[@]} -eq 0 ]; then
    picked=()
    for doc in Documents/*.md; do
        name=$(basename "$doc")
        # `.gitignore` is what says a file is not a document: the implementation plan sits beside the
        # set, is a working document, and has no PDF. Testing the ignore rule rather than a name means
        # the next working file does not need this script edited.
        if command -v git >/dev/null 2>&1 && git check-ignore -q "$doc"; then
            echo "docs-pdf: skipping $name: not part of the document set"
            continue
        fi
        # The set's own map is a table of contents, not a document.
        if [ "$name" = "README.md" ]; then
            continue
        fi
        picked+=("$name")
    done
fi

mkdir -p "$out"
printf '%-34s %6s %9s\n' "document" "pages" "size"
printf '%-34s %6s %9s\n' "----------------------------------" "-----" "---------"
failures=0

for name in "${picked[@]}"; do
    doc="$root/Documents/$name"
    if [ ! -f "$doc" ]; then
        echo "docs-pdf: no such document: $doc" >&2
        failures=$((failures + 1))
        continue
    fi
    stem=${name%.md}
    pdf="$out/$stem.pdf"

    # One self-contained page: the stylesheet and the diagram script are inline, so pandoc has
    # nothing to fetch and the PDF keeps looking the same the day it is moved somewhere else. The
    # lua filter is what keeps the diagram *text* intact — see the note in docs-pdf-mermaid.lua.
    if ! pandoc "$doc" \
            --from gfm --to html5 --standalone \
            --metadata "title=$stem" \
            --lua-filter="$here/docs-pdf-mermaid.lua" \
            --include-in-header="$work/head.html" \
            --mathml \
            --highlight-style tango \
            --toc --toc-depth=2 \
            --output "$work/$stem.html"; then
        echo "docs-pdf: pandoc failed on $name" >&2
        failures=$((failures + 1))
        continue
    fi

    # Chrome, headless, on the local file. The virtual time budget is what lets mermaid finish
    # drawing before the page is printed — without it the diagrams come out as the code they were
    # written as.
    if ! "$chrome" --headless --disable-gpu --no-sandbox --no-pdf-header-footer \
            --virtual-time-budget=15000 \
            --print-to-pdf="$pdf" "file://$work/$stem.html" >/dev/null 2>&1; then
        echo "docs-pdf: $chrome could not print $name" >&2
        failures=$((failures + 1))
        continue
    fi

    pages="-"
    if command -v pdfinfo >/dev/null 2>&1; then
        pages=$(pdfinfo "$pdf" 2>/dev/null | awk '/^Pages/ { print $2 }')
    fi
    printf '%-34s %6s %9s\n' "$stem" "$pages" "$(du -h "$pdf" | cut -f1)"
done

if [ -n "$mermaid" ]; then
    echo
    echo "diagrams: mermaid, drawn in the browser"
else
    echo
    echo "diagrams: printed as code — mermaid could not be fetched (no network?)"
fi

if [ "$failures" -gt 0 ]; then
    echo "docs-pdf: $failures document(s) failed" >&2
    exit 1
fi
echo "docs-pdf: $(( ${#picked[@]} )) document(s) written to Documents/pdf/"
