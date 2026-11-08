# syntax=docker/dockerfile:1
#
# INFOCompanion — one image containing the Rust daemon, the C# co-play agent, the
# frotz interpreter it drives, and the web interface that draws its panes in a browser.
#
# The build pulls the projects together (§9.1): the Rust core is compiled with
# the official Rust image, the agent is published as a self-contained single-file
# Linux binary, and the runtime image stays slim because neither toolchain is
# needed at run time.

# ---------------------------------------------------------------------------
# Stage 1: the Rust core
# ---------------------------------------------------------------------------
FROM rust:bookworm AS core-build

WORKDIR /build

# `git`, for one question the build has to answer itself: which commit is this (spec 05 §6.2). The
# daemon and the web interface are two crates built in this stage, and the debug bundle they can
# produce has to be able to say which build wrote it — `0.1.0` is every build this repository has
# ever made, so a bundle that says nothing else cannot be answered.
#
# Asked here rather than in `build.rs` alone, because there is no repository to run `git` in: the
# source arrives as a copy of the source *tree*. So a copy of the context — `.git` included, which
# is the only reason `.dockerignore` used to exclude it — is made and three questions are asked of
# it. Every answer falls back to `unknown` rather than failing the build: a build from a tarball
# produces an image that admits it does not know its commit, which is an honest answer; refusing to
# compile is not. `git` is already in the official Rust image, but it is asked with `command -v`
# anyway, because that is an assumption about somebody else's image.
#
# A copy, not a read-only bind mount (`RUN --mount`), which would be tidier and is a BuildKit
# feature: the machine this is built on has no `buildx` for `docker build` to find, so a Dockerfile
# that needed one would fail for exactly the people building it by hand. And the copy is of the
# *whole* context because `COPY` fails outright when its source is not there — the missing-`.git`
# case has to come out `unknown`, not stop the build. `rm -rf` after, so the 13 MB is cache and not
# a layer anything downstream looks at.
COPY . /ctx
RUN if [ -d /ctx/.git ] && command -v git >/dev/null 2>&1; then \
        git --git-dir=/ctx/.git rev-parse --short=12 HEAD > /build/ic-build-sha || echo unknown > /build/ic-build-sha; \
        git --git-dir=/ctx/.git rev-parse --abbrev-ref HEAD > /build/ic-build-branch || echo unknown > /build/ic-build-branch; \
        git --git-dir=/ctx/.git show -s --format=%cI HEAD > /build/ic-build-date || echo unknown > /build/ic-build-date; \
    else \
        echo unknown > /build/ic-build-sha; \
        echo unknown > /build/ic-build-branch; \
        echo unknown > /build/ic-build-date; \
    fi \
    && rm -rf /ctx \
    && echo "building from $(cat /build/ic-build-sha) on $(cat /build/ic-build-branch)"

# `dirty` is deliberately not asked for. `git status` needs a working tree, and the tree this build
# can see is the *context* — which has had `Documents/`, `*.md` and the rest filtered out of it, so
# every one of those tracked files reads as deleted there. A manifest claiming the build was dirty
# on that evidence would be a lie; `unknown` is the honest answer, so the line is simply left out.

# Warm the dependency cache with the manifest alone, so editing core sources does
# not re-download and rebuild the world.
COPY Source/Core/Cargo.toml Source/Core/Cargo.lock ./
RUN mkdir -p src \
    && echo 'fn main() {}' > src/main.rs \
    && cargo build --release --locked \
    && rm -rf src

COPY Source/Core/src ./src
RUN touch src/main.rs && cargo build --release --locked

# The web interface is the project's second crate, built in this same stage so it shares the
# registry cache warmed above — and so the runtime image never carries a toolchain. It talks to
# the daemon with `russh`, the crate the daemon serves with, here as a client: one SSH
# implementation at both ends, and one place to fix it when the two disagree.
WORKDIR /build/webui
COPY Source/WebUI/Cargo.toml Source/WebUI/Cargo.lock Source/WebUI/build.rs ./
RUN mkdir -p src \
    && echo 'fn main() {}' > src/main.rs \
    && cargo build --release --locked \
    && rm -rf src

# The identity, in the environment of the build that matters. `build.rs` asks for it first
# (`Source/WebUI/build.rs`), reads it at compile time, and the manifest prints it — so an image built
# here can say which commit it is, which is the difference between a usable bug report and an
# unanswerable one. Read from the files written above rather than recomputed, so the value cannot
# drift between the two `cargo` lines.
#
# The warm-up build above deliberately does not carry it: what it compiles is a stub, and cargo
# re-runs a build script whose declared environment has changed, which is what
# `cargo:rerun-if-env-changed` in `build.rs` is for.
COPY Source/WebUI/src ./src
RUN IC_BUILD_SHA="$(cat /build/ic-build-sha)" \
    IC_BUILD_BRANCH="$(cat /build/ic-build-branch)" \
    IC_BUILD_DATE="$(cat /build/ic-build-date)" \
    cargo build --release --locked

# ---------------------------------------------------------------------------
# Stage 2: the C# agent
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent-build

WORKDIR /build

# A self-contained publish is architecture-specific, so the architecture has to be *asked for*
# rather than assumed: this image is built on an x64 host and on an arm64 one, natively, and
# nothing here may rely on emulation. BuildKit sets `TARGETARCH` from the platform under
# construction; on a legacy builder it is empty and the case fails loudly, which is the right
# outcome — a silently amd64 agent inside an arm64 image is a container that starts and then
# cannot run its companion.
ARG TARGETARCH

# Same trick: restore against the project files only. Note this restores the
# *project*, not the solution: the solution includes the test project, which is
# deliberately not part of the image.
COPY Source/Agent/Directory.Build.props Source/Agent/global.json ./
COPY Source/Agent/src/Agent.Core/Agent.Core.csproj src/Agent.Core/
COPY Source/Agent/src/Agent.Llm/Agent.Llm.csproj src/Agent.Llm/
COPY Source/Agent/src/Agent.Notebook/Agent.Notebook.csproj src/Agent.Notebook/
RUN dotnet restore src/Agent.Core/Agent.Core.csproj

COPY Source/Agent/src ./src
RUN case "${TARGETARCH}" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/Agent.Core/Agent.Core.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        --output /out \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none \
        -p:DebugSymbols=false

# ---------------------------------------------------------------------------
# Stage 2b: the default story — Zork I, compiled from its MIT-licensed source
# ---------------------------------------------------------------------------
# On 2025-11-20 Microsoft's Open Source Programs Office, Team Xbox and Activision placed
# Zork I, II and III under the MIT License, contributing the grant upstream to the
# historicalsource repositories. This stage compiles the story from that source with
# ZILF, both pinned exactly, so the image carries a file built from auditable inputs
# rather than a blob of uncertain provenance. ZILF is a build-time tool only: nothing
# of it — or of the source — ships in the runtime image.
FROM debian:bookworm-slim AS story-build

ARG TARGETARCH

ARG ZORK1_COMMIT=97b7b3d68c075dd9af7da499c3e9690ada3471fd
ARG ZILF_URL=https://github.com/taradinoc/zilf/releases/download/1.9
# ZAPF stamps the build date into the header serial by default, which would make every
# rebuild a different artifact. Pinning it keeps the build byte-reproducible; 260917 is
# the day this recipe was written.
ARG ZORK1_SERIAL=260917

RUN apt-get update && \
    apt-get install -y --no-install-recommends ca-certificates curl libicu72 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /story

# The ZILF release carries one tarball per architecture, and neither host this is built on can
# run the other's binary — x64 runners build the x64 image, and the arm64 image is built on an
# arm64 runner rather than emulated. So the tarball that matches `TARGETARCH` is the one that
# runs. (The story itself is a Z-machine file: what comes out is the same whichever host
# compiled it, which is why the serial is pinned above.)
RUN case "${TARGETARCH}" in \
        amd64) ZILF_ARCH=linux-x64 ;; \
        arm64) ZILF_ARCH=linux-arm64 ;; \
        *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && mkdir -p zilf src \
    && curl -fsSL "${ZILF_URL}/zilf-1.9.0-${ZILF_ARCH}.tar.gz" | tar xz -C zilf --strip-components=1 \
    && curl -fsSL "https://github.com/historicalsource/zork1/archive/${ZORK1_COMMIT}.tar.gz" \
        | tar xz -C src --strip-components=1

WORKDIR /story/src
RUN /story/zilf/bin/zilf zork1.zil && \
    /story/zilf/bin/zapf -q -s "$ZORK1_SERIAL" zork1.zap zork1.z3 && \
    cp LICENSE /story/LICENSE.zork1-mit.txt

# ---------------------------------------------------------------------------
# Stage 2c: the interpreter — dfrotz, built from the source we vendor
# ---------------------------------------------------------------------------
# The daemon drives one interpreter, and the game boundary is a consequence of its
# interface (spec 02): framing, the save protocol, the parser vocabulary, the escape
# rules. It used to come from Debian's `frotz` package. It now comes from
# `Source/dFrotzTooled/` — upstream Frotz 2.54 with Debian's one patch — so that the
# interpreter is ours to change (spec 02 §2); `Source/dFrotzTooled/VENDOR.md` records
# exactly where the source came from and what was applied to it.
#
# `make dumb` builds one binary, at the root of the tree, and links no curses: the dumb
# interface needs a C compiler and nothing else. Only `dumb` is built — the curses, SDL
# and X11 targets are not wanted here.
FROM debian:bookworm-slim AS frotz-build

RUN apt-get update && \
    apt-get install -y --no-install-recommends build-essential && \
    rm -rf /var/lib/apt/lists/*

COPY Source/dFrotzTooled/ /frotz/
RUN cd /frotz && make dumb

# ---------------------------------------------------------------------------
# Stage 3: runtime
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive
ENV TZ=Europe/London

# locales     — the games and the agent both assume UTF-8
# libssl3     — TLS for the agent's HttpClient to the model
# (there is no `frotz` package here: the interpreter is built from the vendored source
#  in Stage 2c and copied below, so the version we run is the version we compiled)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        locales \
        tzdata \
        ncurses-term \
        ca-certificates \
        libssl3 \
        zlib1g \
        bash && \
    rm -rf /var/lib/apt/lists/*

RUN sed -i 's/# en_GB.UTF-8 UTF-8/en_GB.UTF-8 UTF-8/' /etc/locale.gen && \
    locale-gen

ENV LANG=en_GB.UTF-8
ENV LC_ALL=en_GB.UTF-8
ENV TERM=xterm-256color

COPY --from=core-build /build/target/release/infocompanion /usr/local/bin/infocompanion
COPY --from=core-build /build/webui/target/release/infocompanion-webui /usr/local/bin/infocompanion-webui

# The interpreter, built in Stage 2c. The path is the one the package used, and the one
# `docker-compose.yml` names in INFOCOMPANION_DFROTZ, so nothing else moves.
COPY --from=frotz-build /frotz/dfrotz /usr/games/dfrotz
RUN chmod 755 /usr/games/dfrotz

# The published agent is a single self-contained file, so the runtime image needs
# no .NET. The agent still finds a prompt if it is ever run with an empty /prompts:
# the engine and the shipped persona also sit next to the binary as a last resort.
COPY --from=agent-build /out/ /usr/local/bin/
COPY Assets/prompts/engine.txt /usr/local/bin/engine.txt
COPY Assets/prompts/persona/james.txt /usr/local/bin/james.txt

# The shipped assets (spec 08): the entry point seeds this pack into the working paths
# on start — copy-if-missing, never overwriting — so a container with no mounts at all
# is a working one. The sidecars are our commentary on the physical packages; the story
# is the MIT-licensed Zork I compiled above. Deleting this directory is the whole of the
# removal path; nothing else depends on it being there.
COPY Assets/games/*.md /opt/infocompanion/assets/games/
COPY Assets/prompts/ /opt/infocompanion/assets/prompts/
COPY Assets/agent-config.json /opt/infocompanion/assets/agent-config.json
COPY Assets/core-config.json /opt/infocompanion/assets/core-config.json
COPY Assets/webui-config.json /opt/infocompanion/assets/webui-config.json
COPY --from=story-build /story/src/zork1.z3 /opt/infocompanion/assets/games/zork1.z3
COPY --from=story-build /story/LICENSE.zork1-mit.txt /opt/infocompanion/assets/licenses/zork1-MIT.txt
COPY THIRD-PARTY-NOTICES.md /opt/infocompanion/NOTICES.md

# The smoke test runs inside the image, where frotz and a story file exist.
COPY Build/tests/ /tests/
RUN chmod +x /tests/*.sh

# The web interface is its own project (Source/WebUI/README.md). Only its pages are copied: the crate
# itself is compiled in the stage above, and a binary does not need its source at run time. Its
# test is copied into /tests as well so the existing container suite picks it up without any of
# those scripts being edited.
COPY Source/WebUI/public /opt/webui/public
COPY Source/WebUI/tests/smoke.sh /tests/webui.sh
RUN chmod +x /tests/*.sh

# /games holds story files and sidecars — the entry point seeds the bundled pack into
# it on start; /state is the daemon's own storage — saves, notebooks, logs and the
# current-game pointer (§5.2).
RUN mkdir -p /games /state && chmod 755 /games /state

# 2222 is the session itself, as SSH panes; 8080 is the web interface, which the entry point
# starts behind the daemon. The health check stays on the session: the web interface is a
# convenience, so a container whose game is up is healthy even if nobody looks at it in a
# browser.
EXPOSE 2222 8080

# A TCP connect is enough to prove the listener is up.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD timeout 2 bash -c 'exec 3<>/dev/tcp/127.0.0.1/2222' || exit 1

# The entry point starts the web interface and then exec's the daemon, so the daemon is still
# PID 1, still the container's exit code, and still the thing that owns the session. The
# seeder runs first, inside the entry point (spec 08 §3).
COPY Build/docker/seed-assets.sh /usr/local/bin/seed-assets.sh
COPY Build/docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/seed-assets.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
