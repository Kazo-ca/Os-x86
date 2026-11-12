# Os-x86 guest agent (milestone 0, work item 5; decisions 205 A, 206 A, 208 A, 213 A): two static
# musl i386 programs, built in an Alpine stage and copied out by build-agent.py. Nothing is
# installed in a guest image here: images/linux-base and images/alpine-test take the binaries
# from images/build/agent/.
#
# The platform (linux/386) is given by the build command (build-agent.py passes
# --platform linux/386); a constant in FROM is refused by BuildKit's linter.
#
# The compiler packages are pinned by name and version: the build fails when Alpine moves them,
# and the pin is then raised on purpose (the binaries are not byte-reproducible across compiler
# versions).

ARG ALPINE_VERSION=3.24

FROM alpine:${ALPINE_VERSION} AS build
ARG GCC_VERSION=15.2.0-r5
ARG MUSL_DEV_VERSION=1.2.6-r3
RUN apk add --no-cache "gcc=${GCC_VERSION}" "musl-dev=${MUSL_DEV_VERSION}"
COPY src/ /src/
WORKDIR /src
# -Werror: the agent runs as root in every guest; a warning is a defect to fix, not to ship.
# -no-pie: Alpine links position-independent by default; a fixed-address static executable has
# no dynamic section at all, which is what build-agent.py --check verifies.
RUN set -eux; \
    flags="-Os -static -no-pie -s -std=gnu11 -Wall -Wextra -Werror -fno-asynchronous-unwind-tables"; \
    mkdir /out; \
    gcc $flags -o /out/agent agent.c launch.c jobs.c console.c json.c; \
    gcc $flags -o /out/osx86-open-url open-url.c; \
    apk info -v gcc musl-dev musl > /out/toolchain.txt; \
    ls -l /out

# The smoke test of build-agent.py runs here: the binaries, a shell and one guest user.
FROM alpine:${ALPINE_VERSION} AS smoke
COPY --from=build /out/agent /usr/lib/osx86/agent
COPY --from=build /out/osx86-open-url /usr/bin/osx86-open-url
RUN adduser -D -u 1001 -s /bin/sh osx86-test

FROM scratch AS out
COPY --from=build /out/ /
