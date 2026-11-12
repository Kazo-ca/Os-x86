/* Os-x86 guest agent: user consoles (decisions 208 A and 213 A). One pty per console, its shell
 * or program running as the prefix user in a cgroup of its own; the bytes of every console
 * travel on one virtio-console port (hvc1), multiplexed by frames (correction 3 of 205 A):
 *
 *   0xAE  type  console(2, big endian)  length(2, big endian)  payload(length)
 *
 *   type 1 DATA   host -> guest: input for the pty; guest -> host: output of the pty
 *   type 2 CLOSED guest -> host only, after the last DATA of a console: payload = exit code and
 *                 signal of its main process (two int32, big endian; code -1 when signalled)
 *
 * The console number is the sequence number of its launch. A reader that finds anything else
 * than 0xAE where a frame should start skips bytes until the next 0xAE (resynchronisation).
 * Ctrl+C is the byte 0x03 in a DATA frame: the line discipline of the pty turns it into SIGINT
 * (trial E14), nothing else is needed. */
#define _GNU_SOURCE
#include "agent.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pty.h>
#include <pwd.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#define MUX_MAGIC 0xAE
#define MUX_DATA 1
#define MUX_CLOSED 2
#define MUX_HEADER 6
#define MUX_MAX_PAYLOAD 4096
#define PTY_WRITE_TIMEOUT_MS 200

console consoles[AGENT_MAX_CONSOLES];
int console_port_fd = -1;

int console_port_open(const char *device)
{
    int fd = open(device, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) { logmsg("console port %s: %s", device, strerror(errno)); return -1; }
    struct termios t;
    if (!tcgetattr(fd, &t)) {
        cfmakeraw(&t);
        tcsetattr(fd, TCSANOW, &t);
    }
    console_port_fd = fd;
    return 0;
}

int console_count(void)
{
    int n = 0;
    for (int i = 0; i < AGENT_MAX_CONSOLES; i++) n += consoles[i].used;
    return n;
}

console *console_by_seq(int seq)
{
    for (int i = 0; i < AGENT_MAX_CONSOLES; i++) if (consoles[i].used && consoles[i].seq == seq) return &consoles[i];
    return NULL;
}

static void send_frame(int type, int seq, const char *payload, size_t n)
{
    if (console_port_fd < 0) return;
    unsigned char frame[MUX_HEADER + MUX_MAX_PAYLOAD];
    frame[0] = MUX_MAGIC;
    frame[1] = (unsigned char)type;
    frame[2] = (unsigned char)(seq >> 8);
    frame[3] = (unsigned char)seq;
    frame[4] = (unsigned char)(n >> 8);
    frame[5] = (unsigned char)n;
    memcpy(frame + MUX_HEADER, payload, n);
    port_write_all(console_port_fd, (const char *)frame, MUX_HEADER + n);
}

/* ---------------------------------------------------------------- pty -> host */

/* Reads what the pty has (at most `rounds` buffers, so that a program that floods its terminal
 * cannot starve the loop) and sends it as DATA frames. Returns 0 when the pty is closed. */
static int relay_output(console *c, int rounds)
{
    char buf[MUX_MAX_PAYLOAD];
    while (rounds-- > 0 && c->pty >= 0) {
        ssize_t n = read(c->pty, buf, sizeof buf);
        if (n > 0) { send_frame(MUX_DATA, c->seq, buf, (size_t)n); continue; }
        if (n < 0 && (errno == EAGAIN || errno == EINTR)) return 1;
        /* EIO: every process closed the slave side. The console ends with its main process. */
        close(c->pty);
        c->pty = -1;
        return 0;
    }
    return 1;
}

void console_on_pty_readable(console *c)
{
    relay_output(c, 4);
}

/* The main process of the console was reaped: the rest of its output goes out, then the CLOSED
 * frame with the exit code (213 A: the code travels on the channel of the bytes, after them). */
void console_on_child_exit(console *c, int status)
{
    relay_output(c, 64);
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    int sig = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
    unsigned char payload[8] = {
        (unsigned char)((unsigned)code >> 24), (unsigned char)((unsigned)code >> 16), (unsigned char)((unsigned)code >> 8), (unsigned char)code,
        (unsigned char)((unsigned)sig >> 24), (unsigned char)((unsigned)sig >> 16), (unsigned char)((unsigned)sig >> 8), (unsigned char)sig,
    };
    send_frame(MUX_CLOSED, c->seq, (const char *)payload, sizeof payload);
    if (c->pty >= 0) close(c->pty);
    memset(c, 0, sizeof *c);
}

/* ---------------------------------------------------------------- host -> pty */

static void write_to_pty(int seq, const char *data, size_t n)
{
    console *c = console_by_seq(seq);
    if (!c || c->pty < 0) return;  /* a console that is gone: the bytes are dropped */
    double limit = now_s() + PTY_WRITE_TIMEOUT_MS / 1000.0;
    while (n) {
        ssize_t w = write(c->pty, data, n);
        if (w > 0) { data += w; n -= (size_t)w; continue; }
        if (w < 0 && errno == EINTR) continue;
        if (w < 0 && errno == EAGAIN) {
            int left = (int)((limit - now_s()) * 1000);
            if (left <= 0) { logmsg("console %d: input buffer full, %zu bytes dropped", seq, n); return; }
            struct pollfd p = { c->pty, POLLOUT, 0 };
            poll(&p, 1, left);
            continue;
        }
        return;
    }
}

/* The frame parser of the host -> guest direction: it keeps its state between reads, since a
 * frame may arrive in any number of pieces. */
static struct {
    unsigned char header[MUX_HEADER];
    size_t header_length;
    size_t payload_left;
    int type, seq;
} parser;

void console_port_on_readable(void)
{
    unsigned char buf[4096];
    ssize_t r = read(console_port_fd, buf, sizeof buf);
    if (r <= 0) return;
    size_t i = 0, n = (size_t)r;
    while (i < n) {
        if (parser.payload_left > 0) {
            size_t take = n - i < parser.payload_left ? n - i : parser.payload_left;
            if (parser.type == MUX_DATA) write_to_pty(parser.seq, (const char *)buf + i, take);
            i += take;
            parser.payload_left -= take;
            continue;
        }
        if (parser.header_length == 0 && buf[i] != MUX_MAGIC) { i++; continue; }
        parser.header[parser.header_length++] = buf[i++];
        if (parser.header_length == MUX_HEADER) {
            parser.type = parser.header[1];
            parser.seq = (parser.header[2] << 8) | parser.header[3];
            parser.payload_left = ((size_t)parser.header[4] << 8) | parser.header[5];
            parser.header_length = 0;
        }
    }
}

/* ---------------------------------------------------------------- operations */

static console *console_of_request(jn *req)
{
    jn *number = json_get(req, "console");
    if (number && number->type == J_NUM) return console_by_seq((int)number->num);
    launch *l = launch_find(json_str(req, "launch", NULL));
    return l ? console_by_seq(l->seq) : NULL;
}

/* open-console {prefix, program?, args?, env?, cwd?, cols?, rows?, nproc?}: a pty whose slave is
 * the controlling terminal of the program (default: the user's shell, interactive), as the
 * prefix user, in a cgroup of its own. */
void op_open_console(const char *id, jn *req)
{
    const char *prefix = json_str(req, "prefix", NULL);
    if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
    struct passwd *pw = prefix_user(prefix);
    if (!pw) return reply_error(id, "unknown-prefix", "no guest user " AGENT_USER_PREFIX "%s", prefix);
    if (console_port_fd < 0) return reply_error(id, "no-console-port", "the console port is not available in this guest");
    console *c = NULL;
    for (int i = 0; i < AGENT_MAX_CONSOLES && !c; i++) if (!consoles[i].used) c = &consoles[i];
    if (!c) return reply_error(id, "too-many-consoles", "the agent keeps at most %d consoles open", AGENT_MAX_CONSOLES);

    int cols = (int)json_num(req, "cols", 80), rows = (int)json_num(req, "rows", 25);
    if (cols < 1 || cols > 1000 || rows < 1 || rows > 1000) return reply_error(id, "invalid-request", "cols and rows must be between 1 and 1000");
    struct winsize size = { (unsigned short)rows, (unsigned short)cols, 0, 0 };
    int master, slave;
    if (openpty(&master, &slave, NULL, NULL, &size)) return reply_error(id, "pty-failed", "openpty: %s", strerror(errno));
    if (fchown(slave, pw->pw_uid, pw->pw_gid) || fchmod(slave, 0600)) {
        int e = errno;
        close(master);
        close(slave);
        return reply_error(id, "pty-failed", "cannot hand the pty to the user: %s", strerror(e));
    }
    fd_set_cloexec(master);
    fd_set_cloexec(slave);

    launch *l = launch_new(prefix, 1);
    const char *code = NULL;
    int failure = 0;
    if (!l) code = "too-many-launches";
    else if (cgroup_available && cgroup_make(l)) { code = "cgroup-failed"; failure = errno; }
    /* Without a program, the user's shell, interactive: argv = [shell, "-i"]. */
    const char *program = json_str(req, "program", NULL);
    jn interactive = { J_STR, NULL, (char *)"-i", 0, 0, NULL, NULL };
    jn default_args = { J_ARR, NULL, NULL, 0, 0, &interactive, NULL };
    jn *args = json_get(req, "args");
    if (!program || !*program) {
        program = pw->pw_shell && *pw->pw_shell ? pw->pw_shell : "/bin/sh";
        args = &default_args;
    }
    pid_t pid = -1;
    if (!code) {
        pid = spawn_as(pw, l, program, args, json_get(req, "env"), json_str(req, "cwd", NULL),
                       (int)json_num(req, "nproc", AGENT_DEFAULT_NPROC), -1, -1, -1, slave, &failure);
        if (pid < 0) code = failure == ENOENT ? "program-not-found" : "exec-failed";
    }
    close(slave);
    if (code) {
        close(master);
        if (l) launch_forget(l);
        return reply_error(id, code, "cannot open the console: %s", failure ? strerror(failure) : "no room left in the agent's tables");
    }
    l->pid = pid;
    launch_watch_cgroup(l);
    fd_set_nonblock(master);
    memset(c, 0, sizeof *c);
    c->used = 1;
    c->seq = l->seq;
    c->pty = master;
    c->pid = pid;
    char lid[64];
    launch_id(l, lid, sizeof lid);
    sb b = { 0 };
    reply_begin(&b, id);
    sb_puts(&b, ",\"ok\":true,\"launch\":");
    sb_jstrz(&b, lid);
    sb_fmt(&b, ",\"console\":%d,\"pid\":%d,\"uid\":%d,\"cols\":%d,\"rows\":%d}", c->seq, (int)pid, (int)pw->pw_uid, cols, rows);
    send_line(&b);
    logmsg("console %s pid=%d program=%s", lid, (int)pid, program);
}

/* close-console {console | launch, graceMs?, keep?: [pid, ...]} (208 A): the processes named in
 * `keep` (those that have a window, once the image has an X server) move to a sibling cgroup, a
 * detached launch of the same prefix that lives on; the rest of the console gets SIGHUP, then
 * cgroup.kill after the grace period. The reply is immediate; the CLOSED frame and the
 * console-closed event say when the console is over. */
void op_close_console(const char *id, jn *req)
{
    console *c = console_of_request(req);
    launch *l = c ? launch_by_seq(c->seq) : NULL;
    if (!c || !l) return reply_error(id, "unknown-console", "no such console");
    int kept = 0;
    char kept_id[64] = "";
    jn *keep = json_get(req, "keep");
    if (keep && keep->type == J_ARR && keep->child && cgroup_available) {
        launch *sibling = launch_new(l->prefix, 0);
        char path[300];
        if (sibling && !cgroup_make(sibling) && launch_cgroup(sibling, path, sizeof path)) {
            for (jn *p = keep->child; p; p = p->next) {
                pid_t pid = p->type == J_NUM ? (pid_t)p->num : 0;
                if (pid > 1 && pid != c->pid && launch_of_pid(pid) == l && !cgroup_move(path, pid)) kept++;
            }
            sibling->detached = 1;
            sibling->exited = 1;
            launch_watch_cgroup(sibling);
            launch_id(sibling, kept_id, sizeof kept_id);
            if (kept == 0) { launch_forget(sibling); kept_id[0] = 0; }
        } else if (sibling) {
            launch_forget(sibling);
        }
    }
    sb b = { 0 };
    reply_begin(&b, id);
    sb_fmt(&b, ",\"ok\":true,\"console\":%d,\"keptCount\":%d,\"kept\":", c->seq, kept);
    if (kept_id[0]) sb_jstrz(&b, kept_id); else sb_puts(&b, "null");
    sb_puts(&b, "}");
    send_line(&b);
    launch_terminate(l, SIGHUP, (int)json_num(req, "graceMs", AGENT_DEFAULT_GRACE_MS), NULL);
}

void op_console_resize(const char *id, jn *req)
{
    console *c = console_of_request(req);
    if (!c || c->pty < 0) return reply_error(id, "unknown-console", "no such console");
    int cols = (int)json_num(req, "cols", 0), rows = (int)json_num(req, "rows", 0);
    if (cols < 1 || cols > 1000 || rows < 1 || rows > 1000) return reply_error(id, "invalid-request", "cols and rows must be between 1 and 1000");
    struct winsize size = { (unsigned short)rows, (unsigned short)cols, 0, 0 };
    if (ioctl(c->pty, TIOCSWINSZ, &size)) return reply_error(id, "pty-failed", "TIOCSWINSZ: %s", strerror(errno));
    reply_ok(id, ",\"console\":%d,\"cols\":%d,\"rows\":%d", c->seq, cols, rows);
}
