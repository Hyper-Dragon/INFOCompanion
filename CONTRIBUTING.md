# Contributing

Thanks for looking. This is one thing with one job: two people at one keyboard, one of them a
language model. Keep it that narrow.

## Before you start

Read [README.md](README.md) for what it is and what it deliberately is not, and
[SECURITY.md](SECURITY.md) for the deployment assumptions. If you are planning anything that
touches the boundary with the game — turn framing, the tool channel, the parser record — say so
in an issue first. That boundary is the part of this project that has been rewritten most often,
and the reasons are recorded.

## Building and testing

Running it from source is a contributor workflow, not a supported way to run it. The image is the
only runtime anyone is supported in using, and the notes below exist so a patch can be checked
before it is sent. If it does not work on your machine, that is a bug worth an issue, but it is not
a deployment path.

Everything builds without Docker, and the gate is the same command a maintainer runs:

```bash
Build/check.sh --fast        # format, lints, markdown, and all three test suites
```

That runs `cargo fmt --check`, `cargo clippy` and `cargo test` for `Source/Core` and
`Source/WebUI`, and `dotnet test` for `Source/Agent` — the .NET suite runs in a container,
because the SDK is not installed next to the Rust toolchain. Then, with Docker:

```bash
Build/check.sh               # the above, then the image, then the container suite
docker compose up -d --build
```

The container suite plays every story in `Assets/games/`, and the repository carries no story
files, so it needs one of your own to be worth running.

To run it by hand instead:

```bash
cd Source/Core && cargo run -- --games-dir ../../Assets/games --state-dir ../../state --game advent
cd Source/Agent && dotnet test
```

`dfrotz` has to be installed for anything that starts a game (`apt install frotz`,
`brew install frotz`); in the image it is compiled from `Source/dFrotzTooled/` instead.

## What belongs here

- The game boundary: the interpreter, turn framing, the tool channel, the record.
- The session: seats, views, the wire protocol, the player's commands, pacing.
- The companion: its loop, its passes, its tools, its memory, its write-ups.
- The browser surface, as a mux over seats the daemon already draws.
- Documentation that matches what the code does.

What does not:

- **A solver, a walkthrough, or a hint system.** That is the one thing this project exists to
  avoid.
- **Screen scraping, or a map drawn by the harness.** Reading the game is the companion's job.
- **Per-game special cases.** No story names, wordings or room lists in code.
- **A second player, a phone client, or a cloud service.**

The README's *What it is not* and *Known Issues* sections are the long version of that list.

## Conventions worth knowing

- **Documentation rides with the code.** A change that alters behaviour updates the text that
  describes it, in the same commit.
- **The specification describes the present.** It is not a changelog: it is rewritten to read as
  though the code had always been this way. Commit messages are the history.
- **One sentence per commit, no prefix and no full stop.**
- **Requirement ids (`C46`, `G51`, `M33.9`) in comments are stable handles into a specification
  that is not published.** Cite the id rather than a filename; the id survives the file being
  split, renamed or rewritten, and it is what the maintainer can look up.
- **A test that asserts behaviour must be able to fail.** Watch it fail first, or it is a lock on
  whatever the code happens to do.

## Style

- `cargo fmt` and clippy clean; there is no style debate here, the tools decide.
- Comments explain *why*. Where a fact was measured — a timing, a token count, a save wording —
  say when it was measured and on what.
- Errors name the thing to fix. "invalid config" is a support ticket; a message naming the
  variable and the values it accepts is a fix.
- Fail at startup on a bad configuration rather than degrading quietly later.

## Pull requests

One logical change per pull request. Say what broke and what happens instead — a reviewer should
not need the diff to understand the problem. If it changes something observable (a command, a
default, an event), say so explicitly.

By contributing you agree that your work is licensed under the [MIT licence](LICENSE).
