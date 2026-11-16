/* Os-x86 guest agent, protocol 2 (milestone 0, work item 10; decisions 152 A, 193 A, 200 B): the
 * prefixes of the guest and the files Os-x86 hands to them.
 *
 *   prefix-ensure  the guest user osx86-<prefix> and its files exist (created when missing)
 *   prefix-delete  the prefix's processes are killed, its user and its home removed
 *   prefix-list    the guest users of the prefixes
 *   put-file       writes bytes into a file of the prefix's home, as the prefix's user
 *   sync           flushes the guest's file systems to the disk (before the VM stops)
 *
 * The agent never edits /etc/passwd itself and never runs a shell line: prefix-ensure and
 * prefix-delete run the image's fixed script /usr/lib/osx86/prefix-admin (root, owned by root,
 * with the prefix name already checked), in a child, like exec-in-prefix (the loop is never
 * blocked). put-file writes in a child that has become the prefix's user before it opens anything:
 * the kernel's permissions are the boundary, so a path cannot reach what that user cannot. */
#define _GNU_SOURCE
#include "agent.h"

#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <pwd.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#define PREFIX_ADMIN "/usr/lib/osx86/prefix-admin"
#define PREFIX_ADMIN_TIMEOUT_MS 180000     /* copying the Wine template takes 4 to 6 s in the VM */
#define PREFIX_ADMIN_UNCHANGED 10          /* exit code of prefix-admin: nothing to do */
#define PUT_TIMEOUT_MS 30000
#define SYNC_TIMEOUT_MS 60000
#define PUT_MAX_PATH 1024

enum { ACTION_ENSURE = 1, ACTION_DELETE = 2 };

/* ---------------------------------------------------------------- base64 */

static int b64_value(int c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

/* Decodes standard base64 (padding optional, no blanks). Returns the length, or -1. */
static long b64_decode(const char *in, unsigned char *out, size_t cap)
{
    size_t n = strlen(in), o = 0;
    while (n > 0 && in[n - 1] == '=') n--;
    if (n % 4 == 1) return -1;
    unsigned int acc = 0;
    int bits = 0;
    for (size_t i = 0; i < n; i++) {
        int v = b64_value((unsigned char)in[i]);
        if (v < 0) return -1;
        acc = ((acc << 6) | (unsigned int)v) & 0xffffff;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (o >= cap) return -1;
            out[o++] = (unsigned char)(acc >> bits);
        }
    }
    return (long)o;
}

/* ---------------------------------------------------------------- the reply of a finished job */

static void put_error(sb *b, const char *code, const char *message)
{
    sb_puts(b, ",\"ok\":false,\"error\":");
    sb_jstrz(b, code);
    sb_puts(b, ",\"message\":");
    sb_jstrz(b, message);
    sb_puts(b, "}");
}

/* The last line of a child's output, for an error message (bounded). */
static void last_line(const job *j, char *out, size_t n)
{
    const char *text = j->out.buf ? j->out.buf : "";
    size_t len = j->out.len;
    while (len > 0 && (text[len - 1] == '\n' || text[len - 1] == '\r')) len--;
    size_t start = len;
    while (start > 0 && text[start - 1] != '\n') start--;
    size_t k = len - start;
    if (k >= n) k = n - 1;
    memcpy(out, text + start, k);
    out[k] = 0;
}

static void ensure_body(sb *b, const char *prefix, int created, double ms)
{
    struct passwd *pw = prefix_user(prefix);
    if (!pw) return put_error(b, "prefix-failed", "the guest user of the prefix does not exist after prefix-admin");
    sb_fmt(b, ",\"ok\":true,\"created\":%s,\"prefix\":", created ? "true" : "false");
    sb_jstrz(b, prefix);
    sb_fmt(b, ",\"uid\":%d,\"home\":", (int)pw->pw_uid);
    sb_jstrz(b, pw->pw_dir);
    sb_fmt(b, ",\"ms\":%.1f}", ms);
}

void prefix_job_reply(job *j, int status, sb *b)
{
    double ms = (now_s() - j->t0) * 1000;
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    char message[400];
    if (j->kind == JOB_SYNC) {
        sb_fmt(b, ",\"ok\":true,\"timedOut\":%s,\"ms\":%.1f}", j->timed_out ? "true" : "false", ms);
        return;
    }
    if (j->kind == JOB_PUT) {
        if (j->timed_out) return put_error(b, "put-failed", "the write did not finish before its deadline");
        if (code != 0) {
            snprintf(message, sizeof message, "cannot write %s: %s", j->path, code > 0 ? strerror(code) : "the writer was killed");
            return put_error(b, "put-failed", message);
        }
        sb_fmt(b, ",\"ok\":true,\"size\":%lld,\"ms\":%.1f}", j->put_end, ms);
        return;
    }
    /* JOB_PREFIX: the exit code of prefix-admin (0 done, 10 nothing to do). */
    if (j->timed_out || (code != 0 && code != PREFIX_ADMIN_UNCHANGED)) {
        char tail[300];
        last_line(j, tail, sizeof tail);
        snprintf(message, sizeof message, "prefix-admin %s %s %s: %s", j->action == ACTION_ENSURE ? "ensure" : "delete", j->prefix,
                 j->timed_out ? "timed out" : "failed", *tail ? tail : "no output");
        return put_error(b, "prefix-failed", message);
    }
    if (j->action == ACTION_DELETE) {
        sb_fmt(b, ",\"ok\":true,\"deleted\":%s,\"ms\":%.1f}", code == 0 ? "true" : "false", ms);
        return;
    }
    ensure_body(b, j->prefix, code == 0, ms);
}

/* ---------------------------------------------------------------- prefix-ensure, prefix-delete */

static int admin_available(void)
{
    struct stat st;
    return stat(PREFIX_ADMIN, &st) == 0 && S_ISREG(st.st_mode) && st.st_uid == 0 && !(st.st_mode & 022) && (st.st_mode & 0100);
}

static void run_admin(const char *id, jn *req, int action)
{
    const char *prefix = json_str(req, "prefix", NULL);
    if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
    if (!admin_available()) {
        /* An image without the script (the Alpine test image): its prefixes are the users it ships. */
        if (action == ACTION_ENSURE && prefix_user(prefix)) {
            sb b = { 0 };
            reply_begin(&b, id);
            ensure_body(&b, prefix, 0, 0);
            send_line(&b);
            return;
        }
        return reply_error(id, "no-prefix-support", "this image has no %s: its prefixes cannot be created or deleted", PREFIX_ADMIN);
    }
    if (action == ACTION_DELETE && cgroup_available) {
        /* Everything the prefix runs dies first, at once (206 A): its home goes next. */
        char path[300];
        snprintf(path, sizeof path, "%s/%s", cgroup_root_path, prefix);
        if (access(path, F_OK) == 0) cgroup_kill(path);
    }
    job *j = job_new(JOB_PREFIX, id, PREFIX_ADMIN_TIMEOUT_MS, 16 * 1024);
    if (!j) return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS);
    j->action = action;
    snprintf(j->prefix, sizeof j->prefix, "%s", prefix);
    int pipe_fds[2];
    if (pipe2(pipe_fds, O_CLOEXEC)) { memset(j, 0, sizeof *j); return reply_error(id, "exec-failed", "pipe: %s", strerror(errno)); }
    pid_t pid = fork();
    if (pid < 0) {
        int e = errno;
        close(pipe_fds[0]);
        close(pipe_fds[1]);
        memset(j, 0, sizeof *j);
        return reply_error(id, "exec-failed", "fork: %s", strerror(e));
    }
    if (pid == 0) {
        setsid();
        for (int s = 1; s < NSIG; s++) signal(s, SIG_DFL);
        sigset_t none;
        sigemptyset(&none);
        sigprocmask(SIG_SETMASK, &none, NULL);
        int null_fd = open("/dev/null", O_RDONLY);
        if (null_fd >= 0) dup2(null_fd, 0);
        dup2(pipe_fds[1], 1);
        dup2(pipe_fds[1], 2);
        for (int fd = 3; fd < 1024; fd++) close(fd);
        char *const argv[] = { (char *)PREFIX_ADMIN, (char *)(action == ACTION_ENSURE ? "ensure" : "delete"), (char *)prefix, NULL };
        char *const envp[] = { (char *)"PATH=/usr/sbin:/usr/bin:/sbin:/bin", (char *)"HOME=/root", (char *)"LC_ALL=C", NULL };
        execve(PREFIX_ADMIN, argv, envp);
        _exit(127);
    }
    close(pipe_fds[1]);
    j->pid = pid;
    j->fd = pipe_fds[0];
    fd_set_nonblock(j->fd);
}

void op_prefix_ensure(const char *id, jn *req) { run_admin(id, req, ACTION_ENSURE); }

void op_prefix_delete(const char *id, jn *req) { run_admin(id, req, ACTION_DELETE); }

/* The guest users of prefixes, read from the user database (no child: it is a small file). */
void op_prefix_list(const char *id, jn *req)
{
    (void)req;
    sb b = { 0 };
    reply_begin(&b, id);
    sb_puts(&b, ",\"ok\":true,\"prefixes\":[");
    size_t user_prefix = strlen(AGENT_USER_PREFIX);
    int first = 1;
    setpwent();
    struct passwd *pw;
    while ((pw = getpwent()) != NULL) {
        if (strncmp(pw->pw_name, AGENT_USER_PREFIX, user_prefix) || pw->pw_uid == 0 || !valid_prefix(pw->pw_name + user_prefix)) continue;
        if (!first) sb_puts(&b, ",");
        first = 0;
        sb_puts(&b, "{\"prefix\":");
        sb_jstrz(&b, pw->pw_name + user_prefix);
        sb_fmt(&b, ",\"uid\":%d,\"home\":", (int)pw->pw_uid);
        sb_jstrz(&b, pw->pw_dir);
        sb_puts(&b, "}");
    }
    endpwent();
    sb_fmt(&b, "],\"admin\":%s}", admin_available() ? "true" : "false");
    send_line(&b);
}

/* ---------------------------------------------------------------- put-file */

/* A path relative to the home: no leading slash, no empty, "." or ".." component, no control
 * character. What it may reach is still decided by the kernel for the prefix's user. */
static int valid_relative_path(const char *path)
{
    size_t n = path ? strlen(path) : 0;
    if (n == 0 || n > PUT_MAX_PATH || path[0] == '/' || path[n - 1] == '/') return 0;
    for (const char *p = path; *p; p++) if ((unsigned char)*p < 0x20 || *p == 0x7f) return 0;
    const char *component = path;
    while (component) {
        const char *slash = strchr(component, '/');
        size_t len = slash ? (size_t)(slash - component) : strlen(component);
        if (len == 0 || (len == 1 && component[0] == '.') || (len == 2 && component[0] == '.' && component[1] == '.')) return 0;
        component = slash ? slash + 1 : NULL;
    }
    return 1;
}

/* The child, already the prefix's user: creates the missing directories (0700), opens the file
 * without following a final link, writes the bytes at the offset. Exits with 0 or an errno. */
static void put_child(struct passwd *pw, const char *path, const unsigned char *data, size_t len, long long offset, int truncate, int mode)
{
    if (initgroups(pw->pw_name, pw->pw_gid) || setgid(pw->pw_gid) || setuid(pw->pw_uid)) _exit(errno ? errno : EPERM);
    if (getuid() == 0 || geteuid() == 0) _exit(EPERM);
    if (chdir(pw->pw_dir)) _exit(errno);
    char copy[PUT_MAX_PATH + 1];
    snprintf(copy, sizeof copy, "%s", path);
    for (char *slash = strchr(copy, '/'); slash; slash = strchr(slash + 1, '/')) {
        *slash = 0;
        if (mkdir(copy, 0700) && errno != EEXIST) _exit(errno);
        *slash = '/';
    }
    int flags = O_WRONLY | O_CREAT | O_NOFOLLOW | O_CLOEXEC | (truncate ? O_TRUNC : 0);
    int fd = open(copy, flags, (mode_t)mode);
    if (fd < 0) _exit(errno);
    size_t done = 0;
    while (done < len) {
        ssize_t w = pwrite(fd, data + done, len - done, (off_t)(offset + (long long)done));
        if (w < 0) { if (errno == EINTR) continue; _exit(errno); }
        done += (size_t)w;
    }
    if (close(fd)) _exit(errno);
    _exit(0);
}

void op_put_file(const char *id, jn *req)
{
    const char *prefix = json_str(req, "prefix", NULL), *path = json_str(req, "path", NULL), *data = json_str(req, "data", "");
    if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
    struct passwd *pw = prefix_user(prefix);
    if (!pw) return reply_error(id, "unknown-prefix", "no guest user " AGENT_USER_PREFIX "%s", prefix);
    if (!valid_relative_path(path)) return reply_error(id, "invalid-request", "path missing, absolute, or with an empty, '.' or '..' component");
    double offset_num = json_num(req, "offset", 0);
    if (offset_num < 0 || offset_num > 4e15 || offset_num != (double)(long long)offset_num) return reply_error(id, "invalid-request", "offset is not a whole number of bytes");
    long long offset = (long long)offset_num;
    int mode = json_int(req, "mode", 0600, 0, 0777) & 0755;   /* never writable by others, never setuid */
    size_t cap = strlen(data) / 4 * 3 + 3;
    unsigned char *bytes = malloc(cap);
    if (!bytes) return reply_error(id, "put-failed", "out of memory");
    long len = b64_decode(data, bytes, cap);
    if (len < 0) { free(bytes); return reply_error(id, "invalid-request", "data is not base64"); }
    job *j = job_new(JOB_PUT, id, PUT_TIMEOUT_MS, 0);
    if (!j) { free(bytes); return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS); }
    snprintf(j->path, sizeof j->path, "%s", path);
    j->put_end = offset + len;
    pid_t pid = fork();
    if (pid < 0) {
        int e = errno;
        free(bytes);
        memset(j, 0, sizeof *j);
        return reply_error(id, "put-failed", "fork: %s", strerror(e));
    }
    if (pid == 0) {
        setsid();  /* its own group: the deadline kills it with kill(-pid) like any job */
        for (int fd = 3; fd < 1024; fd++) close(fd);
        put_child(pw, path, bytes, (size_t)len, offset, json_bool(req, "truncate", 0), mode);
    }
    free(bytes);
    j->pid = pid;
}

/* ---------------------------------------------------------------- sync */

void op_sync(const char *id, jn *req)
{
    (void)req;
    job *j = job_new(JOB_SYNC, id, SYNC_TIMEOUT_MS, 0);
    if (!j) return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS);
    pid_t pid = fork();
    if (pid < 0) { int e = errno; memset(j, 0, sizeof *j); return reply_error(id, "exec-failed", "fork: %s", strerror(e)); }
    if (pid == 0) {
        setsid();
        for (int fd = 3; fd < 1024; fd++) close(fd);
        sync();
        _exit(0);
    }
    j->pid = pid;
}
