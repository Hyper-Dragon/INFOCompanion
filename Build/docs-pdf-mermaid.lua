-- Diagrams for the PDFs.
--
--   ```mermaid
--   graph LR
--     A -- "a note" --> B
--   ```
--
-- Pandoc's own HTML for a fenced block is `<pre class="mermaid"><code>…</code></pre>`, with the
-- *markdown* escaped into HTML entities on the way — and mermaid, handed that, reads the escaped
-- text and fails with "Lexical error ... Unrecognized text" on the arrow's `--&gt;`. The diagram in
-- spec_06_stats came out as mermaid's own error box because of it.
--
-- So the block is emitted as a raw `<div class="mermaid">` instead: raw HTML is passed through
-- verbatim, which means mermaid is given exactly the text the document has — arrows, quotes and
-- `<br/>` and all. The trade is that a diagram label is no longer escaped; these are our own
-- documents, written by us, and a `<` in a label would be a mistake in the markdown rather than an
-- attack from it.
function CodeBlock(block)
  if block.classes:includes("mermaid") then
    return pandoc.RawBlock("html", '<div class="mermaid">\n' .. block.text .. '\n</div>')
  end
end
