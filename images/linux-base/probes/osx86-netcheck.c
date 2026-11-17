/* Os-x86 linux-base, network probe (work item 12; trial E6): what a program of a prefix can and
 * cannot reach on the private network. Tiny and static, like the R134.5 probes; it gives a user
 * nothing a program of its own could not do.
 *
 *   osx86-netcheck connect <ipv4> <port>         connect, print what the peer sends until it closes
 *   osx86-netcheck get <ipv4> <port> <path>      HTTP/1.0 GET, print the status line, bytes and MiB/s
 *   osx86-netcheck listen <port>                 accept one connection, send one line, close
 *   osx86-netcheck udp <ipv4> <port>             send one datagram (DNS is port 53)
 *   osx86-netcheck ipv6                          try a TCP connection over IPv6
 *
 * Every line it prints starts with "netcheck "; "local=" is the source address the peer sees.
 * Exit codes: 0 done, 7 the connection or the datagram was refused or timed out, 2 usage, 1 other. */
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#define CONNECT_TIMEOUT_MS 15000
#define READ_TIMEOUT_S 60

static double now_s(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}

static int usage(void)
{
    fprintf(stderr, "usage: osx86-netcheck connect|get|listen|udp|ipv6 ...\n");
    return 2;
}

static int address_of(const char *ip, const char *port, struct sockaddr_in *out)
{
    memset(out, 0, sizeof *out);
    out->sin_family = AF_INET;
    int number = atoi(port);
    if (number < 1 || number > 65535 || inet_pton(AF_INET, ip, &out->sin_addr) != 1) return -1;
    out->sin_port = htons((unsigned short)number);
    return 0;
}

static void print_local(int fd)
{
    struct sockaddr_in local;
    socklen_t length = sizeof local;
    char text[INET_ADDRSTRLEN] = "?";
    if (getsockname(fd, (struct sockaddr *)&local, &length) == 0) inet_ntop(AF_INET, &local.sin_addr, text, sizeof text);
    printf("netcheck local=%s\n", text);
}

/* A TCP connection with a deadline; -1 with errno set when it is refused or times out. */
static int connect_to(const struct sockaddr_in *peer)
{
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    int flags = fcntl(fd, F_GETFL);
    fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    if (connect(fd, (const struct sockaddr *)peer, sizeof *peer) < 0 && errno != EINPROGRESS) {
        int e = errno;
        close(fd);
        errno = e;
        return -1;
    }
    struct pollfd p = { fd, POLLOUT, 0 };
    int ready = poll(&p, 1, CONNECT_TIMEOUT_MS);
    int error = 0;
    socklen_t length = sizeof error;
    if (ready <= 0 || getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &length) < 0 || error) {
        close(fd);
        errno = ready == 0 ? ETIMEDOUT : (error ? error : EIO);
        return -1;
    }
    fcntl(fd, F_SETFL, flags);
    struct timeval timeout = { READ_TIMEOUT_S, 0 };
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof timeout);
    return fd;
}

static int refused(const char *what)
{
    printf("netcheck refused %s: %s\n", what, strerror(errno));
    return 7;
}

static int do_connect(const char *ip, const char *port, const char *path)
{
    struct sockaddr_in peer;
    if (address_of(ip, port, &peer)) return usage();
    double started = now_s();
    int fd = connect_to(&peer);
    if (fd < 0) return refused("connect");
    print_local(fd);
    if (path) {
        char request[512];
        int n = snprintf(request, sizeof request, "GET %s HTTP/1.0\r\nHost: %s\r\n\r\n", path, ip);
        if (n <= 0 || (size_t)n >= sizeof request || write(fd, request, (size_t)n) != n) { close(fd); return refused("write"); }
    }
    char buffer[16384];
    long long total = 0;
    int first = 1;
    for (;;) {
        ssize_t count = read(fd, buffer, sizeof buffer);
        if (count < 0) { close(fd); return refused("read"); }
        if (count == 0) break;
        if (first) {
            size_t line = 0;
            while (line < (size_t)count && buffer[line] != '\r' && buffer[line] != '\n' && line < 200) line++;
            printf("netcheck first=%.*s\n", (int)line, buffer);
            first = 0;
        }
        total += count;
    }
    double seconds = now_s() - started;
    close(fd);
    printf("netcheck bytes=%lld seconds=%.3f mibps=%.2f\n", total, seconds, seconds > 0 ? total / 1048576.0 / seconds : 0);
    return 0;
}

static int do_listen(const char *port)
{
    struct sockaddr_in any;
    if (address_of("0.0.0.0", port, &any)) return usage();
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    int on = 1;
    if (fd < 0) return refused("listen");
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, sizeof on);
    if (bind(fd, (struct sockaddr *)&any, sizeof any) || listen(fd, 1)) return refused("listen");
    printf("netcheck listening port=%s\n", port);
    struct sockaddr_in peer;
    socklen_t length = sizeof peer;
    int client = accept(fd, (struct sockaddr *)&peer, &length);
    if (client < 0) return refused("accept");
    char from[INET_ADDRSTRLEN] = "?";
    inet_ntop(AF_INET, &peer.sin_addr, from, sizeof from);
    char line[128];
    int n = snprintf(line, sizeof line, "osx86-netcheck pong from=%s\n", from);
    if (write(client, line, (size_t)n) != n) return refused("write");
    printf("netcheck accepted from=%s\n", from);
    close(client);
    close(fd);
    return 0;
}

static int do_udp(const char *ip, const char *port)
{
    struct sockaddr_in peer;
    if (address_of(ip, port, &peer)) return usage();
    int fd = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    const char query[] = "osx86-netcheck";
    if (fd < 0 || sendto(fd, query, sizeof query - 1, 0, (struct sockaddr *)&peer, sizeof peer) < 0) return refused("udp");
    printf("netcheck sent udp\n");
    close(fd);
    return 0;
}

static int do_ipv6(void)
{
    int fd = socket(AF_INET6, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return refused("ipv6 socket");
    struct sockaddr_in6 peer;
    memset(&peer, 0, sizeof peer);
    peer.sin6_family = AF_INET6;
    peer.sin6_port = htons(80);
    inet_pton(AF_INET6, "2001:db8::1", &peer.sin6_addr);
    if (connect(fd, (struct sockaddr *)&peer, sizeof peer) < 0) { close(fd); return refused("ipv6"); }
    printf("netcheck connected ipv6\n");
    close(fd);
    return 0;
}

int main(int argc, char **argv)
{
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (argc >= 4 && !strcmp(argv[1], "connect")) return do_connect(argv[2], argv[3], NULL);
    if (argc >= 5 && !strcmp(argv[1], "get")) return do_connect(argv[2], argv[3], argv[4]);
    if (argc >= 3 && !strcmp(argv[1], "listen")) return do_listen(argv[2]);
    if (argc >= 4 && !strcmp(argv[1], "udp")) return do_udp(argv[2], argv[3]);
    if (argc >= 2 && !strcmp(argv[1], "ipv6")) return do_ipv6();
    return usage();
}
