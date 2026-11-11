/* Essai E14 — programme console Windows minimal : lit stdin ligne par ligne, écrit stdout
 * (avec un peu de couleur par SetConsoleTextAttribute si la console le permet), rend un code
 * de sortie choisi par la commande « exit N ». Compilé par winegcc (PE i386) dans le conteneur.
 * « --loop » : boucle sans fin (test de Ctrl+C). */
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
int main(int argc, char **argv) {
    char line[256];
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    int n = 0;
    printf("consoletest: tapez des lignes ; 'exit N' rend le code N ; 'color' teste les attributs\n");
    fflush(stdout);
    if (argc > 1 && strcmp(argv[1], "--loop") == 0) {
        for (;;) { printf("tick %d\n", n++); fflush(stdout); Sleep(500); }
    }
    while (fgets(line, sizeof line, stdin)) {
        line[strcspn(line, "\r\n")] = 0;
        if (strncmp(line, "exit ", 5) == 0) { int c = atoi(line + 5); printf("sortie avec %d\n", c); fflush(stdout); return c; }
        if (strcmp(line, "color") == 0) {
            int i;
            for (i = 1; i < 16; i++) { SetConsoleTextAttribute(out, (WORD)i); printf("couleur %2d ", i); }
            SetConsoleTextAttribute(out, 7); printf("\n"); fflush(stdout); continue;
        }
        printf("echo[%d]: %s\n", ++n, line); fflush(stdout);
    }
    printf("fin de stdin (EOF)\n");
    return 0;
}
