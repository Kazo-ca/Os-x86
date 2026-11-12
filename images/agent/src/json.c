/* Minimal JSON reader and writer of the Os-x86 guest agent (from trial E7). The reader parses one
 * request line into a pool of nodes (no allocation per request, reset at every parse); strings
 * are unescaped into a fixed buffer. The writer appends to a growable buffer and escapes strings
 * the JSON way; bytes that are not valid UTF-8 become U+FFFD so that the host's parser never
 * sees malformed text (program output is arbitrary bytes). */
#include "agent.h"

#include <ctype.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define JPOOL 1024
static jn pool[JPOOL];
static int pool_top;
static char strings[AGENT_MAX_LINE + 16];
static int strings_top;
static const char *cursor;

static jn *node(int type)
{
    if (pool_top >= JPOOL) return NULL;
    jn *n = &pool[pool_top++];
    memset(n, 0, sizeof *n);
    n->type = type;
    return n;
}

static void skip_space(void)
{
    while (*cursor && isspace((unsigned char)*cursor)) cursor++;
}

static int put_utf8(unsigned c)
{
    if (strings_top >= (int)sizeof strings - 8) return 0;
    if (c < 0x80) strings[strings_top++] = (char)c;
    else if (c < 0x800) { strings[strings_top++] = (char)(0xC0 | (c >> 6)); strings[strings_top++] = (char)(0x80 | (c & 0x3F)); }
    else if (c < 0x10000) { strings[strings_top++] = (char)(0xE0 | (c >> 12)); strings[strings_top++] = (char)(0x80 | ((c >> 6) & 0x3F)); strings[strings_top++] = (char)(0x80 | (c & 0x3F)); }
    else { strings[strings_top++] = (char)(0xF0 | (c >> 18)); strings[strings_top++] = (char)(0x80 | ((c >> 12) & 0x3F)); strings[strings_top++] = (char)(0x80 | ((c >> 6) & 0x3F)); strings[strings_top++] = (char)(0x80 | (c & 0x3F)); }
    return 1;
}

static int hex4(const char *p, unsigned *out)
{
    unsigned c = 0;
    for (int i = 0; i < 4; i++) {
        if (!isxdigit((unsigned char)p[i])) return 0;
        c = c * 16 + (isdigit((unsigned char)p[i]) ? (unsigned)(p[i] - '0') : (unsigned)(tolower((unsigned char)p[i]) - 'a' + 10));
    }
    *out = c;
    return 1;
}

static char *parse_string(void)
{
    if (*cursor != '"') return NULL;
    cursor++;
    char *out = strings + strings_top;
    while (*cursor && *cursor != '"') {
        if (strings_top >= (int)sizeof strings - 8) return NULL;
        if (*cursor == '\\') {
            cursor++;
            switch (*cursor) {
            case 'n': strings[strings_top++] = '\n'; break;
            case 't': strings[strings_top++] = '\t'; break;
            case 'r': strings[strings_top++] = '\r'; break;
            case 'b': strings[strings_top++] = '\b'; break;
            case 'f': strings[strings_top++] = '\f'; break;
            case '"': case '\\': case '/': strings[strings_top++] = *cursor; break;
            case 'u': {
                unsigned c, low;
                if (!hex4(cursor + 1, &c)) return NULL;
                cursor += 4;
                if (c >= 0xD800 && c <= 0xDBFF && cursor[1] == '\\' && cursor[2] == 'u' && hex4(cursor + 3, &low) && low >= 0xDC00 && low <= 0xDFFF) {
                    c = 0x10000 + ((c - 0xD800) << 10) + (low - 0xDC00);
                    cursor += 6;
                }
                if (!put_utf8(c)) return NULL;
                break;
            }
            default: return NULL;
            }
            cursor++;
        } else {
            strings[strings_top++] = *cursor++;
        }
    }
    if (*cursor != '"') return NULL;
    cursor++;
    strings[strings_top++] = 0;
    return out;
}

static jn *parse_value(int depth)
{
    if (depth > 32) return NULL;
    skip_space();
    if (*cursor == '{') {
        jn *o = node(J_OBJ);
        if (!o) return NULL;
        cursor++;
        jn **tail = &o->child;
        skip_space();
        if (*cursor == '}') { cursor++; return o; }
        for (;;) {
            skip_space();
            char *key = parse_string();
            if (!key) return NULL;
            skip_space();
            if (*cursor != ':') return NULL;
            cursor++;
            jn *v = parse_value(depth + 1);
            if (!v) return NULL;
            v->key = key;
            *tail = v;
            tail = &v->next;
            skip_space();
            if (*cursor == ',') { cursor++; continue; }
            if (*cursor == '}') { cursor++; return o; }
            return NULL;
        }
    }
    if (*cursor == '[') {
        jn *a = node(J_ARR);
        if (!a) return NULL;
        cursor++;
        jn **tail = &a->child;
        skip_space();
        if (*cursor == ']') { cursor++; return a; }
        for (;;) {
            jn *v = parse_value(depth + 1);
            if (!v) return NULL;
            *tail = v;
            tail = &v->next;
            skip_space();
            if (*cursor == ',') { cursor++; continue; }
            if (*cursor == ']') { cursor++; return a; }
            return NULL;
        }
    }
    if (*cursor == '"') {
        char *s = parse_string();
        if (!s) return NULL;
        jn *n = node(J_STR);
        if (n) n->str = s;
        return n;
    }
    if (!strncmp(cursor, "true", 4)) { cursor += 4; jn *n = node(J_BOOL); if (n) n->b = 1; return n; }
    if (!strncmp(cursor, "false", 5)) { cursor += 5; return node(J_BOOL); }
    if (!strncmp(cursor, "null", 4)) { cursor += 4; return node(J_NULL); }
    /* A JSON number and nothing else: strtod alone would also take nan, inf and hexadecimal. */
    const char *digits = cursor + (*cursor == '-');
    if (!isdigit((unsigned char)*digits)) return NULL;
    char *end;
    double d = strtod(cursor, &end);
    if (end == cursor || !__builtin_isfinite(d)) return NULL;  /* not <math.h>: its jn() is a Bessel function */
    for (const char *c = cursor; c < end; c++) if (!isdigit((unsigned char)*c) && !strchr("+-.eE", *c)) return NULL;
    cursor = end;
    jn *n = node(J_NUM);
    if (n) n->num = d;
    return n;
}

jn *json_parse(const char *text)
{
    pool_top = 0;
    strings_top = 0;
    cursor = text;
    jn *v = parse_value(0);
    if (!v) return NULL;
    skip_space();
    return *cursor ? NULL : v;
}

jn *json_get(jn *object, const char *key)
{
    if (!object || object->type != J_OBJ) return NULL;
    for (jn *c = object->child; c; c = c->next) if (!strcmp(c->key, key)) return c;
    return NULL;
}

const char *json_str(jn *object, const char *key, const char *fallback)
{
    jn *v = json_get(object, key);
    return v && v->type == J_STR ? v->str : fallback;
}

double json_num(jn *object, const char *key, double fallback)
{
    jn *v = json_get(object, key);
    return v && v->type == J_NUM ? v->num : fallback;
}

int json_bool(jn *object, const char *key, int fallback)
{
    jn *v = json_get(object, key);
    return v && v->type == J_BOOL ? v->b : fallback;
}

int jn_int(const jn *value, int fallback, int min, int max)
{
    if (!value || value->type != J_NUM) return fallback;
    if (value->num < (double)min) return min;
    if (value->num > (double)max) return max;
    return (int)value->num;
}

int json_int(jn *object, const char *key, int fallback, int min, int max)
{
    return jn_int(json_get(object, key), fallback, min, max);
}

/* ---------------------------------------------------------------- writer */

void sb_put(sb *b, const char *s, size_t n)
{
    if (b->len + n + 1 > b->cap) {
        size_t c = b->cap ? b->cap * 2 : 1024;
        while (c < b->len + n + 1) c *= 2;
        char *grown = realloc(b->buf, c);
        if (!grown) return;  /* out of memory: the line is truncated, the host sees invalid JSON and reports it */
        b->buf = grown;
        b->cap = c;
    }
    memcpy(b->buf + b->len, s, n);
    b->len += n;
    b->buf[b->len] = 0;
}

void sb_puts(sb *b, const char *s)
{
    sb_put(b, s, strlen(s));
}

void sb_fmt(sb *b, const char *fmt, ...)
{
    char tmp[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(tmp, sizeof tmp, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > (int)sizeof tmp - 1) n = sizeof tmp - 1;
    sb_put(b, tmp, (size_t)n);
}

void sb_free(sb *b)
{
    free(b->buf);
    b->buf = NULL;
    b->len = b->cap = 0;
}

/* The length of the valid UTF-8 sequence at s (1 to 4), or 0 when the bytes are not one. */
static size_t utf8_sequence(const unsigned char *s, size_t n)
{
    unsigned c = s[0];
    size_t len;
    unsigned min;
    if (c < 0x80) return 1;
    if (c >= 0xC2 && c <= 0xDF) { len = 2; min = 0x80; }
    else if (c >= 0xE0 && c <= 0xEF) { len = 3; min = 0x800; }
    else if (c >= 0xF0 && c <= 0xF4) { len = 4; min = 0x10000; }
    else return 0;
    if (n < len) return 0;
    unsigned value = c & (0xFF >> (len + 1));
    for (size_t i = 1; i < len; i++) {
        if ((s[i] & 0xC0) != 0x80) return 0;
        value = (value << 6) | (s[i] & 0x3F);
    }
    if (value < min || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF)) return 0;
    return len;
}

void sb_jstr(sb *b, const char *s, size_t n)
{
    const unsigned char *u = (const unsigned char *)s;
    sb_puts(b, "\"");
    for (size_t i = 0; i < n;) {
        unsigned char c = u[i];
        if (c == '"') { sb_puts(b, "\\\""); i++; }
        else if (c == '\\') { sb_puts(b, "\\\\"); i++; }
        else if (c == '\n') { sb_puts(b, "\\n"); i++; }
        else if (c == '\r') { sb_puts(b, "\\r"); i++; }
        else if (c == '\t') { sb_puts(b, "\\t"); i++; }
        else if (c < 0x20 || c == 0x7f) { sb_fmt(b, "\\u%04x", c); i++; }
        else {
            size_t len = utf8_sequence(u + i, n - i);
            if (len == 0) { sb_puts(b, "\xEF\xBF\xBD"); i++; }
            else { sb_put(b, (const char *)u + i, len); i += len; }
        }
    }
    sb_puts(b, "\"");
}

void sb_jstrz(sb *b, const char *s)
{
    sb_jstr(b, s ? s : "", s ? strlen(s) : 0);
}
