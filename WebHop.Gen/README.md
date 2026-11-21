# webhop-gen

Issues the tokens that servers open [WebHop](https://github.com/elebree/WebHop) tunnels with. You create a key set for a gateway once, deploy its public part on the gateway, and issue each server a token of its own, `wh_…`, that says which origin it is, what it serves and until when. The gateway checks tokens with the public keys only, so it holds no secret that could make one.

`webhop-gen` opens no connection: it only reads, signs and writes files. So it's easy to allow on a locked-down admin machine or in a CI image, where a tunnel client might not be. The [`webhop`](https://www.nuget.org/packages/webhop) command line tool has the same commands, for machines where tunnels are allowed anyway.

## Install

```sh
dotnet tool install -g webhop-gen
```

Or download the standalone Native AOT binary, which runs without .NET:

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-gen-win-x64.zip)
[![Windows arm64](https://img.shields.io/badge/Windows-arm64-0078D6)](https://github.com/elebree/WebHop/releases/latest/download/webhop-gen-win-arm64.zip)
[![Linux x64](https://img.shields.io/badge/Linux-x64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-gen-linux-x64.tar.gz)
[![Linux arm64](https://img.shields.io/badge/Linux-arm64-FCC624)](https://github.com/elebree/WebHop/releases/latest/download/webhop-gen-linux-arm64.tar.gz)
[![macOS arm64](https://img.shields.io/badge/macOS-arm64-A2AAAD)](https://github.com/elebree/WebHop/releases/latest/download/webhop-gen-osx-arm64.tar.gz)

## Usage

```sh
# A key set for every subdomain of example.com; prints whk_… for the gateway's WEBHOP_KEYSET
webhop-gen issuer init *.example.com
# A token for https://app1.example.com/ with the origin id app1; prints wh_… for the server
webhop-gen token issue app1.example.com --origin app1
# /robots.txt on every subdomain, from one server
webhop-gen token issue *.example.com/robots.txt --origin robots
# A path on whatever host the server connects to, for a week
webhop-gen token issue app1.example.com/api/v1 --relative --lifetime 7d
# The tokens issued from the set, and revoking one; prints the new whk_… to deploy
webhop-gen token list *.example.com
webhop-gen token revoke <token>
# A new signing key; the old one still verifies until its tokens have expired
webhop-gen issuer rotate *.example.com
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

## Key sets

Each set is a file named after the host its tokens are for, in `%APPDATA%\webhop-gen` on Windows and `~/.config/webhop-gen` elsewhere: `_.example.com.json` is the set for `*.example.com`. A host picks its own set, else the wildcard one label up, as a TLS wildcard covers one label. Beside each set, a log (`_.example.com.log`) lists the serial, origin, audience and times of every token issued from it, never the token itself.

The set is the one secret: whoever has it can issue any token the gateway's `allow` permits. On Unix the directory and files are readable by you only; on Windows they inherit your profile's access rules. Keep it out of repositories. A CI job gets the set as a secret file and passes its path with `--set`.

## More

https://github.com/elebree/WebHop
