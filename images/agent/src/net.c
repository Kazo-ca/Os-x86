/* Os-x86 guest agent, protocol 3 (milestone 0, work item 12; decisions 161 A, 174 A, trial E6):
 * the private network of the guest.
 *
 *   net  {"action":"vm-up","vmSlot":S}                       the card eth0 at 10.86.0.S/24, route
 *                                                            through the gateway, the guest's rules
 *        {"action":"prefix-up","prefix":P,"vmSlot":S,"prefixSlot":N}
 *                                                            the namespace osx86-P, its link, its
 *                                                            address 10.86.S.(4N+2)
 *        {"action":"prefix-down","prefix":P}                 the namespace of the prefix removed
 *
 * Like prefix-ensure, the agent never runs a shell line: it runs the image's fixed script
 * /usr/lib/osx86/net-admin (root, owned by root) with an action it knows, a prefix name and
 * numbers it has checked, in a child (the loop is never blocked). Exit 0: something changed,
 * 10: nothing to do, anything else: a failure whose last line says why. What runs in a prefix
 * enters its namespace before it becomes the prefix's user (launch.c, child_setup). */
#define _GNU_SOURCE
#include "agent.h"

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

#define NET_ADMIN "/usr/lib/osx86/net-admin"
#define NET_ADMIN_TIMEOUT_MS 60000
#define NET_ADMIN_UNCHANGED 10

static int net_admin_available(void)
{
    struct stat st;
    return stat(NET_ADMIN, &st) == 0 && S_ISREG(st.st_mode) && st.st_uid == 0 && !(st.st_mode & 022) && (st.st_mode & 0100);
}

/* The reply of a finished JOB_NET: the exit code of net-admin. */
void net_job_reply(job *j, int status, sb *b)
{
    double ms = (now_s() - j->t0) * 1000;
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    if (j->timed_out || (code != 0 && code != NET_ADMIN_UNCHANGED)) {
        const char *text = j->out.buf ? j->out.buf : "";
        size_t len = j->out.len;
        while (len > 0 && (text[len - 1] == '\n' || text[len - 1] == '\r')) len--;
        size_t start = len;
        while (start > 0 && text[start - 1] != '\n') start--;
        char tail[300];
        size_t k = len - start < sizeof tail - 1 ? len - start : sizeof tail - 1;
        memcpy(tail, text + start, k);
        tail[k] = 0;
        char message[512];
        snprintf(message, sizeof message, "net-admin %.32s %s: %.300s", j->path, j->timed_out ? "timed out" : "failed", *tail ? tail : "no output");
        sb_puts(b, ",\"ok\":false,\"error\":\"net-failed\",\"message\":");
        sb_jstrz(b, message);
        sb_puts(b, "}");
        return;
    }
    sb_fmt(b, ",\"ok\":true,\"changed\":%s,\"ms\":%.1f}", code == 0 ? "true" : "false", ms);
}

void op_net(const char *id, jn *req)
{
    const char *action = json_str(req, "action", "");
    const char *prefix = json_str(req, "prefix", NULL);
    int vm_slot = json_int(req, "vmSlot", 0, 0, 255);
    int prefix_slot = json_int(req, "prefixSlot", -1, -1, 63);
    char vm_text[8], slot_text[8];
    snprintf(vm_text, sizeof vm_text, "%d", vm_slot);
    snprintf(slot_text, sizeof slot_text, "%d", prefix_slot);
    char *argv[6] = { (char *)NET_ADMIN, NULL, NULL, NULL, NULL, NULL };
    if (!strcmp(action, "vm-up")) {
        if (vm_slot < 2 || vm_slot > 254) return reply_error(id, "invalid-request", "vmSlot must be 2 to 254");
        argv[1] = (char *)"vm-up";
        argv[2] = vm_text;
    } else if (!strcmp(action, "prefix-up")) {
        if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
        if (vm_slot < 2 || vm_slot > 254 || prefix_slot < 0) return reply_error(id, "invalid-request", "vmSlot must be 2 to 254 and prefixSlot 0 to 63");
        argv[1] = (char *)"prefix-up";
        argv[2] = (char *)prefix;
        argv[3] = vm_text;
        argv[4] = slot_text;
    } else if (!strcmp(action, "prefix-down")) {
        if (!valid_prefix(prefix)) return reply_error(id, "invalid-prefix", "prefix missing or invalid");
        argv[1] = (char *)"prefix-down";
        argv[2] = (char *)prefix;
    } else {
        return reply_error(id, "invalid-request", "unknown net action '%s' (vm-up, prefix-up, prefix-down)", action);
    }
    if (!net_admin_available())
        return reply_error(id, "no-net-support", "this image has no %s: it has no private network", NET_ADMIN);

    job *j = job_new(JOB_NET, id, NET_ADMIN_TIMEOUT_MS, 16 * 1024);
    if (!j) return reply_error(id, "too-many-jobs", "the agent runs at most %d commands at a time", AGENT_MAX_JOBS);
    snprintf(j->path, sizeof j->path, "%s", argv[1]);
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
        char *const envp[] = { (char *)"PATH=/usr/sbin:/usr/bin:/sbin:/bin", (char *)"HOME=/root", (char *)"LC_ALL=C", NULL };
        execve(NET_ADMIN, argv, envp);
        _exit(127);
    }
    close(pipe_fds[1]);
    j->pid = pid;
    j->fd = pipe_fds[0];
    fd_set_nonblock(j->fd);
}
