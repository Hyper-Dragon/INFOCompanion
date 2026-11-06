# Third-party notices

## Zork I: The Great Underground Empire (the bundled default story)

The story file `zork1.z3` this image ships is compiled from the source code of Zork I,
which Microsoft's Open Source Programs Office, Team Xbox and Activision placed under
the **MIT License** on 20 November 2025, contributing the grant upstream (with Jason
Scott) to the `historicalsource/zork1` repository. The grant covers the source code and
its documentation only: Zork, its name and its packaging remain the property of their
owners, and nothing here is affiliated with, sponsored by or endorsed by Microsoft,
Activision, or the estate of Infocom.

- Source: <https://github.com/historicalsource/zork1>
- Commit compiled: `97b7b3d68c075dd9af7da499c3e9690ada3471fd`
- Licence text: `assets/licenses/zork1-MIT.txt` in the image, and at the repository above.

Zork I is included to give a first-run container something to play. Adding or removing
it changes nothing else — see "Removal" below.

## ZILF (build-time only)

The story is compiled with [ZILF](https://zilf.io/), © Tara McGrew, used under the GNU
General Public License. ZILF is fetched and run inside a build stage that is discarded:
none of it is redistributed, and nothing in the runtime image depends on it.

## Frotz (the interpreter, compiled into the image)

The interpreter this project drives is `dfrotz`, from **Frotz**, © the Frotz contributors,
used under the **GNU General Public License, version 2**. Its source is vendored in
`Source/dFrotzTooled/` in this repository — upstream Frotz 2.54 with Debian's one patch — and the
image compiles it rather than installing Debian's `frotz` package.

Because a GPL program is distributed here as a binary, the corresponding source is the
`Source/dFrotzTooled/` directory named above: upstream's source, unmodified except for the single
patch recorded in `Source/dFrotzTooled/patches/`, with its licence text in
`Source/dFrotzTooled/COPYING` and its provenance in `Source/dFrotzTooled/VENDOR.md`.

## The sidecars

The files beside the games (`Assets/games/*.md` in the source tree — the pack this image
carries) are our own commentary, written from the physical packages in the maintainer's
possession: descriptions of what came in the box, facts useful for play, and brief
quotations. The games, their manuals, maps and feelies remain the property of their
owners; the sidecars are not those things.

## Removal

If you believe something here infringes your rights, open an issue on the project
repository and we will remove it promptly. The bundled files are one directory inside
the image — `/opt/infocompanion/assets` — and deleting it (or any file out of the
source tree) is the whole of the removal; the container keeps working without it.

The interpreter is not part of that pack and is not optional — the project is a front end
for it — but it is unmodified GPL source, vendored and built in this repository (above).
