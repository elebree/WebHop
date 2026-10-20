# Build from the repository root: docker build -f WebHop.Gateway.Host/Dockerfile .
# The .NET 10 SDK: the libraries also target net10.0, which the .NET 9 SDK can't restore. The host
# itself targets net9.0, hence the 9.0 runtime image below.
# The SDK stage runs on the build machine's own architecture and cross-compiles for the target
# with -a, so multi-architecture builds need no emulation:
#   docker buildx build --platform linux/amd64,linux/arm64 -f WebHop.Gateway.Host/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Set by BuildKit (amd64, arm64); the SDK accepts Docker's architecture names
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props ./
COPY WebHop.Core/WebHop.Core.csproj WebHop.Core/
COPY WebHop.Gateway/WebHop.Gateway.csproj WebHop.Gateway/
COPY WebHop.Gateway.Host/WebHop.Gateway.Host.csproj WebHop.Gateway.Host/
RUN dotnet restore WebHop.Gateway.Host/WebHop.Gateway.Host.csproj -a $TARGETARCH
COPY WebHop.Core/ WebHop.Core/
COPY WebHop.Gateway/ WebHop.Gateway/
COPY WebHop.Gateway.Host/ WebHop.Gateway.Host/
# The Docker workflow passes the version from the git tag
ARG VERSION
RUN dotnet publish WebHop.Gateway.Host/WebHop.Gateway.Host.csproj -c Release -o /app -a $TARGETARCH --no-restore \
    ${VERSION:+-p:Version=$VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
# Listens on ASPNETCORE_HTTP_PORTS (8080 by default in .NET images)
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebHop.Gateway.Host.dll"]
