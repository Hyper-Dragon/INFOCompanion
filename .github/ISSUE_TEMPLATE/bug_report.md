---
name: Bug report
about: Something behaves wrongly, or the companion does something you did not expect
title: ''
labels: bug
assignees: ''
---

**What happened, and what you expected instead**

A clear description of the actual and expected behaviour. "It announced it was going north and
typed nothing" is useful. "The AI is bad" is not — there is nothing to reproduce.

**Which part of it is wrong?**

Tick the one that is actually misbehaving, because they fail in different ways:

- [ ] The game itself — the daemon, the interpreter, or the screen
- [ ] The companion's speech — wrong, silent, or repeating itself
- [ ] The companion's actions — it said it would do something and did not
- [ ] Its memory — the notebook lost something, or an errand forgot where it was
- [ ] The browser page
- [ ] The container will not start
- [ ] Something else

**How to reproduce it**

1.
2.
3.

Say whether it happens every time or intermittently, and turn on `/debugon` first if it is
about the companion's tool calls — that prints one line per call in its own pane.

**Does it recover on its own?**

- [ ] Yes, after a turn or two
- [ ] Only if you speak to it
- [ ] Only if you `/restart` or `/switch` away and back
- [ ] Only after recreating the container
- [ ] No, it stays wrong

**Versions**

- Image tag or commit:
- The `build:` line from the debug bundle's `MANIFEST.md`:
- Which story you were playing, and where in it:
- The model endpoint and model name from `agent-config.json` (no key):
- Host OS and CPU architecture: (`uname -a`, or `docker info --format '{{.Architecture}}'`)
- Rust / .NET / Docker version, if you built it yourself:

**The debug bundle**

The **debug bundle** button in the masthead — or `GET /debug/bundle` — produces one zip with the
configuration, the logs, the turn record, the notebooks and the manifest. Attach it. It never
contains the API key: the bundle lists the variable's name and withholds its value.

If you would rather not attach the whole thing, the two files that answer most questions are
`logs/agent-*.jsonl` and `logs/core-*.jsonl`.

**Configuration**

Your three JSON files, or just the values you changed. Remove anything private from the
personality file first.
