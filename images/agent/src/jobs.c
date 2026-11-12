/* Os-x86 guest agent: the operations that run in a child and answer when it is done, without
 * blocking the loop (correction 1 of decision 205 A): exec-in-prefix (a bounded, non-interactive
 * command as the prefix user) and mounts (statfs and readdir of every data mount, where a dead
 * share may hang). The loop polls the pipe of each job and its deadline. */
#define _GNU_SOURCE
#include "agent.h"

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <pwd.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/statfs.h>
#include <sys/wait.h>
#include <unistd.h>

job jobs[AGENT_MAX_JOBS];

static job *job_new(int kind, const char *id, double timeout_ms, size_t max_out)
{
    for (int i = 0; i < AGENT_MAX_JOBS; i++) {
        if (jobs[i].used) continue;
        job *j = &jobs[i];
        memset(j, 0, sizeof *j);
        j->used = 1;
        j->kind = kind;
        j->fd = -1;
        j->t0 = now_s();
        j->deadline = j->t0 + timeout_ms / 1000.0;
        j->max_out = max_out;
        snprintf(j->id, sizeof j->id, "%s", id);
        return j;
    }
    return NULL;
}

static void job_append(job *j, const char *buf, size_t n)
{
    if (j->out.len < j->max_out) {
        size_t k = n;
        if (j->out.len + k > j->max_out) k = j->max_out - j->out.len;
        sb_put(&j->out, buf, k);
    }
    j->total += n;
}

/* The lines of the mounts child: ">path" before a mount is probed, then its JSON object. A path
 * left without its object at the deadline is the mount that hangs. */
static void mounts_reply_body(job *j, sb *b)
{
    int first = 1;
    const char *probing = NULL;
    char *text = j->out.buf ? j->out.buf : (char *)"", *line;
    while ((line = strsep(&text, "\n")) != NULL) {
        if (!*line) continue;
        if (*line == '>') { probing = line + 1; continue; }
        probing = NULL;
        if (!first) sb_puts(b, ",");
        first = 0;
        sb_puts(b, line);
    }
    if (probing && j->timed_out) {
        if (!first) sb_puts(b, ",");
        sb_puts(b, "{\"path\":");
        sb_jstrz(b, probing);
        sb_puts(b, ",\"type\":\"\",\"device\":\"\",\"state\":\"hung\",\"statfsErrno\":0,\"readdirErrno\":0,\"entries\":-1,"
                   "\"blocks\":0,\"blockSize\":0,\"freeBlocks\":0,\"ms\":0,\"error\":\"no answer before the deadline\"}");
    }
}

static void job_finish(job *j, int status)
{
    double ms = (now_s() - j->t0) * 1000;
    sb b = { 0 };
    reply_begin(&b, j->id);
    if (j->kind == JOB_EXEC) {
        sb_fmt(&b, ",\"ok\":true,\"code\":%d,\"signal\":%d,\"timedOut\":%s,\"ms\":%.1f,\"truncated\":%s,\"output\":",
               WIFEXITED(status) ? WEXITSTATUS(status) : -1, WIFSIGNALED(status) ? WTERMSIG(status) : 0,
               j->timed_out ? "true" : "false", ms, j->total > j->out.len ? "true" : "false");
        sb_jstr(&b, j->out.buf ? j->out.buf : "", j->out.len);
        sb_puts(&b, "}");
    } else {
        sb_fmt(&b, ",\"ok\":true,\"timedOut\":%s,\"ms\":%.1f,\"mounts\":[", j->timed_out ? "true" : "false", ms);
        mounts_reply_body(j, &b);
        sb_puts(&b, "]}");
    }
    send_line(&b);
    if (j->fd >= 0) close(j->fd);
    sb_free(&j->out);
    memset(j, 0, sizeof *j);
}

void job_on_readable(job *j)
{
    char buf[4096];
    ssize_t n = read(j->fd, buf, sizeof buf);
    if (n > 0) { job_append(j, buf, (size_t)n); return; }
    if (n < 0 && (errno == EAGAIN || errno == EINTR)) return;
    close(j->fd);
    j->fd = -1;  /* the output is complete; the exit of the child settles the job */
}

/* The child of a job exited: what the pipe still holds is drained without blocking (a grandchild
 * may keep the pipe open; its later output is not waited for), then the reply goes out. */
int job_on_child_exit(pid_t pid, int status)
{
    for (int i = 0; i < AGENT_MAX_JOBS; i++) {
        job *j = &jobs[i];
        if (!j->used || j->pid != pid) continue;
        j->pid = 0;
        char buf[4096];
        ssize_t n;
        size_t drained = 0;  /* bounded: a grandchild may still be writing flat out */
        while (j->fd >= 0 && drained < 256 * 1024 && (n = read(j->fd, buf, sizeof buf)) > 0) {
            job_append(j, buf, (size_t)n);
            drained += (size_t)n;
        }
        job_finish(j, status);
        return 1;
    }
    return 0;
}

void job_on_timer(double now)
{
    for (int i = 0; i < AGENT_MAX_JOBS; i++) {
        job *j = &jobs[i];
        if (!j->used || now < j->deadline) continue;
        if (j->timed_out) {
            /* The SIGKILL of the first deadline did not bring the child's exit: answer anyway. */
            j->pid = 0;
            job_finish(j, SIGKILL);
            continue;
        }
        j->timed_out = 1;
        if (j->pid > 0) kill(-j->pid, SIGKILL);
        launch *l = j->seq ? launch_by_seq(j->seq) : NULL;
        char path[300];
        if (l && launch_cgroup(l, path, sizeof path)) cgroup_kill(path);
        j->deadline = now + 5;
    }
}

double job_next_deadline(void)
{
    double next = 0;
    for (int i = 0; i < AGENT_MAX_JOBS; i++)
        if (jobs[i].used && (next <= 0 || jobs[i].deadline < next)) next = jobs[i].deadline;
    return next;
}

/* ---------------------------------------------------------------- exec-in-prefix */

/* A non-interactive command as the prefix user: stdin closed, output captured and bounded,
 * duration bounded (SIGKILL of the whole launch at the deadline). It is a launch like any other
 * (its own cgroup) except that the reply carries the exit code: no exit event. */
void op_exec_in_prefix(const char *id, jn *req)
{
    const char *prefix = json_str(req, "prefix", NULL), *program = json_str(req, "program", NULL);
    if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
    struct passwd *pw = prefix_user(prefix);
    if (!pw) return reply_error(id, "unknown-prefix", "no guest user " AGENT_USER_PREFIX "%s", prefix);
    if (!program || !*program) return reply_error(id, "invalid-request", "program missing");
    int timeout_ms = json_int(req, "timeoutMs", AGENT_DEFAULT_EXEC_TIMEOUT_MS, 1, AGENT_MAX_TIMEOUT_MS);
    int max_out = json_int(req, "maxOutput", AGENT_DEFAULT_EXEC_MAX_OUTPUT, 0, AGENT_MAX_EXEC_OUTPUT);
    job *j = job_new(JOB_EXEC, id, timeout_ms, (size_t)max_out);
    if (!j) return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS);
    launch *l = launch_new(prefix, 0);
    int pipe_fds[2] = { -1, -1 };
    const char *code = NULL;
    int failure = 0;
    if (!l) code = "too-many-launches";
    else if (cgroup_available && cgroup_make(l)) { code = "cgroup-failed"; failure = errno; }
    else if (pipe2(pipe_fds, O_CLOEXEC)) { code = "exec-failed"; failure = errno; }
    pid_t pid = -1;
    if (!code) {
        pid = spawn_as(pw, l, program, json_get(req, "args"), json_get(req, "env"), json_str(req, "cwd", NULL),
                       json_int(req, "nproc", AGENT_DEFAULT_NPROC, 1, AGENT_MAX_NPROC), -1, pipe_fds[1], pipe_fds[1], -1, &failure);
        if (pid < 0) code = failure == ENOENT ? "program-not-found" : "exec-failed";
    }
    if (pipe_fds[1] >= 0) close(pipe_fds[1]);
    if (code) {
        if (pipe_fds[0] >= 0) close(pipe_fds[0]);
        if (l) launch_forget(l);
        memset(j, 0, sizeof *j);
        return reply_error(id, code, "cannot run %s: %s", program, failure ? strerror(failure) : "no room left in the agent's tables");
    }
    l->pid = pid;
    l->quiet = 1;
    launch_watch_cgroup(l);
    j->pid = pid;
    j->fd = pipe_fds[0];
    j->seq = l->seq;
    fd_set_nonblock(j->fd);
}

/* ---------------------------------------------------------------- mounts */

static const char *const PSEUDO_FS[] = { "proc", "sysfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "securityfs", "debugfs",
                                         "tracefs", "pstore", "bpf", "mqueue", "hugetlbfs", "configfs", "fusectl", "binfmt_misc",
                                         "efivarfs", "autofs", "rpc_pipefs", "nsfs", NULL };

static int is_pseudo(const char *type)
{
    for (int i = 0; PSEUDO_FS[i]; i++) if (!strcmp(type, PSEUDO_FS[i])) return 1;
    return 0;
}

/* The child: for every data mount, statfs then opendir and readdir, one JSON line each. A mount
 * that hangs blocks the child, not the agent; the parent reports what arrived at the deadline.
 * The octal escapes of /proc/mounts (\040 for a space) are left as they are. */
static void mounts_child(int out_fd)
{
    FILE *f = fopen("/proc/mounts", "r");
    if (!f) _exit(1);
    char line[512];
    while (fgets(line, sizeof line, f)) {
        char dev[128], path[256], type[32];
        if (sscanf(line, "%127s %255s %31s", dev, path, type) != 3 || is_pseudo(type)) continue;
        dprintf(out_fd, ">%s\n", path);
        double t0 = now_s();
        struct statfs sf;
        memset(&sf, 0, sizeof sf);
        int statfs_errno = statfs(path, &sf) ? errno : 0;
        int entries = -1, readdir_errno = 0;
        DIR *d = opendir(path);
        if (!d) readdir_errno = errno;
        else {
            entries = 0;
            errno = 0;
            while (readdir(d)) entries++;
            readdir_errno = errno;
            closedir(d);
        }
        sb b = { 0 };
        sb_puts(&b, "{\"path\":"); sb_jstrz(&b, path);
        sb_puts(&b, ",\"type\":"); sb_jstrz(&b, type);
        sb_puts(&b, ",\"device\":"); sb_jstrz(&b, dev);
        sb_fmt(&b, ",\"state\":\"%s\",\"statfsErrno\":%d,\"readdirErrno\":%d,\"entries\":%d,\"blocks\":%lld,\"blockSize\":%lld,\"freeBlocks\":%lld,\"ms\":%.1f,\"error\":",
               (statfs_errno || readdir_errno) ? "error" : "ok", statfs_errno, readdir_errno, entries,
               (long long)sf.f_blocks, (long long)sf.f_bsize, (long long)sf.f_bavail, (now_s() - t0) * 1000);
        sb_jstrz(&b, statfs_errno ? strerror(statfs_errno) : readdir_errno ? strerror(readdir_errno) : "");
        sb_puts(&b, "}\n");
        if (b.buf && write(out_fd, b.buf, b.len) < 0) _exit(1);
        sb_free(&b);
    }
    fclose(f);
    _exit(0);
}

void op_mounts(const char *id, jn *req)
{
    int timeout_ms = json_int(req, "timeoutMs", AGENT_DEFAULT_MOUNTS_TIMEOUT_MS, 1, AGENT_MAX_TIMEOUT_MS);
    job *j = job_new(JOB_MOUNTS, id, timeout_ms, 256 * 1024);
    if (!j) return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS);
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
        /* Only its pipe: a child stuck on a dead share must not hold the ptys and sockets of the agent. */
        for (int fd = 3; fd < 1024; fd++) if (fd != pipe_fds[1]) close(fd);
        mounts_child(pipe_fds[1]);
    }
    close(pipe_fds[1]);
    j->pid = pid;
    j->fd = pipe_fds[0];
    fd_set_nonblock(j->fd);
}
