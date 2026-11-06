# Third-party assets

Two files are vendored rather than fetched at run time, so the interface works with no network
of its own and cannot be changed out from under the daemon:

| File | What | Licence |
| --- | --- | --- |
| `xterm.js`, `xterm.css` | the terminal — `@xterm/xterm` 5.5.0 | MIT |
| `xterm-addon-fit.js` | sizes the terminal to its screen — `@xterm/addon-fit` 0.10.0 | MIT |

Both are the published UMD builds from `cdn.jsdelivr.net/npm/…`, unmodified. To update them:

```sh
cd Source/WebUI/public/assets
curl -O https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.js
curl -O https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.css
curl -O https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.js
```

`frame.svg` is ours — a drawing, not a dependency.
