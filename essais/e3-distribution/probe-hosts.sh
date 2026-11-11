#!/bin/sh
# Essai E3 — sondes HEAD / GET partiel sur des hébergeurs publics (lecture seule, User-Agent générique).
UA="Mozilla/5.0 (compatible; e3-probe)"
ORIGIN="https://example.org"
probe() {
  echo "=================================================================="
  echo "### $1"
  echo "--- HEAD"
  curl.exe -sS -I -L --max-time 30 -A "$UA" -H "Origin: $ORIGIN" "$1" | tr -d '\r' | grep -iE "^(HTTP/|access-control|accept-ranges|content-length|etag|cache-control|age:|server:|x-cache|cf-cache|content-type|location|cross-origin|vary|last-modified|content-encoding)"
  echo "--- GET Range: bytes=0-1023"
  curl.exe -sS -L --max-time 30 -A "$UA" -H "Origin: $ORIGIN" -H "Range: bytes=0-1023" -o /dev/null -D - -w "octets-recus=%{size_download} temps=%{time_total}s\n" "$1" | tr -d '\r' | grep -iE "^(HTTP/|access-control|accept-ranges|content-length|content-range|etag|cache-control|age:|x-cache|cf-cache|octets-recus)"
  echo "--- OPTIONS (prévol CORS, Range demandé)"
  curl.exe -sS -X OPTIONS -L --max-time 30 -A "$UA" -H "Origin: $ORIGIN" -H "Access-Control-Request-Method: GET" -H "Access-Control-Request-Headers: range" -o /dev/null -D - "$1" | tr -d '\r' | grep -iE "^(HTTP/|access-control)"
}
probe "https://microsoft.github.io/monaco-editor/index.html"
probe "https://pages.github.com/images/dinoctocat.png"
probe "https://raw.githubusercontent.com/copy/v86/master/README.md"
probe "https://cdn.jsdelivr.net/npm/jquery@3.7.1/dist/jquery.min.js"
probe "https://cdn.jsdelivr.net/gh/copy/v86@master/README.md"
probe "https://github.com/copy/v86/releases/download/latest/README.md"
probe "https://developers.cloudflare.com/r2/buckets/cors/"
probe "https://pub-83c5db439b40468498b0f6a3e5d20c12.r2.dev/README.md"
