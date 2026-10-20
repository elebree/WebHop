# The webhop tool as a Native AOT binary, built like the release binaries. Build from the repository
# root: docker build -f WebHop.CLI/Dockerfile .
# The SDK stage runs on the build machine's own architecture and cross-compiles for the target, so
# multi-architecture builds need no emulation:
#   docker buildx build --platform linux/amd64,linux/arm64 -f WebHop.CLI/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
# Set by BuildKit (amd64, arm64); the SDK accepts Docker's architecture names
ARG BUILDARCH
ARG TARGETARCH
# Native AOT links with the target's C toolchain and libraries, which the image only has for its own
# architecture. A cross build adds them for the target from Ubuntu's archive, as in the
# cross-compilation samples of dotnet-docker's AspNetCoreNativeAot.
RUN if [ "$TARGETARCH" != "$BUILDARCH" ]; then \
        case "$TARGETARCH" in \
            amd64) uri=http://archive.ubuntu.com/ubuntu/ gcc=gcc-x86-64-linux-gnu ;; \
            arm64) uri=http://ports.ubuntu.com/ubuntu-ports/ gcc=gcc-aarch64-linux-gnu ;; \
            *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
        esac \
        && . /etc/os-release \
        && sed -i "/Signed-By/ a Architectures: $BUILDARCH" /etc/apt/sources.list.d/ubuntu.sources \
        && printf 'Types: deb\nURIs: %s\nSuites: %s %s-updates\nComponents: main restricted\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\nArchitectures: %s\n' \
            "$uri" "$VERSION_CODENAME" "$VERSION_CODENAME" "$TARGETARCH" > /etc/apt/sources.list.d/$TARGETARCH.sources \
        && dpkg --add-architecture $TARGETARCH \
        && apt-get update \
        && apt-get install -y clang $gcc llvm zlib1g-dev:$TARGETARCH \
        && rm -rf /var/lib/apt/lists/*; \
    fi
WORKDIR /src
COPY Directory.Build.props ./
COPY WebHop.Core/WebHop.Core.csproj WebHop.Core/
COPY WebHop.Origin/WebHop.Origin.csproj WebHop.Origin/
COPY WebHop.CLI/WebHop.CLI.csproj WebHop.CLI/
# Only net10.0, as the release workflow builds the AOT binaries
RUN dotnet restore WebHop.CLI/WebHop.CLI.csproj -a $TARGETARCH -p:TargetFrameworks=net10.0 -p:PublishAot=true
COPY WebHop.Core/ WebHop.Core/
COPY WebHop.Origin/ WebHop.Origin/
COPY WebHop.CLI/ WebHop.CLI/
# The version `webhop version` shows; the Docker workflow passes it from the git tag
ARG VERSION
RUN dotnet publish WebHop.CLI/WebHop.CLI.csproj -c Release -o /app -a $TARGETARCH --no-restore \
    -f net10.0 -p:TargetFrameworks=net10.0 -p:PublishAot=true -p:DebugType=none ${VERSION:+-p:Version=$VERSION}

# No shell or package manager, and no ICU: the image runs with invariant globalization
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
# Only the executable: the publish folder also gets aspnetcorev2_inprocess.dll (the IIS module from
# the shared framework), which a console tool never loads
COPY --from=build /app/webhop .
# The live screen (docker run -it) moves the cursor and colors with the terminal's terminfo entry,
# which the chiseled image lacks: without it every redraw prints the whole screen again, uncolored.
# The common entries (xterm, which docker run -t sets, screen, tmux, linux...) are 224 KB.
COPY --from=build /usr/share/terminfo /usr/share/terminfo
# The base image sets 8080 for web servers; webhop listens on no port, and with it set every start
# logs that HTTP_PORTS is overridden
ENV ASPNETCORE_HTTP_PORTS=
USER $APP_UID
# Prints log lines instead of the live screen unless run with -t
ENTRYPOINT ["./webhop"]
