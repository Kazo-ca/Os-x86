/* Essai E7 — aide invitée : « ouvre » une URL = l'envoie à l'agent (socket unix, 0666), qui la
 * remonte à l'hôte avec l'uid et la bouteille de l'appelant (SO_PEERCRED). Ne fait rien d'autre (139). */
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
int main(int argc, char **argv) {
  if (argc < 2) { fprintf(stderr, "usage: e7-open-url URL\n"); return 2; }
  int fd = socket(AF_UNIX, SOCK_STREAM, 0); struct sockaddr_un su = { AF_UNIX, "/run/e7/open-url.sock" };
  if (connect(fd, (struct sockaddr *)&su, sizeof su)) { perror("connect"); return 1; }
  write(fd, argv[1], strlen(argv[1])); write(fd, "\n", 1); close(fd); return 0;
}
