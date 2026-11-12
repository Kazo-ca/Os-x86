/* Os-x86 guest agent: launches (one cgroup v2 per launch, decision 206 A), kill (SIGTERM then
 * cgroup.kill after a grace period), the process list and the prefix users. A launch is
 * identified by "<prefix>/<seq>", which is also its cgroup under the agent's root
 * (/sys/fs/cgroup/osx86/<prefix>/<seq>). */
#define _GNU_SOURCE
#include "agent.h"

#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <pwd.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

launch launches[AGENT_MAX_LAUNCHES];
int cgroup_available;
const char *cgroup_root_path = AGENT_CGROUP_ROOT;
static int next_seq;

/* ---------------------------------------------------------------- prefixes and users */

int valid_prefix(const char *p)
{
    if (!p || !*p || strlen(p) > AGENT_MAX_PREFIX) return 0;
    if (!islower((unsigned char)*p) && !isdigit((unsigned char)*p)) return 0;
    for (; *p; p++) if (!islower((unsigned char)*p) && !isdigit((unsigned char)*p) && *p != '-') return 0;
    return 1;
}

struct passwd *prefix_user(const char *prefix)
{
    char name[64];
    snprintf(name, sizeof name, AGENT_USER_PREFIX "%s", prefix);
    struct passwd *pw = getpwnam(name);
    return pw && pw->pw_uid != 0 ? pw : NULL;
}

const char *uid_prefix(uid_t uid, char *out, size_t n)
{
    if (uid == 0) return NULL;
    struct passwd *pw = getpwuid(uid);
    size_t len = strlen(AGENT_USER_PREFIX);
    if (pw && !strncmp(pw->pw_name, AGENT_USER_PREFIX, len) && valid_prefix(pw->pw_name + len)) {
        snprintf(out, n, "%s", pw->pw_name + len);
        return out;
    }
    return NULL;
}

int pid_uid(pid_t pid)
{
    char path[64], line[256];
    snprintf(path, sizeof path, "/proc/%d/status", (int)pid);
    FILE *f = fopen(path, "r");
    if (!f) return -1;
    int uid = -1;
    while (fgets(line, sizeof line, f)) if (!strncmp(line, "Uid:", 4)) { uid = atoi(line + 4); break; }
    fclose(f);
    return uid;
}

/* ---------------------------------------------------------------- launch records */

launch *launch_new(const char *prefix, int is_console)
{
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) {
        if (launches[i].used) continue;
        launch *l = &launches[i];
        memset(l, 0, sizeof *l);
        l->used = 1;
        l->seq = ++next_seq;
        snprintf(l->prefix, sizeof l->prefix, "%s", prefix);
        l->is_console = is_console;
        l->events_fd = -1;
        l->populated = -1;
        return l;
    }
    return NULL;
}

void launch_id(const launch *l, char *out, size_t n)
{
    snprintf(out, n, "%s/%d", l->prefix, l->seq);
}

launch *launch_find(const char *id)
{
    if (!id) return NULL;
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) {
        if (!launches[i].used) continue;
        char candidate[64];
        launch_id(&launches[i], candidate, sizeof candidate);
        if (!strcmp(candidate, id)) return &launches[i];
    }
    return NULL;
}

launch *launch_by_pid(pid_t pid)
{
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) if (launches[i].used && launches[i].pid == pid && pid > 0) return &launches[i];
    return NULL;
}

launch *launch_by_seq(int seq)
{
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) if (launches[i].used && launches[i].seq == seq) return &launches[i];
    return NULL;
}

int launch_count(void)
{
    int n = 0;
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) n += launches[i].used;
    return n;
}

const char *launch_cgroup(const launch *l, char *out, size_t n)
{
    if (!cgroup_available) return NULL;
    snprintf(out, n, "%s/%s/%d", cgroup_root_path, l->prefix, l->seq);
    return out;
}

void launch_forget(launch *l)
{
    char path[300];
    if (l->events_fd >= 0) close(l->events_fd);
    if (launch_cgroup(l, path, sizeof path)) rmdir(path);
    memset(l, 0, sizeof *l);
}

/* ---------------------------------------------------------------- cgroups */

int cgroup_make(const launch *l)
{
    char path[300];
    if (!launch_cgroup(l, path, sizeof path)) return -1;
    char parent[300];
    snprintf(parent, sizeof parent, "%s/%s", cgroup_root_path, l->prefix);
    mkdir(parent, 0755);
    if (mkdir(path, 0755) && errno != EEXIST) return -1;
    return 0;
}

static int cgroup_write(const char *path, const char *file, const char *text)
{
    char full[320];
    snprintf(full, sizeof full, "%s/%s", path, file);
    int fd = open(full, O_WRONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    size_t n = strlen(text);
    int r = write(fd, text, n) == (ssize_t)n ? 0 : -1;
    close(fd);
    return r;
}

int cgroup_enter(const char *path)
{
    char pid[32];
    snprintf(pid, sizeof pid, "%d", (int)getpid());
    return cgroup_write(path, "cgroup.procs", pid);
}

int cgroup_move(const char *path, pid_t pid)
{
    char text[32];
    snprintf(text, sizeof text, "%d", (int)pid);
    return cgroup_write(path, "cgroup.procs", text);
}

int cgroup_kill(const char *path)
{
    return cgroup_write(path, "cgroup.kill", "1");
}

int cgroup_count(const char *path)
{
    char full[320];
    snprintf(full, sizeof full, "%s/cgroup.procs", path);
    FILE *f = fopen(full, "r");
    if (!f) return -1;
    int n = 0, pid;
    while (fscanf(f, "%d", &pid) == 1) n++;
    fclose(f);
    return n;
}

int cgroup_signal_all(const char *path, int sig)
{
    char full[320];
    snprintf(full, sizeof full, "%s/cgroup.procs", path);
    FILE *f = fopen(full, "r");
    if (!f) return -1;
    int n = 0, pid;
    while (fscanf(f, "%d", &pid) == 1) if (pid > 1 && pid != getpid() && !kill(pid, sig)) n++;
    fclose(f);
    return n;
}

int cgroup_read_populated(int fd)
{
    char buf[256];
    lseek(fd, 0, SEEK_SET);
    ssize_t n = read(fd, buf, sizeof buf - 1);
    if (n <= 0) return -1;
    buf[n] = 0;
    char *p = strstr(buf, "populated ");
    return p ? atoi(p + 10) : -1;
}

/* cgroup.events is polled with POLLPRI by the loop: the kernel signals every change of
 * "populated", which is how the agent learns that a launch is entirely gone. */
void launch_watch_cgroup(launch *l)
{
    char path[320];
    if (!launch_cgroup(l, path, sizeof path)) return;
    strncat(path, "/cgroup.events", sizeof path - strlen(path) - 1);
    l->events_fd = open(path, O_RDONLY | O_CLOEXEC);
    if (l->events_fd >= 0) l->populated = cgroup_read_populated(l->events_fd);
}

/* ---------------------------------------------------------------- the child side of a launch */

void child_setup(struct passwd *pw, const launch *l, jn *env, const char *cwd, int nproc)
{
    char path[300];
    if (launch_cgroup(l, path, sizeof path)) cgroup_enter(path);
    struct rlimit rl = { (rlim_t)nproc, (rlim_t)nproc };
    setrlimit(RLIMIT_NPROC, &rl);
    if (initgroups(pw->pw_name, pw->pw_gid) || setgid(pw->pw_gid) || setuid(pw->pw_uid)) _exit(126);
    if (chdir(cwd && *cwd ? cwd : pw->pw_dir)) { if (chdir("/")) _exit(126); }
    clearenv();
    setenv("HOME", pw->pw_dir, 1);
    setenv("USER", pw->pw_name, 1);
    setenv("LOGNAME", pw->pw_name, 1);
    setenv("SHELL", pw->pw_shell && *pw->pw_shell ? pw->pw_shell : "/bin/sh", 1);
    setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1);
    setenv("TERM", "xterm", 1);
    if (env && env->type == J_OBJ)
        for (jn *e = env->child; e; e = e->next)
            if (e->type == J_STR && *e->key && strncmp(e->key, "LD_", 3) && !strchr(e->key, '=')) setenv(e->key, e->str, 1);
}

char **build_argv(const char *program, jn *args)
{
    int n = 1;
    if (args && args->type == J_ARR) for (jn *a = args->child; a; a = a->next) n++;
    char **v = calloc((size_t)n + 1, sizeof *v);
    if (!v) return NULL;
    v[0] = (char *)program;
    int i = 1;
    if (args && args->type == J_ARR) for (jn *a = args->child; a; a = a->next) v[i++] = a->type == J_STR ? a->str : (char *)"";
    return v;
}

/* Forks and execs program as the prefix user, in a session of its own. The exec failure comes
 * back through a CLOEXEC pipe, so the caller refuses the launch instead of reporting a pid that
 * dies with 127. stdin, stdout and stderr are the three fds given (/dev/null when negative);
 * tty_fd, when not negative, becomes the controlling terminal and the three standard fds. */
pid_t spawn_as(struct passwd *pw, const launch *l, const char *program, jn *args, jn *env, const char *cwd, int nproc,
               int fd_in, int fd_out, int fd_err, int tty_fd, int *exec_errno)
{
    int report[2];
    if (pipe2(report, O_CLOEXEC)) { *exec_errno = errno; return -1; }
    char **argv = build_argv(program, args);
    if (!argv) { close(report[0]); close(report[1]); *exec_errno = ENOMEM; return -1; }
    pid_t pid = fork();
    if (pid < 0) { *exec_errno = errno; free(argv); close(report[0]); close(report[1]); return -1; }
    if (pid == 0) {
        close(report[0]);
        setsid();
        if (tty_fd >= 0) { ioctl(tty_fd, TIOCSCTTY, 0); fd_in = fd_out = fd_err = tty_fd; }
        child_setup(pw, l, env, cwd, nproc);
        int null_fd = open("/dev/null", O_RDWR);
        dup2(fd_in >= 0 ? fd_in : null_fd, 0);
        dup2(fd_out >= 0 ? fd_out : null_fd, 1);
        dup2(fd_err >= 0 ? fd_err : null_fd, 2);
        for (int fd = 3; fd < 1024; fd++) if (fd != report[1]) close(fd);
        signal(SIGPIPE, SIG_DFL);
        signal(SIGHUP, SIG_DFL);
        execvp(program, argv);
        int e = errno;
        if (write(report[1], &e, sizeof e) < 0) { }
        _exit(127);
    }
    free(argv);
    close(report[1]);
    int e = 0;
    ssize_t r;
    do r = read(report[0], &e, sizeof e); while (r < 0 && errno == EINTR);
    close(report[0]);
    if (r == (ssize_t)sizeof e) {
        int status;
        waitpid(pid, &status, 0);
        *exec_errno = e;
        return -1;
    }
    *exec_errno = 0;
    return pid;
}

/* ---------------------------------------------------------------- launch */

void op_launch(const char *id, jn *req)
{
    const char *prefix = json_str(req, "prefix", NULL), *program = json_str(req, "program", NULL);
    if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid (lowercase letters, digits and dashes, 1 to %d characters)", AGENT_MAX_PREFIX);
    struct passwd *pw = prefix_user(prefix);
    if (!pw) return reply_error(id, "unknown-prefix", "no guest user " AGENT_USER_PREFIX "%s", prefix);
    if (!program || !*program) return reply_error(id, "invalid-request", "program missing");
    launch *l = launch_new(prefix, 0);
    if (!l) return reply_error(id, "too-many-launches", "the agent tracks at most %d launches", AGENT_MAX_LAUNCHES);
    if (cgroup_available && cgroup_make(l)) { int e = errno; launch_forget(l); return reply_error(id, "cgroup-failed", "cannot create the cgroup: %s", strerror(e)); }
    int log_fd = -1;
    if (json_bool(req, "log", 0)) {
        char path[320];
        snprintf(path, sizeof path, AGENT_RUN_DIR "/launch-%s-%d.log", prefix, l->seq);
        log_fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
        if (log_fd >= 0 && fchown(log_fd, pw->pw_uid, pw->pw_gid)) { /* root keeps the log */ }
    }
    double t0 = now_s();
    int exec_errno;
    pid_t pid = spawn_as(pw, l, program, json_get(req, "args"), json_get(req, "env"), json_str(req, "cwd", NULL),
                         (int)json_num(req, "nproc", AGENT_DEFAULT_NPROC), -1, log_fd, log_fd, -1, &exec_errno);
    if (log_fd >= 0) close(log_fd);
    if (pid < 0) {
        launch_forget(l);
        return reply_error(id, exec_errno == ENOENT ? "program-not-found" : "exec-failed", "cannot run %s: %s", program, strerror(exec_errno));
    }
    l->pid = pid;
    launch_watch_cgroup(l);
    char lid[64], cg[300];
    launch_id(l, lid, sizeof lid);
    sb b = { 0 };
    reply_begin(&b, id);
    sb_puts(&b, ",\"ok\":true,\"launch\":");
    sb_jstrz(&b, lid);
    sb_fmt(&b, ",\"pid\":%d,\"uid\":%d,\"ms\":%.1f,\"cgroup\":", (int)pid, (int)pw->pw_uid, (now_s() - t0) * 1000);
    if (launch_cgroup(l, cg, sizeof cg)) sb_jstrz(&b, cg); else sb_puts(&b, "null");
    sb_puts(&b, "}");
    send_line(&b);
    logmsg("launch %s pid=%d program=%s", lid, (int)pid, program);
}

/* ---------------------------------------------------------------- exit, cgroup events, kill */

static void send_exit_event(launch *l, int remaining)
{
    char lid[64];
    launch_id(l, lid, sizeof lid);
    sb b = { 0 };
    event_begin(&b, l->is_console ? "console-closed" : "exit");
    sb_puts(&b, ",\"launch\":");
    sb_jstrz(&b, lid);
    if (l->is_console) sb_fmt(&b, ",\"console\":%d", l->seq);
    sb_fmt(&b, ",\"pid\":%d,\"code\":%d,\"signal\":%d,\"remaining\":%d}", (int)l->pid, l->code, l->sig, remaining);
    send_line(&b);
}

static void finish_kill(launch *l)
{
    if (!l->kill_id[0]) return;
    reply_ok(l->kill_id, ",\"forced\":%s,\"ms\":%.1f", l->kill_forced ? "true" : "false", (now_s() - l->kill_t0) * 1000);
    l->kill_id[0] = 0;
}

/* The launch is over when its main process is reaped and its cgroup is empty (or there is no
 * cgroup). A detached launch (the processes kept from a closed console, 208 A) has no main
 * process: its exit event is sent here, when its cgroup drains. */
static void launch_settle(launch *l)
{
    int empty = l->events_fd < 0 || l->populated == 0;
    if (!l->exited || !empty) return;
    if (l->detached) { l->code = -1; l->sig = 0; send_exit_event(l, 0); }
    finish_kill(l);
    launch_forget(l);
}

void launch_on_exit(launch *l, int status)
{
    l->exited = 1;
    l->code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    l->sig = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
    char path[300];
    int remaining = 0;
    if (launch_cgroup(l, path, sizeof path)) {
        remaining = cgroup_count(path);
        if (remaining < 0) remaining = 0;
        if (l->events_fd >= 0) l->populated = cgroup_read_populated(l->events_fd);
    }
    if (!l->quiet) send_exit_event(l, remaining);
    l->pid = 0;
    if ((l->is_console || l->quiet) && remaining > 0 && l->kill_deadline <= 0) {
        /* The shell of a console left with jobs still running, or a command of exec-in-prefix
         * left children behind: what remains gets SIGHUP, then cgroup.kill after the grace
         * period (208 A; nothing has a window to keep without an X server). */
        cgroup_signal_all(path, SIGHUP);
        l->kill_deadline = now_s() + AGENT_DEFAULT_GRACE_MS / 1000.0;
    }
    launch_settle(l);
}

void launch_on_cgroup_event(launch *l)
{
    if (l->events_fd < 0) return;
    l->populated = cgroup_read_populated(l->events_fd);
    launch_settle(l);
}

void launch_on_timer(double now)
{
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++) {
        launch *l = &launches[i];
        if (!l->used || l->kill_deadline <= 0 || now < l->kill_deadline) continue;
        l->kill_deadline = 0;
        l->kill_forced = 1;
        char path[300];
        /* With a cgroup, the cgroup.events notification finishes the kill once every process is
         * gone; without one, the SIGCHLD of the main process does. */
        if (launch_cgroup(l, path, sizeof path)) cgroup_kill(path);
        else if (l->pid > 0) kill(-l->pid, SIGKILL);
    }
}

double launch_next_deadline(void)
{
    double next = 0;
    for (int i = 0; i < AGENT_MAX_LAUNCHES; i++)
        if (launches[i].used && launches[i].kill_deadline > 0 && (next <= 0 || launches[i].kill_deadline < next)) next = launches[i].kill_deadline;
    return next;
}

/* Starts the end of a launch: sig to every process of its cgroup (to its process group without
 * cgroups), cgroup.kill after grace_ms. reply_id, when not NULL, is answered once the launch is
 * gone. */
void launch_terminate(launch *l, int sig, int grace_ms, const char *reply_id)
{
    char path[300];
    double now = now_s();
    if (launch_cgroup(l, path, sizeof path)) cgroup_signal_all(path, sig);
    else if (l->pid > 0) kill(-l->pid, sig);
    if (reply_id) snprintf(l->kill_id, sizeof l->kill_id, "%s", reply_id);
    l->kill_t0 = now;
    l->kill_forced = 0;
    l->kill_deadline = grace_ms <= 0 ? now : now + grace_ms / 1000.0;
    if (l->events_fd >= 0) l->populated = cgroup_read_populated(l->events_fd);
    launch_settle(l);
}

static int signal_uid(uid_t uid, int sig)
{
    int n = 0;
    DIR *d = opendir("/proc");
    struct dirent *e;
    while (d && (e = readdir(d))) {
        if (!isdigit((unsigned char)e->d_name[0])) continue;
        pid_t pid = (pid_t)atoi(e->d_name);
        if (pid <= 1 || pid == getpid()) continue;
        if ((uid_t)pid_uid(pid) == uid && !kill(pid, sig)) n++;
    }
    if (d) closedir(d);
    return n;
}

/* kill by launch id (206 A: SIGTERM to every process of the cgroup, cgroup.kill after the grace
 * period; the reply comes when the launch is gone), or by scope for the administration:
 * pid | group (the pid's process group) | prefix (every process of the prefix user) | cgroup
 * (cgroup.kill of one launch or of the whole prefix, immediate). */
void op_kill(const char *id, jn *req)
{
    const char *launch_name = json_str(req, "launch", NULL);
    const char *scope = json_str(req, "scope", launch_name ? "launch" : "pid");
    int sig = (int)json_num(req, "signal", SIGTERM);
    double t0 = now_s();
    if (sig < 0 || sig > 64) return reply_error(id, "invalid-request", "signal %d out of range", sig);
    if (!strcmp(scope, "launch")) {
        launch *l = launch_find(launch_name);
        if (!l) return reply_error(id, "unknown-launch", "no launch '%s'", launch_name ? launch_name : "");
        if (l->kill_id[0]) return reply_error(id, "kill-pending", "a kill of %s is already under way", launch_name);
        launch_terminate(l, sig, (int)json_num(req, "graceMs", AGENT_DEFAULT_GRACE_MS), id);
        return;
    }
    int n = 0;
    char what[320] = "";
    if (!strcmp(scope, "pid") || !strcmp(scope, "group")) {
        int pid = (int)json_num(req, "pid", 0);
        if (pid <= 1) return reply_error(id, "invalid-request", "pid missing");
        int uid = pid_uid(pid);
        if (uid < 0) return reply_error(id, "unknown-pid", "no process %d", pid);
        if (uid == 0 && !json_bool(req, "allowRoot", 0)) return reply_error(id, "root-process", "process %d belongs to root: refused without allowRoot", pid);
        if ((!strcmp(scope, "pid") ? kill(pid, sig) : kill(-pid, sig))) return reply_error(id, "kill-failed", "kill: %s", strerror(errno));
        n = 1;
        snprintf(what, sizeof what, "%s %d", scope, pid);
    } else if (!strcmp(scope, "prefix")) {
        const char *prefix = json_str(req, "prefix", NULL);
        if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
        struct passwd *pw = prefix_user(prefix);
        if (!pw) return reply_error(id, "unknown-prefix", "no guest user " AGENT_USER_PREFIX "%s", prefix);
        for (int pass = 0; pass < 50; pass++) {
            n += signal_uid(pw->pw_uid, sig);
            if (sig != SIGKILL || signal_uid(pw->pw_uid, 0) == 0) break;
            usleep(10000);
        }
        snprintf(what, sizeof what, "uid %d", (int)pw->pw_uid);
    } else if (!strcmp(scope, "cgroup")) {
        const char *prefix = json_str(req, "prefix", NULL);
        int seq = (int)json_num(req, "seq", 0);
        if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
        if (!cgroup_available) return reply_error(id, "no-cgroup", "cgroup v2 is not available in this guest");
        if (seq) snprintf(what, sizeof what, "%s/%s/%d", cgroup_root_path, prefix, seq);
        else snprintf(what, sizeof what, "%s/%s", cgroup_root_path, prefix);
        n = cgroup_count(what);
        if (cgroup_kill(what)) return reply_error(id, "kill-failed", "cgroup.kill %s: %s", what, strerror(errno));
        /* Bounded wait (at most 200 ms) so that the reply usually follows the disappearance. */
        for (int i = 0; i < 100 && cgroup_count(what) > 0; i++) usleep(2000);
    } else {
        return reply_error(id, "invalid-request", "unknown scope '%s'", scope);
    }
    sb b = { 0 };
    reply_begin(&b, id);
    sb_fmt(&b, ",\"ok\":true,\"signal\":%d,\"count\":%d,\"ms\":%.1f,\"target\":", sig, n, (now_s() - t0) * 1000);
    sb_jstrz(&b, what);
    sb_puts(&b, "}");
    send_line(&b);
}

/* ---------------------------------------------------------------- list-processes */

/* The launch of a cgroup path as /proc/<pid>/cgroup gives it: "/osx86/<prefix>/<seq>". */
static launch *launch_of_cgroup(const char *cgroup_path)
{
    const char *root = cgroup_root_path + strlen("/sys/fs/cgroup");
    size_t len = strlen(root);
    if (!cgroup_available || strncmp(cgroup_path, root, len) || cgroup_path[len] != '/') return NULL;
    const char *slash = strchr(cgroup_path + len + 1, '/');
    return slash ? launch_by_seq(atoi(slash + 1)) : NULL;
}

static void read_first_line(const char *path, char *out, size_t n)
{
    out[0] = 0;
    FILE *f = fopen(path, "r");
    if (!f) return;
    if (fgets(out, (int)n, f)) { char *nl = strchr(out, '\n'); if (nl) *nl = 0; } else out[0] = 0;
    fclose(f);
}

launch *launch_of_pid(pid_t pid)
{
    char path[64], cg[256];
    snprintf(path, sizeof path, "/proc/%d/cgroup", (int)pid);
    read_first_line(path, cg, sizeof cg);
    char *colons = strstr(cg, "::");
    return colons ? launch_of_cgroup(colons + 2) : NULL;
}

static void append_process(sb *b, int pid)
{
    int ppid = 0, uid = -1, pgid = 0;
    char path[64], line[512], name[64] = "", state[4] = "";
    snprintf(path, sizeof path, "/proc/%d/status", pid);
    FILE *f = fopen(path, "r");
    if (!f) return;
    while (fgets(line, sizeof line, f)) {
        if (!strncmp(line, "Name:", 5)) sscanf(line + 5, " %63[^\n]", name);
        else if (!strncmp(line, "State:", 6)) sscanf(line + 6, " %1s", state);
        else if (!strncmp(line, "PPid:", 5)) ppid = atoi(line + 5);
        else if (!strncmp(line, "Uid:", 4)) uid = atoi(line + 4);
    }
    fclose(f);
    snprintf(path, sizeof path, "/proc/%d/stat", pid);
    read_first_line(path, line, sizeof line);
    char *paren = strrchr(line, ')');
    if (paren) sscanf(paren + 1, " %*c %*d %d", &pgid);
    char cmd[512] = "";
    snprintf(path, sizeof path, "/proc/%d/cmdline", pid);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    int n = fd >= 0 ? (int)read(fd, cmd, sizeof cmd - 1) : 0;
    if (fd >= 0) close(fd);
    if (n < 0) n = 0;
    for (int i = 0; i < n; i++) if (!cmd[i]) cmd[i] = ' ';
    while (n && cmd[n - 1] == ' ') n--;
    cmd[n] = 0;
    char cg[256];
    snprintf(path, sizeof path, "/proc/%d/cgroup", pid);
    read_first_line(path, cg, sizeof cg);
    char *colons = strstr(cg, "::");
    if (colons) memmove(cg, colons + 2, strlen(colons + 2) + 1);
    char prefix[AGENT_MAX_PREFIX + 1];
    const char *owner = uid_prefix((uid_t)uid, prefix, sizeof prefix);
    launch *l = launch_of_cgroup(cg);
    sb_fmt(b, "{\"pid\":%d,\"ppid\":%d,\"pgid\":%d,\"uid\":%d,\"state\":\"%s\",\"prefix\":", pid, ppid, pgid, uid, state);
    if (owner) sb_jstrz(b, owner); else sb_puts(b, "null");
    sb_puts(b, ",\"launch\":");
    if (l) { char lid[64]; launch_id(l, lid, sizeof lid); sb_jstrz(b, lid); } else sb_puts(b, "null");
    sb_puts(b, ",\"comm\":"); sb_jstrz(b, name);
    sb_puts(b, ",\"cmd\":"); sb_jstrz(b, cmd);
    sb_puts(b, ",\"cgroup\":"); sb_jstrz(b, cg);
    sb_puts(b, "},");
}

void op_list_processes(const char *id, jn *req)
{
    (void)req;
    sb b = { 0 };
    reply_begin(&b, id);
    sb_puts(&b, ",\"ok\":true,\"processes\":[");
    DIR *d = opendir("/proc");
    struct dirent *e;
    while (d && (e = readdir(d))) if (isdigit((unsigned char)e->d_name[0])) append_process(&b, atoi(e->d_name));
    if (d) closedir(d);
    if (b.len && b.buf[b.len - 1] == ',') b.buf[--b.len] = 0;
    sb_puts(&b, "]}");
    send_line(&b);
}
