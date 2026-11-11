// Essai E4 — agent invité (prototype jetable).
// Suit les fenêtres X11 de premier niveau (propriétés EWMH/ICCCM), les redirige avec XComposite,
// capture chaque fenêtre à part (XGetImage) quand XDamage signale un changement, et écrit sur la
// racine 9p (lue par la page via emu.read_file) :
//   <dir>/windows.json      liste des fenêtres + compteurs CPU + horodatages (clock monotone, ms)
//   <dir>/w-<id>.raw        pixels BGRX 32 bpp de la fenêtre (mode composite seulement)
//   <dir>/w-<id>.icon       première icône _NET_WM_ICON (ARGB 32 bpp), écrite une fois
// Usage : e4agent [-m composite|geometry] [-z hz] [-o dir] [-v]
//   geometry : pas de redirection ni de capture ; seulement la liste, la géométrie et les dommages.
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/Xatom.h>
#include <X11/extensions/Xcomposite.h>
#include <X11/extensions/Xdamage.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <time.h>
#include <errno.h>
#include <dirent.h>
#include <sys/resource.h>
#include <sys/time.h>
#include <sys/select.h>

#define MAXWIN 64

typedef struct {
  Window top, client;        // fenêtre fille de root (cadre du WM ou client) et fenêtre cliente
  int x, y, w, h, z, mapped, override, depth;
  char name[256], cls[128], type[64], state[128], icon[16];
  Window transient_for;
  int input_hint;
  Damage damage;
  int dirty, tree_dirty;
  double damage_t, capture_t;
  long frame;                // numéro de la dernière capture écrite
  unsigned char *buf; size_t buflen;
  long bytes;
  int seen, icon_done;
} Win;

static Display *dpy; static Window root; static int screen;
static Win wins[MAXWIN]; static int nwin;
static int composite = 1, hz = 20, verbose = 0;
static const char *outdir = "/root/e4";
static int damage_event_base, damage_error_base;
static Atom A_NET_WM_NAME, A_UTF8, A_NET_WM_WINDOW_TYPE, A_NET_WM_STATE, A_NET_WM_ICON, A_WM_STATE;
static long seq = 0;
static long xpid = 0;
static struct { long frames, bytes, damage_events, getimage_us, write_us; } cap;

static double now_ms(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6; }
static int xerr(Display *d, XErrorEvent *e) { (void)d; (void)e; return 0; }  // fenêtres disparues : on ignore

static int has_prop(Window w, Atom a) {
  Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
  if (XGetWindowProperty(dpy, w, a, 0, 0, False, AnyPropertyType, &type, &fmt, &n, &after, &data) != Success) return 0;
  if (data) XFree(data);
  return type != None;
}
// Fenêtre cliente (celle qui porte WM_STATE) sous une fille de root, comme XmuClientWindow.
static Window find_client(Window w, int depth) {
  if (has_prop(w, A_WM_STATE)) return w;
  if (depth >= 3) return 0;
  Window r, p, *ch = NULL; unsigned int n = 0;
  if (!XQueryTree(dpy, w, &r, &p, &ch, &n)) return 0;
  Window found = 0;
  for (unsigned int i = 0; i < n && !found; i++) found = find_client(ch[i], depth + 1);
  if (ch) XFree(ch);
  return found;
}
static void get_text(Window w, Atom a, char *out, size_t len) {
  Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
  out[0] = 0;
  if (XGetWindowProperty(dpy, w, a, 0, 64, False, AnyPropertyType, &type, &fmt, &n, &after, &data) == Success && data) {
    if (type == A_UTF8) { strncpy(out, (char *)data, len - 1); out[len - 1] = 0; }
    else if (type == XA_STRING) {  // WM_NAME en STRING = Latin-1 (xterm sans locale UTF-8) : conversion en UTF-8 pour le JSON
      size_t o = 0;
      for (unsigned char *s = data; *s && o + 2 < len; s++) {
        if (*s < 0x80) out[o++] = (char)*s;
        else { out[o++] = (char)(0xc0 | (*s >> 6)); out[o++] = (char)(0x80 | (*s & 0x3f)); }
      }
      out[o] = 0;
    }
    XFree(data);
  }
}
static Window get_transient_for(Window w) {
  // Comme XGetTransientForHint, mais sans exiger le type WINDOW (xprop -set ne sait poser que CARDINAL).
  Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL; Window r = 0;
  if (XGetWindowProperty(dpy, w, XA_WM_TRANSIENT_FOR, 0, 1, False, AnyPropertyType, &type, &fmt, &n, &after, &data) == Success && data) {
    if (fmt == 32 && n >= 1) r = (Window)*(unsigned long *)data;
    XFree(data);
  }
  return r;
}
static void get_atoms(Window w, Atom a, char *out, size_t len) {
  Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
  out[0] = 0;
  if (XGetWindowProperty(dpy, w, a, 0, 16, False, XA_ATOM, &type, &fmt, &n, &after, &data) == Success && data) {
    for (unsigned long i = 0; i < n; i++) {
      char *nm = XGetAtomName(dpy, ((Atom *)data)[i]);
      if (nm) { if (out[0]) strncat(out, ",", len - strlen(out) - 1); strncat(out, nm, len - strlen(out) - 1); XFree(nm); }
    }
    XFree(data);
  }
}
static void write_icon(Win *wi) {
  Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
  wi->icon[0] = 0;
  if (XGetWindowProperty(dpy, wi->client, A_NET_WM_ICON, 0, 1 << 20, False, XA_CARDINAL, &type, &fmt, &n, &after, &data) != Success || !data) return;
  if (n >= 2) {
    unsigned long *c = (unsigned long *)data; unsigned long w = c[0], h = c[1];
    if (w > 0 && h > 0 && n >= 2 + w * h) {
      snprintf(wi->icon, sizeof wi->icon, "%lux%lu", w, h);
      if (!wi->icon_done) {
        char p[256]; snprintf(p, sizeof p, "%s/w-0x%lx.icon", outdir, wi->client);
        FILE *f = fopen(p, "wb");
        if (f) { for (unsigned long i = 0; i < w * h; i++) { unsigned int px = (unsigned int)c[2 + i]; fwrite(&px, 4, 1, f); } fclose(f); wi->icon_done = 1; }
      }
    }
  }
  XFree(data);
}
static void read_props(Win *wi) {
  Window c = wi->client;
  get_text(c, A_NET_WM_NAME, wi->name, sizeof wi->name);
  if (!wi->name[0]) get_text(c, XA_WM_NAME, wi->name, sizeof wi->name);
  XClassHint ch = {0};
  wi->cls[0] = 0;
  if (XGetClassHint(dpy, c, &ch)) { snprintf(wi->cls, sizeof wi->cls, "%s.%s", ch.res_name ? ch.res_name : "", ch.res_class ? ch.res_class : ""); if (ch.res_name) XFree(ch.res_name); if (ch.res_class) XFree(ch.res_class); }
  wi->transient_for = get_transient_for(c);
  get_atoms(c, A_NET_WM_WINDOW_TYPE, wi->type, sizeof wi->type);
  get_atoms(c, A_NET_WM_STATE, wi->state, sizeof wi->state);
  XWMHints *h = XGetWMHints(dpy, c);
  wi->input_hint = h ? ((h->flags & InputHint) ? h->input : -1) : -2;
  if (h) XFree(h);
  write_icon(wi);
}
static Win *find_win(Window top) { for (int i = 0; i < nwin; i++) if (wins[i].top == top) return &wins[i]; return NULL; }
static Win *find_by_drawable(Drawable d) { for (int i = 0; i < nwin; i++) if (wins[i].client == d || wins[i].top == d) return &wins[i]; return NULL; }

// Relit l'arbre : fenêtres filles de root, de bas en haut (ordre z).
static void scan_tree(void) {
  Window r, p, *ch = NULL; unsigned int n = 0;
  for (int i = 0; i < nwin; i++) wins[i].seen = 0;
  if (XQueryTree(dpy, root, &r, &p, &ch, &n)) {
    int z = 0;
    for (unsigned int i = 0; i < n; i++) {
      XWindowAttributes a;
      if (!XGetWindowAttributes(dpy, ch[i], &a)) continue;
      if (a.map_state != IsViewable) { Win *old = find_win(ch[i]); if (old) { old->seen = 1; old->mapped = 0; } continue; }
      Win *wi = find_win(ch[i]);
      if (!wi) {
        if (nwin >= MAXWIN) continue;
        wi = &wins[nwin++]; memset(wi, 0, sizeof *wi);
        wi->top = ch[i];
        wi->client = a.override_redirect ? ch[i] : find_client(ch[i], 0);
        if (!wi->client) wi->client = ch[i];
        wi->override = a.override_redirect;
        wi->depth = a.depth;
        wi->frame = 0; wi->dirty = 1; wi->damage_t = now_ms();
        XSelectInput(dpy, wi->client, PropertyChangeMask | StructureNotifyMask);
        wi->damage = XDamageCreate(dpy, wi->client, XDamageReportNonEmpty);
        read_props(wi);
      }
      wi->seen = 1; wi->mapped = 1; wi->z = z++;
      // géométrie de la fenêtre cliente en coordonnées root
      XWindowAttributes ca; int cx = 0, cy = 0; Window dummy;
      if (XGetWindowAttributes(dpy, wi->client, &ca) && XTranslateCoordinates(dpy, wi->client, root, 0, 0, &cx, &cy, &dummy)) {
        if (ca.width != wi->w || ca.height != wi->h) { wi->dirty = 1; wi->damage_t = now_ms(); }
        wi->x = cx; wi->y = cy; wi->w = ca.width; wi->h = ca.height;
      } else { wi->x = a.x; wi->y = a.y; wi->w = a.width; wi->h = a.height; }
    }
    if (ch) XFree(ch);
  }
  // fenêtres disparues
  for (int i = 0; i < nwin; i++) if (!wins[i].seen) {
    if (wins[i].damage) XDamageDestroy(dpy, wins[i].damage);
    free(wins[i].buf);
    char p[256];
    snprintf(p, sizeof p, "%s/w-0x%lx.0.raw", outdir, wins[i].client); unlink(p);
    snprintf(p, sizeof p, "%s/w-0x%lx.1.raw", outdir, wins[i].client); unlink(p);
    wins[i] = wins[nwin - 1]; nwin--; i--;
  }
}

static int capture(Win *wi) {
  if (wi->w < 1 || wi->h < 1) return 0;
  double t0 = now_ms();
  XImage *img = XGetImage(dpy, wi->client, 0, 0, wi->w, wi->h, AllPlanes, ZPixmap);
  if (!img) return 0;
  size_t len = (size_t)wi->w * wi->h * 4;
  unsigned char *tmp = malloc(len);
  if (!tmp) { XDestroyImage(img); return 0; }
  if (img->bits_per_pixel == 32) {
    for (int y = 0; y < wi->h; y++) memcpy(tmp + (size_t)y * wi->w * 4, img->data + (size_t)y * img->bytes_per_line, (size_t)wi->w * 4);
  } else {  // 16 ou 24 bpp : conversion lente, suffisante pour la maquette
    for (int y = 0; y < wi->h; y++) for (int x = 0; x < wi->w; x++) {
      unsigned long px = XGetPixel(img, x, y); unsigned char *d = tmp + ((size_t)y * wi->w + x) * 4;
      if (img->bits_per_pixel == 16) { d[0] = (px & 0x1f) << 3; d[1] = ((px >> 5) & 0x3f) << 2; d[2] = ((px >> 11) & 0x1f) << 3; d[3] = 0; }
      else { d[0] = px & 0xff; d[1] = (px >> 8) & 0xff; d[2] = (px >> 16) & 0xff; d[3] = 0; }
    }
  }
  XDestroyImage(img);
  double t1 = now_ms();
  cap.getimage_us += (long)((t1 - t0) * 1000);
  if (wi->buf && wi->buflen == len && memcmp(wi->buf, tmp, len) == 0) { free(tmp); return 0; }  // dommage sans changement de pixels
  free(wi->buf); wi->buf = tmp; wi->buflen = len;
  // Double tampon : trame paire / impaire dans deux fichiers ; le JSON (écrit après) dit lequel lire.
  // (Pas de rename : sur le 9p de v86 il laisse un instant sans fichier, et la page lisait "File not found".)
  char p[256];
  snprintf(p, sizeof p, "%s/w-0x%lx.%ld.raw", outdir, wi->client, (wi->frame + 1) & 1);
  FILE *f = fopen(p, "wb");
  if (!f) return 0;
  fwrite(tmp, 1, len, f);
  fflush(f); fsync(fileno(f)); fclose(f);
  wi->capture_t = now_ms();
  cap.write_us += (long)((wi->capture_t - t1) * 1000);
  cap.frames++; cap.bytes += (long)len; wi->bytes = (long)len;
  wi->frame++;
  return 1;
}

static void json_str(FILE *f, const char *s) {
  fputc('"', f);
  for (; *s; s++) { unsigned char c = (unsigned char)*s; if (c == '"' || c == '\\') { fputc('\\', f); fputc(c, f); } else if (c < 0x20) fprintf(f, "\\u%04x", c); else fputc(c, f); }
  fputc('"', f);
}
static void read_proc_stat(long out[7]) {
  FILE *f = fopen("/proc/stat", "r"); char line[512];
  for (int i = 0; i < 7; i++) out[i] = 0;
  if (f) { if (fgets(line, sizeof line, f)) sscanf(line, "cpu %ld %ld %ld %ld %ld %ld %ld", &out[0], &out[1], &out[2], &out[3], &out[4], &out[5], &out[6]); fclose(f); }
}
static long proc_cpu_ms(long pid) {
  if (!pid) return 0;
  char p[64]; snprintf(p, sizeof p, "/proc/%ld/stat", pid);
  FILE *f = fopen(p, "r"); if (!f) return 0;
  char buf[1024]; long ut = 0, st = 0;
  if (fgets(buf, sizeof buf, f)) { char *rp = strrchr(buf, ')'); if (rp) sscanf(rp + 2, "%*c %*d %*d %*d %*d %*d %*u %*u %*u %*u %*u %ld %ld", &ut, &st); }
  fclose(f);
  return (ut + st) * 1000 / sysconf(_SC_CLK_TCK);
}
static long find_pid(const char *const names[]) {
  DIR *d = opendir("/proc"); struct dirent *e; long found = 0;
  if (!d) return 0;
  while ((e = readdir(d)) && !found) {
    if (e->d_name[0] < '0' || e->d_name[0] > '9') continue;
    char p[64], comm[64] = ""; snprintf(p, sizeof p, "/proc/%s/comm", e->d_name);
    FILE *f = fopen(p, "r"); if (!f) continue;
    if (fgets(comm, sizeof comm, f)) { comm[strcspn(comm, "\n")] = 0; for (int i = 0; names[i]; i++) if (!strcmp(comm, names[i])) found = atol(e->d_name); }
    fclose(f);
  }
  closedir(d);
  return found;
}
static void write_json(void) {
  char p[256];
  snprintf(p, sizeof p, "%s/windows.json", outdir);
  FILE *f = fopen(p, "w"); if (!f) return;  // en place : une lecture partielle donne un JSON invalide, que la page ignore
  long st[7]; read_proc_stat(st);
  struct rusage ru; getrusage(RUSAGE_SELF, &ru);
  long agent_ms = ru.ru_utime.tv_sec * 1000 + ru.ru_utime.tv_usec / 1000 + ru.ru_stime.tv_sec * 1000 + ru.ru_stime.tv_usec / 1000;
  fprintf(f, "{\"seq\":%ld,\"t_ms\":%.1f,\"mode\":\"%s\",\"hz\":%d,", ++seq, now_ms(), composite ? "composite" : "geometry", hz);
  fprintf(f, "\"cpu\":{\"stat\":[%ld,%ld,%ld,%ld,%ld,%ld,%ld],\"ticks_hz\":%ld,\"agent_ms\":%ld,\"x_ms\":%ld,\"x_pid\":%ld},", st[0], st[1], st[2], st[3], st[4], st[5], st[6], sysconf(_SC_CLK_TCK), agent_ms, proc_cpu_ms(xpid), xpid);
  fprintf(f, "\"capture\":{\"frames\":%ld,\"bytes\":%ld,\"damage_events\":%ld,\"getimage_ms\":%ld,\"write_ms\":%ld},", cap.frames, cap.bytes, cap.damage_events, cap.getimage_us / 1000, cap.write_us / 1000);
  fputs("\"windows\":[", f);
  for (int i = 0; i < nwin; i++) {
    Win *w = &wins[i];
    if (i) fputc(',', f);
    fprintf(f, "{\"id\":\"0x%lx\",\"top\":\"0x%lx\",\"x\":%d,\"y\":%d,\"w\":%d,\"h\":%d,\"z\":%d,\"mapped\":%d,\"override\":%d,\"depth\":%d,", w->client, w->top, w->x, w->y, w->w, w->h, w->z, w->mapped, w->override, w->depth);
    fputs("\"name\":", f); json_str(f, w->name);
    fputs(",\"class\":", f); json_str(f, w->cls);
    fputs(",\"type\":", f); json_str(f, w->type);
    fputs(",\"state\":", f); json_str(f, w->state);
    if (w->transient_for) fprintf(f, ",\"transient_for\":\"0x%lx\"", w->transient_for); else fputs(",\"transient_for\":\"\"", f);
    fprintf(f, ",\"icon\":\"%s\",\"input_hint\":%d,\"frame\":%ld,\"file\":\"w-0x%lx.%ld.raw\",\"damage_t\":%.1f,\"capture_t\":%.1f,\"bytes\":%ld}", w->icon, w->input_hint, w->frame, w->client, w->frame & 1, w->damage_t, w->capture_t, w->bytes);
  }
  fputs("]}\n", f);
  fflush(f); fsync(fileno(f)); fclose(f);
}

int main(int argc, char **argv) {
  int opt;
  while ((opt = getopt(argc, argv, "m:z:o:v")) != -1) {
    if (opt == 'm') composite = strcmp(optarg, "geometry") != 0;
    else if (opt == 'z') hz = atoi(optarg);
    else if (opt == 'o') outdir = optarg;
    else if (opt == 'v') verbose = 1;
  }
  dpy = XOpenDisplay(NULL);
  if (!dpy) { fprintf(stderr, "e4agent: pas d'affichage\n"); return 1; }
  XSetErrorHandler(xerr);
  screen = DefaultScreen(dpy); root = RootWindow(dpy, screen);
  int ev, er, maj, min;
  if (!XCompositeQueryExtension(dpy, &ev, &er)) { fprintf(stderr, "e4agent: pas de Composite\n"); return 1; }
  XCompositeQueryVersion(dpy, &maj, &min);
  if (!XDamageQueryExtension(dpy, &damage_event_base, &damage_error_base)) { fprintf(stderr, "e4agent: pas de Damage\n"); return 1; }
  A_NET_WM_NAME = XInternAtom(dpy, "_NET_WM_NAME", False); A_UTF8 = XInternAtom(dpy, "UTF8_STRING", False);
  A_NET_WM_WINDOW_TYPE = XInternAtom(dpy, "_NET_WM_WINDOW_TYPE", False); A_NET_WM_STATE = XInternAtom(dpy, "_NET_WM_STATE", False);
  A_NET_WM_ICON = XInternAtom(dpy, "_NET_WM_ICON", False); A_WM_STATE = XInternAtom(dpy, "WM_STATE", False);
  if (composite) XCompositeRedirectSubwindows(dpy, root, CompositeRedirectAutomatic);
  XSelectInput(dpy, root, SubstructureNotifyMask);
  XSync(dpy, False);
  { char cmd[300]; snprintf(cmd, sizeof cmd, "mkdir -p '%s'", outdir); if (system(cmd) != 0) fprintf(stderr, "e4agent: mkdir %s a échoué\n", outdir); }
  static const char *const xnames[] = { "Xorg", "X", "Xvfb", NULL };
  xpid = find_pid(xnames);
  fprintf(stderr, "e4agent: mode %s, %d Hz, Composite %d.%d, X pid %ld, sortie %s\n", composite ? "composite" : "geometry", hz, maj, min, xpid, outdir);
  int tree_dirty = 1;
  double last_json = 0;
  int xfd = ConnectionNumber(dpy);
  for (;;) {
    double tick_end = now_ms() + 1000.0 / hz;
    while (XPending(dpy)) {
      XEvent e; XNextEvent(dpy, &e);
      if (e.type == damage_event_base + XDamageNotify) {
        XDamageNotifyEvent *de = (XDamageNotifyEvent *)&e;
        Win *w = find_by_drawable(de->drawable);
        cap.damage_events++;
        if (w) { if (!w->dirty) w->damage_t = now_ms(); w->dirty = 1; }
      } else if (e.type == MapNotify || e.type == UnmapNotify || e.type == DestroyNotify || e.type == ConfigureNotify || e.type == CreateNotify || e.type == CirculateNotify || e.type == ReparentNotify) {
        tree_dirty = 1;
      } else if (e.type == PropertyNotify) {
        Win *w = find_by_drawable(e.xproperty.window);
        if (w) { read_props(w); tree_dirty = 1; }
      }
    }
    int changed = 0;
    if (tree_dirty) { scan_tree(); tree_dirty = 0; changed = 1; }
    for (int i = 0; i < nwin; i++) {
      Win *w = &wins[i];
      if (!w->dirty || !w->mapped) continue;
      if (composite) { if (capture(w)) changed = 1; }
      else { w->frame++; w->capture_t = now_ms(); changed = 1; }  // géométrie seule : la page redécoupe l'écran
      if (w->damage) XDamageSubtract(dpy, w->damage, None, None);
      w->dirty = 0;
    }
    double t = now_ms();
    if (changed || t - last_json > 1000) { write_json(); last_json = t; if (verbose) fprintf(stderr, "seq %ld fenêtres %d trames %ld\n", seq, nwin, cap.frames); }
    // attente jusqu'au prochain tick, réveillée par un événement X
    double rem = tick_end - now_ms();
    if (rem > 0) { fd_set fds; FD_ZERO(&fds); FD_SET(xfd, &fds); struct timeval tv = { (long)(rem / 1000), (long)((long)(rem * 1000) % 1000000) }; select(xfd + 1, &fds, NULL, NULL, &tv); }
    // un événement arrivé pendant l'attente est traité au tick suivant, qui démarre tout de suite
    XFlush(dpy);
  }
}
