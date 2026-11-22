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
webhop http 5000 --token <token>
```

The token, `wh_…`, says where the gateway is, what this tunnel serves there and its origin id, so that's all `webhop` needs. Instead of `--token`, it can come from the `WEBHOP_TOKEN` environment variable. Whoever issues tokens for the gateway makes one with `webhop token issue`; see [Issuing tokens](#issuing-tokens).

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
| `webhop issuer init\|list\|public\|rotate`, `webhop token issue\|list\|revoke` | Create a gateway's key set and issue its tokens; see [Issuing tokens](#issuing-tokens) |
| `webhop version` | Show the version |
| `webhop help [command]`, `webhop <command> --help` | Usage and all flags |

| Flag for `webhop http` | Meaning |
|---|---|
| `--token` | The token. Default: `WEBHOP_TOKEN` |
| `--gateway` | The gateway's scheme, host and port, instead of the token's; a token that serves paths alone needs it. Default: `WEBHOP_GATEWAY` |
| `--host-header` | Host header sent to the local server. `rewrite` sends the target's own, such as `localhost:5000`, for dev servers that reject other hosts (Vite, webpack dev server); any other value is sent as is. Default: the public host |
| `--log=stdout\|stderr\|<file>` | `stdout`/`stderr` print log lines instead of the live screen (also used when output is redirected); a file path keeps the screen and writes the log to the file |
| `--log-level=debug\|info\|warn\|error\|crit` | How much to log (default `info`) |
| `--log-format=term\|json` | Log line format |
| `--connections` | Tunnels to keep open, at most the token's limit (WebHop-specific) |

Flags can come in any order, before or after the command, as `--flag value` or `--flag=value`.

## Issuing tokens

You create a key set for a gateway once, deploy its public part on the gateway, and issue each server a token of its own, `wh_…`, that says which origin it is, what it serves and until when. The gateway checks tokens with the public keys only, so it holds no secret that could make one.

```sh
# A key set for every subdomain of example.com; prints whk_… for the gateway's WEBHOP_KEYSET
webhop issuer init *.example.com
# A token for https://app1.example.com/; prints wh_… for the server
webhop token issue app1.example.com
# /robots.txt on every subdomain, from one server
webhop token issue *.example.com/robots.txt
# A path on whatever host the server connects to, for a week
webhop token issue app1.example.com/api/v1 --relative --lifetime 7d
# The tokens issued from the set, and revoking one; prints the new whk_… to deploy
webhop token list *.example.com
webhop token revoke <token>
# A new signing key; the old one still verifies until its tokens have expired
webhop issuer rotate *.example.com
```

The values meant for other programs, `whk_…` and `wh_…`, go to standard output alone; what's said about them goes to standard error.

| Command | What it does |
|---|---|
| `issuer init <host>` | A new key set for a gateway; prints the public set, `whk_…` |
| `issuer list` | The key sets on this machine, with their keys and how many of their tokens haven't expired |
| `issuer public <host\|file>` | Prints the public set again |
| `issuer rotate <host\|file>` | Adds a signing key; the old one only verifies, and is dropped once its tokens have expired |
| `token issue <host[/path]\|url>...` | A token serving those; several make one token serving all of them |
| `token list <host\|file>` | The tokens issued from a set, from its log |
| `token revoke <token>` | Revokes a token by its serial |
| `token revoke <host\|file> --origin <id>` | Revokes an origin's tokens, found in the log, when the token itself is lost |
| `token revoke <host\|file> --all` | Revokes every token issued from the set until now |

| Flag | Meaning |
|---|---|
| `--origin` | The origin id: lowercase letters and digits. Default: the token's serial |
| `--lifetime` | How long a token is valid, such as `12h` or `7d`. Default: the set's, 30 days unless `issuer init` said otherwise |
| `--max-lifetime` | With `issuer init`: the longest a token may be valid (default 90 days) |
| `--tunnels` | The most tunnels the server may keep open at once (default 3) |
| `--relative` | Serve the path on whatever host the server connects to, instead of the host named |
| `--allow` | With `issuer init`: more hosts or paths tokens may claim |
| `--ept` | With `issuer init`: another tunnel endpoint than `/webhop`, so scanners don't find the gateway by it |
| `--set` | The set's file, instead of finding it by host |

Hosts are written as `app1.example.com`, with a path (`app1.example.com/api/v1`) or as a URL (`https://app1.example.com`); without a scheme it's `https://`, and `http://` is for a gateway without TLS. zsh refuses an unquoted `*`, so `_.example.com` works too.

Revoking writes the serial into the set's `deny` list, so it takes effect once the gateway runs with the new public set, which restarts it; servers reconnect within seconds.

Each set is a file named after the host its tokens are for, in `%APPDATA%\webhop\keys` on Windows and `~/.config/webhop/keys` elsewhere: `_.example.com.json` is the set for `*.example.com`. A host picks its own set, else the wildcard one label up, as a TLS wildcard covers one label. Beside each set, a log (`_.example.com.log`) lists the serial, origin, audience and times of every token issued from it, never the token itself.

The set is the one secret: whoever has it can issue any token the gateway's `allow` permits. On Unix the directory and files are readable by you only; on Windows they inherit your profile's access rules. Keep it out of repositories. A CI job gets the set as a secret file and passes its path with `--set`.

## Docker

The `elebree/webhop-cli` image on Docker Hub (also `ghcr.io/elebree/webhop-cli`) runs `webhop` on linux/amd64 and linux/arm64, for tunnels that run unattended next to other containers. Each release is tagged with its version (`2.1.0`), its minor version (`2.1`) and `latest`; `edge` is the newest build of `main`.

Pass the token as an environment variable. Inside a container `localhost` is the container itself, so reach a server on the host as `host.docker.internal` (on Linux, add `--add-host=host.docker.internal:host-gateway`):

```sh
docker run --rm -it -e WEBHOP_TOKEN=<token> elebree/webhop-cli http http://host.docker.internal:5000
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
      WEBHOP_TOKEN: ${WEBHOP_TOKEN}
    restart: unless-stopped
```

## Notes

- The local server sees the public Host header and `X-Forwarded-For/Proto/Host`.
- Self-signed certificates are accepted for local https targets.
- If the local server isn't running, visitors get a 502 page that says so.
- When the gateway refuses the token, the screen says why: unauthorized (bad, expired or revoked), forbidden (it doesn't serve the host `webhop` connects to) or conflict (another process serves the same origin id with a token for something else). Shortly before the token expires, its expiry turns yellow.
- A token passed with `--token` may be visible to other users in the process list; on a shared machine, set `WEBHOP_TOKEN` instead.
- From a clone of the repository, run it with `dotnet run --project WebHop.CLI -f net10.0 -- http 5000`.

## More

https://github.com/elebree/WebHop
