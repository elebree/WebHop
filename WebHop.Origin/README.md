# WebHop.Origin

Serve an ASP.NET Core app through a [WebHop](https://github.com/elebree/WebHop) gateway from behind NAT or a firewall, with no inbound ports. The app opens WebSocket tunnels out to the gateway, and Kestrel serves them like ordinary connections, so your middleware, endpoints, WebSockets and server-sent events work unchanged.

```
browser ──HTTP──▶ WebHop gateway ◀──WebSocket tunnels── your app (WebHop.Origin)
                  (public host)                          (behind NAT, no open ports)
```

You need a running gateway and a token for it; see the [quick start](https://github.com/elebree/WebHop#quick-start).

## Usage

```sh
dotnet add package WebHop.Origin
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebHop();   // instead of listening on a port
```

Start the app with its token. The token, `wh_…`, says where the gateway is, what the app serves there and its origin id, so the app needs nothing else:

```sh
export WEBHOP_TOKEN=<token>
dotnet run --no-launch-profile
```

`--urls`, or `applicationUrl` in `launchSettings.json`, takes the place of the token's gateway: its scheme, host and port, never a path. Leave it out (that's why `--no-launch-profile` above), unless the token serves paths alone and so names no gateway. In code:

```csharp
builder.WebHost.UseWebHop("https://gateway.example.com/", options =>
{
    options.MaxConnections = 32;
});
```

The app reconnects on its own when the gateway restarts. Requests arrive with the client's scheme and IP address restored from the gateway's forwarded headers.

## Token

The app refuses to start without a token. It comes from the `WEBHOP_TOKEN` environment variable, the `WebHop:AuthToken` configuration key, or `options.AuthToken`. Whoever issues tokens for the gateway makes one with `webhop token issue`. Keep it out of `appsettings.json` in source control: whoever reads it can serve as your app until it expires or is revoked.

`WebHopServer.Status` says why the gateway refuses the token: `Unauthorized` (bad, expired or revoked), `Forbidden` (it doesn't serve the host the app connects to) or `Conflict` (another process serves the same origin id with a token for something else). The app keeps retrying, so it connects once the token is replaced.

## Options

`UseWebHop` reads these from the `WebHop` configuration section (`appsettings.json`, environment variables such as `WebHop__MaxConnections`, …). Values set in the callback win.

| Option | Default | Meaning |
|---|---|---|
| `AuthToken` | `WEBHOP_TOKEN` | The app's token, `wh_…` |
| `MaxConnections` | 10 | Tunnels kept open; caps concurrent requests and WebSockets. The token (3 unless it says otherwise) and the gateway may set a lower limit, and the smallest wins |
| `MaxReconnectDelay` | 30 s | Upper bound of the reconnect backoff |
| `KeepAliveInterval` | 15 s | WebSocket ping interval |

## Notes

- Requests no connected app serves get a 503 from a standalone gateway. A request that gets no response within 120 s gets 504.
- The gateway closes the app's tunnels when its token expires; until it has a new one, it reports `Unauthorized`.
- Requests carry `X-Webhop-Origin-Id` and `X-Webhop-Request-Id`; the request id becomes `HttpContext.TraceIdentifier`.
- When you run the app from Visual Studio in Development, pages opened through the gateway may trigger a browser prompt about apps on your device. That is Visual Studio's Browser Link and Hot Reload script connecting back to `localhost`; it doesn't happen outside Development.

## More

Documentation, the gateway and the `webhop` command line tool: https://github.com/elebree/WebHop
