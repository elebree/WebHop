# WebHop.Origin

Serve an ASP.NET Core app through a [WebHop](https://github.com/elebree/WebHop) gateway from behind NAT or a firewall, with no inbound ports. The app opens WebSocket tunnels out to the gateway, and Kestrel serves them like ordinary connections, so your middleware, endpoints, WebSockets and server-sent events work unchanged.

```
browser ──HTTP──▶ WebHop gateway ◀──WebSocket tunnels── your app (WebHop.Origin)
                  (public host)                          (behind NAT, no open ports)
```

You need a running gateway; see [WebHop.Gateway](https://www.nuget.org/packages/WebHop.Gateway).

## Usage

```sh
dotnet add package WebHop.Origin
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseWebHop();   // instead of listening on a port
```

Start the app with the gateway's auth token, and set `--urls` (or `applicationUrl` in `launchSettings.json`) to the **gateway's** URL. That is both where the app connects and where it listens for requests:

```sh
export WEBHOP_AUTHTOKEN=<token>
dotnet run --urls https://gateway.example.com/
```

Or set both in code:

```csharp
builder.WebHost.UseWebHop("https://gateway.example.com/", options =>
{
    options.MaxConnections = 32;
});
```

The app reconnects on its own when the gateway restarts. Requests arrive with the client's scheme and IP address restored from the gateway's forwarded headers.

## Auth token

The app refuses to start without the gateway's auth token. It comes from the `WEBHOP_AUTHTOKEN` environment variable, the `WebHop:AuthToken` configuration key, or `options.AuthToken`. Keep it out of `appsettings.json` in source control.

## Options

`UseWebHop` reads these from the `WebHop` configuration section (`appsettings.json`, environment variables such as `WebHop__MaxConnections`, …). Values set in the callback win.

| Option | Default | Meaning |
|---|---|---|
| `AuthToken` | `WEBHOP_AUTHTOKEN` | The gateway's auth token |
| `MaxConnections` | 10 | Tunnels kept open; caps concurrent requests and WebSockets. The gateway may advertise a lower limit, and the smaller one wins |
| `OriginId` | random | How the gateway identifies this app |
| `MaxReconnectDelay` | 30 s | Upper bound of the reconnect backoff |
| `KeepAliveInterval` | 15 s | WebSocket ping interval |

## Notes

- While no app is connected, the gateway answers 503. A request that gets no response within 120 s gets 504.
- Requests carry `X-Webhop-Origin-Id` and `X-Webhop-Request-Id`; the request id becomes `HttpContext.TraceIdentifier`.
- When you run the app from Visual Studio in Development, pages opened through the gateway may trigger a browser prompt about apps on your device. That is Visual Studio's Browser Link and Hot Reload script connecting back to `localhost`; it doesn't happen outside Development.

## More

Documentation, the gateway and the `webhop` command line tool: https://github.com/elebree/WebHop
