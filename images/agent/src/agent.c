/* Os-x86 guest agent: the event loop, the protocol port (hvc0), replies and events, the SIGCHLD
 * pipe and the timers. See agent.h for the whole picture.
 *
 * Request  {"id":N,"op":"...", ...}\n      N: number or string, echoed back as is.
 * Reply    {"id":N,"ok":true, ...}\n        or {"id":N,"ok":false,"error":"<code>","message":"..."}\n
 * Event    {"event":"ready"|"exit"|"open-url"|"console-closed", ...}\n
 *
 * Options: -p <port> (default /dev/hvc0; "-" = stdin and stdout, for the smoke test of the
 * recipe), -c <console port> (default /dev/hvc1; "none" disables consoles), -s <socket path>,
 * -l <log path>, -g <cgroup root>.
 */
#define _GNU_SOURCE
#include "agent.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

static int port_in = -1, port_out = -1;
static int sigchld_pipe[2] = { -1, -1 };
static FILE *log_file;
static const char *port_name = AGENT_PORT;

/* ---------------------------------------------------------------- small helpers */

double now_s(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec / 1e9;
}

void logmsg(const char *fmt, ...)
{
    if (!log_file) return;
    va_list ap;
    va_start(ap, fmt);
    fprintf(log_file, "%.3f ", now_s());
    vfprintf(log_file, fmt, ap);
    fputc('\n', log_file);
    fflush(log_file);
    va_end(ap);
}

int fd_set_nonblock(int fd)
{
    int flags = fcntl(fd, F_GETFL);
    return flags < 0 ? -1 : fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

int fd_set_cloexec(int fd)
{
    int flags = fcntl(fd, F_GETFD);
    return flags < 0 ? -1 : fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
}

outq port_queue = { -1, NULL, 0, 0, AGENT_PORT_QUEUE_MAX, 0 };
outq console_queue = { -1, NULL, 0, 0, AGENT_CONSOLE_QUEUE_MAX, 0 };

void outq_flush(outq *q)
{
    size_t done = 0;
    while (done < q->len) {
        ssize_t w = write(q->fd, q->buf + done, q->len - done);
        if (w > 0) { done += (size_t)w; continue; }
        if (w < 0 && errno == EINTR) continue;
        if (w < 0 && errno != EAGAIN) {
            /* The port is broken: keeping the bytes would only fill the memory. */
            logmsg("port write: %s, %zu bytes dropped", strerror(errno), q->len - done);
            done = q->len;
        }
        break;
    }
    if (done) {
        memmove(q->buf, q->buf + done, q->len - done);
        q->len -= done;
    }
}

int outq_put(outq *q, const char *data, size_t n, int force)
{
    if (q->fd < 0 || (!force && q->len + n > q->limit)) { q->dropped++; return -1; }
    if (q->len + n > q->cap) {
        size_t cap = q->cap ? q->cap : 4096;
        while (cap < q->len + n) cap *= 2;
        char *grown = realloc(q->buf, cap);
        if (!grown) { q->dropped++; return -1; }
        q->buf = grown;
        q->cap = cap;
    }
    memcpy(q->buf + q->len, data, n);
    q->len += n;
    outq_flush(q);
    return 0;
}

void send_line(sb *b)
{
    sb_puts(b, "\n");
    if (b->buf && outq_put(&port_queue, b->buf, b->len, 0)) logmsg("protocol port full: a line of %zu bytes dropped", b->len);
    sb_free(b);
}

void reply_begin(sb *b, const char *id_json)
{
    sb_puts(b, "{\"id\":");
    sb_puts(b, id_json && *id_json ? id_json : "null");
}

void reply_ok(const char *id_json, const char *body_fmt, ...)
{
    sb b = { 0 };
    reply_begin(&b, id_json);
    sb_puts(&b, ",\"ok\":true");
    if (body_fmt && *body_fmt) {
        char tmp[4096];
        va_list ap;
        va_start(ap, body_fmt);
        vsnprintf(tmp, sizeof tmp, body_fmt, ap);
        va_end(ap);
        sb_puts(&b, tmp);
    }
    sb_puts(&b, "}");
    send_line(&b);
}

void reply_error(const char *id_json, const char *code, const char *fmt, ...)
{
    sb b = { 0 };
    reply_begin(&b, id_json);
    sb_puts(&b, ",\"ok\":false,\"error\":");
    sb_jstrz(&b, code);
    sb_puts(&b, ",\"message\":");
    char tmp[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(tmp, sizeof tmp, fmt, ap);
    va_end(ap);
    sb_jstrz(&b, tmp);
    sb_puts(&b, "}");
    send_line(&b);
}

void event_begin(sb *b, const char *name)
{
    sb_puts(b, "{\"event\":");
    sb_jstrz(b, name);
}

/* The id of a request as JSON text (a number printed without decimals, or a string), kept for
 * the deferred replies. "null" when the request has none. */
static void request_id(jn *req, char *out, size_t n)
{
    jn *id = json_get(req, "id");
    if (id && id->type == J_NUM) snprintf(out, n, "%.0f", id->num);
    else if (id && id->type == J_STR && strlen(id->str) <= 24) {  /* escaped, it still fits the buffers of the deferred replies */
        sb b = { 0 };
        sb_jstrz(&b, id->str);
        snprintf(out, n, "%s", b.buf ? b.buf : "null");
        sb_free(&b);
    } else snprintf(out, n, "null");
}

/* ---------------------------------------------------------------- requests */

static void op_ping(const char *id, jn *req)
{
    (void)req;
    struct sysinfo si;
    sysinfo(&si);
    long rss = 0;
    FILE *f = fopen("/proc/self/status", "r");
    char line[256];
    while (f && fgets(line, sizeof line, f)) if (!strncmp(line, "VmRSS:", 6)) rss = atol(line + 6);
    if (f) fclose(f);
    reply_ok(id, ",\"version\":%d,\"uptime\":%ld,\"rssKb\":%ld,\"launches\":%d,\"consoles\":%d,\"cgroups\":%s",
             AGENT_PROTOCOL_VERSION, si.uptime, rss, launch_count(), console_count(), cgroup_available ? "true" : "false");
}

static void op_list_windows(const char *id, jn *req)
{
    (void)req;
    /* The image has no X server yet (work item 9 brings one): the permanent X connection of
     * 205 A comes with it. Until then the answer is explicit rather than an empty list. */
    reply_error(id, "no-x-server", "this image has no X server: windows cannot be listed");
}

static void handle_request(char *line)
{
    jn *req = json_parse(line);
    if (!req || req->type != J_OBJ) {
        reply_error("null", "invalid-json", "the request is not a JSON object");
        return;
    }
    char id[64];
    request_id(req, id, sizeof id);
    const char *op = json_str(req, "op", "");
    double t0 = now_s();
    if (!strcmp(op, "ping")) op_ping(id, req);
    else if (!strcmp(op, "list-processes")) op_list_processes(id, req);
    else if (!strcmp(op, "launch")) op_launch(id, req);
    else if (!strcmp(op, "kill")) op_kill(id, req);
    else if (!strcmp(op, "list-windows")) op_list_windows(id, req);
    else if (!strcmp(op, "mounts")) op_mounts(id, req);
    else if (!strcmp(op, "exec-in-prefix")) op_exec_in_prefix(id, req);
    else if (!strcmp(op, "open-console")) op_open_console(id, req);
    else if (!strcmp(op, "close-console")) op_close_console(id, req);
    else if (!strcmp(op, "console-resize")) op_console_resize(id, req);
    else reply_error(id, "unknown-op", "unknown operation '%s'", op);
    logmsg("op %s id=%s %.1f ms", op, id, (now_s() - t0) * 1000);
}

static char line_buffer[AGENT_MAX_LINE];
static size_t line_length;
static int line_overflow;

static void port_on_readable(void)
{
    char buf[4096];
    ssize_t r = read(port_in, buf, sizeof buf);
    if (r <= 0) {
        if (r == 0 && port_in == 0) { logmsg("stdin closed"); outq_flush(&port_queue); exit(0); }
        /* A port that reports an error or a hang-up on every poll must not spin the loop. */
        if (r == 0 || (errno != EAGAIN && errno != EINTR)) usleep(20000);
        return;
    }
    for (ssize_t i = 0; i < r; i++) {
        if (buf[i] == '\n') {
            if (line_overflow) reply_error("null", "line-too-long", "a request longer than %d bytes was dropped", AGENT_MAX_LINE);
            else if (line_length) { line_buffer[line_length] = 0; handle_request(line_buffer); }
            line_length = 0;
            line_overflow = 0;
        } else if (line_length < sizeof line_buffer - 1) {
            line_buffer[line_length++] = buf[i];
        } else {
            line_overflow = 1;
        }
    }
}

/* ---------------------------------------------------------------- children */

static void on_sigchld(int sig)
{
    (void)sig;
    int saved = errno;
    if (write(sigchld_pipe[1], "c", 1) < 0) { /* the pipe is full: a wake-up is already pending */ }
    errno = saved;
}

static void reap_children(void)
{
    int status;
    pid_t pid;
    while ((pid = waitpid(-1, &status, WNOHANG)) > 0) {
        /* A job replies first (exec-in-prefix carries the exit code), then its launch settles. */
        job_on_child_exit(pid, status);
        launch *l = launch_by_pid(pid);
        if (!l) continue;  /* an orphan adopted as subreaper: reaped, not reported (its cgroup tracks it, 206 A) */
        if (l->is_console) {
            console *c = console_by_seq(l->seq);
            if (c) console_on_child_exit(c, status);
        }
        launch_on_exit(l, status);
    }
}

/* ---------------------------------------------------------------- main loop */

static int open_port_raw(const char *device)
{
    int fd = open(device, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return -1;
    struct termios t;
    if (!tcgetattr(fd, &t)) {
        cfmakeraw(&t);
        tcsetattr(fd, TCSANOW, &t);
    }
    return fd;
}

static void send_ready(const char *console_port)
{
    sb b = { 0 };
    event_begin(&b, "ready");
    sb_fmt(&b, ",\"version\":%d,\"pid\":%d,\"cgroups\":%s,\"port\":", AGENT_PROTOCOL_VERSION, (int)getpid(), cgroup_available ? "true" : "false");
    sb_jstrz(&b, port_name);
    sb_puts(&b, ",\"consolePort\":");
    if (console_port_fd >= 0) sb_jstrz(&b, console_port); else sb_puts(&b, "null");
    sb_puts(&b, "}");
    send_line(&b);
}

int main(int argc, char **argv)
{
    const char *console_port = AGENT_CONSOLE_PORT, *socket_path = AGENT_URL_SOCKET, *log_path = AGENT_LOG;
    const char *cgroup_root = AGENT_CGROUP_ROOT;
    for (int i = 1; i + 1 < argc; i++) {
        if (!strcmp(argv[i], "-p")) port_name = argv[++i];
        else if (!strcmp(argv[i], "-c")) console_port = argv[++i];
        else if (!strcmp(argv[i], "-s")) socket_path = argv[++i];
        else if (!strcmp(argv[i], "-l")) log_path = argv[++i];
        else if (!strcmp(argv[i], "-g")) cgroup_root = argv[++i];
    }
    mkdir(AGENT_RUN_DIR, 0755);
    log_file = fopen(log_path, "a");
    if (log_file) {
        fd_set_cloexec(fileno(log_file));
        fchmod(fileno(log_file), 0600);  /* it names every program launched: root only */
    }
    signal(SIGPIPE, SIG_IGN);
    signal(SIGHUP, SIG_IGN);
    prctl(PR_SET_CHILD_SUBREAPER, 1);  /* orphans of the launched programs come back here */

    if (pipe2(sigchld_pipe, O_NONBLOCK | O_CLOEXEC)) { perror("pipe"); return 1; }
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_sigchld;
    sa.sa_flags = SA_RESTART | SA_NOCLDSTOP;
    sigaction(SIGCHLD, &sa, NULL);

    cgroup_root_path = cgroup_root;
    /* cgroup v2 is there when its root is mounted and the agent can make its own subtree;
     * otherwise (a container, an old kernel) launches fall back to process groups. */
    struct stat st;
    cgroup_available = 0;
    if (!stat("/sys/fs/cgroup/cgroup.procs", &st) && (!mkdir(cgroup_root, 0755) || errno == EEXIST)) {
        char probe[300];
        snprintf(probe, sizeof probe, "%s/cgroup.procs", cgroup_root);
        cgroup_available = !stat(probe, &st);
    }

    if (!strcmp(port_name, "-")) {
        port_in = 0;
        port_out = 1;
        fd_set_nonblock(port_in);
    } else {
        port_in = port_out = open_port_raw(port_name);
        if (port_in < 0) { fprintf(stderr, "agent: %s: %s\n", port_name, strerror(errno)); logmsg("port %s: %s", port_name, strerror(errno)); return 1; }
    }
    port_queue.fd = port_out;
    if (strcmp(console_port, "none")) console_port_open(console_port);
    url_socket_open(socket_path);
    logmsg("ready port=%s console=%s cgroup=%d socket=%d", port_name, console_port, cgroup_available, url_socket_fd >= 0);
    send_ready(console_port);

    for (;;) {
        struct pollfd p[8 + URL_MAX_CLIENTS + AGENT_MAX_LAUNCHES + AGENT_MAX_CONSOLES + AGENT_MAX_JOBS];
        int n = 0;
        p[n++] = (struct pollfd){ port_in, POLLIN, 0 };
        p[n++] = (struct pollfd){ sigchld_pipe[0], POLLIN, 0 };
        int i_socket = -1, i_console_port = -1, i_consoles = -1, i_jobs = -1, i_launches = -1, i_urls = -1;
        int i_port_out = -1, i_console_out = -1;
        if (port_queue.len > 0) { i_port_out = n; p[n++] = (struct pollfd){ port_out, POLLOUT, 0 }; }
        if (console_queue.len > 0) { i_console_out = n; p[n++] = (struct pollfd){ console_port_fd, POLLOUT, 0 }; }
        if (url_socket_fd >= 0) { i_socket = n; p[n++] = (struct pollfd){ url_socket_fd, POLLIN, 0 }; }
        if (console_port_fd >= 0 && console_count() > 0) { i_console_port = n; p[n++] = (struct pollfd){ console_port_fd, POLLIN, 0 }; }
        i_consoles = n;
        /* Flow control: while the host has not taken what the consoles already wrote, their ptys
         * are not read, so their programs wait instead of the agent dropping or blocking. */
        int read_ptys = console_queue.len < AGENT_CONSOLE_QUEUE_HIGH;
        for (int i = 0; i < AGENT_MAX_CONSOLES; i++) p[n++] = (struct pollfd){ consoles[i].used && read_ptys ? consoles[i].pty : -1, POLLIN, 0 };
        i_jobs = n;
        for (int i = 0; i < AGENT_MAX_JOBS; i++) p[n++] = (struct pollfd){ jobs[i].used ? jobs[i].fd : -1, POLLIN, 0 };
        i_urls = n;
        for (int i = 0; i < URL_MAX_CLIENTS; i++) p[n++] = (struct pollfd){ url_clients[i].used ? url_clients[i].fd : -1, POLLIN, 0 };
        i_launches = n;
        for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) p[n++] = (struct pollfd){ launches[i].used ? launches[i].events_fd : -1, POLLPRI, 0 };

        double next = launch_next_deadline();
        double others[2] = { job_next_deadline(), url_next_deadline() };
        for (int i = 0; i < 2; i++) if (others[i] > 0 && (next <= 0 || others[i] < next)) next = others[i];
        int timeout = -1;
        if (next > 0) {
            double wait_ms = (next - now_s()) * 1000;
            timeout = wait_ms <= 0 ? 0 : wait_ms >= 60000 ? 60000 : (int)wait_ms + 1;
        }
        if (poll(p, (nfds_t)n, timeout) < 0) {
            if (errno == EINTR) continue;
            logmsg("poll: %s", strerror(errno));
            break;
        }
        double now = now_s();
        if (i_port_out >= 0 && (p[i_port_out].revents & (POLLOUT | POLLERR))) outq_flush(&port_queue);
        if (i_console_out >= 0 && (p[i_console_out].revents & (POLLOUT | POLLERR))) outq_flush(&console_queue);
        if (p[0].revents & (POLLIN | POLLHUP | POLLERR)) port_on_readable();
        if (p[1].revents & POLLIN) {
            char drain[64];
            while (read(sigchld_pipe[0], drain, sizeof drain) > 0) { }
            reap_children();
        }
        if (i_socket >= 0 && (p[i_socket].revents & POLLIN)) url_on_accept();
        for (int i = 0; i < URL_MAX_CLIENTS; i++)
            if (url_clients[i].used && (p[i_urls + i].revents & (POLLIN | POLLHUP | POLLERR))) url_client_on_readable(&url_clients[i]);
        if (i_console_port >= 0 && (p[i_console_port].revents & (POLLIN | POLLHUP))) console_port_on_readable();
        for (int i = 0; i < AGENT_MAX_CONSOLES; i++)
            if (consoles[i].used && (p[i_consoles + i].revents & (POLLIN | POLLHUP | POLLERR))) console_on_pty_readable(&consoles[i]);
        for (int i = 0; i < AGENT_MAX_JOBS; i++)
            if (jobs[i].used && (p[i_jobs + i].revents & (POLLIN | POLLHUP | POLLERR))) job_on_readable(&jobs[i]);
        for (int i = 0; i < AGENT_MAX_LAUNCHES; i++)
            if (launches[i].used && (p[i_launches + i].revents & (POLLPRI | POLLERR))) launch_on_cgroup_event(&launches[i]);
        launch_on_timer(now);
        job_on_timer(now);
        url_on_timer(now);
    }
    return 0;
}
