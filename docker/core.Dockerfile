# The build is the same for every platform (the app is portable .NET), so it runs natively and only the runtime image differs.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Melodex.sln ./
COPY src/Melodex.Contracts/Melodex.Contracts.csproj src/Melodex.Contracts/
COPY src/Melodex.Client/Melodex.Client.csproj src/Melodex.Client/
COPY src/Melodex.Web/Melodex.Web.csproj src/Melodex.Web/
COPY src/Melodex.Plugins.MusicBrainz/Melodex.Plugins.MusicBrainz.csproj src/Melodex.Plugins.MusicBrainz/
COPY src/Melodex.Plugins.Navidrome/Melodex.Plugins.Navidrome.csproj src/Melodex.Plugins.Navidrome/
COPY src/Melodex.Plugins.Plex/Melodex.Plugins.Plex.csproj src/Melodex.Plugins.Plex/
COPY src/Melodex.Plugins.Jellyfin/Melodex.Plugins.Jellyfin.csproj src/Melodex.Plugins.Jellyfin/
COPY src/Melodex.Plugins.FanartTv/Melodex.Plugins.FanartTv.csproj src/Melodex.Plugins.FanartTv/
COPY src/Melodex.Plugins.Wikimedia/Melodex.Plugins.Wikimedia.csproj src/Melodex.Plugins.Wikimedia/
COPY src/Melodex.Plugins.Deezer/Melodex.Plugins.Deezer.csproj src/Melodex.Plugins.Deezer/
COPY src/Melodex.Plugins.Lrclib/Melodex.Plugins.Lrclib.csproj src/Melodex.Plugins.Lrclib/
COPY src/Melodex.Core/Melodex.Core.csproj src/Melodex.Core/
RUN dotnet restore src/Melodex.Core/Melodex.Core.csproj
COPY src/ src/
# The web app carries the user guide and changelog, for Settings → Help.
COPY docs/guide/ docs/guide/
COPY CHANGELOG.md ./
# Releases build with the tag's version; anything else keeps the one in Directory.Build.props.
# The commit, which the About page links to, is passed in too: .git isn't part of the build context.
ARG VERSION=""
ARG COMMIT=""
RUN dotnet publish src/Melodex.Core/Melodex.Core.csproj -c Release -o /app --no-restore \
    ${VERSION:+-p:Version=$VERSION} ${COMMIT:+-p:SourceRevisionId=$COMMIT}

# An ffmpeg that only decodes audio, for the visualiser's bars (#89), and repacks Opus in WebM for Safari (#93): about
# 2 MB, where Debian's is over 400. Like the app, it's built natively, cross-compiled for the image's platform when
# that's another, and has no GPL parts.
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS ffmpeg
ARG FFMPEG_VERSION=7.1.1
ARG TARGETARCH
ARG BUILDARCH
RUN case "$TARGETARCH" in arm64) triple=aarch64-linux-gnu ;; *) triple=x86_64-linux-gnu ;; esac \
    && cross=$([ "$TARGETARCH" != "$BUILDARCH" ] && echo "gcc-$(echo $triple | tr _ -) libc6-dev-$TARGETARCH-cross" || true) \
    && apt-get update && apt-get install -y --no-install-recommends build-essential nasm curl ca-certificates xz-utils $cross \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN curl -fsSL https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz | tar -xJ --strip-components=1
RUN case "$TARGETARCH" in arm64) arch=aarch64 ;; *) arch=x86_64 ;; esac \
    && prefix=$([ "$TARGETARCH" != "$BUILDARCH" ] && echo "$arch-linux-gnu-" || true) \
    && ./configure --prefix=/out ${prefix:+--enable-cross-compile --arch=$arch --target-os=linux --cross-prefix=$prefix} \
        --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
        --disable-ffplay --disable-ffprobe --disable-avdevice --disable-swscale --disable-postproc \
        --enable-small --enable-swresample \
        --enable-protocol=file,pipe \
        --enable-demuxer=flac,ogg,mp3,mov,wav,aiff,matroska,aac,w64 \
        --enable-parser=flac,opus,vorbis,mpegaudio,aac \
        --enable-decoder=flac,opus,vorbis,mp3,mp3float,aac,aac_fixed,alac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_s16be,pcm_s24be,pcm_s32be \
        --enable-encoder=pcm_f32le --enable-muxer=pcm_f32le,webm \
        --enable-filter=aresample,aformat,anull,amix,pan \
    && make -j"$(nproc)" && make install && ${prefix}strip /out/bin/ffmpeg

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="Melodex" \
      org.opencontainers.image.description="Find what's missing from your music library, fetch it, and listen now." \
      org.opencontainers.image.source="https://github.com/dextrd/Melodex" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
# Staging is shared with the beets worker and the plugins, which run as other users. Whichever image first mounts a
# new volume sets its owner, so every image makes it writable by all, sticky so nobody removes another's jobs.
RUN mkdir -p /config /music /staging /listen-now /plugins \
    && chown -R $APP_UID /config /listen-now /plugins \
    && chmod 1777 /staging
COPY --from=build /app .
COPY --from=ffmpeg /out/bin/ffmpeg /usr/local/bin/ffmpeg
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Melodex__DataPath=/config \
    Melodex__LibraryPath=/music \
    Melodex__StagingPath=/staging \
    Melodex__ListenNowPath=/listen-now \
    Melodex__PluginsPath=/plugins
EXPOSE 8080
VOLUME ["/config", "/staging", "/listen-now"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Melodex.Core.dll"]
