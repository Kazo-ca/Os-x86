/* Os-x86 guest agent: the event loop, the protocol port (hvc0), replies and events, the SIGCHLD
 * pipe, the open-url socket and the timers. See agent.h for the whole picture.
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
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/sysinfo.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

static int port_in = -1, port_out = -1;
static int sigchld_pipe[2] = { -1, -1 };
static int url_socket = -1;
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

/* Writes everything, waiting for the port to drain when it is full; gives up (and logs) after
 * AGENT_PORT_WRITE_TIMEOUT_MS so that a host that stopped reading cannot freeze the agent. */
void port_write_all(int fd, const char *s, size_t n)
{
    double limit = now_s() + AGENT_PORT_WRITE_TIMEOUT_MS / 1000.0;
    while (n) {
        ssize_t w = write(fd, s, n);
        if (w > 0) { s += w; n -= (size_t)w; continue; }
        if (w < 0 && errno == EINTR) continue;
        if (w < 0 && errno == EAGAIN) {
            int left = (int)((limit - now_s()) * 1000);
            if (left <= 0) { logmsg("port write: timeout, %zu bytes dropped", n); return; }
            struct pollfd p = { fd, POLLOUT, 0 };
            poll(&p, 1, left);
            continue;
        }
        logmsg("port write: %s", strerror(errno));
        return;
    }
}

void send_line(sb *b)
{
    sb_puts(b, "\n");
    if (b->buf) port_write_all(port_out, b->buf, b->len);
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
    else if (id && id->type == J_STR) {
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
    reply_ok(id, ",\"version\":%d,\"uptime\":%ld,\"rssKb\":%ld,\"launches\":%d,\"consoles\":%d,\"cgroup\":%s",
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
        if (r == 0 && port_in == 0) { logmsg("stdin closed"); exit(0); }
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

/* ---------------------------------------------------------------- open-url (decision 139, trial E7) */

static int url_socket_open(const char *path)
{
    unlink(path);
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    struct sockaddr_un address;
    memset(&address, 0, sizeof address);
    address.sun_family = AF_UNIX;
    snprintf(address.sun_path, sizeof address.sun_path, "%s", path);
    if (bind(fd, (struct sockaddr *)&address, sizeof address) || listen(fd, 8)) {
        logmsg("open-url socket %s: %s", path, strerror(errno));
        close(fd);
        return -1;
    }
    chmod(path, 0666);
    return fd;
}

void url_socket_on_readable(int sock_fd)
{
    int client = accept4(sock_fd, NULL, NULL, SOCK_CLOEXEC);
    if (client < 0) return;
    struct ucred credentials;
    socklen_t length = sizeof credentials;
    memset(&credentials, 0, sizeof credentials);
    getsockopt(client, SOL_SOCKET, SO_PEERCRED, &credentials, &length);
    char buf[2048];
    int n = 0;
    struct pollfd p = { client, POLLIN, 0 };
    if (poll(&p, 1, 500) > 0) n = (int)read(client, buf, sizeof buf - 1);
    if (n < 0) n = 0;
    buf[n] = 0;
    char *newline = strchr(buf, '\n');
    if (newline) *newline = 0;
    close(client);
    char prefix[AGENT_MAX_PREFIX + 1];
    const char *owner = uid_prefix(credentials.uid, prefix, sizeof prefix);
    sb b = { 0 };
    event_begin(&b, "open-url");
    sb_fmt(&b, ",\"uid\":%d,\"pid\":%d,\"prefix\":", (int)credentials.uid, (int)credentials.pid);
    if (owner) sb_jstrz(&b, owner); else sb_puts(&b, "null");
    sb_puts(&b, ",\"url\":");
    sb_jstrz(&b, buf);
    sb_puts(&b, "}");
    send_line(&b);
    /* The agent does nothing else with the URL: the host decides (139). */
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
    sb_fmt(&b, ",\"version\":%d,\"pid\":%d,\"cgroup\":%s,\"port\":", AGENT_PROTOCOL_VERSION, (int)getpid(), cgroup_available ? "true" : "false");
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
    if (log_file) fd_set_cloexec(fileno(log_file));
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
    if (strcmp(console_port, "none")) console_port_open(console_port);
    url_socket = url_socket_open(socket_path);
    logmsg("ready port=%s console=%s cgroup=%d socket=%d", port_name, console_port, cgroup_available, url_socket >= 0);
    send_ready(console_port);

    for (;;) {
        struct pollfd p[8 + AGENT_MAX_LAUNCHES + AGENT_MAX_CONSOLES + AGENT_MAX_JOBS];
        int n = 0;
        p[n++] = (struct pollfd){ port_in, POLLIN, 0 };
        p[n++] = (struct pollfd){ sigchld_pipe[0], POLLIN, 0 };
        int i_socket = -1, i_console_port = -1, i_consoles = -1, i_jobs = -1, i_launches = -1;
        if (url_socket >= 0) { i_socket = n; p[n++] = (struct pollfd){ url_socket, POLLIN, 0 }; }
        if (console_port_fd >= 0 && console_count() > 0) { i_console_port = n; p[n++] = (struct pollfd){ console_port_fd, POLLIN, 0 }; }
        i_consoles = n;
        for (int i = 0; i < AGENT_MAX_CONSOLES; i++) p[n++] = (struct pollfd){ consoles[i].used ? consoles[i].pty : -1, POLLIN, 0 };
        i_jobs = n;
        for (int i = 0; i < AGENT_MAX_JOBS; i++) p[n++] = (struct pollfd){ jobs[i].used ? jobs[i].fd : -1, POLLIN, 0 };
        i_launches = n;
        for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) p[n++] = (struct pollfd){ launches[i].used ? launches[i].events_fd : -1, POLLPRI, 0 };

        double next = launch_next_deadline(), job_next = job_next_deadline();
        if (job_next > 0 && (next <= 0 || job_next < next)) next = job_next;
        int timeout = next > 0 ? (int)((next - now_s()) * 1000) + 1 : -1;
        if (timeout < 0 && next > 0) timeout = 0;
        if (poll(p, (nfds_t)n, timeout) < 0) {
            if (errno == EINTR) continue;
            logmsg("poll: %s", strerror(errno));
            break;
        }
        double now = now_s();
        if (p[0].revents & (POLLIN | POLLHUP | POLLERR)) port_on_readable();
        if (p[1].revents & POLLIN) {
            char drain[64];
            while (read(sigchld_pipe[0], drain, sizeof drain) > 0) { }
            reap_children();
        }
        if (i_socket >= 0 && (p[i_socket].revents & POLLIN)) url_socket_on_readable(url_socket);
        if (i_console_port >= 0 && (p[i_console_port].revents & (POLLIN | POLLHUP))) console_port_on_readable();
        for (int i = 0; i < AGENT_MAX_CONSOLES; i++)
            if (consoles[i].used && (p[i_consoles + i].revents & (POLLIN | POLLHUP | POLLERR))) console_on_pty_readable(&consoles[i]);
        for (int i = 0; i < AGENT_MAX_JOBS; i++)
            if (jobs[i].used && (p[i_jobs + i].revents & (POLLIN | POLLHUP | POLLERR))) job_on_readable(&jobs[i]);
        for (int i = 0; i < AGENT_MAX_LAUNCHES; i++)
            if (launches[i].used && (p[i_launches + i].revents & (POLLPRI | POLLERR))) launch_on_cgroup_event(&launches[i]);
        launch_on_timer(now);
        job_on_timer(now);
    }
    return 0;
}
