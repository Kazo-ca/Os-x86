/* Os-x86 linux-base, R134.5 probe (trial E7): tries to attach with ptrace to the given pid. A
 * guest user must get EPERM on a process of another user (and, with yama ptrace_scope 1, on any
 * process that is not its descendant). Exit 0 when the attach succeeded. */
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ptrace.h>
#include <sys/wait.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: osx86-ptrace <pid>\n");
        return 2;
    }
    pid_t pid = (pid_t)atoi(argv[1]);
    if (ptrace(PTRACE_ATTACH, pid, NULL, NULL) != 0) {
        printf("ptrace attach refused: %s\n", strerror(errno));
        return 1;
    }
    waitpid(pid, NULL, 0);
    ptrace(PTRACE_DETACH, pid, NULL, NULL);
    printf("ptrace attach succeeded\n");
    return 0;
}
