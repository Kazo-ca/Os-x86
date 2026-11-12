/* Os-x86 guest agent: the open-url socket (decision 139, trial E7). A guest program hands a URL
 * to the host by writing one line on a unix socket (osx86-open-url does it); the agent reads the
 * caller's uid from the socket itself (SO_PEERCRED), so a program cannot pass for another
 * prefix, and reports {"event":"open-url"}. The agent does nothing else with the URL: the host
 * decides.
 *
 * The socket is open to every guest user, so nothing here may block: a connection is read by
 * the loop like any other descriptor, has URL_CLIENT_TIMEOUT_MS to send its line, and at most
 * URL_MAX_CLIENTS are served at a time, URL_MAX_CLIENTS_PER_USER for one user (the others are
 * closed at once). */
#define _GNU_SOURCE
#include "agent.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

#define URL_CLIENT_TIMEOUT_MS 500
#define URL_MAX_CLIENTS_PER_USER 2

int url_socket_fd = -1;
url_client url_clients[URL_MAX_CLIENTS];

int url_socket_open(const char *path)
{
    unlink(path);
    int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    struct sockaddr_un address;
    memset(&address, 0, sizeof address);
    address.sun_family = AF_UNIX;
    snprintf(address.sun_path, sizeof address.sun_path, "%s", path);
    if (bind(fd, (struct sockaddr *)&address, sizeof address) || listen(fd, URL_MAX_CLIENTS) || chmod(path, 0666)) {
        logmsg("open-url socket %s: %s", path, strerror(errno));
        close(fd);
        return -1;
    }
    url_socket_fd = fd;
    return 0;
}

void url_on_accept(void)
{
    int fd = accept4(url_socket_fd, NULL, NULL, SOCK_NONBLOCK | SOCK_CLOEXEC);
    if (fd < 0) return;
    url_client *c = NULL;
    for (int i = 0; i < URL_MAX_CLIENTS && !c; i++) if (!url_clients[i].used) c = &url_clients[i];
    struct ucred credentials;
    socklen_t length = sizeof credentials;
    memset(&credentials, 0, sizeof credentials);
    int unknown = getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &credentials, &length);
    int same_user = 0;
    for (int i = 0; i < URL_MAX_CLIENTS; i++) same_user += url_clients[i].used && url_clients[i].uid == credentials.uid;
    if (!c || unknown || same_user >= URL_MAX_CLIENTS_PER_USER) {
        close(fd);  /* too many callers at once (one user cannot take every place), or a caller the kernel does not name */
        return;
    }
    memset(c, 0, sizeof *c);
    c->used = 1;
    c->fd = fd;
    c->uid = credentials.uid;
    c->pid = credentials.pid;
    c->deadline = now_s() + URL_CLIENT_TIMEOUT_MS / 1000.0;
}

static void url_client_finish(url_client *c, int complete)
{
    close(c->fd);
    if (complete && c->length > 0) {
        char prefix[AGENT_MAX_PREFIX + 1];
        const char *owner = uid_prefix(c->uid, prefix, sizeof prefix);
        sb b = { 0 };
        event_begin(&b, "open-url");
        sb_fmt(&b, ",\"uid\":%d,\"pid\":%d,\"prefix\":", (int)c->uid, (int)c->pid);
        if (owner) sb_jstrz(&b, owner); else sb_puts(&b, "null");
        sb_puts(&b, ",\"url\":");
        sb_jstr(&b, c->text, c->length);
        sb_puts(&b, "}");
        send_line(&b);
    }
    memset(c, 0, sizeof *c);
}

/* Bytes of a caller: the URL ends at the first LF, at the end of the stream or when the buffer is
 * full. A caller that sends nothing, or no end, before its deadline is dropped without event. */
void url_client_on_readable(url_client *c)
{
    ssize_t n = read(c->fd, c->text + c->length, sizeof c->text - c->length);
    if (n < 0 && (errno == EAGAIN || errno == EINTR)) return;
    if (n <= 0) return url_client_finish(c, n == 0);
    char *newline = memchr(c->text + c->length, '\n', (size_t)n);
    c->length = newline ? (size_t)(newline - c->text) : c->length + (size_t)n;
    if (newline || c->length == sizeof c->text) url_client_finish(c, 1);
}

void url_on_timer(double now)
{
    for (int i = 0; i < URL_MAX_CLIENTS; i++)
        if (url_clients[i].used && now >= url_clients[i].deadline) url_client_finish(&url_clients[i], 0);
}

double url_next_deadline(void)
{
    double next = 0;
    for (int i = 0; i < URL_MAX_CLIENTS; i++)
        if (url_clients[i].used && (next <= 0 || url_clients[i].deadline < next)) next = url_clients[i].deadline;
    return next;
}
