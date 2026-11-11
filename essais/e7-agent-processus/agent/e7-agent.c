/* Essai E7 — agent invité (prototype jetable). Root dans l'invité, parle à l'hôte par un port
 * virtio-console (hvc1 : JSON par ligne), ouvre une console d'usager sur un second port (hvc2).
 * Lance les programmes comme l'usager invité de la bouteille (wine-<prefix>), jamais root, chacun
 * dans un cgroup v2 /sys/fs/cgroup/e7/<prefix>/<id> (arrêt d'un arbre entier par cgroup.kill).
 * Compilé en statique (musl, i386). Pas de bibliothèque JSON : analyseur et écrivain minimaux.
 *
 * Requête  : {"id":N,"op":"...", ...}\n      Réponse : {"id":N,"ok":true, ...}\n | {"id":N,"ok":false,"error":"..."}\n
 * Événement : {"event":"exit"|"open-url"|"console-closed", ...}\n
 * Ops : ping, list-processes, launch, kill, list-windows, mounts, exec-in-prefix, open-console,
 *       close-console, console-write (secours si le second port n'est pas disponible).
 */
#define _GNU_SOURCE
#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <poll.h>
#include <pty.h>
#include <pwd.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/sysinfo.h>
#include <sys/time.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

/* ------------------------------------------------------------------ JSON minimal (lecture) */
enum { J_NULL, J_BOOL, J_NUM, J_STR, J_ARR, J_OBJ };
typedef struct jn { int type; const char *key; char *str; double num; int b; struct jn *child, *next; } jn;
#define JPOOL 512
static jn jpool[JPOOL]; static int jtop;
static char jstrbuf[65536]; static int jstrtop;
static const char *jp;

static jn *jnew(int t) { if (jtop >= JPOOL) return NULL; jn *n = &jpool[jtop++]; memset(n, 0, sizeof *n); n->type = t; return n; }
static void jws(void) { while (*jp && isspace((unsigned char)*jp)) jp++; }
static char *jparse_str(void) {
  if (*jp != '"') return NULL;
  jp++;
  char *out = jstrbuf + jstrtop;
  while (*jp && *jp != '"') {
    if (jstrtop >= (int)sizeof jstrbuf - 8) return NULL;
    if (*jp == '\\') {
      jp++;
      switch (*jp) {
        case 'n': jstrbuf[jstrtop++] = '\n'; break;
        case 't': jstrbuf[jstrtop++] = '\t'; break;
        case 'r': jstrbuf[jstrtop++] = '\r'; break;
        case 'b': jstrbuf[jstrtop++] = '\b'; break;
        case 'f': jstrbuf[jstrtop++] = '\f'; break;
        case 'u': {
          unsigned c = 0; for (int i = 1; i <= 4 && jp[i]; i++) { c = c * 16 + (isdigit((unsigned char)jp[i]) ? jp[i] - '0' : (tolower((unsigned char)jp[i]) - 'a' + 10)); }
          jp += 4;
          if (c < 0x80) jstrbuf[jstrtop++] = (char)c;
          else if (c < 0x800) { jstrbuf[jstrtop++] = (char)(0xC0 | (c >> 6)); jstrbuf[jstrtop++] = (char)(0x80 | (c & 0x3F)); }
          else { jstrbuf[jstrtop++] = (char)(0xE0 | (c >> 12)); jstrbuf[jstrtop++] = (char)(0x80 | ((c >> 6) & 0x3F)); jstrbuf[jstrtop++] = (char)(0x80 | (c & 0x3F)); }
          break; }
        default: jstrbuf[jstrtop++] = *jp;
      }
      jp++;
    } else jstrbuf[jstrtop++] = *jp++;
  }
  if (*jp != '"') return NULL;
  jp++;
  jstrbuf[jstrtop++] = 0;
  return out;
}
static jn *jparse_val(void) {
  jws();
  if (*jp == '{') {
    jn *o = jnew(J_OBJ); if (!o) return NULL; jp++; jn **tail = &o->child;
    jws(); if (*jp == '}') { jp++; return o; }
    for (;;) {
      jws(); char *k = jparse_str(); if (!k) return NULL;
      jws(); if (*jp != ':') return NULL; jp++;
      jn *v = jparse_val(); if (!v) return NULL; v->key = k; *tail = v; tail = &v->next;
      jws(); if (*jp == ',') { jp++; continue; } if (*jp == '}') { jp++; return o; } return NULL;
    }
  }
  if (*jp == '[') {
    jn *a = jnew(J_ARR); if (!a) return NULL; jp++; jn **tail = &a->child;
    jws(); if (*jp == ']') { jp++; return a; }
    for (;;) { jn *v = jparse_val(); if (!v) return NULL; *tail = v; tail = &v->next; jws(); if (*jp == ',') { jp++; continue; } if (*jp == ']') { jp++; return a; } return NULL; }
  }
  if (*jp == '"') { char *s = jparse_str(); if (!s) return NULL; jn *n = jnew(J_STR); if (!n) return NULL; n->str = s; return n; }
  if (!strncmp(jp, "true", 4)) { jp += 4; jn *n = jnew(J_BOOL); if (n) n->b = 1; return n; }
  if (!strncmp(jp, "false", 5)) { jp += 5; jn *n = jnew(J_BOOL); return n; }
  if (!strncmp(jp, "null", 4)) { jp += 4; return jnew(J_NULL); }
  char *end; double d = strtod(jp, &end); if (end == jp) return NULL; jp = end; jn *n = jnew(J_NUM); if (n) n->num = d; return n;
}
static jn *jparse(const char *s) { jtop = 0; jstrtop = 0; jp = s; jn *v = jparse_val(); if (!v) return NULL; jws(); return *jp ? NULL : v; }
static jn *jget(jn *o, const char *k) { if (!o || o->type != J_OBJ) return NULL; for (jn *c = o->child; c; c = c->next) if (!strcmp(c->key, k)) return c; return NULL; }
static const char *jstr(jn *o, const char *k, const char *dflt) { jn *v = jget(o, k); return v && v->type == J_STR ? v->str : dflt; }
static double jnum(jn *o, const char *k, double dflt) { jn *v = jget(o, k); return v && v->type == J_NUM ? v->num : dflt; }

/* ------------------------------------------------------------------ JSON minimal (écriture) */
typedef struct { char *buf; size_t len, cap; } sb;
static void sb_put(sb *b, const char *s, size_t n) {
  if (b->len + n + 1 > b->cap) { size_t c = b->cap ? b->cap * 2 : 4096; while (c < b->len + n + 1) c *= 2; b->buf = realloc(b->buf, c); b->cap = c; }
  memcpy(b->buf + b->len, s, n); b->len += n; b->buf[b->len] = 0;
}
static void sb_puts(sb *b, const char *s) { sb_put(b, s, strlen(s)); }
static void sb_fmt(sb *b, const char *fmt, ...) { char tmp[1024]; va_list ap; va_start(ap, fmt); int n = vsnprintf(tmp, sizeof tmp, fmt, ap); va_end(ap); if (n > (int)sizeof tmp - 1) n = sizeof tmp - 1; sb_put(b, tmp, n); }
static void sb_jstr(sb *b, const char *s, size_t n) {
  sb_puts(b, "\"");
  for (size_t i = 0; i < n; i++) {
    unsigned char c = (unsigned char)s[i];
    if (c == '"') sb_puts(b, "\\\""); else if (c == '\\') sb_puts(b, "\\\\"); else if (c == '\n') sb_puts(b, "\\n"); else if (c == '\r') sb_puts(b, "\\r"); else if (c == '\t') sb_puts(b, "\\t");
    else if (c < 0x20 || c == 0x7f) sb_fmt(b, "\\u%04x", c); else sb_put(b, (const char *)&c, 1);
  }
  sb_puts(b, "\"");
}
static void sb_jstrz(sb *b, const char *s) { sb_jstr(b, s ? s : "", s ? strlen(s) : 0); }

/* ------------------------------------------------------------------ état global */
static int port_fd = -1;                 /* hvc1 : protocole */
static const char *console_dev = "/dev/hvc2";
static int cons_port_fd = -1;            /* hvc2 : console d'usager */
static int cons_pty = -1, cons_pid = 0;  /* console ouverte */
static char cons_prefix[32];
static int sock_fd = -1;                 /* open-url */
static int sigpipe[2];
static int launch_seq = 0;
static int cgroup_ok = 0;
static FILE *logf;
static struct { int pid; char prefix[32]; int seq; } children[256]; static int nchildren;

static double now_s(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return ts.tv_sec + ts.tv_nsec / 1e9; }
static void logmsg(const char *fmt, ...) { if (!logf) return; va_list ap; va_start(ap, fmt); fprintf(logf, "%.3f ", now_s()); vfprintf(logf, fmt, ap); fputc('\n', logf); fflush(logf); va_end(ap); }
static void port_write(const char *s, size_t n) { while (n) { ssize_t w = write(port_fd, s, n); if (w <= 0) { if (errno == EINTR || errno == EAGAIN) continue; logmsg("port write: %s", strerror(errno)); return; } s += w; n -= w; } }
static void send_line(sb *b) { sb_puts(b, "\n"); port_write(b->buf, b->len); free(b->buf); b->buf = NULL; b->len = b->cap = 0; }
static void reply_begin(sb *b, jn *req) { jn *id = jget(req, "id"); sb_puts(b, "{\"id\":"); if (id && id->type == J_NUM) sb_fmt(b, "%.0f", id->num); else if (id && id->type == J_STR) sb_jstrz(b, id->str); else sb_puts(b, "null"); }
static void reply_error(jn *req, const char *fmt, ...) { sb b = {0}; reply_begin(&b, req); sb_puts(&b, ",\"ok\":false,\"error\":"); char tmp[512]; va_list ap; va_start(ap, fmt); vsnprintf(tmp, sizeof tmp, fmt, ap); va_end(ap); sb_jstrz(&b, tmp); sb_puts(&b, "}"); send_line(&b); }

static int valid_prefix(const char *p) { if (!p || !*p || strlen(p) > 16) return 0; for (; *p; p++) if (!islower((unsigned char)*p) && !isdigit((unsigned char)*p) && *p != '-') return 0; return 1; }
static struct passwd *prefix_user(const char *prefix) { char name[64]; snprintf(name, sizeof name, "wine-%s", prefix); struct passwd *pw = getpwnam(name); if (!pw || pw->pw_uid == 0) return NULL; return pw; }
static const char *uid_prefix(uid_t uid, char *out, size_t n) { struct passwd *pw = getpwuid(uid); if (pw && !strncmp(pw->pw_name, "wine-", 5)) { snprintf(out, n, "%s", pw->pw_name + 5); return out; } return NULL; }

/* cgroup v2 : /sys/fs/cgroup/e7/<prefix>/<seq> ; retourne le chemin ou NULL */
static const char *cgroup_make(const char *prefix, int seq, char *out, size_t n) {
  if (!cgroup_ok) return NULL;
  char p[256]; snprintf(p, sizeof p, "/sys/fs/cgroup/e7/%s", prefix); mkdir("/sys/fs/cgroup/e7", 0755); mkdir(p, 0755);
  snprintf(out, n, "/sys/fs/cgroup/e7/%s/%d", prefix, seq); if (mkdir(out, 0755) && errno != EEXIST) return NULL; return out;
}
static int cgroup_enter(const char *cg) { char p[300]; snprintf(p, sizeof p, "%s/cgroup.procs", cg); int fd = open(p, O_WRONLY); if (fd < 0) return -1; char s[32]; int n = snprintf(s, sizeof s, "%d", getpid()); int r = write(fd, s, n) == n ? 0 : -1; close(fd); return r; }
static int cgroup_kill(const char *cg) { char p[300]; snprintf(p, sizeof p, "%s/cgroup.kill", cg); int fd = open(p, O_WRONLY); if (fd < 0) return -1; int r = write(fd, "1", 1) == 1 ? 0 : -1; close(fd); return r; }
static int cgroup_count(const char *cg) { char p[300]; snprintf(p, sizeof p, "%s/cgroup.procs", cg); FILE *f = fopen(p, "r"); if (!f) return -1; int n = 0, x; while (fscanf(f, "%d", &x) == 1) n++; fclose(f); return n; }

/* Prépare l'enfant : setsid, cgroup, limites, identité, environnement. Ne revient qu'en cas d'erreur. */
static void child_setup(struct passwd *pw, const char *cg, jn *env, const char *cwd, int nproc) {
  setsid();
  if (cg) cgroup_enter(cg);
  struct rlimit rl = { nproc, nproc }; setrlimit(RLIMIT_NPROC, &rl);
  if (initgroups(pw->pw_name, pw->pw_gid) || setgid(pw->pw_gid) || setuid(pw->pw_uid)) { _exit(126); }
  if (chdir(cwd && *cwd ? cwd : pw->pw_dir)) chdir("/");
  clearenv();
  setenv("HOME", pw->pw_dir, 1); setenv("USER", pw->pw_name, 1); setenv("LOGNAME", pw->pw_name, 1);
  setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1); setenv("DISPLAY", ":0", 1); setenv("TERM", "vt100", 1);
  char wp[300]; snprintf(wp, sizeof wp, "%s/.wine", pw->pw_dir); setenv("WINEPREFIX", wp, 1);
  setenv("WINEDLLOVERRIDES", "mscoree,mshtml=", 1); setenv("WINEDEBUG", "-all", 1);
  if (env && env->type == J_OBJ) for (jn *e = env->child; e; e = e->next) if (e->type == J_STR && strncmp(e->key, "LD_", 3)) setenv(e->key, e->str, 1);
}
static char **build_argv(const char *program, jn *args) {
  int n = 1; if (args && args->type == J_ARR) for (jn *a = args->child; a; a = a->next) n++;
  char **v = calloc(n + 1, sizeof *v); v[0] = (char *)program; int i = 1;
  if (args && args->type == J_ARR) for (jn *a = args->child; a; a = a->next) v[i++] = a->type == J_STR ? a->str : (char *)"";
  return v;
}

/* ------------------------------------------------------------------ ops */
static void op_ping(jn *req) {
  sb b = {0}; reply_begin(&b, req);
  struct sysinfo si; sysinfo(&si);
  long rss = 0; FILE *f = fopen("/proc/self/status", "r"); char l[256]; while (f && fgets(l, sizeof l, f)) if (!strncmp(l, "VmRSS:", 6)) rss = atol(l + 6); if (f) fclose(f);
  sb_fmt(&b, ",\"ok\":true,\"uptime\":%ld,\"rss_kb\":%ld,\"console\":%d,\"cgroup\":%d,\"children\":%d}", si.uptime, rss, cons_pid, cgroup_ok, nchildren);
  send_line(&b);
}

static void op_list_processes(jn *req) {
  sb b = {0}; reply_begin(&b, req); sb_puts(&b, ",\"ok\":true,\"processes\":[");
  DIR *d = opendir("/proc"); struct dirent *e; int first = 1;
  while (d && (e = readdir(d))) {
    if (!isdigit((unsigned char)e->d_name[0])) continue;
    char p[64], l[512]; int pid = atoi(e->d_name), ppid = 0, uid = -1; char name[64] = "", state[4] = "";
    snprintf(p, sizeof p, "/proc/%d/status", pid); FILE *f = fopen(p, "r"); if (!f) continue;
    while (fgets(l, sizeof l, f)) { if (!strncmp(l, "Name:", 5)) sscanf(l + 5, " %63[^\n]", name); else if (!strncmp(l, "State:", 6)) sscanf(l + 6, " %1s", state); else if (!strncmp(l, "PPid:", 5)) ppid = atoi(l + 5); else if (!strncmp(l, "Uid:", 4)) uid = atoi(l + 4); }
    fclose(f);
    int pgid = 0; snprintf(p, sizeof p, "/proc/%d/stat", pid); f = fopen(p, "r"); if (f) { if (fgets(l, sizeof l, f)) { char *rp = strrchr(l, ')'); if (rp) sscanf(rp + 1, " %*c %*d %d", &pgid); } fclose(f); }
    char cmd[512] = ""; snprintf(p, sizeof p, "/proc/%d/cmdline", pid); int fd = open(p, O_RDONLY); int n = fd >= 0 ? (int)read(fd, cmd, sizeof cmd - 1) : 0; if (fd >= 0) close(fd); if (n < 0) n = 0; for (int i = 0; i < n; i++) if (!cmd[i]) cmd[i] = ' '; while (n && cmd[n - 1] == ' ') n--; cmd[n] = 0;
    char cg[256] = ""; snprintf(p, sizeof p, "/proc/%d/cgroup", pid); f = fopen(p, "r"); if (f) { if (fgets(cg, sizeof cg, f)) { char *nl = strchr(cg, '\n'); if (nl) *nl = 0; char *c = strstr(cg, "::"); if (c) memmove(cg, c + 2, strlen(c + 2) + 1); } fclose(f); }
    char pfx[32]; const char *pf = uid_prefix(uid, pfx, sizeof pfx);
    if (!first) sb_puts(&b, ","); first = 0;
    sb_fmt(&b, "{\"pid\":%d,\"ppid\":%d,\"pgid\":%d,\"uid\":%d,\"state\":\"%s\",\"prefix\":", pid, ppid, pgid, uid, state);
    if (pf) sb_jstrz(&b, pf); else sb_puts(&b, "null");
    sb_puts(&b, ",\"comm\":"); sb_jstrz(&b, name); sb_puts(&b, ",\"cmd\":"); sb_jstrz(&b, cmd); sb_puts(&b, ",\"cgroup\":"); sb_jstrz(&b, cg); sb_puts(&b, "}");
  }
  if (d) closedir(d);
  sb_puts(&b, "]}"); send_line(&b);
}

static void op_launch(jn *req) {
  const char *prefix = jstr(req, "prefix", NULL), *program = jstr(req, "program", NULL);
  if (!valid_prefix(prefix)) return reply_error(req, "prefix invalide");
  struct passwd *pw = prefix_user(prefix); if (!pw) return reply_error(req, "usager wine-%s inconnu", prefix);
  if (!program || !*program) return reply_error(req, "program manquant");
  int seq = ++launch_seq; char cg[256]; const char *cgp = cgroup_make(prefix, seq, cg, sizeof cg);
  char logp[300]; snprintf(logp, sizeof logp, "%s/e7-launch-%d.log", pw->pw_dir, seq);
  char **argv = build_argv(program, jget(req, "args"));
  double t0 = now_s();
  pid_t pid = fork();
  if (pid < 0) return reply_error(req, "fork: %s", strerror(errno));
  if (pid == 0) {
    child_setup(pw, cgp, jget(req, "env"), jstr(req, "cwd", NULL), (int)jnum(req, "nproc", 256));
    int fd = open("/dev/null", O_RDONLY); dup2(fd, 0); int lf = open(logp, O_WRONLY | O_CREAT | O_APPEND, 0600); if (lf < 0) lf = open("/dev/null", O_WRONLY); dup2(lf, 1); dup2(lf, 2);
    execvp(program, argv); _exit(127);
  }
  free(argv);
  if (nchildren < 256) { children[nchildren].pid = pid; children[nchildren].seq = seq; snprintf(children[nchildren].prefix, 32, "%s", prefix); nchildren++; }
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"pid\":%d,\"uid\":%d,\"seq\":%d,\"fork_ms\":%.1f,\"cgroup\":", pid, pw->pw_uid, seq, (now_s() - t0) * 1000); sb_jstrz(&b, cgp); sb_puts(&b, ",\"log\":"); sb_jstrz(&b, logp); sb_puts(&b, "}"); send_line(&b);
  logmsg("launch %s pid=%d prefix=%s cg=%s", program, pid, prefix, cgp ? cgp : "-");
}

static int pid_uid(int pid) { char p[64], l[256]; snprintf(p, sizeof p, "/proc/%d/status", pid); FILE *f = fopen(p, "r"); if (!f) return -1; int uid = -1; while (fgets(l, sizeof l, f)) if (!strncmp(l, "Uid:", 4)) { uid = atoi(l + 4); break; } fclose(f); return uid; }
static int kill_uid(uid_t uid, int sig) { int n = 0; DIR *d = opendir("/proc"); struct dirent *e; while (d && (e = readdir(d))) { if (!isdigit((unsigned char)e->d_name[0])) continue; int pid = atoi(e->d_name); if (pid == getpid()) continue; if ((uid_t)pid_uid(pid) == uid && !kill(pid, sig)) n++; } if (d) closedir(d); return n; }
static int count_uid(uid_t uid) { int n = 0; DIR *d = opendir("/proc"); struct dirent *e; while (d && (e = readdir(d))) { if (!isdigit((unsigned char)e->d_name[0])) continue; if ((uid_t)pid_uid(atoi(e->d_name)) == uid) n++; } if (d) closedir(d); return n; }

/* kill : scope = pid (défaut) | group (pgid du pid) | prefix (tous les processus de l'uid, par balayage de /proc)
 *        | cgroup (cgroup.kill sur le cgroup du lancement « seq » ou de toute la bouteille « prefix ») */
static void op_kill(jn *req) {
  const char *scope = jstr(req, "scope", "pid"); int sig = (int)jnum(req, "signal", SIGTERM); int pid = (int)jnum(req, "pid", 0);
  double t0 = now_s(); int n = 0, rc = 0; char what[300] = "";
  if (!strcmp(scope, "pid") || !strcmp(scope, "group")) {
    if (pid <= 1) return reply_error(req, "pid manquant");
    int uid = pid_uid(pid); if (uid < 0) return reply_error(req, "pid %d inexistant", pid);
    if (uid == 0 && !jget(req, "allow_root")) return reply_error(req, "pid %d appartient à root : refusé", pid);
    rc = !strcmp(scope, "pid") ? kill(pid, sig) : kill(-pid, sig); n = rc == 0; snprintf(what, sizeof what, "%s %d", scope, pid);
    if (rc) return reply_error(req, "kill: %s", strerror(errno));
  } else if (!strcmp(scope, "prefix")) {
    const char *prefix = jstr(req, "prefix", NULL); if (!valid_prefix(prefix)) return reply_error(req, "prefix invalide");
    struct passwd *pw = prefix_user(prefix); if (!pw) return reply_error(req, "usager inconnu");
    for (int pass = 0; pass < 50; pass++) { int k = kill_uid(pw->pw_uid, sig); n += k; if (sig != SIGKILL || count_uid(pw->pw_uid) == 0) break; usleep(10000); }
    snprintf(what, sizeof what, "uid %d", pw->pw_uid);
  } else if (!strcmp(scope, "cgroup")) {
    const char *prefix = jstr(req, "prefix", NULL); int seq = (int)jnum(req, "seq", 0);
    if (!valid_prefix(prefix)) return reply_error(req, "prefix invalide");
    if (seq) snprintf(what, sizeof what, "/sys/fs/cgroup/e7/%s/%d", prefix, seq); else snprintf(what, sizeof what, "/sys/fs/cgroup/e7/%s", prefix);
    n = cgroup_count(what); rc = cgroup_kill(what); if (rc) return reply_error(req, "cgroup.kill %s: %s", what, strerror(errno));
    for (int i = 0; i < 500 && cgroup_count(what) > 0; i++) usleep(2000);  /* attend la disparition (max 1 s) */
  } else return reply_error(req, "scope inconnu");
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"signal\":%d,\"count\":%d,\"ms\":%.1f,\"target\":", sig, n, (now_s() - t0) * 1000); sb_jstrz(&b, what); sb_puts(&b, "}"); send_line(&b);
  logmsg("kill %s sig=%d n=%d", what, sig, n);
}

/* Exécute un programme comme l'usager de la bouteille, entrée fermée, sortie capturée (bornée), durée bornée. */
static void run_capture(const char *prefix, const char *program, jn *args, jn *env, int timeout_ms, int max_out, int as_root, sb *out, int *exit_code, int *sig, int *timed_out, double *ms) {
  struct passwd *pw = as_root ? getpwuid(0) : prefix_user(prefix);
  int pfd[2]; pipe(pfd); double t0 = now_s(); *exit_code = -1; *sig = 0; *timed_out = 0;
  char **argv = build_argv(program, args);
  pid_t pid = fork();
  if (pid == 0) {
    if (as_root) { setsid(); clearenv(); setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1); setenv("DISPLAY", ":0", 1); setenv("HOME", "/root", 1); }
    else { int seq = ++launch_seq; char cg[256]; child_setup(pw, cgroup_make(prefix, seq, cg, sizeof cg), env, NULL, 256); }
    int fd = open("/dev/null", O_RDONLY); dup2(fd, 0); dup2(pfd[1], 1); dup2(pfd[1], 2); close(pfd[0]); close(pfd[1]);
    execvp(program, argv); dprintf(2, "exec %s: %s\n", program, strerror(errno)); _exit(127);
  }
  free(argv); close(pfd[1]);
  int total = 0; char buf[4096];
  for (;;) {
    struct pollfd p = { pfd[0], POLLIN, 0 }; int left = timeout_ms - (int)((now_s() - t0) * 1000);
    if (left <= 0) { *timed_out = 1; kill(-pid, SIGKILL); break; }
    int r = poll(&p, 1, left); if (r <= 0) { if (r == 0) { *timed_out = 1; kill(-pid, SIGKILL); } break; }
    ssize_t n = read(pfd[0], buf, sizeof buf); if (n <= 0) break;
    if (total < max_out) { int k = (int)n; if (total + k > max_out) k = max_out - total; sb_put(out, buf, k); } total += n;
  }
  close(pfd[0]);
  int st; waitpid(pid, &st, 0); if (WIFEXITED(st)) *exit_code = WEXITSTATUS(st); if (WIFSIGNALED(st)) *sig = WTERMSIG(st);
  *ms = (now_s() - t0) * 1000;
  for (int i = 0; i < nchildren; i++) if (children[i].pid == pid) { children[i] = children[--nchildren]; break; }
}
static void op_exec(jn *req) {
  const char *prefix = jstr(req, "prefix", NULL), *program = jstr(req, "program", NULL);
  if (!valid_prefix(prefix) || !prefix_user(prefix)) return reply_error(req, "prefix invalide");
  if (!program || !*program) return reply_error(req, "program manquant");
  sb out = {0}; int ec, sg, to; double ms;
  run_capture(prefix, program, jget(req, "args"), jget(req, "env"), (int)jnum(req, "timeout_ms", 30000), (int)jnum(req, "max_output", 65536), 0, &out, &ec, &sg, &to, &ms);
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"exit_code\":%d,\"signal\":%d,\"timed_out\":%d,\"ms\":%.1f,\"output\":", ec, sg, to, ms); sb_jstr(&b, out.buf ? out.buf : "", out.len); sb_puts(&b, "}"); send_line(&b); free(out.buf);
}

/* list-windows : script auxiliaire (xwininfo/xprop) exécuté comme root, lignes « id pid titre ». */
static void op_list_windows(jn *req) {
  sb out = {0}; int ec, sg, to; double ms;
  run_capture(NULL, "/usr/local/lib/e7/list-windows.sh", NULL, NULL, 20000, 65536, 1, &out, &ec, &sg, &to, &ms);
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"exit_code\":%d,\"ms\":%.1f,\"windows\":[", ec, ms);
  int first = 1; char *s = out.buf ? out.buf : (char *)"", *line;
  while ((line = strsep(&s, "\n")) != NULL) {
    unsigned long id; int pid; char title[256] = ""; if (sscanf(line, "0x%lx %d %255[^\n]", &id, &pid, title) < 2) continue;
    char pfx[32]; const char *pf = uid_prefix(pid > 0 ? pid_uid(pid) : -1, pfx, sizeof pfx);
    if (!first) sb_puts(&b, ","); first = 0;
    sb_fmt(&b, "{\"id\":\"0x%lx\",\"pid\":%d,\"uid\":%d,\"prefix\":", id, pid, pid > 0 ? pid_uid(pid) : -1); if (pf) sb_jstrz(&b, pf); else sb_puts(&b, "null"); sb_puts(&b, ",\"title\":"); sb_jstrz(&b, title); sb_puts(&b, "}");
  }
  sb_puts(&b, "]}"); send_line(&b); free(out.buf);
}

/* mounts : pour chaque montage 9p/ext4/tmpfs hors /proc,/sys,/dev : statfs + opendir/readdir dans un enfant, délai borné. */
static void op_mounts(jn *req) {
  int timeout_ms = (int)jnum(req, "timeout_ms", 8000);
  FILE *f = fopen("/proc/mounts", "r"); if (!f) return reply_error(req, "/proc/mounts");
  sb b = {0}; reply_begin(&b, req); sb_puts(&b, ",\"ok\":true,\"mounts\":["); int first = 1; char l[512];
  while (fgets(l, sizeof l, f)) {
    char dev[128], path[256], type[32]; if (sscanf(l, "%127s %255s %31s", dev, path, type) != 3) continue;
    if (strcmp(type, "9p") && strcmp(type, "ext4") && strcmp(type, "tmpfs")) continue;
    if (!strncmp(path, "/proc", 5) || !strncmp(path, "/sys", 4) || !strncmp(path, "/dev", 4) || !strcmp(path, "/run")) continue;
    int pfd[2]; pipe(pfd); double t0 = now_s(); pid_t pid = fork();
    if (pid == 0) {
      close(pfd[0]); struct statfs sf; int r = statfs(path, &sf); int e1 = r ? errno : 0; int n = -1, e2 = 0;
      DIR *d = opendir(path); if (!d) e2 = errno; else { n = 0; errno = 0; struct dirent *de; while ((de = readdir(d))) n++; e2 = errno; closedir(d); }
      dprintf(pfd[1], "%d %d %d %d\n", e1, n, e2, (int)sf.f_blocks); _exit(0);
    }
    close(pfd[1]); struct pollfd p = { pfd[0], POLLIN, 0 }; int r = poll(&p, 1, timeout_ms); char res[128] = ""; int e1 = -1, n = -1, e2 = -1, blocks = 0; const char *state = "hung";
    if (r > 0) { ssize_t k = read(pfd[0], res, sizeof res - 1); if (k > 0) { res[k] = 0; sscanf(res, "%d %d %d %d", &e1, &n, &e2, &blocks); state = (e1 || e2) ? "error" : "ok"; } }
    if (r <= 0) kill(pid, SIGKILL); close(pfd[0]); int st; waitpid(pid, &st, 0);
    if (!first) sb_puts(&b, ","); first = 0;
    sb_puts(&b, "{\"path\":"); sb_jstrz(&b, path); sb_puts(&b, ",\"type\":"); sb_jstrz(&b, type); sb_puts(&b, ",\"dev\":"); sb_jstrz(&b, dev);
    sb_fmt(&b, ",\"state\":\"%s\",\"statfs_errno\":%d,\"readdir_errno\":%d,\"entries\":%d,\"blocks\":%d,\"ms\":%.1f,\"error\":", state, e1, e2, n, blocks, (now_s() - t0) * 1000);
    sb_jstrz(&b, e1 > 0 ? strerror(e1) : e2 > 0 ? strerror(e2) : r <= 0 ? "délai dépassé" : ""); sb_puts(&b, "}");
  }
  fclose(f); sb_puts(&b, "]}"); send_line(&b);
}

static int open_raw(const char *dev) { int fd = open(dev, O_RDWR | O_NOCTTY | O_NONBLOCK); if (fd < 0) return -1; struct termios t; if (!tcgetattr(fd, &t)) { cfmakeraw(&t); tcsetattr(fd, TCSANOW, &t); } return fd; }

static void op_open_console(jn *req) {
  const char *prefix = jstr(req, "prefix", NULL); if (!valid_prefix(prefix)) return reply_error(req, "prefix invalide");
  struct passwd *pw = prefix_user(prefix); if (!pw) return reply_error(req, "usager inconnu");
  if (cons_pid) return reply_error(req, "console déjà ouverte (pid %d)", cons_pid);
  if (cons_port_fd < 0) { cons_port_fd = open_raw(console_dev); if (cons_port_fd < 0) return reply_error(req, "port console %s: %s", console_dev, strerror(errno)); }
  int master, slave; struct winsize ws = { 25, 80, 0, 0 };
  if (openpty(&master, &slave, NULL, NULL, &ws)) return reply_error(req, "openpty: %s", strerror(errno));
  fchown(slave, pw->pw_uid, pw->pw_gid); fchmod(slave, 0600);
  int seq = ++launch_seq; char cg[256]; const char *cgp = cgroup_make(prefix, seq, cg, sizeof cg);
  pid_t pid = fork(); if (pid < 0) return reply_error(req, "fork");
  if (pid == 0) {
    close(master); setsid(); ioctl(slave, TIOCSCTTY, 0);
    if (cgp) cgroup_enter(cgp);
    struct rlimit rl = { (int)jnum(req, "nproc", 256), (int)jnum(req, "nproc", 256) }; setrlimit(RLIMIT_NPROC, &rl);
    initgroups(pw->pw_name, pw->pw_gid); setgid(pw->pw_gid); setuid(pw->pw_uid); chdir(pw->pw_dir);
    clearenv(); setenv("HOME", pw->pw_dir, 1); setenv("USER", pw->pw_name, 1); setenv("LOGNAME", pw->pw_name, 1); setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1); setenv("DISPLAY", ":0", 1); setenv("TERM", "vt100", 1);
    char wp[300]; snprintf(wp, sizeof wp, "%s/.wine", pw->pw_dir); setenv("WINEPREFIX", wp, 1); setenv("WINEDLLOVERRIDES", "mscoree,mshtml=", 1); setenv("WINEDEBUG", "-all", 1);
    char ps1[64]; snprintf(ps1, sizeof ps1, "%s:\\w$ ", pw->pw_name); setenv("PS1", ps1, 1);
    dup2(slave, 0); dup2(slave, 1); dup2(slave, 2); if (slave > 2) close(slave);
    execl("/bin/sh", "sh", "-i", (char *)NULL); _exit(127);
  }
  close(slave); cons_pty = master; cons_pid = pid; snprintf(cons_prefix, sizeof cons_prefix, "%s", prefix); fcntl(master, F_SETFL, O_NONBLOCK);
  if (nchildren < 256) { children[nchildren].pid = pid; children[nchildren].seq = seq; snprintf(children[nchildren].prefix, 32, "%s", prefix); nchildren++; }
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"pid\":%d,\"uid\":%d,\"seq\":%d,\"port\":", pid, pw->pw_uid, seq); sb_jstrz(&b, console_dev); sb_puts(&b, ",\"cgroup\":"); sb_jstrz(&b, cgp); sb_puts(&b, "}"); send_line(&b);
  logmsg("console pid=%d prefix=%s", pid, prefix);
}
static void console_teardown(void) { if (cons_pty >= 0) close(cons_pty); cons_pty = -1; cons_pid = 0; }
static void op_close_console(jn *req) {
  if (!cons_pid) return reply_error(req, "aucune console");
  int sig = (int)jnum(req, "signal", SIGHUP); int pid = cons_pid; kill(-pid, sig); console_teardown();
  sb b = {0}; reply_begin(&b, req); sb_fmt(&b, ",\"ok\":true,\"pid\":%d,\"signal\":%d}", pid, sig); send_line(&b);
}
static void op_console_write(jn *req) { const char *t = jstr(req, "text", ""); if (cons_pty < 0) return reply_error(req, "aucune console"); write(cons_pty, t, strlen(t)); sb b = {0}; reply_begin(&b, req); sb_puts(&b, ",\"ok\":true}"); send_line(&b); }

static void handle_request(char *line) {
  jn *req = jparse(line);
  if (!req || req->type != J_OBJ) { sb b = {0}; sb_puts(&b, "{\"id\":null,\"ok\":false,\"error\":\"JSON invalide\"}"); send_line(&b); return; }
  const char *op = jstr(req, "op", "");
  double t0 = now_s();
  if (!strcmp(op, "ping")) op_ping(req);
  else if (!strcmp(op, "list-processes")) op_list_processes(req);
  else if (!strcmp(op, "launch")) op_launch(req);
  else if (!strcmp(op, "kill")) op_kill(req);
  else if (!strcmp(op, "list-windows")) op_list_windows(req);
  else if (!strcmp(op, "mounts")) op_mounts(req);
  else if (!strcmp(op, "exec-in-prefix")) op_exec(req);
  else if (!strcmp(op, "open-console")) op_open_console(req);
  else if (!strcmp(op, "close-console")) op_close_console(req);
  else if (!strcmp(op, "console-write")) op_console_write(req);
  else reply_error(req, "op inconnue: %s", op);
  logmsg("op %s %.1f ms", op, (now_s() - t0) * 1000);
}

/* ------------------------------------------------------------------ événements */
static void on_sigchld(int s) { (void)s; int e = errno; write(sigpipe[1], "c", 1); errno = e; }
static void reap(void) {
  int st; pid_t pid;
  while ((pid = waitpid(-1, &st, WNOHANG)) > 0) {
    int known = -1; for (int i = 0; i < nchildren; i++) if (children[i].pid == pid) { known = i; break; }
    sb b = {0}; sb_fmt(&b, "{\"event\":\"exit\",\"pid\":%d,\"known\":%d,\"exit_code\":%d,\"signal\":%d,\"prefix\":", pid, known >= 0, WIFEXITED(st) ? WEXITSTATUS(st) : -1, WIFSIGNALED(st) ? WTERMSIG(st) : 0);
    sb_jstrz(&b, known >= 0 ? children[known].prefix : ""); sb_puts(&b, "}"); send_line(&b);
    if (known >= 0) children[known] = children[--nchildren];
    if (pid == cons_pid) { console_teardown(); sb c = {0}; sb_fmt(&c, "{\"event\":\"console-closed\",\"pid\":%d}", pid); send_line(&c); }
  }
}
static void on_url_client(void) {
  int c = accept(sock_fd, NULL, NULL); if (c < 0) return;
  struct ucred cr = {0}; socklen_t cl = sizeof cr; getsockopt(c, SOL_SOCKET, SO_PEERCRED, &cr, &cl);
  char buf[2048]; struct pollfd p = { c, POLLIN, 0 }; int n = 0; if (poll(&p, 1, 500) > 0) n = (int)read(c, buf, sizeof buf - 1); if (n < 0) n = 0; buf[n] = 0; char *nl = strchr(buf, '\n'); if (nl) *nl = 0;
  close(c);
  char pfx[32]; const char *pf = uid_prefix(cr.uid, pfx, sizeof pfx);
  sb b = {0}; sb_fmt(&b, "{\"event\":\"open-url\",\"uid\":%d,\"pid\":%d,\"prefix\":", cr.uid, cr.pid); if (pf) sb_jstrz(&b, pf); else sb_puts(&b, "null"); sb_puts(&b, ",\"url\":"); sb_jstrz(&b, buf); sb_puts(&b, "}"); send_line(&b);
}

int main(int argc, char **argv) {
  const char *port = "/dev/hvc1", *sock = "/run/e7/open-url.sock", *logp = "/var/log/e7-agent.log";
  for (int i = 1; i < argc - 1; i++) { if (!strcmp(argv[i], "-p")) port = argv[++i]; else if (!strcmp(argv[i], "-c")) console_dev = argv[++i]; else if (!strcmp(argv[i], "-s")) sock = argv[++i]; else if (!strcmp(argv[i], "-l")) logp = argv[++i]; }
  logf = fopen(logp, "a");
  prctl(PR_SET_CHILD_SUBREAPER, 1);  /* les orphelins des programmes lancés reviennent à l'agent (événements exit) */
  signal(SIGPIPE, SIG_IGN); pipe(sigpipe); fcntl(sigpipe[0], F_SETFL, O_NONBLOCK);
  struct sigaction sa = {0}; sa.sa_handler = on_sigchld; sa.sa_flags = SA_RESTART | SA_NOCLDSTOP; sigaction(SIGCHLD, &sa, NULL);
  struct stat st; cgroup_ok = !stat("/sys/fs/cgroup/cgroup.procs", &st);
  port_fd = open_raw(port); if (port_fd < 0) { logmsg("port %s: %s", port, strerror(errno)); fprintf(stderr, "port %s: %s\n", port, strerror(errno)); return 1; }
  mkdir("/run/e7", 0755); unlink(sock);
  sock_fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0); struct sockaddr_un su = { AF_UNIX, "" }; snprintf(su.sun_path, sizeof su.sun_path, "%s", sock);
  if (bind(sock_fd, (struct sockaddr *)&su, sizeof su) || listen(sock_fd, 8)) { logmsg("socket: %s", strerror(errno)); close(sock_fd); sock_fd = -1; } else chmod(sock, 0666);
  { sb b = {0}; sb_fmt(&b, "{\"event\":\"ready\",\"pid\":%d,\"cgroup\":%d,\"port\":", getpid(), cgroup_ok); sb_jstrz(&b, port); sb_puts(&b, "}"); send_line(&b); }
  logmsg("ready port=%s console=%s cgroup=%d", port, console_dev, cgroup_ok);
  static char line[65536]; int ll = 0; char buf[4096];
  for (;;) {
    struct pollfd p[5]; int n = 0;
    p[n++] = (struct pollfd){ port_fd, POLLIN, 0 };
    p[n++] = (struct pollfd){ sigpipe[0], POLLIN, 0 };
    int isock = -1, ipty = -1, icons = -1;
    if (sock_fd >= 0) { isock = n; p[n++] = (struct pollfd){ sock_fd, POLLIN, 0 }; }
    if (cons_pty >= 0) { ipty = n; p[n++] = (struct pollfd){ cons_pty, POLLIN, 0 }; }
    if (cons_pty >= 0 && cons_port_fd >= 0) { icons = n; p[n++] = (struct pollfd){ cons_port_fd, POLLIN, 0 }; }
    if (poll(p, n, -1) < 0) { if (errno == EINTR) continue; break; }
    if (p[0].revents & (POLLIN | POLLHUP)) {
      ssize_t r = read(port_fd, buf, sizeof buf);
      if (r > 0) for (ssize_t i = 0; i < r; i++) {
        if (buf[i] == '\n') { line[ll] = 0; if (ll) handle_request(line); ll = 0; }
        else if (ll < (int)sizeof line - 1) line[ll++] = buf[i];
      }
    }
    if (p[1].revents & POLLIN) { while (read(sigpipe[0], buf, sizeof buf) > 0) {} reap(); }
    if (isock >= 0 && (p[isock].revents & POLLIN)) on_url_client();
    if (ipty >= 0 && (p[ipty].revents & (POLLIN | POLLHUP))) { ssize_t r = read(cons_pty, buf, sizeof buf); if (r > 0) { if (cons_port_fd >= 0) { ssize_t off = 0; while (off < r) { ssize_t w = write(cons_port_fd, buf + off, r - off); if (w <= 0) { if (errno == EAGAIN) { usleep(2000); continue; } break; } off += w; } } } else if (r == 0 || (r < 0 && errno != EAGAIN && errno != EINTR)) { /* pty fermé : la fin est signalée par SIGCHLD */ } }
    if (icons >= 0 && (p[icons].revents & POLLIN)) { ssize_t r = read(cons_port_fd, buf, sizeof buf); if (r > 0 && cons_pty >= 0) write(cons_pty, buf, r); }
  }
  return 0;
}
