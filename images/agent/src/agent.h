/* Os-x86 guest agent (milestone 0, work item 5; decisions 205 A, 206 A, 208 A, 213 A).
 *
 * Root inside the guest, started by the init. Talks to the host through the virtio-console port
 * hvc0 (JSON, one request or reply per line, replies paired by "id", events without "id") and
 * relays the user consoles (one pty each) through hvc1, multiplexed by frames that carry the
 * console number (see console.c). Static musl i386 binary, no library: the JSON reader and
 * writer are the ones of trial E7, cleaned up. One single-threaded, non-blocking event loop
 * (poll on the two ports, the ptys, the open-url socket, a SIGCHLD pipe, the pipes of the
 * running jobs and the cgroup.events of every launch): no operation blocks another.
 */
#ifndef OSX86_AGENT_H
#define OSX86_AGENT_H

#include <stddef.h>
#include <stdint.h>
#include <sys/types.h>

#define AGENT_PROTOCOL_VERSION 1
#define AGENT_PORT "/dev/hvc0"
#define AGENT_CONSOLE_PORT "/dev/hvc1"
#define AGENT_RUN_DIR "/run/osx86"
#define AGENT_URL_SOCKET AGENT_RUN_DIR "/open-url.sock"
#define AGENT_LOG AGENT_RUN_DIR "/agent.log"
#define AGENT_CGROUP_ROOT "/sys/fs/cgroup/osx86"
#define AGENT_USER_PREFIX "osx86-"        /* the guest user of prefix "p" is osx86-p */
#define AGENT_MAX_PREFIX 16
#define AGENT_MAX_LAUNCHES 256
#define AGENT_MAX_CONSOLES 32
#define AGENT_MAX_JOBS 32
#define AGENT_MAX_LINE 65536
#define AGENT_DEFAULT_NPROC 256
#define AGENT_DEFAULT_GRACE_MS 2000
#define AGENT_DEFAULT_EXEC_TIMEOUT_MS 30000
#define AGENT_DEFAULT_EXEC_MAX_OUTPUT 65536
#define AGENT_MAX_EXEC_OUTPUT (1024 * 1024)
#define AGENT_DEFAULT_MOUNTS_TIMEOUT_MS 8000
#define AGENT_PORT_WRITE_TIMEOUT_MS 2000

/* ---------------------------------------------------------------- JSON (json.c) */
enum { J_NULL, J_BOOL, J_NUM, J_STR, J_ARR, J_OBJ };
typedef struct jn { int type; const char *key; char *str; double num; int b; struct jn *child, *next; } jn;

jn *json_parse(const char *text);
jn *json_get(jn *object, const char *key);
const char *json_str(jn *object, const char *key, const char *fallback);
double json_num(jn *object, const char *key, double fallback);
int json_bool(jn *object, const char *key, int fallback);

typedef struct { char *buf; size_t len, cap; } sb;
void sb_put(sb *b, const char *s, size_t n);
void sb_puts(sb *b, const char *s);
void sb_fmt(sb *b, const char *fmt, ...);
void sb_jstr(sb *b, const char *s, size_t n);   /* a JSON string, invalid UTF-8 replaced by U+FFFD */
void sb_jstrz(sb *b, const char *s);
void sb_free(sb *b);

/* ---------------------------------------------------------------- port and replies (agent.c) */
double now_s(void);
void logmsg(const char *fmt, ...);
void port_write_all(int fd, const char *s, size_t n);  /* waits for a full port, bounded */
void send_line(sb *b);                            /* appends LF, writes on hvc0, frees the buffer */
void reply_begin(sb *b, const char *id_json);     /* {"id":<id> */
void reply_ok(const char *id_json, const char *body_fmt, ...);  /* {"id":..,"ok":true<body>} */
void reply_error(const char *id_json, const char *code, const char *fmt, ...);
void event_begin(sb *b, const char *name);        /* {"event":"name" */
int fd_set_nonblock(int fd);
int fd_set_cloexec(int fd);

/* ---------------------------------------------------------------- launches and cgroups (launch.c) */
struct passwd;
typedef struct launch {
    int used;
    int seq;
    char prefix[AGENT_MAX_PREFIX + 1];
    pid_t pid;                 /* the process started (0 once reaped) */
    int is_console;
    int quiet;                 /* exec-in-prefix: the reply carries the exit code, no exit event */
    int detached;              /* no main process: the processes kept from a closed console (208 A) */
    int exited;                /* the main process has been reaped */
    int code, sig;
    int events_fd;             /* cgroup.events, polled with POLLPRI; -1 without cgroups */
    int populated;             /* last value read from cgroup.events */
    char kill_id[64];          /* pending kill reply, "" when none */
    double kill_deadline;      /* when SIGTERM gives way to cgroup.kill */
    int kill_forced;
    double kill_t0;
} launch;

extern launch launches[AGENT_MAX_LAUNCHES];
extern int cgroup_available;
extern const char *cgroup_root_path;           /* AGENT_CGROUP_ROOT unless -g says otherwise */

int valid_prefix(const char *prefix);
struct passwd *prefix_user(const char *prefix);
const char *uid_prefix(uid_t uid, char *out, size_t n);
launch *launch_new(const char *prefix, int is_console);
launch *launch_find(const char *id);
launch *launch_by_pid(pid_t pid);
launch *launch_by_seq(int seq);
void launch_id(const launch *l, char *out, size_t n);
const char *launch_cgroup(const launch *l, char *out, size_t n);
int launch_count(void);
int cgroup_make(const launch *l);
int cgroup_enter(const char *path);
int cgroup_move(const char *path, pid_t pid);
int cgroup_kill(const char *path);
int cgroup_signal_all(const char *path, int sig);
int cgroup_count(const char *path);
int cgroup_read_populated(int fd);
void launch_forget(launch *l);
void child_setup(struct passwd *pw, const launch *l, jn *env, const char *cwd, int nproc);
char **build_argv(const char *program, jn *args);
pid_t spawn_as(struct passwd *pw, const launch *l, const char *program, jn *args, jn *env, const char *cwd, int nproc,
               int fd_in, int fd_out, int fd_err, int tty_fd, int *exec_errno);
int pid_uid(pid_t pid);

void op_launch(const char *id, jn *req);
void op_kill(const char *id, jn *req);
void op_list_processes(const char *id, jn *req);
void launch_on_exit(launch *l, int status);     /* the main process was reaped */
void launch_on_cgroup_event(launch *l);          /* cgroup.events changed */
void launch_watch_cgroup(launch *l);
void launch_terminate(launch *l, int sig, int grace_ms, const char *reply_id);
launch *launch_of_pid(pid_t pid);                /* the launch whose cgroup holds pid */
void launch_on_timer(double now);                /* kill deadlines */
double launch_next_deadline(void);

/* ---------------------------------------------------------------- jobs: exec-in-prefix, mounts (jobs.c) */
typedef struct job {
    int used;
    int kind;                  /* JOB_EXEC or JOB_MOUNTS */
    pid_t pid;
    int fd;                    /* the pipe from the child */
    double t0, deadline;
    sb out;
    size_t total, max_out;
    int timed_out;
    char id[64];
    int seq;                   /* the launch of an exec (its cgroup) */
} job;
enum { JOB_EXEC = 1, JOB_MOUNTS = 2 };
extern job jobs[AGENT_MAX_JOBS];
void op_exec_in_prefix(const char *id, jn *req);
void op_mounts(const char *id, jn *req);
void job_on_readable(job *j);
void job_on_timer(double now);
double job_next_deadline(void);
int job_on_child_exit(pid_t pid, int status);   /* 1 when pid was the child of a job */

/* ---------------------------------------------------------------- consoles and the mux port (console.c) */
typedef struct console {
    int used;
    int seq;                   /* the console number in the frames = the launch seq */
    int pty;                   /* the master side */
    pid_t pid;
} console;
extern console consoles[AGENT_MAX_CONSOLES];
extern int console_port_fd;

int console_port_open(const char *device);
void console_port_on_readable(void);            /* frames from the host -> the ptys */
void console_on_pty_readable(console *c);       /* pty output -> a frame to the host */
void op_open_console(const char *id, jn *req);
void op_close_console(const char *id, jn *req);
void op_console_resize(const char *id, jn *req);
void console_on_child_exit(console *c, int status);
int console_count(void);
console *console_by_seq(int seq);

/* ---------------------------------------------------------------- open-url (agent.c) */
void url_socket_on_readable(int sock_fd);

#endif
