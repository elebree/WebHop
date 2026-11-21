# WebHop.Gateway

The public side of [WebHop](https://github.com/elebree/WebHop), as ASP.NET Core middleware. Apps behind NAT or a firewall open WebSocket tunnels to the gateway, and the gateway forwards public HTTP traffic through them with [YARP](https://github.com/dotnet/yarp). Request and response bodies stream in both directions; WebSockets, server-sent events and multi-value headers pass through.

```
browser ──HTTP──▶ WebHop.Gateway ◀──WebSocket tunnels── your app
                  (public host)                          (behind NAT, no open ports)
```

Apps connect with [WebHop.Origin](https://www.nuget.org/packages/WebHop.Origin), or any local server with the [`webhop`](https://www.nuget.org/packages/webhop) command line tool, each with a token you issue with [`webhop-gen`](https://www.nuget.org/packages/webhop-gen).

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

`GatewayApp` is the complete gateway: unlimited request body size, the WebHop middleware, and a 503 for requests no connected app serves.

For a gateway ready to deploy without writing code, with error pages for visitors and the IIS settings Azure App Service needs, use [WebHop.Gateway.Host](https://github.com/elebree/WebHop/tree/main/WebHop.Gateway.Host): run its Docker image, `elebree/webhop`, or publish it from the repository.

## In an existing app

```csharp
builder.Services.AddWebHopGateway();

var app = builder.Build();
app.UseWebSockets();
app.UseWebHopGateway();
app.MapGet("/", () => "No app connected");   // your own content
```

Every request is forwarded to an app whose token serves the request's host and path; with several serving it, they take turns. Requests no app serves fall through to the rest of your pipeline.

## Keys and tokens

Each app opens its tunnels with a token, `wh_…`: a JWT signed with ES256 that names the app (its origin id), what it serves, how many tunnels it may keep open, and when it expires. You issue tokens with [`webhop-gen`](https://www.nuget.org/packages/webhop-gen) on your own machine, which keeps the private key. The gateway only needs the public key set, `whk_…`, which `webhop-gen issuer init` prints:

```sh
webhop-gen issuer init gateway.example.com     # prints whk_… for the gateway
webhop-gen token issue gateway.example.com     # prints wh_… for an app
```

Set the public set with the `WEBHOP_KEYSET` environment variable or the `WebHop:KeySet` configuration key (the setting wins when both are set). In `appsettings.json` it can also be the set's JSON, as a nested object. Without a set, or with one it can't use, the gateway still starts, but rejects every tunnel and logs an error. It refuses a set with a private key.

Besides the keys, the set holds the issuer's limits on this gateway: `allow` lists the hosts and paths tokens may claim, `maxLifetime` caps how long a token may be valid, and `deny` and `issuedAfter` revoke tokens. The gateway reads the set at startup, so revoking a token (`webhop-gen token revoke <token>`) means deploying the new set it prints. A tunnel lives no longer than its token: the gateway closes it when the token expires.

A tunnel is refused with 401 when its token is bad, expired or revoked, 403 when the token doesn't serve the host the tunnel arrived on, 409 when its origin id is connected with a token that serves something else, and 503 when the app has as many tunnels open as its token allows. Addresses that keep presenting refused tokens are held off for a minute.

The token is sent each time a tunnel opens, and whoever reads it can serve as that app until it expires or is revoked, so serve the gateway over https outside local development.

## Several sites on one gateway

What an app serves is in its token: a URL, a path alone (served on whatever host the app connects to), a host pattern such as `https://*.example.com/robots.txt`, or several of them. Point several names at one gateway (a wildcard DNS record such as `*.gateway.example.com` and a matching certificate), create one key set for all of them, and give each app a token of its own:

```sh
webhop-gen issuer init *.gateway.example.com
webhop-gen token issue app1.gateway.example.com --origin app1
webhop-gen token issue app2.gateway.example.com --origin app2
```

Requests for `app1.gateway.example.com` reach only the first app. Names are matched without the port and regardless of case, and paths on segment boundaries: `/drop` serves `/drop` and `/drop/x`, not `/dropbox`. For a request, the app serving the longest path of its host wins, and at equal paths an exact host beats a pattern. So a token for `https://*.gateway.example.com/robots.txt` answers `/robots.txt` on every subdomain, while `https://*.gateway.example.com/` serves the subdomains without an app of their own.

## Overriding parts of an app

Embedded with `UseWebHopGateway`, apps can serve parts of your own app, such as `/api/v1/` from a developer's machine, while your app serves the rest. Create the set for your app's host with the paths that may be overridden, and issue tokens with paths alone:

```sh
webhop-gen issuer init app.example.com/api/v1/
webhop-gen token issue app.example.com/api/v1/ --relative --lifetime 8h
webhop http 5000 --url https://app.example.com/       # on the developer's machine, with that token
```

A token with paths alone doesn't say which gateway it's for, so the app needs the gateway's URL. Paths outside `allow`, such as `/login`, can never be overridden. Requests no app serves fall through to your app, again as soon as the app disconnects. Such an app receives your visitors' requests with their cookies and `Authorization` headers, so issue these tokens only to people who may see them, with a short lifetime.

## Endpoints

| Path | Purpose |
|---|---|
| `/webhop` | Where apps open their tunnels (WebSocket) |
| `/webhop/status` | Health check |
| anything else | Forwarded to the connected app that serves it |

`webhop-gen issuer init --ept /t-7f3k9q` moves both elsewhere, so scanners probing `/webhop` don't find the gateway. The path is in the key set and in every token, so apps follow it.

Forwarded requests carry `X-Forwarded-For` (the client's address and port, or the address alone when a proxy in front named the client), `X-Forwarded-Proto` and `X-Forwarded-Host`, plus `X-Webhop-Origin-Id` and `X-Webhop-Request-Id` on requests and responses. A request without a response within 120 s gets a 504.

## Hosting notes

- Enable WebSockets on the host (on Azure App Service, in the app's configuration).
- Run a single instance: tunnels live in memory, so behind a load balancer a request can land on an instance the app isn't connected to and get a 503.
- The gateway accepts tunnels only on the hosts its key set's `allow` lists, so names it's reachable by but doesn't mean to serve (its IP address, `*.azurewebsites.net`) stay unused.
- Behind a TLS-terminating reverse proxy (nginx, Traefik, Caddy), have it send `X-Forwarded-For` and `X-Forwarded-Proto` (nginx: `proxy_set_header X-Forwarded-For $remote_addr;` and `proxy_set_header X-Forwarded-Proto $scheme;`), so apps see the client's real IP and scheme. The standalone gateway believes these from a proxy on loopback or a private network, such as one on the same host or the same Docker network, and never from a public address, so a visitor can't name another client. For a proxy that connects from a public address (a CDN), set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, which believes every client, and let nothing but the proxy reach the gateway. Embedded with `UseWebHopGateway`, your app's own forwarded headers setup applies instead.
- The proxy must keep the request's `Host` (nginx: `proxy_set_header Host $host;`), since apps are picked by it.
- The proxy must also pass WebSocket upgrades on the tunnel endpoint (`/webhop`); nginx needs `proxy_http_version 1.1` and the `Upgrade`/`Connection` headers for this.

## Azure App Service

- Enable **WebSockets** in the app's configuration.
- Set `WEBHOP_KEYSET` as an application setting.
- Issue tokens for `https://<your-app>.azurewebsites.net/` (`webhop-gen issuer init <your-app>.azurewebsites.net`).
- Mind the plan's WebSocket limit per instance: Free allows 5, Shared 35, Basic 350, Standard and up have no fixed limit. Every tunnel is one WebSocket, and so is every WebSocket your visitors open. On Free and Shared the gateway tells apps how many tunnels each may open (60% of the limit), leaving room for visitors; `WebHop:MaxWebSockets` overrides the detected limit.

Step by step, with the Azure CLI or Visual Studio: [deploying WebHop.Gateway.Host](https://github.com/elebree/WebHop/tree/main/WebHop.Gateway.Host#azure-app-service).

## More

https://github.com/elebree/WebHop
