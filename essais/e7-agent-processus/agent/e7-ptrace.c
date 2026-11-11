/* Essai E7 — contrôle R134.5 : tente PTRACE_ATTACH sur un pid (d'une autre bouteille) et imprime le résultat. */
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ptrace.h>
#include <sys/wait.h>
int main(int argc, char **argv) {
  if (argc < 2) return 2;
  int pid = atoi(argv[1]);
  if (ptrace(PTRACE_ATTACH, pid, 0, 0) == 0) { int st; waitpid(pid, &st, 0); ptrace(PTRACE_DETACH, pid, 0, 0); printf("ATTACHED pid %d\n", pid); return 0; }
  printf("ptrace attach %d: %s\n", pid, strerror(errno)); return 1;
}
