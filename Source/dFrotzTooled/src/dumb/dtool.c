/*
 * dtool.c - the tool channel for dFrotzTooled.
 *
 * See dtool.h for what this is and why. The short version: the dumb interface
 * already has a private conversation with its driver (the `\` runtime commands).
 * This gives that conversation a machine on the other end, over two extra
 * descriptors, and it reports only what the Z-machine or this interpreter knows.
 *
 * Design notes worth keeping in mind when changing anything here:
 *
 *  * **Frames are small and the writer never blocks.** A frame that cannot be
 *    written in full is dropped and counted, because the tool channel must never
 *    be able to stall a game. The host can read the counters with `\t stats`.
 *
 *  * **Text is escaped, not filtered.** Bytes outside printable ASCII are
 *    written as \u00XX, so a frame is always valid JSON and a byte that came out
 *    of a story survives the round trip. Nothing is ever matched against.
 *
 *  * **stdout is counted, not parsed.** Every byte the interface prints goes
 *    through dtool_count_* so that the prompt event can tell a host exactly how
 *    much it has been sent. That is what lets the host frame a turn by bytes.
 */

#define DTOOL_NO_MACROS
#include "dfrotz.h"
#include "dtool.h"

#include <stdarg.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <sys/stat.h>

extern z_header_t z_header;

/* --------------------------------------------------------------------------
 * State
 * ------------------------------------------------------------------------ */

static int  out_fd = -1;          /* events out (stderr), or -1 when there is none */

/* Commands arrive on stdin, in the `\` namespace, not on a descriptor of their own:
 * that is the whole reason this design needs no wiring. The host already writes the
 * story's input there, and the interface already intercepts `\`-commands far away
 * from the story, so a tool call is a line like any other from the process's point
 * of view. */
static char *save_target = NULL;
static char *restore_target = NULL;

static unsigned long frames = 0;      /* frames emitted */
static unsigned long dropped = 0;     /* frames the reader was too slow for */
static unsigned long out_bytes = 0;   /* bytes written to stdout, ever */
static int byte_counts_trusted = 1;   /* cleared if a wrapper is bypassed */
/* The seed the story will be told, as given on the command line: -1 means the
 * interpreter picks one, and the host is told that rather than a number that
 * would be different the next time it asked. */
int dtool_seed = -1;

/* A restore has happened and no input has been taken since: the first line a
 * story reads after a restore is not an ordinary line, and a host that is
 * keeping a transcript needs to know that. It starts clear, because a process
 * that was started without `-L` has restored nothing. */
static int waiting_after_restore = 0;

/* The frame under construction. One at a time, and the writer is synchronous, so
 * there is nothing to interleave. */
static char frame_text[8192];
static size_t frame_len = 0;
static int frame_broken = 0;

/* --------------------------------------------------------------------------
 * Output accounting
 * ------------------------------------------------------------------------ */

int dtool_count_putchar(int c)
{
	out_bytes++;
	return putchar(c);
}

int dtool_count_fputs(const char *s, FILE *stream)
{
	if (stream == stdout && s != NULL)
		out_bytes += strlen(s);
	return fputs(s, stream);
}

int dtool_count_puts(const char *s)
{
	if (s != NULL)
		out_bytes += strlen(s) + 1;   /* puts adds the newline */
	return puts(s);
}

int dtool_count_printf(const char *format, ...)
{
	va_list ap;
	int n;

	va_start(ap, format);
	n = vprintf(format, ap);
	va_end(ap);

	if (n > 0 && format != NULL)
		out_bytes += (unsigned long) n;
	return n;
}

unsigned long dtool_out_bytes(void)
{
	return out_bytes;
}

/* --------------------------------------------------------------------------
 * The frame writer
 * ------------------------------------------------------------------------ */

static void json_bytes(const char *s, size_t len)
{
	size_t i;

	for (i = 0; i < len; i++) {
		unsigned char c = (unsigned char) s[i];

		if (frame_len + 8 >= sizeof(frame_text)) {
			frame_broken = 1;
			return;
		}
		switch (c) {
		case '"':  frame_text[frame_len++] = '\\'; frame_text[frame_len++] = '"';  break;
		case '\\': frame_text[frame_len++] = '\\'; frame_text[frame_len++] = '\\'; break;
		case '\n': frame_text[frame_len++] = '\\'; frame_text[frame_len++] = 'n';  break;
		case '\r': frame_text[frame_len++] = '\\'; frame_text[frame_len++] = 'r';  break;
		case '\t': frame_text[frame_len++] = '\\'; frame_text[frame_len++] = 't';  break;
		default:
			if (c < 0x20 || c >= 0x7f) {
				/* Latin-1 as an escape: valid JSON, and a byte for byte
				 * round trip, which a story's odd character needs. */
				sprintf(frame_text + frame_len, "\\u%04x", c);
				frame_len += 6;
			} else {
				frame_text[frame_len++] = (char) c;
			}
			break;
		}
	}
}

static void json_key(const char *key)
{
	/* A comma before every field but the first. Tested against the last character
	 * rather than a counter, because arrays are built with the same buffer. */
	if (frame_len > 1 && frame_text[frame_len - 1] != '{'
	    && frame_text[frame_len - 1] != '[' && frame_text[frame_len - 1] != ',')
		frame_text[frame_len++] = ',';
	frame_text[frame_len++] = '"';
	json_bytes(key, strlen(key));
	frame_text[frame_len++] = '"';
	frame_text[frame_len++] = ':';
}

static void frame_begin(const char *event)
{
	frame_len = 0;
	frame_broken = 0;
	frame_text[frame_len++] = '{';
	sprintf(frame_text + frame_len, "\"seq\":%lu,", ++frames);
	frame_len += strlen(frame_text + frame_len);
	json_key("event");
	frame_text[frame_len++] = '"';
	json_bytes(event, strlen(event));
	frame_text[frame_len++] = '"';
}

static void frame_str(const char *key, const char *value)
{
	if (value == NULL)
		return;
	json_key(key);
	frame_text[frame_len++] = '"';
	json_bytes(value, strlen(value));
	frame_text[frame_len++] = '"';
}

static void frame_long(const char *key, long value)
{
	json_key(key);
	sprintf(frame_text + frame_len, "%ld", value);
	frame_len += strlen(frame_text + frame_len);
}

static void frame_bool(const char *key, int value)
{
	json_key(key);
	strcpy(frame_text + frame_len, value ? "true" : "false");
	frame_len += strlen(frame_text + frame_len);
}

/* Finish the frame and hand it to the reader, or drop it and count it. */
static void frame_end(void)
{
	ssize_t written;

	if (out_fd < 0)
		return;
	if (frame_broken) {
		dropped++;
		return;
	}
	if (frame_len + 2 >= sizeof(frame_text)) {
		dropped++;
		return;
	}
	frame_text[frame_len++] = '}';
	frame_text[frame_len++] = '\n';

	written = write(out_fd, frame_text, frame_len);
	if (written != (ssize_t) frame_len) {
		/* A short or refused write means the reader is not keeping up. The
		 * game is not held up for it: the frame is counted as lost and the
		 * channel carries on. */
		dropped++;
	}
}

/* --------------------------------------------------------------------------
 * Lifecycle
 * ------------------------------------------------------------------------ */

int dtool_init(void)
{
	int flags;

	/* A descriptor that is not open is not an error: `-T` on a command line whose
	 * stderr was closed gets an interface with no tools rather than a refusal to
	 * start. */
	if (fcntl(STDERR_FILENO, F_GETFD) == -1)
		return 0;

	out_fd = STDERR_FILENO;

	flags = fcntl(out_fd, F_GETFL, 0);
	if (flags != -1)
		fcntl(out_fd, F_SETFL, flags | O_NONBLOCK);

	return 1;
}

int dtool_active(void)
{
	return out_fd >= 0;
}

void dtool_announce(void)
{
	long pid = (long) getpid();

	if (!dtool_active())
		return;

	frame_begin("hello");
	frame_long("protocol", DTOOL_PROTOCOL);
	frame_str("build", DTOOL_BUILD);
	frame_long("pid", pid);
	frame_long("zversion", (long) z_header.version);
	frame_str("story", f_setup.story_file);
	frame_str("stem", f_setup.story_name);
	frame_long("interpreter", (long) z_header.interpreter_number);
	frame_long("seed", (long) dtool_seed);
	frame_long("width", (long) z_header.screen_cols);
	frame_long("height", (long) z_header.screen_rows);
	frame_long("undo", (long) f_setup.undo_slots);
	frame_bool("tandy", (z_header.config & CONFIG_TANDY) != 0);
	frame_bool("timed_input", (z_header.config & CONFIG_TIMEDINPUT) != 0);
	frame_str("restricted", f_setup.restricted_path);
	frame_str("restore", f_setup.restore_mode ? f_setup.save_name : NULL);
	frame_str("save_target", save_target);
	frame_str("restore_target", restore_target);
	/* What this build offers, so a host can refuse to use what is not there
	 * rather than guess. */
	frame_str("tools", "hello,ping,status,screen,stats,save,restore,target,clear");
	frame_long("out_bytes", (long) out_bytes);
	frame_end();
}

/* --------------------------------------------------------------------------
 * Events
 * ------------------------------------------------------------------------ */

void dtool_input_wanted(const char *kind, int continued)
{
	if (!dtool_active())
		return;

	frame_begin("input_wanted");
	frame_str("kind", kind);
	frame_bool("continued", continued);
	frame_long("out_bytes", (long) out_bytes);
	frame_bool("after_restore", waiting_after_restore);
	frame_bool("counts_trusted", byte_counts_trusted);

	/* For the versions where the interpreter itself keeps the status line, the
	 * numbers on it are ours to report: they are global variables, and they are
	 * already on the screen. Later versions draw their own, from their own
	 * globals, and we do not pretend to know what they mean. */
	if (z_header.version <= V3) {
		zword addr = z_header.globals;
		zword g0, g1, g2;

		LOW_WORD(addr, g0);
		addr += 2;
		LOW_WORD(addr, g1);
		addr += 2;
		LOW_WORD(addr, g2);

		frame_long("location", (long) g0);
		if (z_header.config & CONFIG_TIME) {
			frame_long("hours", (long) g1);
			frame_long("minutes", (long) g2);
		} else {
			frame_long("score", (long) g1);
			frame_long("moves", (long) g2);
		}
	}
	frame_end();
}

void dtool_input_taken(const char *text, const char *kind)
{
	if (!dtool_active())
		return;

	frame_begin("input");
	frame_str("kind", kind);
	frame_str("text", text);
	frame_bool("after_restore", waiting_after_restore);
	frame_long("out_bytes", (long) out_bytes);
	frame_end();
	waiting_after_restore = 0;
}

void dtool_parse(zword text_addr, zword parse_addr)
{
	zbyte max_words, word_count;
	zword typed_start;
	int typed_end;
	zword p;
	int i;
	char line[INPUT_BUFFER_SIZE + 1];
	size_t line_len = 0;

	if (!dtool_active() || parse_addr == 0)
		return;

	/* The text buffer: versions 1 to 4 hold the characters from byte 1 with a
	 * zero terminator; version 5 and later hold the length in byte 1 and the
	 * characters from byte 2. */
	if (z_header.version >= V5) {
		zbyte len;

		typed_start = text_addr + 2;
		LOW_BYTE(text_addr + 1, len);
		typed_end = typed_start + len;
	} else {
		zword addr;

		typed_start = text_addr + 1;
		addr = typed_start;
		while (1) {
			zbyte c;

			LOW_BYTE(addr, c);
			if (c == 0)
				break;
			addr++;
			if (addr - typed_start > INPUT_BUFFER_SIZE)
				break;
		}
		typed_end = addr;
	}

	p = parse_addr;
	LOW_BYTE(p, max_words);
	p++;
	LOW_BYTE(p, word_count);
	p++;

	/* The line as the story sees it: the text buffer, which is what the words
	 * below are positions into. */
	{
		zword q = typed_start;

		while ((int) q < typed_end && line_len < sizeof(line) - 1) {
			zbyte c;

			LOW_BYTE(q, c);
			if (c == 0)
				break;
			line[line_len++] = (char) c;
			q++;
		}
		line[line_len] = '\0';
	}

	frame_begin("parse");
	frame_str("line", line);
	frame_long("max", (long) max_words);
	frame_long("words", (long) word_count);

	/* One four-byte block per word, from byte 2 of the parse buffer: the address
	 * of the dictionary entry, or 0 when the dictionary does not have the word;
	 * the number of letters; and the position of the word in the text buffer.
	 * Zero is the Z-machine's own way of saying "not a word I know", so this is
	 * the story's parser answering, not us guessing at its wording.
	 *
	 * The position is measured from the start of the *text buffer*, not from
	 * where the typed characters begin, so a word starts at text_addr + at in
	 * both versions. Reading it as an offset into the typed text puts every
	 * word two characters early in Version 5, which is how it was found. */
	json_key("tokens");
	frame_text[frame_len++] = '[';
	for (i = 0; i < word_count && i < 32; i++) {
		zword dict;
		zbyte letters, at;
		zword entry = p + (zword) (4 * i);
		zword from;
		size_t len;
		char word[128];
		size_t k;
		zword q;

		LOW_WORD(entry, dict);
		LOW_BYTE(entry + 2, letters);
		LOW_BYTE(entry + 3, at);

		if (i > 0)
			frame_text[frame_len++] = ',';

		from = (zword) (text_addr + at);
		if ((int) from < (int) typed_start)
			from = typed_start;
		if ((int) from > typed_end)
			from = typed_start;
		len = letters;
		if ((int) (from + len) > typed_end)
			len = (size_t) (typed_end - (int) from);
		if (len > sizeof(word) - 1)
			len = sizeof(word) - 1;

		q = from;
		for (k = 0; k < len; k++) {
			zbyte c;

			LOW_BYTE(q, c);
			word[k] = (char) c;
			q++;
		}
		word[k] = '\0';

		/* Written by hand: the field helpers add commas for the enclosing
		 * object, and these are inside an array. */
		frame_text[frame_len++] = '{';
		strcpy(frame_text + frame_len, "\"dict\":");
		frame_len += strlen(frame_text + frame_len);
		sprintf(frame_text + frame_len, "%ld", (long) dict);
		frame_len += strlen(frame_text + frame_len);
		strcpy(frame_text + frame_len, ",\"known\":");
		frame_len += strlen(frame_text + frame_len);
		strcpy(frame_text + frame_len, dict != 0 ? "true" : "false");
		frame_len += strlen(frame_text + frame_len);
		strcpy(frame_text + frame_len, ",\"letters\":");
		frame_len += strlen(frame_text + frame_len);
		sprintf(frame_text + frame_len, "%ld", (long) letters);
		frame_len += strlen(frame_text + frame_len);
		strcpy(frame_text + frame_len, ",\"at\":");
		frame_len += strlen(frame_text + frame_len);
		sprintf(frame_text + frame_len, "%ld", (long) at);
		frame_len += strlen(frame_text + frame_len);
		strcpy(frame_text + frame_len, ",\"text\":\"");
		frame_len += strlen(frame_text + frame_len);
		json_bytes(word, k);
		frame_text[frame_len++] = '"';
		frame_text[frame_len++] = '}';
	}
	frame_text[frame_len++] = ']';
	frame_end();
}

void dtool_saved(const char *path, int ok, int target)
{
	struct stat st;
	long bytes = -1;

	if (!dtool_active())
		return;

	/* The size comes from the file, not from the write, so a save that reported
	 * success but wrote nothing cannot be described as one. */
	if (path != NULL && stat(path, &st) == 0)
		bytes = (long) st.st_size;

	frame_begin("saved");
	frame_str("path", path);
	frame_bool("ok", ok);
	frame_long("bytes", bytes);
	/* `target` rather than `prompted`: whether the host named the file is a fact
	 * about this save, while whether the interpreter asked a question is only
	 * visible in what it printed. */
	frame_bool("target", target);
	frame_end();
}

void dtool_restored(const char *path, int ok)
{
	if (!dtool_active())
		return;

	frame_begin("restored");
	frame_str("path", path);
	frame_bool("ok", ok);
	frame_end();
}

void dtool_note(const char *event, const char *detail)
{
	if (!dtool_active())
		return;

	frame_begin(event);
	frame_str("detail", detail);
	frame_end();
}

void dtool_after_restore(void)
{
	waiting_after_restore = 1;
}

/* `dumb_screen_*` hand out the dumb interface's own screen (doutput.c). */
extern int dumb_screen_size(int *rows, int *cols);
extern int dumb_screen_cursor(int *row, int *col);
extern int dumb_screen_mode(void);
extern int dumb_screen_hidden(void);
extern int dumb_screen_row_changed(int row);
extern int dumb_screen_row_text(int row, char *out, int max);

void dtool_screen(void)
{
	int rows = 0, cols = 0, crow = 0, ccol = 0, r;
	char line[512];

	if (!dtool_active())
		return;

	dumb_screen_size(&rows, &cols);
	dumb_screen_cursor(&crow, &ccol);

	frame_begin("screen");
	frame_long("rows", (long) rows);
	frame_long("cols", (long) cols);
	frame_long("cursor_row", (long) crow);
	frame_long("cursor_col", (long) ccol);
	frame_long("hidden", (long) dumb_screen_hidden());
	frame_long("compression", (long) dumb_screen_mode());
	frame_long("out_bytes", (long) out_bytes);

	/* The rows themselves, by position, spaces and all: a form's border is an extent of
	 * spaces, and throwing that away is what made one unreadable. */
	json_key("lines");
	frame_text[frame_len++] = '[';
	for (r = 0; r < rows && frame_len + (size_t) strlen(line) + 24 < sizeof(frame_text); r++) {
		dumb_screen_row_text(r, line, (int) sizeof(line));
		if (r > 0)
			frame_text[frame_len++] = ',';
		frame_text[frame_len++] = '"';
		json_bytes(line, strlen(line));
		frame_text[frame_len++] = '"';
	}
	frame_text[frame_len++] = ']';

	json_key("changed");
	frame_text[frame_len++] = '[';
	for (r = 0; r < rows; r++) {
		if (r > 0)
			frame_text[frame_len++] = ',';
		frame_text[frame_len++] = dumb_screen_row_changed(r) ? '1' : '0';
	}
	frame_text[frame_len++] = ']';
	frame_end();
}

void dtool_quit(int status)
{
	if (!dtool_active())
		return;

	frame_begin("quit");
	frame_long("status", (long) status);
	frame_end();
}

void dtool_after_restart(void)
{
	if (!dtool_active())
		return;
	frame_begin("restart");
	frame_end();
}

const char *dtool_save_target(void)
{
	return save_target;
}

const char *dtool_restore_target(void)
{
	return restore_target;
}

void dtool_diagnostic(const char *format, ...)
{
	va_list ap;
	char text[512];

	va_start(ap, format);
	vsnprintf(text, sizeof(text), format, ap);
	va_end(ap);

	/* With a channel, stderr belongs to the channel: a host reading it expects a
	 * frame per line and nothing else. Without one this is an ordinary
	 * diagnostic, which is what upstream prints. */
	if (dtool_active()) {
		dtool_note("error", text);
		return;
	}
	fprintf(stderr, "%s\n", text);
}

/* --------------------------------------------------------------------------
 * Commands
 * ------------------------------------------------------------------------ */

static char *dupn(const char *s, size_t n)
{
	char *copy = malloc(n + 1);

	if (copy == NULL)
		return NULL;
	memcpy(copy, s, n);
	copy[n] = '\0';
	return copy;
}

static void set_target(char **slot, const char *value)
{
	if (*slot != NULL)
		free(*slot);
	*slot = dupn(value, strlen(value));
}

static void announce_targets(const char *verb)
{
	frame_begin("command");
	frame_str("verb", verb);
	frame_bool("ok", 1);
	frame_str("save", save_target);
	frame_str("restore", restore_target);
	frame_end();
}

/* Every command acknowledges itself exactly once.
 *
 * The host counts acknowledgements to know that the prompt it is about to be shown is a
 * refresh rather than the end of a turn -- a count that has to line up with the prompts, so
 * one command must never be silent and none may speak twice. `stats`, `ping`, `hello` and
 * `status` used to answer with their own event and no acknowledgement at all, which left the
 * host believing the next prompt ended a turn it had never begun. */
static void ack(const char *verb)
{
	frame_begin("command");
	frame_str("verb", verb);
	frame_bool("ok", 1);
	frame_end();
}

static const char *tool_usage =
	"DUMB-FROTZ tools (\\t <verb>):\n"
	"  \\t help              Show this message.\n"
	"  \\t hello             Repeat the handshake.\n"
	"  \\t save <path>       Send the next save to this file, with no question.\n"
	"  \\t restore <path>    Load from this file, with no question.\n"
	"  \\t target            Show the two paths in force.\n"
	"  \\t clear             Ask for a filename again, the old way.\n"
	"  \\t status            Report the state the status line is made of.\n"	"  \t screen            Report the screen itself, rows and cursor.\n"	"  \\t stats             Report what this channel has carried.\n"
	"  \\t ping              Answer with a pong, for a test.\n";

int dtool_command(const char *line)
{
	const char *arg = strchr(line, ' ');

	if (strcmp(line, "help") == 0) {
		dtool_count_fputs(tool_usage, stdout);
		fflush(stdout);
		announce_targets("help");
		return 1;
	}
	if (strcmp(line, "hello") == 0) {
		dtool_announce();
		ack("hello");
		return 1;
	}
	if (strcmp(line, "target") == 0) {
		announce_targets("target");
		return 1;
	}
	if (strcmp(line, "clear") == 0) {
		set_target(&save_target, "");
		set_target(&restore_target, "");
		free(save_target);
		free(restore_target);
		save_target = NULL;
		restore_target = NULL;
		announce_targets("clear");
		return 1;
	}
	if (strcmp(line, "stats") == 0) {
		frame_begin("stats");
		frame_long("frames", (long) frames);
		frame_long("dropped", (long) dropped);
		frame_long("out_bytes", (long) out_bytes);
		frame_bool("counts_trusted", byte_counts_trusted);
		frame_str("save", save_target);
		frame_str("restore", restore_target);
		frame_end();
		ack("stats");
		return 1;
	}
	if (strcmp(line, "ping") == 0) {
		frame_begin("pong");
		frame_long("out_bytes", (long) out_bytes);
		frame_end();
		ack("ping");
		return 1;
	}
	if (strcmp(line, "screen") == 0) {
		dtool_screen();
		ack("screen");
		return 1;
	}
	if (strcmp(line, "status") == 0) {		frame_begin("status");
		frame_long("zversion", (long) z_header.version);
		frame_bool("interpreter_keeps_it", z_header.version <= V3);
		if (z_header.version <= V3) {
			zword addr = z_header.globals;
			zword g0, g1, g2;

			LOW_WORD(addr, g0);
			addr += 2;
			LOW_WORD(addr, g1);
			addr += 2;
			LOW_WORD(addr, g2);

			frame_long("location", (long) g0);
			if (z_header.config & CONFIG_TIME) {
				frame_long("hours", (long) g1);
				frame_long("minutes", (long) g2);
			} else {
				frame_long("score", (long) g1);
				frame_long("moves", (long) g2);
			}
		}
		frame_end();
		ack("status");
		return 1;
	}
	if (arg != NULL) {
		size_t verb_len = (size_t) (arg - line);
		const char *value = arg + 1;

		if (value[0] == '\0') {
			dtool_note("error", "a tool command that wanted a path was given none");
			return 1;
		}
		if (verb_len == 4 && strncmp(line, "save", 4) == 0) {
			set_target(&save_target, value);
			announce_targets("save");
			return 1;
		}
		if (verb_len == 7 && strncmp(line, "restore", 7) == 0) {
			set_target(&restore_target, value);
			announce_targets("restore");
			return 1;
		}
	}

	/* A `\t` line we do not know is still ours: it must never fall through to
	 * the story, which would be the host's typo arriving as a game command. */
	dtool_diagnostic("unknown tool command: %.100s", line);
	ack("unknown");
	return 1;
}

/* -------------------------------------------------------------------------- */
