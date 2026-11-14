# WebHop.Gateway

The public side of [WebHop](https://github.com/elebree/WebHop), as ASP.NET Core middleware. Apps behind NAT or a firewall open WebSocket tunnels to the gateway, and the gateway forwards public HTTP traffic through them with [YARP](https://github.com/dotnet/yarp). Request and response bodies stream in both directions; WebSockets, server-sent events and multi-value headers pass through.

```
browser ──HTTP──▶ WebHop.Gateway ◀──WebSocket tunnels── your app
                  (public host)                          (behind NAT, no open ports)
```

Apps connect with [WebHop.Origin](https://www.nuget.org/packages/WebHop.Origin), or any local server with the [`webhop`](https://www.nuget.org/packages/webhop) command line tool.

## Standalone gateway

```sh
dotnet new web -n MyGateway
cd MyGateway
dotnet add package WebHop.Gateway
```

```csharp
using WebHop.Gateway;

GatewayApp.Create(args).Run();
```

`GatewayApp` is the complete gateway: unlimited request body size, the WebHop middleware, and a 503 while no app is connected.

For a gateway ready to deploy without writing code, with error pages for visitors and the IIS settings Azure App Service needs, use [WebHop.Gateway.Host](https://github.com/elebree/WebHop/tree/main/WebHop.Gateway.Host): run its Docker image, `elebree/webhop`, or publish it from the repository.

## In an existing app

```csharp
builder.Services.AddWebHopGateway();

var app = builder.Build();
app.UseWebSockets();
app.UseWebHopGateway();
app.MapGet("/", () => "No app connected");   // your own content
```

Every request is forwarded to an app connected on the request's host name; with several connected there, they take turns. While none is connected, requests fall through to the rest of your pipeline.

## Several sites on one gateway

An app serves the host name it reached the gateway on. Point several names at one gateway (a wildcard DNS record such as `*.gateway.example.com` and a matching certificate) and give each app its own:

```text
webhop http 5000 --url https://app1.gateway.example.com/
webhop http 5001 --url https://app2.gateway.example.com/
```

Requests for `app1.gateway.example.com` reach only the first app, and requests for a name no app is connected on get a 503. Names are matched without the port and regardless of case. Every app presents the same auth token, so any of them may connect on any name: share the token only with apps you trust.

## Auth token

Only apps that present the gateway's auth token can open tunnels; public visitors don't need it. Set it with the `WEBHOP_AUTHTOKEN` environment variable or the `WebHop:AuthToken` configuration key (the setting wins when both are set). Without one the gateway still starts, but rejects every tunnel and logs an error.

The token is sent each time a tunnel opens, so serve the gateway over https outside local development.

## Endpoints

| Path | Purpose |
|---|---|
| `/webhop` | Where apps open their tunnels (WebSocket) |
| `/webhop/status` | Health check |
| anything else | Forwarded to a connected app |

Forwarded requests carry `X-Forwarded-For` (the client's address and port, or the address alone when a proxy in front named the client), `X-Forwarded-Proto` and `X-Forwarded-Host`, plus `X-Webhop-Origin-Id` and `X-Webhop-Request-Id` on requests and responses. A request without a response within 120 s gets a 504.

## Hosting notes

- Enable WebSockets on the host (on Azure App Service, in the app's configuration).
- Run a single instance: tunnels live in memory, so behind a load balancer a request can land on an instance the app isn't connected to and get a 503.
- Behind a TLS-terminating reverse proxy (nginx, Traefik, Caddy), have it send `X-Forwarded-For` and `X-Forwarded-Proto` (nginx: `proxy_set_header X-Forwarded-For $remote_addr;` and `proxy_set_header X-Forwarded-Proto $scheme;`), so apps see the client's real IP and scheme. The standalone gateway believes these from a proxy on loopback or a private network, such as one on the same host or the same Docker network, and never from a public address, so a visitor can't name another client. For a proxy that connects from a public address (a CDN), set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, which believes every client, and let nothing but the proxy reach the gateway. Embedded with `UseWebHopGateway`, your app's own forwarded headers setup applies instead.
- The proxy must keep the request's `Host` (nginx: `proxy_set_header Host $host;`), since apps are picked by it.
- The proxy must also pass WebSocket upgrades on `/webhop`; nginx needs `proxy_http_version 1.1` and the `Upgrade`/`Connection` headers for this.

## Azure App Service

- Enable **WebSockets** in the app's configuration.
- Set `WEBHOP_AUTHTOKEN` as an application setting.
- Point your apps at `https://<your-app>.azurewebsites.net/`.
- Mind the plan's WebSocket limit per instance: Free allows 5, Shared 35, Basic 350, Standard and up have no fixed limit. Every tunnel is one WebSocket, and so is every WebSocket your visitors open. On Free and Shared the gateway tells apps how many tunnels each may open (60% of the limit), leaving room for visitors; `WebHop:MaxWebSockets` overrides the detected limit.

Step by step, with the Azure CLI or Visual Studio: [deploying WebHop.Gateway.Host](https://github.com/elebree/WebHop/tree/main/WebHop.Gateway.Host#azure-app-service).

## More

https://github.com/elebree/WebHop
