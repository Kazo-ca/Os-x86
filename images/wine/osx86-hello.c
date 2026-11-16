/* Os-x86 wine image (work items 9 and 10): the console program the guest tests run as "a first
 * Windows program". Built from this source by build-wine.py with the MinGW-w64 cross compiler of
 * Debian (gcc-mingw-w64-i686-win32), so that no third-party binary enters the image.
 *
 *   osx86-hello [args...]   prints a line with its arguments, the Windows directory and the Windows
 *                           user name, and exits with code 7
 *   osx86-hello --exit N    prints the same line and exits with code N
 *   osx86-hello --wait      prints "osx86-hello waiting" and waits until it is interrupted (Ctrl+C)
 *   osx86-hello --read      reads one line from its input and prints "osx86-hello read <line>" */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

int main(int argc, char **argv)
{
    char directory[MAX_PATH] = "";
    char user[256] = "";
    DWORD size = sizeof user;
    if (argc == 2 && strcmp(argv[1], "--wait") == 0) {
        printf("osx86-hello waiting\n");
        fflush(stdout);
        for (;;)
            Sleep(1000);
    }
    if (argc == 2 && strcmp(argv[1], "--read") == 0) {
        char line[512] = "";
        if (!fgets(line, sizeof line, stdin))
            return 3;
        line[strcspn(line, "\r\n")] = 0;
        printf("osx86-hello read %s\n", line);
        return 0;
    }
    GetWindowsDirectoryA(directory, sizeof directory);
    GetUserNameA(user, &size);
    printf("osx86-hello windows=%s user=%s args=%d", directory, user, argc - 1);
    for (int i = 1; i < argc; i++)
        printf(" %s", argv[i]);
    printf("\n");
    if (argc == 3 && strcmp(argv[1], "--exit") == 0)
        return atoi(argv[2]);
    return 7;
}
