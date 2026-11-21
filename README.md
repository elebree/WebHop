<h1 align="center">
  <img src="./assets/hopper.svg" alt="Hopper, the WebHop rabbit, peeking out of its burrow" width="160">
  <br/>
  WebHop
</h1>

<p align="center">
  <a href="https://github.com/elebree/WebHop/actions/workflows/ci.yml"><img src="https://github.com/elebree/WebHop/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://codecov.io/gh/elebree/WebHop"><img src="https://codecov.io/gh/elebree/WebHop/graph/badge.svg" alt="Coverage"></a>
  <a href="https://www.nuget.org/packages/WebHop.Origin"><img src="https://img.shields.io/nuget/v/WebHop.Origin?label=Origin" alt="WebHop.Origin on NuGet"></a>
  <a href="https://www.nuget.org/packages/WebHop.Gateway"><img src="https://img.shields.io/nuget/v/WebHop.Gateway?label=Gateway" alt="WebHop.Gateway on NuGet"></a>
  <a href="https://www.nuget.org/packages/webhop"><img src="https://img.shields.io/nuget/v/webhop?label=webhop%20tool" alt="webhop dotnet tool on NuGet"></a>
  <a href="https://hub.docker.com/r/elebree/webhop"><img src="https://img.shields.io/docker/v/elebree/webhop?sort=semver&label=Docker" alt="Gateway image on Docker Hub"></a>
  <img src="https://img.shields.io/badge/.NET-7.0%20%7C%208.0%20%7C%209.0%20%7C%2010.0-512BD4" alt=".NET 7.0 | 8.0 | 9.0 | 10.0">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/elebree/WebHop" alt="MIT license"></a>
</p>

Expose a web server behind NAT or a firewall on a public URL, through a gateway you host yourself, like a self-hosted ngrok. The server opens WebSocket tunnels out to the gateway, so it needs no inbound ports, and the gateway forwards public HTTP traffic through them.

Everything HTTP passes through: streamed bodies of any size, WebSockets, server-sent events and client disconnects.

- [webhop](WebHop.CLI/README.md): an ngrok-style command line tool that exposes any local HTTP server, and issues the tokens servers open tunnels with. Start here if you just want to share a local site.
- [WebHop.Origin](WebHop.Origin/README.md): a library that serves your ASP.NET Core app through the gateway, in place of Kestrel's port.
- [WebHop.Gateway](WebHop.Gateway/README.md): the public gateway, standalone or as middleware in your ASP.NET Core app.

## Quick start

Below, `https://gateway.example.com/` stands for your gateway's URL.

### 1. Install `webhop` and create the gateway's keys

Servers open tunnels with a token you issue for each of them, signed with your key. The gateway checks tokens with your public keys only, so it holds no secret that could make one; public visitors need nothing. On your own machine, install the `webhop` command line tool:

```sh
dotnet tool install -g webhop
```

Or download a standalone binary that runs without .NET:

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-x64.zip)
[![Windows arm64](https://img.shields.io/badge/Windows-arm64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-arm64.zip)
[![Linux x64](https://img.shields.io/badge/Linux-x64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-x64.tar.gz)
[![Linux arm64](https://img.shields.io/badge/Linux-arm64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-arm64.tar.gz)
[![macOS arm64](https://img.shields.io/badge/macOS-arm64-A2AAAD)](https://github.com/elebree/WebHop/releases/latest/download/webhop-osx-arm64.tar.gz)

Then create a key set for the gateway:

```sh
webhop issuer init gateway.example.com
```

It keeps the private key set in your user profile and prints the public set, `whk_…`, for the gateway.

### 2. Deploy the gateway

The gateway runs on a public host with WebSockets enabled. Set the public set on it as `WEBHOP_KEYSET`.

[WebHop.Gateway.Host](WebHop.Gateway.Host/README.md) is the ready-to-deploy gateway, with error pages for visitors and the IIS settings Azure App Service needs. Run its Docker image on your host (it serves http on port 8080; put a TLS-terminating proxy in front):

```sh
docker run -d -p 8080:8080 -e WEBHOP_KEYSET=<whk_…> --restart unless-stopped elebree/webhop
```

Or publish it from a clone of this repository (needs the .NET 10 SDK) and deploy the `gateway` folder like any ASP.NET Core app:

```sh
git clone https://github.com/elebree/WebHop.git
dotnet publish WebHop/WebHop.Gateway.Host -c Release -o gateway
```

For **Azure App Service**, follow the [step-by-step guide](WebHop.Gateway.Host/README.md#azure-app-service). To embed the gateway in your own ASP.NET Core app instead, or for reverse proxy and hosting notes, see [WebHop.Gateway](WebHop.Gateway/README.md).

### 3. Issue a token for your server

```sh
webhop token issue gateway.example.com
```

It prints the server's token, `wh_…`, valid for 30 days. The token says where the gateway is and what the server serves there, so the server needs nothing else. To serve several sites on one gateway, see [Several sites on one gateway](WebHop.Gateway/README.md#several-sites-on-one-gateway).

### 4a. Expose any local server with `webhop`

Forward a local port with the token. On another machine, install `webhop` there first, as in step 1.

```sh
webhop http 5000 --token <token>
```

The site on `localhost:5000` is now public at `https://gateway.example.com/`. For tunnels that run unattended next to other containers, there's also a [Docker image](WebHop.CLI/README.md#docker). All commands and flags: [webhop](WebHop.CLI/README.md).

### 4b. Or connect an ASP.NET Core app directly

```sh
dotnet add package WebHop.Origin
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebHop();
```

Start it with the token in `WEBHOP_TOKEN`, leaving out the launch profile, whose `applicationUrl` would take the gateway's place:

```sh
WEBHOP_TOKEN=<token> dotnet run --no-launch-profile
```

The app is now public at `https://gateway.example.com/`. Options and configuration: [WebHop.Origin](WebHop.Origin/README.md).

## License

[MIT](LICENSE)
