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
