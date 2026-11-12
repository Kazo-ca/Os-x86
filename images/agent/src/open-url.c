/* osx86-open-url: what a guest program runs to "open" a URL (decision 139, trial E7). It hands
 * the URL to the agent through a unix socket; the agent reads the caller's uid from the socket
 * (SO_PEERCRED), so a program cannot pass for another prefix, and reports the event to the
 * host, which decides. Nothing else happens in the guest. */
#include "agent.h"

#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#define MAX_URL 2000

int main(int argc, char **argv)
{
    if (argc != 2 || !*argv[1]) {
        fprintf(stderr, "usage: osx86-open-url URL\n");
        return 2;
    }
    size_t length = strlen(argv[1]);
    if (length > MAX_URL || strchr(argv[1], '\n')) {
        fprintf(stderr, "osx86-open-url: the URL must be one line of at most %d bytes\n", MAX_URL);
        return 2;
    }
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        perror("osx86-open-url: socket");
        return 1;
    }
    struct sockaddr_un address;
    memset(&address, 0, sizeof address);
    address.sun_family = AF_UNIX;
    snprintf(address.sun_path, sizeof address.sun_path, "%s", AGENT_URL_SOCKET);
    if (connect(fd, (struct sockaddr *)&address, sizeof address)) {
        perror("osx86-open-url: the agent is not listening");
        return 1;
    }
    /* One write for the URL and its line end: the agent closes as soon as it has the line, and a
     * second write would then meet a closed socket. */
    char line[MAX_URL + 2];
    memcpy(line, argv[1], length);
    line[length] = '\n';
    if (send(fd, line, length + 1, MSG_NOSIGNAL) != (ssize_t)(length + 1)) {
        perror("osx86-open-url: send");
        return 1;
    }
    /* Wait until the agent has taken the line (it closes the connection) before exiting. */
    shutdown(fd, SHUT_WR);
    char end;
    while (read(fd, &end, 1) > 0) { }
    close(fd);
    return 0;
}
