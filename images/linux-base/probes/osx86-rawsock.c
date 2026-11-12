/* Os-x86 linux-base, R134.5 probe (trial E7): tries to open a raw socket. A guest user must get
 * EPERM (no CAP_NET_RAW, no setuid program, no file capability). Exit 0 when the socket opened. */
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

int main(void) {
    int fd = socket(AF_INET, SOCK_RAW, 255);
    if (fd < 0) {
        printf("raw socket refused: %s\n", strerror(errno));
        return 1;
    }
    close(fd);
    printf("raw socket opened\n");
    return 0;
}
