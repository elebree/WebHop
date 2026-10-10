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
  <img src="https://img.shields.io/badge/.NET-7.0%20%7C%208.0%20%7C%209.0%20%7C%2010.0-512BD4" alt=".NET 7.0 | 8.0 | 9.0 | 10.0">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/elebree/WebHop" alt="MIT license"></a>
</p>

Expose a web server behind NAT or a firewall on a public URL, through a gateway you host yourself, like a self-hosted ngrok. The server opens WebSocket tunnels out to the gateway, so it needs no inbound ports, and the gateway forwards public HTTP traffic through them.

```
browser ──HTTP──▶ gateway ◀──WebSocket tunnels── your server
                  (public host)                  (behind NAT, no open ports)
```

Everything HTTP passes through: streamed bodies of any size, WebSockets, server-sent events and client disconnects.

- [webhop](WebHop.CLI/README.md): an ngrok-style command line tool that exposes any local HTTP server. Start here if you just want to share a local site.
- [WebHop.Origin](WebHop.Origin/README.md): a library that serves your ASP.NET Core app through the gateway, in place of Kestrel's port.
- [WebHop.Gateway](WebHop.Gateway/README.md): the public gateway, standalone or as middleware in your ASP.NET Core app.

## Quick start

### 1. Deploy a gateway

The gateway runs on a public host with WebSockets enabled. Pick an auth token (any long random string) and set it on the gateway as `WEBHOP_AUTHTOKEN`. Only servers that present it can open tunnels; public visitors don't need it.

[WebHop.Gateway.Host](WebHop.Gateway.Host/README.md) is the ready-to-deploy gateway, with error pages for visitors and the IIS settings Azure App Service needs. Publish it from a clone of this repository (needs the .NET 10 SDK):

```sh
git clone https://github.com/elebree/WebHop.git
dotnet publish WebHop/WebHop.Gateway.Host -c Release -o gateway
```

and deploy the `gateway` folder like any ASP.NET Core app. On **Azure App Service**, enable WebSockets in the app's configuration and add `WEBHOP_AUTHTOKEN` as an application setting.

Or run it in Docker on your host, from the clone's root (it serves http on port 8080; put a TLS-terminating proxy in front):

```sh
WEBHOP_AUTHTOKEN=<token> docker compose up --build -d
```

To embed the gateway in your own ASP.NET Core app instead, or for reverse proxy and hosting notes, see [WebHop.Gateway](WebHop.Gateway/README.md).

Below, `https://gateway.example.com/` stands for your gateway's URL.

### 2a. Expose any local server with `webhop`

```sh
dotnet tool install -g webhop
```

Or download a standalone binary that runs without .NET:

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-x64.zip)
[![Windows arm64](https://img.shields.io/badge/Windows-arm64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-arm64.zip)
[![Linux x64](https://img.shields.io/badge/Linux-x64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-x64.tar.gz)
[![Linux arm64](https://img.shields.io/badge/Linux-arm64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-arm64.tar.gz)
[![macOS arm64](https://img.shields.io/badge/macOS-arm64-A2AAAD)](https://github.com/elebree/WebHop/releases/latest/download/webhop-osx-arm64.tar.gz)

Point it at your gateway and forward a local port:

```sh
webhop config add-gateway-url https://gateway.example.com/
webhop config add-authtoken <token>
webhop http 5000
```

The site on `localhost:5000` is now public at `https://gateway.example.com/`. All commands and flags: [webhop](WebHop.CLI/README.md).

### 2b. Or connect an ASP.NET Core app directly

```sh
dotnet add package WebHop.Origin
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebHop();
```

Start it with the gateway's auth token in `WEBHOP_AUTHTOKEN`, and set `--urls` to the **gateway's** URL. That is both where the app connects and where it listens for requests:

```sh
dotnet run --urls https://gateway.example.com/
```

The app is now public at `https://gateway.example.com/`. Options and configuration: [WebHop.Origin](WebHop.Origin/README.md).

## License

[MIT](LICENSE)
