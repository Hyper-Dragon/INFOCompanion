/* The web interface's own half: one terminal per pane, wired to the bridge.
 *
 * It draws and nothing else. Every byte on a screen came from the daemon, which owns the
 * colours, the layout and what each pane is allowed to show; all this does is put a socket in
 * front of a terminal and a chassis behind it. Text frames are control (resize), binary frames
 * are terminal bytes — the same division the bridge uses on the other side.
 *
 * Two panes are monitors and one is a strip, and that is decided for us: the config says which
 * seat is which (`shape`), so nothing here has to know that `me` means hands.
 */
(() => {
  "use strict";

  const IC = window.IC || { single: [{ seat: "player", label: "SESSION" }], multi: [] };
  const MONO = 'ui-monospace, "Cascadia Mono", "DejaVu Sans Mono", Menlo, Consolas, monospace';

  /* Small on purpose: a monitor here is a whole game, and every pixel of the tube that is not
   * a line of it is wasted. */
  const FONT_SIZE = 13.5;

  /* Settings live in the browser, not in a reload: the size a pane's type is set to, and whether
   * the tubes are switched on, are both remembered, and both are per browser rather than per
   * page — the same person looking at the same session should not have to say it twice. */
  const CRT_KEY = "ic.crt";
  const FONT_KEY = "ic.font";

  /* Type size belongs to a pane, not to the page: a game screen wants as much of the story as
   * it can hold, and three lines of typing want the same size as the line above them. So each
   * pane has its own pair of buttons, and its own remembered answer. */
  const FONT_STEP = 0.5;
  const FONT_MIN = 8;
  const FONT_MAX = 26;
  const fontKey = (seat) => `ic.font.${seat}`;

  /* The daemon's own roles, landing in this palette: 1;36 for a command, 1;37 for speech, 90 for
   * a thought, 33 for a notice, 1;34 for the prompt, 1;35 for the player.
   *
   * Three surfaces, not one (spec 05 §2 says so: this is still a
   * theme for the codes the daemon emits, and now it is a theme *per pane*). The game's screen is
   * a green phosphor tube; the companion's pane is the warm bone-and-amber machine this page has
   * always been built from; the strip the player types in is a third, cool and quiet. Which one a
   * pane gets is decided by its seat, which the config already names.
   *
   * Nothing here is a second opinion about what a line *is* — the daemon decides that, and hands
   * over the codes. This decides only what those codes look like, on each machine. */
  const THEME_GAME = {
    background: "#04100a", foreground: "#8fe6a0", cursor: "#c8ffd4", cursorAccent: "#04100a",
    selectionBackground: "#14512c",
    black: "#07160d", red: "#c9633f", green: "#5fdc7f", yellow: "#d7c05a",
    blue: "#5ec9c0", magenta: "#9fd08a", cyan: "#5fe6ff", white: "#d6f5dd",
    brightBlack: "#41785a", brightRed: "#e07a4a", brightGreen: "#8dffa8", brightYellow: "#f0d269",
    brightBlue: "#7fe9e0", brightMagenta: "#b8e8a0", brightCyan: "#93f0ff", brightWhite: "#eafff0",
  };

  const THEME_COMPANION = {
    background: "#0f0c05", foreground: "#cfc9b4", cursor: "#e6b25e", cursorAccent: "#0f0c05",
    selectionBackground: "#3a3527",
    black: "#1b1813", red: "#b4562f", green: "#8fae6a", yellow: "#d8a44c",
    blue: "#7d9b9e", magenta: "#b08a99", cyan: "#8fb3ac", white: "#d9d5c8",
    brightBlack: "#6b6656", brightRed: "#d4744a", brightGreen: "#a8c98a", brightYellow: "#f0b25e",
    brightBlue: "#9fbcc0", brightMagenta: "#c9a2b0", brightCyan: "#a9c9c2", brightWhite: "#f2efe4",
  };

  /* The strip: the machine answering you. Cool where the other two are warm and green, and quiet
   * enough that the prompt is the brightest thing on it. */
  const THEME_STRIP = {
    background: "#090d12", foreground: "#9db0c0", cursor: "#cfe6ff", cursorAccent: "#090d12",
    selectionBackground: "#243343",
    black: "#141b22", red: "#c46a52", green: "#7fae8f", yellow: "#c9a95f",
    blue: "#7fb6ff", magenta: "#a894c4", cyan: "#7fd3d8", white: "#c6d2dd",
    brightBlack: "#5b6b7a", brightRed: "#de7f63", brightGreen: "#9acbad", brightYellow: "#e6c274",
    brightBlue: "#a3ccff", brightMagenta: "#c3aede", brightCyan: "#a6e6ea", brightWhite: "#e6eef6",
  };

  /* The seat names are the ones the config uses and the ones a person logs in as. The player's own
   * window shows everything, so it is the tube — the game is what is on it — and an unrecognised
   * seat is a tube too, which is the same fallback the chassis uses. */
  const THEME_FOR_SEAT = {
    game: THEME_GAME, player: THEME_GAME,
    quill: THEME_COMPANION, companion: THEME_COMPANION,
    me: THEME_STRIP, input: THEME_STRIP,
  };

  const themeFor = (seat) =>
    THEME_FOR_SEAT[String(seat || "").trim().toLowerCase()] || THEME_GAME;

  /* The switches, kept in the browser (spec 05 §5.1).
   *
   * The daemon publishes no session state — `/stats.json` is deliberately blind to the session
   * (spec 06 §4.1) — so what this page shows is what it last *sent*, and what it holds across
   * games is its own memory. Every one of these is a command the daemon already understands, and
   * they are all *set* rather than toggle, so sending one that is already in force costs nothing
   * and a page reopened in another tab can put the session back without asking anybody.
   *
   * They are applied by typing them into the pane that accepts input — the strip in the
   * multiview, the session pane on the one-screen page — which is the same bytes a person would
   * have sent, and the only route this page has to the session at all (spec 05 §2, §3). */
  const SETTINGS_KEY = "ic.settings";
  const SWITCHES = [
    { key: "tags", on: "tagson", off: "tagsoff", label: "tags", on_by_default: true,
      title: "mark every line with who it is from" },
    { key: "trace", on: "debugon", off: "debugoff", label: "trace", on_by_default: false,
      title: "show every call the companion makes" },
    { key: "thinking", on: "thinkon", off: "thinkoff", label: "thinking", on_by_default: false,
      title: "show its reasoning as it arrives" },
  ];

  /* Five levels, medium the default (spec 02 §8.2). `veryfast` is what the daemon has always
   * done: the words as they come off the wire. */
  const SPEEDS = ["veryslow", "slow", "medium", "fast", "veryfast"];

  /* The danger level, three states rather than two (spec 05 §5.1).
   *
   * `null` is a real third value and it is where this starts: a page has no opinion about the danger
   * level until somebody presses one, and the daemon's own default is the careful one. Sending
   * `/risklow` on every connect would be the page putting a level back that the person at the
   * keyboard may have changed in the last hour — the one thing §5.1 says a restore must not do. So
   * the level is remembered and given back like the others *once it has been chosen*, and until then
   * it is simply not said. */
  const RISKS = [
    { key: "low", verb: "risklow", label: "careful",
      title: "careful: nothing above this without asking" },
    { key: "medium", verb: "riskmedium", label: "medium",
      title: "medium: ordinary moves go ahead" },
    { key: "high", verb: "riskhigh", label: "bold",
      title: "bold: it may take real risks without asking" },
  ];

  const DEFAULT_SETTINGS = { tags: true, trace: false, thinking: false, speed: "medium", risk: null };

  function rememberedSettings() {
    const settings = { ...DEFAULT_SETTINGS };
    SWITCHES.forEach((sw) => { settings[sw.key] = sw.on_by_default; });
    try {
      const stored = JSON.parse(window.localStorage.getItem(SETTINGS_KEY) || "{}");
      SWITCHES.forEach((sw) => {
        if (typeof stored[sw.key] === "boolean") settings[sw.key] = stored[sw.key];
      });
      if (SPEEDS.includes(stored.speed)) settings.speed = stored.speed;
      if (RISKS.some((risk) => risk.key === stored.risk)) settings.risk = stored.risk;
    } catch (err) {
      /* A store somebody has edited by hand: the defaults are what was there before. */
    }
    return settings;
  }

  const rememberSettings = (settings) => {
    try {
      window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
    } catch (err) {
      /* Private mode, or a full store: the switch still works for this page. */
    }
  };

  /* What puts the session where the store says it should be, in the order it reads. */
  const restoreCommands = (settings) => [
    ...SWITCHES.map((sw) => `/${settings[sw.key] ? sw.on : sw.off}`),
    `/${SPEEDS.includes(settings.speed) ? settings.speed : DEFAULT_SETTINGS.speed}`,
    // And the danger level, when this browser has been told one. Nothing is sent for `null`: a page
    // that has never been asked has no business overruling a level set at the keyboard.
    ...RISKS.filter((risk) => risk.key === settings.risk).map((risk) => `/${risk.verb}`),
  ];

  /* The frame's own proportions, and the same numbers the tube's inset is measured in (see
   * assets/frame.svg and the .screen rule in app.css). */
  const FRAME = { width: 1000, height: 880 };

  const escape = (text) => String(text).replace(/[&<>"']/g,
    (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  const signage = (label) => `
    <div class="signage">
      <span class="name">${escape(label)}</span>
      <span class="rule"></span>
      <span class="font" data-font>
        <button type="button" data-smaller title="smaller type">−</button>
        <span class="value" data-value>—</span>
        <button type="button" data-larger title="larger type">+</button>
      </span>
      <span class="size" data-size>—</span>
      <span class="locked" data-locked hidden>actions locked</span>
    </div>`;
  /* A screen you watch: the beige plastic, the tube, the glass over it, and the lamps. The stage
   * around it is what gets fitted; see fitMonitor for why the page does that arithmetic. */
  const MONITOR_MARKUP = `
    <div class="stage" data-stage>
      <div class="monitor" data-monitor>
        <img class="shell" src="assets/frame.svg" alt="">
        <div class="screen" data-screen></div>
        <div class="glass"></div>
        <div class="lamps" data-lamps></div>
      </div>
    </div>`;

  /* The lamps on the monitor's control strip, and what each one watches.
   *
   * There are three, because in the multiview three things are happening at once and the monitor
   * is the only chassis with anywhere to put a light. Left to right: the screen printing, the
   * player typing, the companion working.
   *
   * What a lamp can honestly see from here is **bytes**. This is a dumb mux: it does not know what
   * a turn is, and it could not tell a thought from a sentence if it tried. So a lamp lights when
   * the pane it watches has moved bytes *its own way* — output arriving on the screen, output
   * arriving on the companion's pane, a keystroke leaving the pane you type in. That is a fact
   * about the wire rather than an opinion about the session, which is the only kind of fact this
   * page is allowed to have.
   */
  const LAMP_SLOTS = [
    { signal: "screen", title: "the screen, printing" },
    { signal: "typing", title: "the player, typing" },
    { signal: "quill", title: "the companion, working" },
  ];

  /* A signal is a pane's own direction of travel, decided by the chassis the config gave it: the
   * monitor is the game, the strip is your hands, the panel is the companion. A stranger's shape
   * falls back to the monitor, which is the same fallback the chassis itself uses. */
  const SIGNAL_FOR_SHAPE = { monitor: "screen", strip: "typing", panel: "quill" };

  /* How long a lamp stays lit after the byte that lit it. Short for a screen that is printing
   * constantly, longer for the two the player is meant to notice happening to *other* panes. */
  const LAMP_HOLD = { screen: 140, typing: 280, quill: 480 };

  /* What each lamp is watching, in one word, for the tooltip: "THE GAME — printing". */
  const LAMP_VERB = { screen: "printing", typing: "typing", quill: "working" };

  /* The one fact this page is given rather than watching for (spec 02 §8.5).
   *
   * The companion's lamp used to be a byte-light like the other two, which means it said nothing
   * during the seconds that matter most — between the daemon handing the model the turn and the
   * first thought chunk coming back. Those are exactly the seconds in which a companion working
   * hard and a hung terminal look the same. So the companion's lamp watches the *turn* instead:
   * the daemon sends `turn_started` when the first model call leaves and `turn_finished` when the
   * reply has been delivered, as an OSC, and an escape is how something reaches a pane without
   * being shown on it. Nothing here reads the protocol to work this out — a dumb mux cannot — and
   * it is still a fact about the session rather than an opinion about it: the daemon decides what
   * a turn is, and this lights a lamp while it is going on.
   *
   * The other two lamps stay byte-driven. The game's screen prints constantly and a light that is
   * held on looks broken; the player's own hands are the strip. */
  const LAMP_OSC = 1337;
  const LAMP_TURN = "ic-turn";

  /* The second escape, for the second fact (B3): the line you are typing begins with a space, so
   * the companion is waiting and will not act until you either finish the line or take the space
   * off the front. The number and the payload come from the daemon (`HOLD_OSC`, `hold_escape`). */
  const HOLD_OSC = 1338;
  const HOLD_KEY = "ic-hold";

  /* signal → the lamps watching it. A lamp can be lit from a pane in another cell, which is the
   * whole point of the strip: the typing lamp is on the monitor and it watches the strip. */
  const lampWatchers = new Map();

  /* Signals the daemon is holding lit right now. A held lamp ignores the byte-light: it stays on
   * until the turn it belongs to is over, and the hold is what makes it a slow pulse rather than a
   * flicker. */
  const lampHeld = new Set();

  function light(signal) {
    if (lampHeld.has(signal)) return;
    const lamps = lampWatchers.get(signal);
    if (!lamps) return;
    lamps.forEach((lamp) => {
      lamp.element.classList.add("on");
      clearTimeout(lamp.timer);
      lamp.timer = setTimeout(
        () => lamp.element.classList.remove("on"),
        LAMP_HOLD[signal] || 150);
    });
  }

  /* Hold a lamp lit, or let it go. The class is separate from `on` so that the two can coexist:
   * the byte-light is a flash and the hold is a state, and the last turn's hold being cleared must
   * not clear a flash that is still running. */
  function hold(signal, on) {
    const lamps = lampWatchers.get(signal);
    if (!lamps) return;
    if (on) lampHeld.add(signal); else lampHeld.delete(signal);
    lamps.forEach((lamp) => {
      lamp.element.classList.toggle("held", on);
    });
  }

  /* What the daemon says about a turn, from the escape it sends into the panes it has. */
  function theTurnMoved(payload) {
    const [name, state] = String(payload).split("=");
    if (name !== LAMP_TURN) return;
    hold("quill", state === "start");
  }

  /* What the daemon says about the line being typed, on the pane being typed in (B3).
   *
   * The pane carries the state, not the page: two panes are open in the multiview and only one of
   * them is the player's hands, so the class goes on the cell that received the escape rather than
   * on the document. The chip is part of the signage, which is the one place on the chassis that
   * can say a word without taking a line from the screen — the same reason the hold is an escape
   * and not a notice. */
  function theLineHolds(payload, cell) {
    const [name, state] = String(payload).split("=");
    if (name !== HOLD_KEY) return;
    const on = state === "on";
    cell.classList.toggle("locked", on);
    const chip = cell.querySelector("[data-locked]");
    if (chip) chip.hidden = !on;
  }

  /* The plain chassis, for the panes that are not monitors: the same beige plastic as the strip
   * you type into, with no artwork and no lamp on it. The strip is three lines tall and the panel
   * fills its pane — same plastic, different amount of it. */
  const plainMarkup = (shape) => `
    <div class="${shape}">
      <div class="screen" data-screen></div>
    </div>`;

  /* --- the controls -------------------------------------------------------------------- */

  /* Where a command goes: the pane that accepts input, decided by the config's own answer
   * (`focus`), which is the strip in the multiview and the one screen otherwise. */
  let typist = null;

  function sendToSession(line) {
    if (!typist || !typist.open()) return;
    typist.send(line);
  }

  /* The masthead's own bar. The switch for the tubes is added later, once it is known whether
   * this page has any — a page of strips has nothing to switch. The link to the games is a link
   * rather than a button because it leaves this page: it is the way to the list of what has been
   * played and what the companion has written up about it (spec 05 §6.1). */
  function controls() {
    const masthead = document.querySelector(".masthead");
    if (!masthead) return null;

    const bar = document.createElement("span");
    bar.className = "controls";
    bar.innerHTML =
      `<a class="link" href="/games">games</a>` +
      `<button type="button" data-fullscreen aria-pressed="false">full screen</button>`;
    masthead.insertBefore(bar, masthead.querySelector(".aside"));
    return bar;
  }

  /* What the browser will actually give us. Fullscreen on the document rather than on a pane,
   * because the point is to fill the wall with all three screens, not to lose the signage. */
  async function toggleFullscreen() {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await document.documentElement.requestFullscreen();
    } catch (err) {
      /* A browser that refuses, or a frame that is not allowed to ask: the button does nothing
       * rather than lying about it. */
    }
  }

  /* On by default, and off is remembered: it is a look, and a look somebody has turned off
   * should stay off across a reload. */
  const crtOn = () => window.localStorage.getItem(CRT_KEY) !== "off";

  /* What a pane's type was last set to, or what it would have been. The pane's own answer wins;
   * failing that, the last size chosen anywhere in this browser; failing that, the default. So
   * setting a size once is enough for the pages you have not touched, and a pane that has been
   * set keeps its own answer. */
  function rememberedFont(seat, given) {
    const stored = (key) => {
      const size = Number(window.localStorage.getItem(key));
      return Number.isFinite(size) && size >= FONT_MIN && size <= FONT_MAX ? size : null;
    };
    const fromConfig = Number(given) > 0 ? Number(given) : null;
    return stored(fontKey(seat)) ?? stored(FONT_KEY) ?? fromConfig ?? FONT_SIZE;
  }

  /* One switch for every screen on the page. The game and the single-screen view are both tubes
   * with glass over them, so they get the same treatment and the same button; the plain panel
   * is words on plastic and gets none of it. */
  function crtSwitch(bar, monitors) {
    if (!bar || !monitors.length) return;

    const button = document.createElement("button");
    button.type = "button";
    button.dataset.crt = "";
    button.title = "scanlines, a shadow mask and a rolling bar, on the screens";
    bar.appendChild(button);

    const apply = () => {
      const on = crtOn();
      monitors.forEach((monitor) => monitor.classList.toggle("crt", on));
      button.setAttribute("aria-pressed", on ? "true" : "false");
      button.textContent = "crt";
    };

    button.addEventListener("click", () => {
      window.localStorage.setItem(CRT_KEY, crtOn() ? "off" : "on");
      apply();
    });

    apply();
  }

  /* The companion's voice (08_webui_tts.md).
   *
   * A switch and not a state: remembered in the browser beside the CRT setting, off until it is
   * asked for, and offered at all only when the deployment named a voice (`V14`, `V15`). What
   * arrives while it is on is the companion's speech and nothing else — the bridge holds a seat of
   * the daemon's own, and the daemon decided what a line *is* long before this page saw it.
   *
   * **The voice is not this page's to make.** It asks the bridge to speak a line, and the bridge
   * asks whatever the deployment pointed at; what comes back is played as it arrives, so the first
   * words are heard while the rest are still being made. When there is nothing to ask — no endpoint
   * configured, or one that will not answer — the line is said with the browser's own voice instead.
   * Two sinks, walked per line, and never a silence (`08_webui_tts.md` §7).
   */
  const TTS_KEY = "ic.tts";
  // What to assume when the bridge does not say: the endpoints this is written against answer at
  // 24 kHz, and the header it sets is what is believed when it is there.
  const VOICE_RATE = 24000;
  // How far ahead of the cursor the first chunk of a line is scheduled. Too little and the seams
  // between chunks are audible; too much and the voice drifts behind the words on the screen.
  const VOICE_LEAD = 0.2;
  const voiceOn = () => window.localStorage.getItem(TTS_KEY) === "on";

  /* The speaker: one audio context, one cursor, and samples scheduled as they arrive.
   *
   * Chaining `<audio>` elements would put a seam between every chunk and ask the browser's media
   * policy for permission each time. Scheduling buffers on one context plays a stream as one
   * continuous voice, and the cursor is the whole of its state: it is when the next samples will be
   * heard, so a chunk is scheduled there and moves it on by its own length.
   *
   * Samples are gathered into blocks worth scheduling — a buffer per 1 kB of stream is work the
   * audio thread does not need — and the block is what the player hears as one sound. */
  function theSpeaker() {
    const context = new (window.AudioContext || window.webkitAudioContext)();
    let cursor = 0;
    let waiting = [];
    let waited = 0;
    const playing = new Set();

    const schedule = (samples, rate) => {
      const buffer = context.createBuffer(1, samples.length, rate);
      buffer.copyToChannel(samples, 0);
      const source = context.createBufferSource();
      source.buffer = buffer;
      source.connect(context.destination);
      // Where the last one ends, so two blocks are one voice; or a lead ahead, so the first block
      // of a line is not fighting the clock it was scheduled on.
      const at = Math.max(cursor, context.currentTime + VOICE_LEAD);
      source.start(at);
      cursor = at + buffer.duration;
      playing.add(source);
      source.onended = () => playing.delete(source);
    };

    const block = () => {
      const samples = new Float32Array(waited);
      let at = 0;
      for (const part of waiting) {
        samples.set(part, at);
        at += part.length;
      }
      waiting = [];
      waited = 0;
      return samples;
    };

    return {
      // A suspended context is policy rather than a fault: sound is allowed only after a gesture,
      // so a page reloaded with the switch already on wakes the speaker on the first thing the
      // player does.
      wake: () => {
        if (context.state === "suspended") context.resume().catch(() => {});
      },
      push: (samples, rate) => {
        waiting.push(samples);
        waited += samples.length;
        if (waited >= rate * (VOICE_LEAD + 0.1)) schedule(block(), rate);
      },
      // Whatever is left when the stream ends, however short: the tail of a line is still words.
      flush: (rate) => {
        if (waited) schedule(block(), rate);
      },
      // Scheduled and not yet finished. This is what "sounding" means to the wait below: a line
      // being *asked for* is not a line the player is hearing yet (`V31`).
      sounding: () => playing.size > 0 || context.currentTime < cursor,
      stop: () => {
        playing.forEach((source) => {
          try {
            source.stop();
          } catch (err) {
            /* finished on its own between the check and the call: stopping it twice is not a fault */
          }
        });
        playing.clear();
        waiting = [];
        waited = 0;
        cursor = 0;
      }
    };
  }

  /* The endpoint's answer, as samples: 16-bit, little-endian, one channel.
   *
   * A byte stream promises nothing about where a sample begins, so an odd byte at the end of one
   * chunk waits for the next rather than being rounded off into a click. */
  async function* theSamplesOf(body) {
    const reader = body.getReader();
    let kept = new Uint8Array(0);
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      const bytes = new Uint8Array(kept.length + value.length);
      bytes.set(kept);
      bytes.set(value, kept.length);
      const whole = bytes.length - (bytes.length % 2);
      kept = bytes.slice(whole);
      const words = new Int16Array(bytes.buffer, 0, whole / 2);
      const samples = new Float32Array(words.length);
      for (let i = 0; i < words.length; i++) samples[i] = words[i] / 32768;
      yield samples;
    }
  }

  function voiceControl(bar) {
    // No voice named is no switch: a button that could do nothing is worse than no button (`V15`).
    // A browser with no speech of its own is no longer that case: the deployment's endpoint may be
    // the one speaking, and the browser's voice is only what speaks when it is not.
    if (!bar || !IC.voice) return;

    const queue = [];
    let speaking = false;
    let speaker = null;
    // Whether any of the line in flight was actually heard. A failure **after** the sound started is
    // not a line to say again (`V31`): the browser reading it over the top of its own voice is worse
    // than a line that stopped early.
    let heard = false;
    // The request in flight, so that a keystroke can take it away rather than waiting for it: the
    // endpoint notices a closed connection and stops making sound nobody will hear (`V13`).
    let asking = null;

    /* **One page speaks** (`V1`).
     *
     * Two windows onto the same evening are ordinary — one on the screen, one on a laptop — and two of
     * them reading the same line at once is a doubled voice no listener can make sense of. The pages
     * agree among themselves rather than through the bridge: a lease in the browser's own store,
     * refreshed while it is held and taken over when it goes stale, so the window that speaks is the one
     * that most recently asked to. A window that cannot have the lease stays silent, says so once, and
     * still shows everything — the panes are the panes.
     *
     * It reaches across the windows of one browser and no further, which is the honest extent of it: two
     * devices in a room are two people's decision, not something this page should be arbitrating. */
    const LEASE = "ic.voice.speaker";
    const LEASE_MS = 6000;
    const me = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    let mine = false;

    const theLease = () => {
      try {
        return JSON.parse(window.localStorage.getItem(LEASE) || "null");
      } catch (err) {
        // A store that will not be read is a store with no opinion: this page speaks.
        return null;
      }
    };

    const takeTheLease = () => {
      const held = theLease();
      if (held && held.who !== me && Date.now() - held.at < LEASE_MS) return false;
      try {
        window.localStorage.setItem(LEASE, JSON.stringify({ who: me, at: Date.now() }));
      } catch (err) {
        /* No store to write to: better this page says the line than nobody does. */
      }
      return true;
    };

    const dropTheLease = () => {
      if (!mine) return;
      mine = false;
      try {
        window.localStorage.removeItem(LEASE);
      } catch (err) {
        /* nothing to release where nothing can be written */
      }
    };

    /* What the browser has, for the fallback: the name the deployment chose if this browser has it,
     * its default voice otherwise, and never silence. */
    let browserVoice = null;
    const chooseBrowserVoice = () => {
      const voices = window.speechSynthesis ? window.speechSynthesis.getVoices() : [];
      const wanted = String(IC.voice).trim().toLowerCase();
      browserVoice =
        voices.find((voice) => voice.name.toLowerCase() === wanted) ||
        voices.find((voice) => voice.name.toLowerCase().includes(wanted)) ||
        null;
    };

    const sayWithTheBrowser = (text) =>
      new Promise((done) => {
        if (!window.speechSynthesis) return done();
        const utterance = new SpeechSynthesisUtterance(text);
        if (browserVoice) utterance.voice = browserVoice;
        utterance.onend = utterance.onerror = () => done();
        window.speechSynthesis.speak(utterance);
      });

    /* One line, asked of the bridge and played while it is still being made.
     *
     * Anything but a stream of samples is a reason to say it with the browser instead: no endpoint
     * configured is a `404`, an endpoint that did not answer is a `502`, and neither is worth
     * troubling somebody who only wanted to hear the companion. The reason goes to the console,
     * because a voice that keeps falling back is a deployment with something to fix. */
    const sayWithTheEndpoint = async (text) => {
      asking = new AbortController();
      const answer = await fetch("/voice/say", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ text }),
        signal: asking.signal
      });
      if (!answer.ok) throw new Error(`the bridge answered ${answer.status}`);
      const rate = Number(answer.headers.get("x-sample-rate")) || VOICE_RATE;
      if (!speaker) speaker = theSpeaker();
      speaker.wake();
      for await (const samples of theSamplesOf(answer.body)) {
        if (!samples.length) continue;
        heard = true;
        speaker.push(samples, rate);
      }
      speaker.flush(rate);
    };

    /* Wait until nothing this page scheduled is still sounding.
     *
     * A browser utterance cannot be scheduled behind the speaker's cursor the way a buffer can, so the
     * only way to keep the two sinks from talking over each other is to wait for one to finish before
     * the other starts. Bounded, because a node that never reports its end must not hold a line for
     * ever (`V31`). */
    const untilQuiet = async () => {
      for (let waited = 0; speaker && speaker.sounding() && waited < 30000; waited += 50) {
        await new Promise((rest) => setTimeout(rest, 50));
      }
    };

    const next = async () => {
      if (speaking || !queue.length) return;
      // Decided per line rather than once, so a window whose neighbour has gone takes over by itself.
      if (!mine && takeTheLease()) {
        mine = true;
        console.info("voice: this window is speaking now");
      }
      if (!mine) {
        // Another window has it. The line is on the screen either way, and it is being read aloud.
        queue.length = 0;
        return;
      }
      speaking = true;
      const text = queue.shift();
      heard = false;
      try {
        await sayWithTheEndpoint(text);
      } catch (err) {
        // Taken away — by a keystroke, or by the switch going off, both of which clear the request
        // before the connection closes — is not a fault, and is not something to say a second time.
        const takenAway = !asking || asking.signal.aborted;
        if (!takenAway && heard) {
          // Half a line is a line that stopped early, not a line to repeat: saying it again over the
          // top of its own voice is how one companion becomes two (`V31`).
          console.warn("voice: the endpoint stopped part-way through a line", err);
        } else if (!takenAway) {
          // Nothing of it was heard, so the browser saying it **is** the fallback — once whatever is
          // still playing has finished, so the two never overlap.
          console.warn("voice: the endpoint did not speak, so the browser will", err);
          await untilQuiet();
          await sayWithTheBrowser(text);
        }
      }
      speaking = false;
      next();
    };

    /* Everything stops, whatever state it is in. Used when the switch goes off, which is a decision
     * rather than a keystroke and does not wait for anything to be audible. */
    const silence = () => {
      queue.length = 0;
      if (asking) {
        asking.abort();
        asking = null;
      }
      if (speaker) speaker.stop();
      if (window.speechSynthesis) window.speechSynthesis.cancel();
    };

    const akeystroke = (event) => {
      // **Tab is the one key with a job of its own**: it asks for the rest of what is being written,
      // at once, and stops the voice (`S38.1`, `V16`). Consumed, so it does not also walk the focus
      // round the page's buttons — somebody pressing it is skipping ahead, not navigating — and sent
      // to the session as the byte that means it. Nothing is sounding is not a reason not to skip.
      if (event && event.key === "Tab") {
        event.preventDefault();
        silence();
        if (typist && typist.open()) {
          typist.type("\t");
        } else {
          // Said out loud rather than swallowed: the dump is the whole point of the key, and a page
          // that cannot ask for it is a page where nothing happens for no visible reason.
          console.warn("voice: cannot ask the session for the rest — no input pane is open yet");
        }
        return;
      }
      // **A keystroke does not stop the voice** (`V16`). Typing *while* listening is the whole reason
      // there is a voice — the hands and the ears are free of each other — so this touches neither the
      // line being spoken nor the one behind it. What keeps the voice off the conversation's heels is
      // the bound on the queue (`V20.2`) rather than a keystroke, and `Tab` above, or the switch, is
      // what stops a sound.
      //
      // Sound is allowed through only after a gesture, and a page reloaded with the switch already on
      // has not had one yet: the player's first keystroke is what wakes the speaker up. Nothing to
      // wake is not a fault.
      if (speaker) speaker.wake();
    };

    let stream = null;
    const listen = (on) => {
      if (on && !stream) {
        // The lease first: a second window shows the evening and leaves the speaking to one page.
        if (takeTheLease()) {
          mine = true;
        } else {
          console.info("voice: another window is speaking, so this one stays quiet");
        }
        // The bridge opens the daemon's seat when this connects and releases it when the page goes
        // away, so nothing is held for a page that is not listening (`V13`). A stream that drops is
        // not a broken page: EventSource reconnects on its own and the bridge starts a fresh seat.
        stream = new EventSource("/voice");
        stream.onmessage = (event) => {
          queue.push(event.data);
          // **The voice may lead the text; it may never trail it** (`V20.2`).
          //
          // The queue grows whenever the companion says more than it can finish saying — six lines in
          // half a minute is half a minute of speech and then some — and a queue allowed to grow is a
          // voice still reading the *previous* line while the next one is already on the screen. So one
          // line waits and the rest lose to the newest. Nothing is lost from the screen: the text is
          // the record, and a skipped line is a line heard late or not at all.
          if (queue.length > 1) {
            const skipped = queue.splice(0, queue.length - 1).length;
            console.info(`voice: the voice was behind, so ${skipped} line(s) were skipped`);
          }
          next();
        };
      } else if (!on && stream) {
        stream.close();
        stream = null;
        silence();
        dropTheLease();
      }
    };

    const button = document.createElement("button");
    button.type = "button";
    button.dataset.tts = "";
    button.title = "the companion's words, read aloud";
    bar.appendChild(button);

    const apply = () => {
      const on = voiceOn();
      button.setAttribute("aria-pressed", on ? "true" : "false");
      button.textContent = on ? "voice on" : "voice";
      listen(on);
    };

    button.addEventListener("click", () => {
      window.localStorage.setItem(TTS_KEY, voiceOn() ? "off" : "on");
      apply();
    });

    // Every keystroke wakes the speaker — sound is allowed through only after a gesture, and this is
    // the page's one chance to notice one — and the key is **not** consumed (`V17`), because it
    // belongs to the game, or to the line being typed. Captured rather than bubbled, so the page sees
    // it even if a pane stops the event on the way past. **Tab is the one exception**: it asks the
    // session for the rest (`S38.1`) and is consumed, so it skips ahead rather than moving the focus.
    window.addEventListener("keydown", akeystroke, true);
    // The lease is kept alive while it is held, and given up when the page goes — so the window that
    // is left speaking is the one somebody is looking at.
    window.setInterval(() => {
      if (mine && voiceOn()) takeTheLease();
    }, 2000);
    window.addEventListener("pagehide", dropTheLease);
    // Some browsers fill the voice list in after the first call, and only after one.
    if (window.speechSynthesis) {
      window.speechSynthesis.addEventListener("voiceschanged", chooseBrowserVoice);
      chooseBrowserVoice();
    }
    apply();
  }

  /* The session's switches, in the masthead beside the CRT button, on both pages: the house style
   * already there, and a thing you can see and press rather than a `/` command you have to
   * remember (spec 05 §5.1). */
  function sessionSwitches(bar, settings, refresh) {
    if (!bar) return;

    SWITCHES.forEach((sw) => {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.switch = sw.key;
      button.title = sw.title;
      button.textContent = sw.label;
      bar.appendChild(button);

      button.addEventListener("click", () => {
        settings[sw.key] = !settings[sw.key];
        rememberSettings(settings);
        refresh();
        applySettings(settings, true);
      });
    });
  }

  /* The danger level: three buttons in one piece of housing, one of them pressed (spec 05 §5.1).
   *
   * The shape is the argument. It is not an on/off switch — the setting has three states and they
   * are words, not a quantity — so it is not a fourth button and it is not a slider: it is a
   * **segmented control**, which is what a small set of mutually exclusive choices looks like
   * everywhere else on a page. It sits in the masthead with the other session controls rather than
   * on the strip, because it is a decision about the *evening* rather than something you do with
   * your hands mid-sentence; and the daemon's own answer to the verb goes to the pane that asked, so
   * pressing it leaves one line in the strip and nothing in the conversation.
   *
   * A fresh page has nothing pressed, because a page has no opinion until somebody gives it one:
   * what is shown is what this page last sent, which is the same rule the switches and the pace
   * follow. Pressing one sends the daemon's own `/risklow|medium|high` — the verbs a person types,
   * and the only route this page has to the session (spec 05 §5.1). */
  function riskControl(bar, settings, refresh) {
    if (!bar) return;

    const group = document.createElement("span");
    group.className = "segmented";
    group.setAttribute("role", "group");
    group.setAttribute("aria-label", "the danger level");
    RISKS.forEach((risk) => {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.risk = risk.key;
      button.title = risk.title;
      button.textContent = risk.label;
      button.addEventListener("click", () => {
        settings.risk = risk.key;
        rememberSettings(settings);
        refresh();
        applySettings(settings, true);
      });
      group.appendChild(button);
    });
    bar.appendChild(group);
  }

  /* The pace control. A slider rather than five buttons: it is one setting with an order, and the
   * panel's tactile control is the point of the panel (spec 05 §5.1). It shows what this page last
   * sent, and nothing that pretends to be the session's own state. */
  function speedControl(settings, refresh) {
    const panel = document.createElement("div");
    panel.className = "speedpanel";
    panel.innerHTML = `
      <label for="ic-speed">pace</label>
      <input type="range" id="ic-speed" min="0" max="${SPEEDS.length - 1}" step="1">
      <span class="value" data-speed-value>—</span>
      <span class="note">what this page last sent</span>`;
    const slider = panel.querySelector("input");
    const readout = panel.querySelector("[data-speed-value]");

    const show = () => {
      const index = Math.max(0, SPEEDS.indexOf(settings.speed));
      if (document.activeElement !== slider) slider.value = String(index);
      readout.textContent = SPEEDS[index];
    };

    slider.addEventListener("input", () => {
      // Live while dragging, so the reader can see where it is going…
      readout.textContent = SPEEDS[Number(slider.value)];
    });
    slider.addEventListener("change", () => {
      // …and sent once, when it is let go: a command per pixel would be a command per pixel.
      settings.speed = SPEEDS[Number(slider.value)];
      rememberSettings(settings);
      refresh();
      applySettings(settings, true);
    });

    return { element: panel, show };
  }

  /* Put the session where the store says it should be, by typing the commands in.
   *
   * Throttled: the socket retries every 1.5 s while the daemon is away, and without this a
   * flapping connection would retype the whole set on every attempt. A press, though, is sent at
   * once — that is somebody waiting for the machine to change. */
  let appliedAt = 0;

  function applySettings(settings, now = false) {
    const at = Date.now();
    if (!now && at - appliedAt < 2000) return;
    if (!typist || !typist.open()) return;
    appliedAt = at;
    restoreCommands(settings).forEach((line, index) => {
      // Spaced out, so the pane's own echo keeps up and the daemon reads four commands rather
      // than one long line.
      setTimeout(() => sendToSession(line), index * 80);
    });
  }

  /* --- what has to be measured --------------------------------------------------------- */

  /* Every pane's fit, gathered so they can all be run again on demand.
   *
   * A pane's size depends on the browser having laid the page out, and a document in a
   * background tab is never laid out: `ResizeObserver` callbacks and animation frames do not
   * arrive while the document is hidden. A page opened in another tab would therefore come up
   * with collapsed panes and look broken until something else forced a layout. So the fits also
   * run whenever the page comes back into view, into focus, or out of the back/forward cache. */
  const settles = [];

  function settleAll() {
    settles.forEach((settle) => {
      try {
        settle();
      } catch (err) {
        /* a pane that cannot be measured yet: the next event will do */
      }
    });
  }

  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) settleAll();
  });
  window.addEventListener("focus", settleAll);
  window.addEventListener("pageshow", settleAll);
  window.addEventListener("resize", settleAll);

  /* --- a pane -------------------------------------------------------------------------- */

  function openPane(pane, index, total, monitors) {
    // Anything that is not a plain chassis is a monitor: a screen is the safe assumption for a
    // shape this page has never heard of.
    const shape = pane.shape === "strip" || pane.shape === "panel" ? pane.shape : "monitor";
    const cell = document.createElement("div");
    cell.className = "cell";
    cell.dataset.seat = String(pane.seat).trim().toLowerCase();
    cell.dataset.shape = shape;
    cell.innerHTML = signage(pane.label) + (shape === "monitor" ? MONITOR_MARKUP : plainMarkup(shape));
    document.querySelector("#panes").appendChild(cell);

    const screen = cell.querySelector("[data-screen]");
    const readout = cell.querySelector("[data-size]");
    const lamps = cell.querySelector("[data-lamps]");
    const stage = cell.querySelector("[data-stage]");
    const monitor = cell.querySelector("[data-monitor]");

    // Any pane drawn on a monitor is a screen, and every screen is switchable from the one
    // button in the masthead (see crtSwitch).
    if (monitor) monitors.push(monitor);

    // Which way this pane's bytes travel on the lamps. The strip is the player's own hands, so
    // its signal is the typing lamp; the panel is the companion. See LAMP_SLOTS above.
    const signal = SIGNAL_FOR_SHAPE[shape] || SIGNAL_FOR_SHAPE.monitor;

    /* The lamps a monitor carries. In the multiview that is all three, because all three panes
     * are on the page; on the single-screen page it is the one this pane can light itself, and
     * two dead lights would be worse than none. */
    if (lamps) {
      const slots = multi
        ? LAMP_SLOTS
        : LAMP_SLOTS.filter((slot) => slot.signal === signal);
      slots.forEach((slot) => {
        // The name on a lamp's tooltip comes from configuration, like every other name on the
        // page: the pane that owns the signal is the pane the lamp is watching.
        const owner = panes.find(
          (p) => (SIGNAL_FOR_SHAPE[p.shape] || SIGNAL_FOR_SHAPE.monitor) === slot.signal);
        const title = owner && owner.label
          ? `${owner.label} — ${LAMP_VERB[slot.signal]}`
          : slot.title;
        const lamp = document.createElement("div");
        lamp.className = "led";
        lamp.dataset.signal = slot.signal;
        lamp.title = title;
        lamp.setAttribute("role", "img");
        lamp.setAttribute("aria-label", title);
        lamps.appendChild(lamp);
        const watchers = lampWatchers.get(slot.signal) || [];
        watchers.push({ element: lamp, timer: 0 });
        lampWatchers.set(slot.signal, watchers);
      });
    }

    const term = new window.Terminal({
      theme: themeFor(cell.dataset.seat),
      fontFamily: MONO,
      fontSize: rememberedFont(cell.dataset.seat, pane.fontSize),
      lineHeight: 1.16,
      letterSpacing: 0,
      cursorBlink: true,
      scrollback: 2000,
      allowProposedApi: true,
    });
    const fit = new window.FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(screen);

    /* The one thing this pane is *told* rather than shown. The daemon writes an OSC into the pane
     * when the companion takes a turn and again when the reply has been delivered; xterm hands the
     * payload over and prints nothing, which is the whole point — a light, not a line. Wrapped,
     * because a page talking to an older xterm without the parser would otherwise fail to open a
     * pane at all, and a missing lamp is not worth a missing screen. */
    if (term.parser && term.parser.registerOscHandler) {
      try {
        term.parser.registerOscHandler(LAMP_OSC, (payload) => {
          theTurnMoved(payload);
          return true;
        });
      } catch (err) {
        /* an xterm that will not take it: the byte-lights still work */
      }

      /* The same trick for the second fact the page cannot work out for itself (B3): whether the
       * line being typed is holding the companion back. It is the daemon's rule and the daemon's
       * line — a page that tried to reimplement "starts with a space" would be guessing at the
       * core's buffer, and would be wrong the moment a character arrives out of order. The pane
       * that may not act says so on itself, in red, without a single line being written. */
      try {
        term.parser.registerOscHandler(HOLD_OSC, (payload) => {
          theLineHolds(payload, cell);
          return true;
        });
      } catch (err) {
        /* an old xterm: no indicator, and nothing else changes */
      }
    }

    const size = () => {
      try { fit.fit(); } catch (err) { /* a hidden or zero-sized pane: nothing to fit yet */ }
      readout.textContent = `${term.cols} × ${term.rows}`;
    };
    size();

    /* A monitor has to be the largest exact FRAME box that fits its stage. Left to CSS that is
     * a choice between overflowing the cell (auto margins on a stretched grid item) and
     * collapsing to nothing (any centring that unstretches it), so the arithmetic is done
     * here: two numbers, no ambiguity, and the overlays stay locked to the artwork. */
    const fitMonitor = () => {
      if (!monitor || !stage) return;
      const box = stage.getBoundingClientRect();
      const scale = Math.min(box.width / FRAME.width, box.height / FRAME.height);
      if (!(scale > 0)) return;
      monitor.style.width = `${Math.floor(FRAME.width * scale)}px`;
      monitor.style.height = `${Math.floor(FRAME.height * scale)}px`;
    };

    /* The terminal is fitted after the monitor, because its size is the monitor's size. */
    const settle = () => { fitMonitor(); size(); };
    settles.push(settle);
    new ResizeObserver(settle).observe(stage || screen);
    settle();

    /* One pair of buttons per pane. They refit rather than reload: the terminal reflows, the
     * bridge is told the new size, and the daemon lays the pane out again. */
    const fontValue = cell.querySelector("[data-value]");
    let fontSize = term.options.fontSize;
    const setFont = (wanted, remember) => {
      fontSize = Math.min(FONT_MAX, Math.max(FONT_MIN, Math.round(wanted / FONT_STEP) * FONT_STEP));
      fontValue.textContent = `${fontSize}`;
      term.options.fontSize = fontSize;
      if (remember) {
        window.localStorage.setItem(fontKey(cell.dataset.seat), String(fontSize));
        // And as the answer for any pane that has not been given one of its own.
        window.localStorage.setItem(FONT_KEY, String(fontSize));
      }
      size();
      term.focus();
    };
    setFont(fontSize, false);
    cell.querySelector("[data-smaller]").addEventListener("click", () => setFont(fontSize - FONT_STEP, true));
    cell.querySelector("[data-larger]").addEventListener("click", () => setFont(fontSize + FONT_STEP, true));

    /* The pane's own activity, on the lamp that watches it: bytes arriving here are this pane's
     * direction of travel, and a keystroke sent from here is the same thing the other way. */
    const flash = () => light(signal);

    const socket = () => {
      const scheme = location.protocol === "https:" ? "wss" : "ws";
      const url = `${scheme}://${location.host}/ws?seat=${encodeURIComponent(pane.seat)}` +
                  `&cols=${term.cols}&rows=${term.rows}`;
      const ws = new WebSocket(url);
      ws.binaryType = "arraybuffer";

      const tell = (cols, rows) => {
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ resize: [cols, rows] }));
        }
      };

      ws.onopen = () => {
        cell.classList.remove("down");
        tell(term.cols, term.rows);
        // The pane that accepts input is this page's way into the session, so it is remembered
        // here rather than found by asking the document — and every connect is a chance to put
        // the remembered settings back, which is what makes a redeploy survivable: the daemon
        // comes up with its own defaults, not with the player's (spec 05 §5.1).
        //
        // Which pane that is: the config's `focus` says so in the multiview, and on the
        // one-screen page it is the session pane itself, which is the only one there is — and the
        // pane that has taken the cursor all along.
        if (pane.focus || (!multi && index === total - 1)) {
          typist = {
            open: () => ws.readyState === WebSocket.OPEN,
            send: (line) => ws.send(new TextEncoder().encode(`${line}\r`)),
            // A **signal** rather than a line: Tab asks the session for the rest of what is being
            // written, and it goes as the byte it is, with no Return of its own
            // (03_session.md §5 (`S38.1`)).
            type: (keys) => ws.send(new TextEncoder().encode(keys)),
          };
          appliedAt = 0;
          setTimeout(() => applySettings(settings), 250);
        }
      };
      ws.onmessage = (event) => {
        // A string message would be protocol, and there is none: everything is bytes.
        term.write(new Uint8Array(event.data));
        flash();
      };
      ws.onclose = () => {
        cell.classList.add("down");
        // The daemon replays what a late pane missed, so a reconnect wants a clean screen
        // rather than a second copy of the last two hundred lines.
        term.reset();
        term.write("\r\n\x1b[90m[webui] no signal — reconnecting\x1b[0m\r\n");
        // A light that was being held for a turn nobody will hear the end of: the socket that
        // would have carried `turn_finished` is the one that has just gone, so it is let go here.
        // Left on, it would say "working" until the page was reloaded, which is the one thing a
        // lamp must never do.
        hold("quill", false);
        setTimeout(socket, 1500);
      };

      term.onData((data) => {
        light(signal);
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(new TextEncoder().encode(data));
        }
      });
      term.onResize(({ cols, rows }) => {
        readout.textContent = `${cols} × ${rows}`;
        tell(cols, rows);
      });
    };

    socket();

    // The pane you type in takes the cursor, so a page load lands with the keyboard ready.
    if (pane.focus || (!IC.multi.some((p) => p.focus) && index === total - 1)) {
      setTimeout(() => term.focus(), 60);
    }
    return term;
  }

  const multi = document.body.dataset.mode === "multi";
  const panes = multi ? IC.multi : IC.single;
  const settings = rememberedSettings();
  const bar = controls();
  const monitors = [];
  panes.forEach((pane, i) => openPane(pane, i, panes.length, monitors));
  crtSwitch(bar, monitors);
  voiceControl(bar);

  /* The switches and the pace control (spec 05 §5.1).
   *
   * The switches are masthead buttons on both pages, in the same style as the CRT one. The pace
   * control is the multiview's panel — under the companion's monitor, which gives up the height
   * for it — and on the one-screen page it sits in the bar beside the other controls. Both show
   * and send; neither knows anything the session has not been told. */
  const speed = speedControl(settings, () => refreshControls(settings, speed));
  const refreshControls = () => {
    SWITCHES.forEach((sw) => {
      const button = bar && bar.querySelector(`[data-switch="${sw.key}"]`);
      if (button) button.setAttribute("aria-pressed", settings[sw.key] ? "true" : "false");
    });
    // The danger level shows what this page last sent, and nothing at all until it has sent one:
    // a page that has never been asked is not the same as a session that is careful.
    RISKS.forEach((risk) => {
      const button = bar && bar.querySelector(`[data-risk="${risk.key}"]`);
      if (button) button.setAttribute("aria-pressed", settings.risk === risk.key ? "true" : "false");
    });
    speed.show();
  };

  sessionSwitches(bar, settings, refreshControls);
  riskControl(bar, settings, refreshControls);

  if (multi) {
    // Under the companion's own pane, which is what makes the panel the companion's panel.
    const panel = document.querySelector('.cell[data-shape="panel"]');
    if (panel) {
      panel.dataset.panel = "speed";
      panel.appendChild(speed.element);
    } else if (bar) {
      bar.appendChild(speed.element);
    }
  } else if (bar) {
    bar.appendChild(speed.element);
  }

  refreshControls();

  /* The game in progress, over its own screen.
   *
   * Every name on this page comes from configuration and is baked in when the page is served, so
   * the game's says THE GAME whatever is loaded. Which game is in play is published — the state
   * directory's pointer, in `/stats.json`, the same document the home page reads — and it is
   * re-read on a timer and whenever the page comes back into view, because a `/switch` happens at
   * a keyboard and nothing on this page is told about it. Nothing here reads the session's text:
   * the pointer is a fact about the instance, which is all this surface is allowed to know
   * (spec 06 §4.1). With no stats document the configured label stands. */
  function followTheGame() {
    const names = [...document.querySelectorAll('.cell[data-seat="game"] .signage .name')];
    if (!names.length) return;
    const lamps = [...document.querySelectorAll('.lamps .led[data-signal="screen"]')];

    const read = async () => {
      try {
        const response = await fetch("/stats.json", { cache: "no-store" });
        if (!response.ok) return;
        const stats = await response.json();
        const stem = String(stats && stats.game ? stats.game : "").trim();
        if (!stem) return;
        const label = stem.toUpperCase();
        names.forEach((name) => { name.textContent = label; });
        // The lamp that watches the game names it too, so the two do not disagree.
        lamps.forEach((lamp) => {
          const title = `${label} — ${LAMP_VERB.screen}`;
          lamp.title = title;
          lamp.setAttribute("aria-label", title);
        });
      } catch (err) {
        /* A label is a nicety; the panes do not depend on it. */
      }
    };

    read();
    setInterval(read, 10000);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) read(); });
    window.addEventListener("focus", read);
  }

  followTheGame();

  const fullscreen = bar && bar.querySelector("[data-fullscreen]");
  if (fullscreen) {
    fullscreen.addEventListener("click", toggleFullscreen);
    document.addEventListener("fullscreenchange", () => {
      const on = Boolean(document.fullscreenElement);
      fullscreen.setAttribute("aria-pressed", on ? "true" : "false");
      fullscreen.textContent = on ? "windowed" : "full screen";
    });
  }

  // Captured before the terminal, or the chord would be typed into the game as text.
  window.addEventListener("keydown", (event) => {
    if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === "f") {
      event.preventDefault();
      event.stopPropagation();
      toggleFullscreen();
    }
  }, true);

  if (!panes.length) {
    document.querySelector("#panes").innerHTML =
      '<p class="prose">No panes are configured. Set INFOCOMPANION_PANE_MULTI (or _SINGLE) and reload.</p>';
  }
})();
