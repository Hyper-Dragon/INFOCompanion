# Security policy

INFOCompanion runs an SSH server, a web bridge, and a companion that talks to a model
endpoint. It is built to sit on a machine you control, on a network you trust. None of it is
hardened for the open internet.

## Reporting a vulnerability

Please **do not** open a public issue for a security problem. Use GitHub's private
vulnerability reporting on this repository. Include:

- what an attacker can do that they should not be able to do,
- the smallest reproduction you have,
- the image tag or commit, and whether either port was published.

You will get an acknowledgement, and either a fix or a written explanation of why it is not
one. There is no bounty; there is gratitude.

## What is protected, and how

| Asset | Position |
|---|---|
| The model API key | Read from the environment. It never enters the image, the repository, `agent-config.json`, or a debug bundle — the bundle lists the variable's **name** and never its value. |
| SSH | Password authentication through `INFOCOMPANION_AUTH_PASSWORD`. The default in `.env.example` is a placeholder, not a secret, and it is the first thing to change. |
| The state directory | Checkpoints, notebooks, the turn record and the playback logs: your game history, and nothing that authenticates. It is yours to protect with file permissions. |
| The debug bundle | Built in memory, store-only, timestamps zeroed, credentials filtered. It carries configuration, logs, the record and the manifest, and it is only produced when you ask for it. |

## Deliberate non-features

Please do not file these as vulnerabilities. They are choices, and the alternatives make the
thing worse at its job:

- **The browser surface has no authentication.** One server, one session, one person. The page
  is open to anyone who can reach the port, and a reverse proxy is the right place to ask for
  more. The model key lives in that process's environment and is never sent to the browser.
- **The SSH password is a single shared secret.** There are no accounts, no keys and no
  per-user permissions, because there is one player.
- **The companion sends your game to the model provider.** The transcript, its notebook, the
  game's sidecar and the screen go to whichever endpoint `Assets/agent-config.json` names, with
  the key from `INFOCOMPANION_LLM_API_KEY`. If that is not acceptable, point it at an endpoint
  you run yourself, or set `INFOCOMPANION_NO_AGENT=true` and play without a companion.
- **The daemon's `/` commands are trusted.** They are for the person at the keyboard, and every
  one of them is a session action rather than a privilege boundary.

## Deployment expectations

1. **Do not publish either port to an untrusted network.** Bind the browser surface to a LAN
   address or leave it off with `INFOCOMPANION_NO_WEBUI=true`.
2. **Change the SSH password** before the machine is reachable by anyone but you.
3. **Keep the state directory off shared storage.** It holds the whole evening, in plain text.
4. **Pin the image tag** rather than tracking `latest`, so an update is a decision.
