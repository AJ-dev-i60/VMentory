# VMentory Core — single Linux container image (ENG-0010).
# Carries the .NET app + an SSH client (for the Proxmox SSH executor, ENG-0009).
# Deliberately NO qemu/qm — those run on the PVE node, reached over SSH.
# syntax=docker/dockerfile:1

# ── Build ─────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project files first for layer caching.
COPY VMentory.Core/VMentory.Core.csproj VMentory.Core/
COPY VMentory.Web.csproj ./
RUN dotnet restore VMentory.Web.csproj

# Build + publish (framework-dependent; the aspnet runtime base supplies the framework).
COPY . .
ARG VERSION=1.0.0
RUN dotnet publish VMentory.Web.csproj -c Release -o /app --no-restore -p:Version=${VERSION}

# Compute the build stamp from the latest commit timestamp in SAST (UTC+2).
# Format: v{YY}.{MM}.{DD}.{HHMM} — same convention as TableTopCafe.
# Falls back to "dev" if .git is unavailable (local docker build without git history).
RUN git log -1 --format=%cI HEAD > /app/build-stamp.txt 2>/dev/null || echo "dev" > /app/build-stamp.txt

# ── Runtime ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Runtime contract (override at `docker run`). Nothing secret is baked in — the KEK (ENG-0002) and
# TLS cert material (ENG-0010) are injected at runtime via env/mounted volume.
ENV VMENTORY_HTTP_ADDR=0.0.0.0 \
    VMENTORY_HTTP_PORT=8443 \
    VMENTORY_DB=/data/vmentory.db \
    DOTNET_RUNNING_IN_CONTAINER=true

# SQLite DB + cached self-signed cert live on a mounted volume so they survive container replacement.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin vmentory \
    && mkdir -p /data && chown vmentory:vmentory /data
VOLUME /data
USER vmentory

EXPOSE 8443
ENTRYPOINT ["./VMentory"]
