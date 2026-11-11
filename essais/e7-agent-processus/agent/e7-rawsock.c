/* Essai E7 — contrôle R134.5 : un socket brut (CAP_NET_RAW) est-il ouvrable ? (iputils absent de l'image) */
#include <errno.h>
#include <netinet/in.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
int main(void) { int fd = socket(AF_INET, SOCK_RAW, IPPROTO_ICMP); if (fd >= 0) { printf("raw socket opened (fd %d)\n", fd); return 0; } printf("raw socket: %s\n", strerror(errno)); return 1; }
