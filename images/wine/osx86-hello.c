/* Os-x86 wine image (work item 9): the console program the guest test runs as "a first Windows
 * program". Built from this source by build-wine.py with the MinGW-w64 cross compiler of Debian
 * (gcc-mingw-w64-i686-win32), so that no third-party binary enters the image. Prints a line with
 * its arguments, the Windows directory and the Windows user name, and exits with code 7. */
#include <stdio.h>
#include <windows.h>

int main(int argc, char **argv)
{
    char directory[MAX_PATH] = "";
    char user[256] = "";
    DWORD size = sizeof user;
    GetWindowsDirectoryA(directory, sizeof directory);
    GetUserNameA(user, &size);
    printf("osx86-hello windows=%s user=%s args=%d", directory, user, argc - 1);
    for (int i = 1; i < argc; i++)
        printf(" %s", argv[i]);
    printf("\n");
    return 7;
}
