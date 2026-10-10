# webhop

Expose any local HTTP server through your own [WebHop](https://github.com/elebree/WebHop) gateway, much like `ngrok http`. The local server needs no WebHop code and doesn't have to be .NET. `webhop` opens no listening port: it only connects out to the gateway and to your local server.

You need a running gateway; see [WebHop.Gateway](https://www.nuget.org/packages/WebHop.Gateway).

## Install

```sh
dotnet tool install -g webhop
```

Or download the standalone Native AOT binary, which runs without .NET, and unpack it somewhere on your `PATH`:

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-x64.zip)
[![Windows arm64](https://img.shields.io/badge/Windows-arm64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-win-arm64.zip)
[![Linux x64](https://img.shields.io/badge/Linux-x64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-x64.tar.gz)
[![Linux arm64](https://img.shields.io/badge/Linux-arm64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-linux-arm64.tar.gz)
[![macOS arm64](https://img.shields.io/badge/macOS-arm64-A2AAAD)](https://github.com/elebree/WebHop/releases/latest/download/webhop-osx-arm64.tar.gz)

On macOS, a binary downloaded with a browser is quarantined; clear that with `xattr -d com.apple.quarantine webhop`.

## Usage

```sh
webhop config add-gateway-url https://my-gateway.example.com/
webhop config add-authtoken <token>
webhop http 5000
```

```
WebHop                                                (Ctrl+C to quit)

Session Status                online
Version                       2.0.0
Origin Id                     5c11642ef14844fb98e6cfcb5ac6ea99
Tunnels                       10/10 open
Forwarding                    https://my-gateway.example.com -> http://localhost:5000

Connections                   ttl     opn     p50       p90
                              6       0       17 ms     46 ms

HTTP Requests
-------------
01:51:47  GET     /slow                                     200 OK                        1,5 s
01:51:45  GET     /missing?x=1                              404 Not Found                 17 ms
```

## Commands

The commands and flags follow ngrok's.

| Command | What it does |
|---|---|
| `webhop http 5000` | Forward to `http://localhost:5000`; also accepts `localhost:5000` or a full URL such as `https://localhost:7001` |
| `webhop config add-gateway-url <url>` | Save the gateway to connect to |
| `webhop config add-authtoken <token>` | Save the gateway's auth token |
| `webhop config check` / `config edit` | Show or edit the saved settings |
| `webhop version` | Show the version |
| `webhop help [command]`, `webhop <command> --help` | Usage and all flags |

| Flag for `webhop http` | Meaning |
|---|---|
| `--url` | Gateway URL, which is also the public URL. Default: `WEBHOP_URL`, then the saved gateway |
| `--authtoken` | The gateway's auth token. Default: `WEBHOP_AUTHTOKEN`, then the saved auth token |
| `--host-header=rewrite` | Send `Host: localhost:<port>`, for dev servers that reject other hosts (Vite, webpack dev server) |
| `--log=stdout\|stderr\|<file>` | `stdout`/`stderr` print log lines instead of the live screen (also used when output is redirected); a file path keeps the screen and writes the log to the file |
| `--log-level=debug\|info\|warn\|error\|crit` | How much to log (default `info`) |
| `--log-format=term\|json` | Log line format |
| `--config` | Use another config file |
| `--connections` | Tunnels to keep open (WebHop-specific) |

Flags can come in any order, before or after the command, as `--flag value` or `--flag=value`.

The settings are saved in `%APPDATA%\webhop\webhop.json` on Windows and `~/.config/webhop/webhop.json` elsewhere.

## Notes

- The local server sees the public Host header and `X-Forwarded-For/Proto/Host`.
- Self-signed certificates are accepted for local https targets.
- If the local server isn't running, visitors get a 502 page that says so.
- From a clone of the repository, run it with `dotnet run --project WebHop.CLI -- http 5000`.

## More

https://github.com/elebree/WebHop
