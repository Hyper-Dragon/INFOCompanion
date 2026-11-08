/*
 * dtool.h - the tool channel for dFrotzTooled.
 *
 * This is part of Frotz, and like the rest of it, GPL v2.
 *
 * The dumb interface has always had a second, private conversation with whoever
 * is driving it: the `\`-prefixed runtime commands, which the story never sees.
 * This adds a third party to that conversation -- a *host* -- over two extra file
 * descriptors, and it is deliberately additive:
 *
 *   * fd `out`  : events, one JSON object per line, newline-terminated;
 *   * fd `out+1`: commands, one per line, in the same `\`-command namespace the
 *                 interface already uses, so a person at a terminal can type them.
 *
 * Nothing here is story-specific and nothing here describes the story. The
 * channel reports what the Z-machine or the interpreter itself knows -- which
 * words the story's parser accepted, whether it is ready for input, which file a
 * save went to -- and it never matches, invents or interprets a word of the
 * game's own text. The screen keeps going to stdout, untouched.
 *
 * With no `-T`, none of this exists: the interface behaves exactly as upstream's
 * does, which is what lets the host run a stock interpreter beside this one.
 */

#ifndef DTOOL_H
#define DTOOL_H

#include <stdio.h>
#include <stddef.h>

/* Wire protocol version. Additive: a host ignores an event it does not know. */
#define DTOOL_PROTOCOL 1
/* Which interpreter this is, for the handshake. */
#define DTOOL_BUILD "dfrotztooled-2.54"

/* --------------------------------------------------------------------------
 * Output accounting
 *
 * Every byte the dumb interface writes to stdout goes through one of these
 * wrappers, so `dtool_out_bytes()` is an exact count of what the reader has been
 * sent. The prompt event carries that number, which lets a host frame a turn by
 * bytes instead of by waiting for the output to go quiet.
 *
 * dtool.c itself defines these and therefore compiles with DTOOL_NO_MACROS.
 * ------------------------------------------------------------------------ */
#ifndef DTOOL_NO_MACROS
#define putchar(c)   dtool_count_putchar(c)
#define fputs(s, f)  dtool_count_fputs(s, f)
#define puts(s)      dtool_count_puts(s)
#define printf(...)  dtool_count_printf(__VA_ARGS__)
#endif

int   dtool_count_putchar(int c);
int   dtool_count_fputs(const char *s, FILE *stream);
int   dtool_count_puts(const char *s);
int   dtool_count_printf(const char *format, ...);
unsigned long dtool_out_bytes(void);

/* --------------------------------------------------------------------------
 * Lifecycle
 * ------------------------------------------------------------------------ */

/* Start the channel. Returns 1 when it is live. Called from the argument parser:
 * a host opts in with `-T` and gets `dtool_active()` true, anything else gets an
 * interface that behaves as upstream's does. */
int  dtool_init(void);
int  dtool_active(void);

/* Write a diagnostic. With a channel this becomes an event, so that the stream the
 * host reads is frames and nothing else; without one it goes to stderr the way it
 * always has. */
void dtool_diagnostic(const char *format, ...);

/* The handshake. Emitted once the story is loaded, so it can say what it is. */
void dtool_announce(void);

/* --------------------------------------------------------------------------
 * Events
 * ------------------------------------------------------------------------ */

/* The interpreter is at a keyboard read and everything it has printed is
 * flushed: `out_bytes` is the total written to stdout so far. `kind` is
 * "line" or "char"; `continued` is set when a line is resuming leftovers. */
void dtool_input_wanted(const char *kind, int continued);

/* A line (or key) was consumed as input. Emitted after the read returns, so the
 * interpreter knows whether it was the first input since a restore. */
void dtool_input_taken(const char *text, const char *kind);

/* The story's parser has run: what its dictionary did with the line. Read from
 * the parse buffer and the text buffer the story itself nominated, so this is the
 * Z-machine's answer rather than a guess about the game's wording. */
void dtool_parse(zword text_addr, zword parse_addr);

/* A save or restore finished. `path` is what was actually opened, and `target`
 * says whether the host named it or the interpreter asked for it. */
void dtool_saved(const char *path, int ok, int target);
void dtool_restored(const char *path, int ok);

/* A one-off note: a restart, a fatal error, a command that was not understood. */
void dtool_note(const char *event, const char *detail);

/* The interpreter's own screen, on demand (`\t screen`). */
void dtool_screen(void);

/* The interpreter is leaving, by choice. */
void dtool_quit(int status);

/* --------------------------------------------------------------------------
 * Commands (the `\t` namespace)
 * ------------------------------------------------------------------------ */

/* Handle one `\t ...` command line. Returns 1 if it was ours, 0 if the caller
 * should keep looking. */
int dtool_command(const char *line);

/* The file a save or restore should use, or NULL to let the interpreter prompt
 * the way it always has. When one is set, `os_read_file_name` returns it
 * directly: no prompt, no overwrite question, and no `-R` rewrite. */
const char *dtool_save_target(void);
const char *dtool_restore_target(void);

/* Did the last input line come from the tool namespace rather than the keyboard
 * path the story sees? */

/* The seed given with -s, or -1 when the interpreter chooses one. Set by the
 * argument parser and reported in the handshake, because "this session used a
 * fixed seed" is the difference between a replay and a rerun. */
extern int dtool_seed;

/* --------------------------------------------------------------------------
 * Hooks the common code calls
 * ------------------------------------------------------------------------ */
void dtool_after_restore(void);   /* a restore has completed */
void dtool_after_restart(void);   /* the story has restarted */

#endif /* DTOOL_H */
