# INFOCompanion

[![licence](https://img.shields.io/badge/licence-MIT-3da639)](LICENSE)

## 'Two friends, one keyboard, a green screen and a flashing cursor, on a cold night in 1983.'

A shared interactive-fiction shell with a **co-player**: a language model that sits at the same
terminal, shares the keyboard, and plays *with* you rather than *for* you.

It is a second person in the room, not an assistant. It keeps its own notes, forms its own opinion
of the puzzle in front of you, and will sometimes disagree with you — and it has no hands on the
wheel unless you hand them over.

**This project is not affiliated with, endorsed by or supported by Activision, Microsoft or the
estate of Infocom.** It ships no story files: the games it plays are yours. The one story in the
image, **Zork I**, is compiled at build time from the source Microsoft's Open Source Programs Office
and Activision placed under the MIT Licence — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## What it is not

| Not this | This |
|---|---|
| A bot that plays the game while you watch | A companion with a voice and a vote, but not the wheel |
| A hint system that only answers when spoken to | Something that initiates, proposes, asks, and sometimes acts |
| A narrator restating what you can already see | A co-player that says things you do not already know |
| A screen-scraping automaton | A seat on a structured event bus |

---

## What you need

* **Docker.** Nothing else on the host — no Rust toolchain, no .NET SDK, no `frotz` package. The
  image compiles its own interpreter.
* **A terminal, or a browser.** The terminal is the point: one window, a green screen, a prompt
  and somebody else in the chair beside you. The same panes are served as a page too, so a browser
  on its own is enough — nothing here needs both.
* **A story to play.** Zork I ships in the image. Anything else you own goes into the games
  directory as `.z3`, `.z4`, `.z5`, `.z6`, `.z7`, `.z8`, `.zblorb` or Infocom's own `.DAT`.
* **A model — or not.** With an OpenAI-compatible endpoint, the companion plays alongside you. With
  none at all you get the game and nothing else: it takes its seat, says it cannot reach a model,
  and stays quiet.

Optional: a voice endpoint answering the OpenAI speech shape, if you want the companion spoken aloud. Without one, your browser reads its lines.

### About the model

This was built and played against **DeepSeek**. There is nothing DeepSeek-specific in it — it
speaks the OpenAI chat shape, and any endpoint answering that shape will work — but DeepSeek is the
only one it has been tested against, and **the evening follows the model you choose**. A small model
reacts and comments well enough; the personalities, the notebook and the multi-step errands are
written for something that can hold a plan. The cadences are tunable in `Assets/*.json` for exactly
that reason.

| Variable | |
|---|---|
| `INFOCOMPANION_LLM_API_KEY` | the key, and the only one of the three that is a secret |
| `INFOCOMPANION_LLM_BASE_URL` | where the calls go, e.g. `https://api.deepseek.com`. Most providers want a `/v1` suffix |
| `INFOCOMPANION_LLM_MODEL` | which model plays, e.g. `deepseek-flash`. One the price book does not list still plays; only the cost figure stops meaning anything |

---

## Run it

### 1. Get the image

```bash
docker pull ghcr.io/hyper-dragon/infocompanion:latest
```

Both architectures are published — `amd64` and `arm64` — and `docker pull` picks the one you are
on. To build it yourself instead, `docker build -f Build/Dockerfile -t infocompanion .` and use
that name below.

### 2. Run it

```bash
mkdir -p games state
export INFOCOMPANION_LLM_API_KEY=sk-...
docker run -d --name infocompanion \
  -p 2223:2222 -p 8080:8080 \
  --env INFOCOMPANION_LLM_API_KEY \
  --env INFOCOMPANION_LLM_BASE_URL=https://api.deepseek.com \
  --env INFOCOMPANION_LLM_MODEL=deepseek-flash \
  -v "$PWD/games:/games" \
  -v "$PWD/state:/state" \
  ghcr.io/hyper-dragon/infocompanion:latest
```

`games/` fills with the bundled Zork I on first start, and never overwrites what you put there.
`state/` is the evening itself: saves, the companion's notebook, and the game it was last in — so
delete it only if you mean to start over.

Leave the three `--env` lines out, or leave the key unset, and it runs as a game with no companion.

### 3. Connect

```bash
ssh player@localhost -p 2223   # password: letsplay
```

You land in Zork I. Talk to it:

```text
> what do you make of this grate?
> can we try the cellar?
```

A bare command (`north`, `take lamp`) goes straight to the game. `/type north` forces one past the
companion. `/help` lists everything else, and the same panes are at <http://localhost:8080>.

It is also fine with Compose, which is how the reference deployment runs it — `docker compose up -d
--build`, with `.env` beside `docker-compose.yml`.

---

## What it does for you

* **It initiates.** It notices things, asks questions, and proposes plans without being prompted.
* **Or it leaves you alone.** A bare command is executed directly, with no model call. `/type
  <command>` forces one line past the companion entirely.
* **It takes a job, not just a step.** *"scout the open locations and report back"* and it plans,
  works through the steps, says what it is doing, and tells you what it found — without asking
  permission for each one. Risky acts still stop for you.
* **It asks before it spends, breaks or gives anything away.** `/risklow`, `/riskmedium` (default)
  and `/riskhigh` set how careful it is.
* **It remembers.** Corrections override its own notes, and a game you come back to a week later is
  one it has notes on.
* **It gives you room.** Three SSH logins — `game`, `companion` and `me` — put the game, the
  conversation and your typing line in three panes on one evening. `quill` is the same pane, and
  `player` still gives you everything in one window.
* **It keeps going without the model.** No network, no credit, a provider having a bad hour: the
  game carries on and it says so, then catches up when the model returns.

---

## Settings you might change

Everything is in `.env.example` with its default and a paragraph on what it does. The ones people
actually touch:

| Variable | Default | |
|---|---|---|
| `INFOCOMPANION_LLM_API_KEY` | — | the key for your model endpoint |
| `INFOCOMPANION_LLM_BASE_URL` | from `agent-config.json` | where the model is called |
| `INFOCOMPANION_LLM_MODEL` | from `agent-config.json` | which model plays |
| `INFOCOMPANION_PLAYER_NAME` | `Peter` | what the companion calls you |
| `INFOCOMPANION_PERSONA` | `james` | which personality plays, by slug |
| `INFOCOMPANION_VOICE_URL` | — | where its words are spoken, if anywhere |
| `INFOCOMPANION_AUTH_PASSWORD` | `letsplay` | the SSH password — change it |
| `INFOCOMPANION_NO_AGENT` | `false` | play with no companion at all |

Cadences, budgets, pane geometry and the model endpoint itself live in the three `Assets/*.json`
files, mounted into the container, so tuning needs no rebuild.

---

## Known Issues

* **Games that draw their own screen do not work, and are unsupported.** The harness reads turns,
  and a story that paints a form or a menu with cursor addressing and expects a pointer is outside
  what that can carry. **Bureaucracy is the known casualty**: it starts, then stops at the licence
  application. Work on it was started and abandoned, so this is a limit of the version rather than
  a defect waiting on a patch.
* **Story files are yours to supply.** The repository carries none: they are copyrighted works.
* **The voice endpoint is not ours.** The optional `voice` profile runs upstream Kokoro, which is
  **amd64 only**, so a Pi uses the browser's voice.

---

## Software Support

Report defects on the [issues page](https://github.com/Hyper-Dragon/INFOCompanion/issues/new/choose).
The template asks for the build, which you can get from the debug bundle — **debug bundle** in the
masthead, or `GET /debug/bundle` — because with it the build is known. Say which story you were
playing and whether the companion could reach the model at the time, because those two answers
separate most reports from each other. "It didn't work" is hard to act on.

For anything security-shaped, please use [SECURITY.md](SECURITY.md) rather than an issue.

---

## Saying Thank You

This was written by me, initially for me. I put in the extra work to make it usable
by someone who isn't me, which is the tedious part. If you find it useful and want
to say thanks, you can [sponsor it on GitHub](https://github.com/sponsors/Hyper-Dragon)
or [bung a few quid in the hat](https://www.paypal.com/donate/?hosted_button_id=QNKADKV5BAM5C).
No pressure.

---

## Licence

MIT. See [LICENSE](LICENSE).

The image is not MIT-only:

* **Frotz**, the interpreter, is **GPL-2**. The source is vendored in `Source/dFrotzTooled/` with
  its `COPYING` file, and `Build/Dockerfile` compiles it into the image. If you redistribute that
  image, the GPL applies to it.
* **Zork I**, the only story in the image, is compiled at build time from MIT-licensed source.
* **ZILF** compiles that story in a build stage that is thrown away. None of it ships.

[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) has each one in full.
