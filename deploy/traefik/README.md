# Gateway with Traefik and Let's Encrypt

Runs the WebHop gateway behind [Traefik](https://traefik.io/traefik/), using the official images of both. Traefik serves the gateway over https (HTTP/1.1, HTTP/2 and HTTP/3), redirects http to https, and gets a Let's Encrypt certificate and renews it. Let's Encrypt has to check that the name is yours, and how depends on the setup below.

`WEBHOP_HOST` in `.env` sets where the gateway serves apps, one of:

- **Every subdomain**, `WEBHOP_HOST=*.example.com`, with a wildcard certificate. Wildcards are only issued through the DNS challenge: Traefik creates the challenge records through your DNS host's API. It supports [more than 150 DNS hosts](https://go-acme.github.io/lego/dns/), and you pick yours with `DNS_PROVIDER` in `.env`. Each app's token names its subdomain, so one gateway serves several sites:

  ```text
  webhop http 5000 --token <token for app1.example.com>
  webhop http 5001 --token <token for app2.example.com>
  ```

- **One host name**, such as `WEBHOP_HOST=webhop.example.com`, with a certificate for that name only. It needs no DNS API: without `DNS_PROVIDER`, Let's Encrypt checks the name by connecting to it on port 443, where Traefik answers. (With `DNS_PROVIDER`, the DNS challenge is used here too.) Apps connect with tokens for `webhop.example.com`. Several apps on it take turns, so this suits a single app.

The certificate covers exactly `WEBHOP_HOST`. The domain's other names, the bare `example.com` included, stay free for other uses, such as a website.

## Setup

You need Docker with Compose 2.24.4 or newer, and the files from this folder on the server: [docker-compose.yml](docker-compose.yml), [behind-proxy.yml](behind-proxy.yml) and [.env.example](.env.example).

1. **DNS records.** Add an `A` record (and `AAAA` for IPv6) pointing to your server for `WEBHOP_HOST`: `*` for every subdomain, or `webhop` for `webhop.example.com`. Other names can point anywhere; if one points here, Traefik answers it with a 404 and a certificate that doesn't match.
2. **API credentials** (every subdomain only). Find your DNS host on the [list of DNS providers](https://go-acme.github.io/lego/dns/). Its page gives the code for `DNS_PROVIDER` (such as `cloudflare` or `porkbun`) and the variables for its credentials. Restrict the key to editing DNS records of this domain if your host allows that.
3. **Key set.** On your own machine, run `webhop issuer init` with your `WEBHOP_HOST`, such as `webhop issuer init *.example.com`. It keeps the private key there and prints the public set, `whk_…`, for `WEBHOP_KEYSET`.
4. **Settings.** Copy `.env.example` to `.env` next to `docker-compose.yml` and fill it in. Keep it readable only by you (`chmod 600 .env`), since a DNS API key can change every record of the domain.
5. **Ports.** Open 80 and 443 (TCP, and UDP 443 for HTTP/3) on the server's firewall.
6. **Try it on staging.** The first time, uncomment `ACME_STAGING=true` in `.env`, so a mistake in DNS or the settings doesn't count against Let's Encrypt's weekly limits. Staging certificates aren't trusted by browsers.
7. **Start:**

   ```sh
   docker compose up -d
   docker compose logs -f traefik
   ```

   With the DNS challenge, the first certificate takes a minute or two while Traefik waits for the TXT records to propagate. Once it's issued, `WEBHOP_HOST` answers with it, e.g. `https://test.example.com/` for every subdomain.
8. **Switch to real certificates.** Comment out `ACME_STAGING` again and run `docker compose up -d`. Staging certificates are kept in their own file, so Traefik requests a real one right away.

To update both images later: `docker compose pull && docker compose up -d`.

## Example: Porkbun

```text
DNS_PROVIDER=porkbun
PORKBUN_API_KEY=pk1_...
PORKBUN_SECRET_API_KEY=sk1_...
```

The domain must use Porkbun's name servers. Create the key at [porkbun.com/account/api](https://porkbun.com/account/api) with custom scopes `dns` only, this domain as the only allowed domain, and the server's address as the only allowed IP. A key without scopes has full access, purchases included. Then turn on **API Access** in the domain's details (leave "Opt In All Domains" off); without it the key can't change the domain's records.

## Behind nginx

If nginx already serves other sites on 80 and 443, it can pass the TLS connections for `WEBHOP_HOST` through to Traefik unopened, so Traefik still gets and serves the certificates. Uncomment `COMPOSE_FILE=docker-compose.yml:behind-proxy.yml` in `.env`. [behind-proxy.yml](behind-proxy.yml) then publishes only `127.0.0.1:8443`, where Traefik believes the client address nginx sends with PROXY protocol, and gives the network the fixed subnet `172.30.0.0/24` that this trust relies on. If the stack is already running, run `docker compose down` before `docker compose up -d`, so the network is recreated; the certificates stay.

Then route by host name in nginx's `stream` block (top level of `nginx.conf`, next to `http`) and move your https sites to an internal port:

```nginx
stream {
    map $ssl_preread_server_name $backend {
        ~^.+\.example\.com$  127.0.0.1:8443;   # WEBHOP_HOST=*.example.com: WebHop through Traefik
        # webhop.example.com 127.0.0.1:8443;   # or, for WEBHOP_HOST=webhop.example.com, this instead
        default              127.0.0.1:4443;   # example.com and your other sites
    }
    server {
        listen 443;
        listen [::]:443;
        ssl_preread    on;
        proxy_pass     $backend;
        proxy_protocol on;    # passes the client's address on
        proxy_timeout  1h;    # the default of 10m drops quiet tunnels
    }
}

http {
    server {
        listen 80;
        server_name *.example.com;    # or webhop.example.com
        return 301 https://$host$request_uri;
    }

    # Every existing https site, since the stream block sends PROXY protocol to them too
    server {
        listen 127.0.0.1:4443 ssl proxy_protocol;
        set_real_ip_from 127.0.0.1;
        real_ip_header proxy_protocol;
        port_in_redirect off;    # or nginx's own redirects point to :4443
        # ...
    }
}
```

For one host name without `DNS_PROVIDER`, Let's Encrypt's check on port 443 also passes through nginx to Traefik, as long as the `map` sends that name there. Behind nginx, WebHop sites use HTTP/2: nginx can't route QUIC by host name, so HTTP/3 stays with nginx's own sites. If `172.30.0.0/24` is taken on the host, change the subnet in `behind-proxy.yml` and the trusted address (its `.1`) in `docker-compose.yml`.

## Notes

- **Gateway image.** The compose file uses the newest release, `elebree/webhop:latest`. To pin one, set `WEBHOP_IMAGE=elebree/webhop:<version>` in `.env`.
- A wildcard covers one level only: `app1.example.com`, not `a.app1.example.com`.
- Certificates live in the `traefik_data` volume. Keep it: removing it makes Traefik request new certificates, and Let's Encrypt limits how many you can get per week.
- If issuance times out waiting for the TXT record, give the DNS host more time with its `<PROVIDER>_PROPAGATION_TIMEOUT` variable (seconds), listed on its page.
- Variables set in the shell take precedence over `.env` when Compose fills in `docker-compose.yml`.
- The gateway publishes no port; only Traefik reaches it. Traefik keeps the request's `Host`, which the gateway routes by, and the gateway trusts Traefik's `X-Forwarded-For`/`-Proto`, since Traefik connects from the compose network. The DNS credentials go to Traefik only.
- Issue each app its own token on the machine with the key set, such as `webhop token issue app1.example.com`. An app serves only what its token names.
