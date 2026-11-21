# webhop

Expose any local HTTP server through your own [WebHop](https://github.com/elebree/WebHop) gateway, much like `ngrok http`. The local server needs no WebHop code and doesn't have to be .NET. `webhop` opens no listening port: it only connects out to the gateway and to your local server.

You need a running gateway and a token for it; see the [quick start](https://github.com/elebree/WebHop#quick-start).

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
webhop config add-authtoken <token>
webhop http 5000
```

The token, `wh_…`, says where the gateway is, what this tunnel serves there and its origin id, so that's all `webhop` needs. Whoever issues tokens for the gateway makes one with `webhop-gen token issue` (or `webhop token issue`).

```
WebHop                                                (Ctrl+C to quit)

Session Status                online
Version                       2.0.0
Origin Id                     app1
Tunnels                       3/3 open
Forwarding                    https://app1.example.com -> http://localhost:5000
Token Expires                 2026-12-19 14:00 UTC (in 29 days)

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
| `webhop config add-authtoken <token>` | Save the token, and show what it serves and when it expires |
| `webhop config add-gateway-url <url>` | Save a gateway to connect to instead of the token's, for a token that serves paths alone |
| `webhop config check` / `config edit` | Show or edit the saved settings |
| `webhop issuer init\|list\|public\|rotate`, `webhop token issue\|list\|revoke` | Issue tokens for a gateway, as [`webhop-gen`](https://www.nuget.org/packages/webhop-gen) does, for machines where tunnels are allowed anyway |
| `webhop version` | Show the version |
| `webhop help [command]`, `webhop <command> --help` | Usage and all flags |

| Flag for `webhop http` | Meaning |
|---|---|
| `--token` | The token. Default: `WEBHOP_AUTHTOKEN`, then the saved token |
| `--url` | The gateway's scheme, host and port, instead of the token's; a token that serves paths alone needs it. Default: `WEBHOP_URL`, then the saved gateway |
| `--host-header` | Host header sent to the local server. `rewrite` sends the target's own, such as `localhost:5000`, for dev servers that reject other hosts (Vite, webpack dev server); any other value is sent as is. Default: the public host |
| `--log=stdout\|stderr\|<file>` | `stdout`/`stderr` print log lines instead of the live screen (also used when output is redirected); a file path keeps the screen and writes the log to the file |
| `--log-level=debug\|info\|warn\|error\|crit` | How much to log (default `info`) |
| `--log-format=term\|json` | Log line format |
| `--config` | Use another config file |
| `--connections` | Tunnels to keep open, at most the token's limit (WebHop-specific) |

Flags can come in any order, before or after the command, as `--flag value` or `--flag=value`.

The settings are saved in `%APPDATA%\webhop\webhop.json` on Windows and `~/.config/webhop/webhop.json` elsewhere.

## Docker

The `elebree/webhop-cli` image on Docker Hub (also `ghcr.io/elebree/webhop-cli`) runs `webhop` on linux/amd64 and linux/arm64, for tunnels that run unattended next to other containers. Each release is tagged with its version (`2.1.0`), its minor version (`2.1`) and `latest`; `edge` is the newest build of `main`.

Pass the token as an environment variable. Inside a container `localhost` is the container itself, so reach a server on the host as `host.docker.internal` (on Linux, add `--add-host=host.docker.internal:host-gateway`):

```sh
docker run --rm -it -e WEBHOP_AUTHTOKEN=<token> elebree/webhop-cli http http://host.docker.internal:5000
```

`-it` shows the live screen; without it, `webhop` prints log lines. In Docker Compose, it exposes another service of the stack by its service name:

```yaml
services:
  app:
    image: my-app
  tunnel:
    image: elebree/webhop-cli
    command: http http://app:8080
    environment:
      WEBHOP_AUTHTOKEN: ${WEBHOP_AUTHTOKEN}
    restart: unless-stopped
```

## Notes

- The local server sees the public Host header and `X-Forwarded-For/Proto/Host`.
- Self-signed certificates are accepted for local https targets.
- If the local server isn't running, visitors get a 502 page that says so.
- When the gateway refuses the token, the screen says why: unauthorized (bad, expired or revoked), forbidden (it doesn't serve the host `webhop` connects to) or conflict (another process serves the same origin id with a token for something else). Shortly before the token expires, its expiry turns yellow.
- The settings file holds the token: on Unix it's readable by you only; on Windows it inherits your profile's access rules.
- From a clone of the repository, run it with `dotnet run --project WebHop.CLI -f net10.0 -- http 5000`.

## More

https://github.com/elebree/WebHop
