# Essai E7 — agent et processus : image E17 (Debian trixie i386 + WineHQ 11.0 + noyau linux-virt
# d'Alpine) + agent statique (musl i386) + deux usagers-bouteilles (wine-b1, wine-b2) avec chacun
# une copie du préfixe préfabriqué + inotify-tools et xterm. Prototype jetable.
# Construire : pwsh -File build.ps1   (exige l'image Docker e17-wine-stable de l'essai E17)

# --- Étape 1 : agent et aides, compilés en statique (musl, i386).
FROM --platform=linux/386 alpine:3.24 AS build
RUN apk add --no-cache gcc musl-dev
COPY agent/ /src/
RUN cd /src && for f in e7-agent e7-open-url e7-ptrace e7-rawsock; do gcc -O2 -static -s -o $f $f.c; done && ls -l /src

# --- Étape 2 : image E17 + usagers, préfixes, outils, agent, init.
FROM e17-wine-stable
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends inotify-tools xterm util-linux \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

# Deux bouteilles = deux usagers invités (uid 1001, 1002), home 0700 sur la racine 9p, préfixe Wine
# copié du préfixe préfabriqué de E17 (les octets sont les mêmes : dédoublonnés par hachage dans l'image 9p).
RUN for i in 1 2; do \
      useradd -m -u 100$i -U -s /bin/sh -d /home/wine-b$i wine-b$i \
   && cp -a /root/.wine /home/wine-b$i/.wine \
   && mkdir -p /home/wine-b$i/share /home/wine-b$i/disk \
   && chown -R 100$i:100$i /home/wine-b$i && chmod 700 /home/wine-b$i ; \
    done \
 && mkdir -p /srv/share-b1/sub && echo "hello from the share" > /srv/share-b1/hello.txt \
 && echo "never read before the fault" > /srv/share-b1/never-read.txt \
 && echo "deep" > /srv/share-b1/sub/deep.txt && chown -R 1001:1001 /srv/share-b1

# Garantie 2 : aucun setuid/setgid dans l'image, ni sudo ni doas.
RUN find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -exec chmod u-s,g-s {} + ; \
    find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -print

COPY --from=build /src/e7-agent /src/e7-open-url /src/e7-ptrace /src/e7-rawsock /usr/local/bin/
COPY guest/list-windows.sh /usr/local/lib/e7/list-windows.sh
COPY guest/tree.sh guest/bomb.sh guest/verify-console.sh /usr/local/lib/e7/
COPY guest/init.sh /sbin/init
COPY guest/x-up.sh /root/x-up.sh
COPY guest/shrc /root/.shrc
RUN chmod 755 /sbin/init /usr/local/bin/e7-* /usr/local/lib/e7/*.sh /root/x-up.sh \
 && chmod 755 /usr/local/lib/e7 && echo debian > /etc/hostname && rm -rf /tmp/.wine-0 /root/.cache
