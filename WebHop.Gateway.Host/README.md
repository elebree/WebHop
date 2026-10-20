# WebHop.Gateway.Host

The ready-to-deploy [WebHop](../README.md) gateway. It runs the standalone gateway from the [WebHop.Gateway](../WebHop.Gateway/README.md) package and adds what a public deployment needs:

- **Error pages.** Visitors get a WebHop page that explains what happened instead of a bare status code: 502 (the tunnel dropped), 503 (no server connected) and 504 (the server took longer than 120 s).
- **IIS settings** in [web.config](web.config), for Azure App Service on Windows. IIS's defaults reject valid URLs before they reach the gateway: `+` in paths (for example Vite's `/@fs/…/@scope+pkg`), extensions like `.cs` or `.config`, `bin` segments, and bodies over about 30 MB. `web.config` relaxes these rules; the gateway serves no files from disk, so they protect nothing here.

## Configuration

| Setting | Meaning |
|---|---|
| `WEBHOP_AUTHTOKEN` or `WebHop:AuthToken` | The auth token servers must present to open tunnels. Without one the gateway starts but rejects every tunnel and logs an error. The setting wins when both are set |
| `WebHop:MaxWebSockets` | The host's WebSocket limit, if Azure's plan detection doesn't fit; the gateway lets each server open 60% of it |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | Behind a TLS-terminating reverse proxy, so servers see the client's real IP and scheme |

Any ASP.NET Core configuration source works: environment variables (`WebHop__AuthToken`), `appsettings.json`, App Service application settings.

## Azure App Service

Publish the host from the repository root (needs the .NET 10 SDK):

```sh
dotnet publish WebHop.Gateway.Host -c Release -o gateway
```

Deploy the `gateway` folder to a web app on .NET 9, with Visual Studio's Publish, `az webapp deploy` or any other way. In the web app's configuration:

- enable **WebSockets**;
- add `WEBHOP_AUTHTOKEN` as an application setting;
- on Basic and higher plans, turn on **Always on**, so the app isn't unloaded while idle.

### With the Azure CLI

From the repository root, after `az login`. Pick your own names for the resource group, plan and app; the app name becomes the `<your-app>.azurewebsites.net` host name, so it must be globally unique.

1. Create a Windows web app on .NET 9. `web.config` only applies on Windows, where it lets IIS pass URLs the gateway must forward:

   ```sh
   az group create --name webhop-rg --location westeurope
   az appservice plan create --name webhop-plan --resource-group webhop-rg --sku B1
   az webapp create --name <your-app> --resource-group webhop-rg --plan webhop-plan --runtime "dotnet:9"
   ```

2. Enable WebSockets and Always on, require HTTPS, and set the auth token:

   ```sh
   az webapp config set --name <your-app> --resource-group webhop-rg --web-sockets-enabled true --always-on true
   az webapp update --name <your-app> --resource-group webhop-rg --https-only true
   az webapp config appsettings set --name <your-app> --resource-group webhop-rg --settings WEBHOP_AUTHTOKEN=<token>
   ```

   On the Free (`F1`) and Shared (`D1`) plans, leave out `--always-on true`; those plans don't support it.

3. Publish, zip the output and deploy it:

   ```sh
   dotnet publish WebHop.Gateway.Host -c Release -o gateway
   cd gateway && zip -r ../gateway.zip . && cd ..
   az webapp deploy --name <your-app> --resource-group webhop-rg --src-path gateway.zip --type zip
   ```

   In PowerShell, zip with `Compress-Archive -Path gateway/* -DestinationPath gateway.zip -Force` instead. The zip must hold the files themselves, not the `gateway` folder.

To update the gateway later, repeat step 3. If the gateway doesn't pick up a changed setting or deployment, restart it with `az webapp restart --name <your-app> --resource-group webhop-rg`.

### With Visual Studio

Right-click **WebHop.Gateway.Host** > **Publish** > **Azure** > **Azure App Service (Windows)**, and pick or create the web app. Visual Studio builds and deploys the app; enable WebSockets and set `WEBHOP_AUTHTOKEN` in the web app's configuration in the Azure portal as above.

### After deploying

Servers then connect to `https://<your-app>.azurewebsites.net/`. Every tunnel is one WebSocket, and the Free and Shared plans cap WebSockets; see [Azure App Service](../WebHop.Gateway/README.md#azure-app-service) for how the gateway handles that.

## Docker

Prebuilt images for linux/amd64 and linux/arm64 are published as `elebree/webhop` on Docker Hub and `ghcr.io/elebree/webhop` on GitHub. Each release is tagged with its version (`2.1.0`), its minor version (`2.1`) and `latest`; `edge` is the newest build of `main`. To run the gateway on http://localhost:8080:

```sh
docker run -d -p 8080:8080 -e WEBHOP_AUTHTOKEN=<token> --restart unless-stopped elebree/webhop
```

The container serves plain http, so on a public host put a TLS-terminating proxy in front of it; the proxy must pass WebSocket upgrades on `/webhop`.

From a clone, the compose file in the repository root builds the image and runs it the same way:

```sh
WEBHOP_AUTHTOKEN=<token> docker compose up --build -d
```

`WEBHOP_AUTHTOKEN` can also go in a `.env` file next to `docker-compose.yml`.

To build the image on its own, build from the repository root, since the Dockerfile needs the sibling projects:

```sh
docker build -f WebHop.Gateway.Host/Dockerfile -t webhop .
```

## Running locally

For experiments and development:

```sh
dotnet run --project WebHop.Gateway.Host --launch-profile https
```

The gateway listens on https://localhost:7188 and http://localhost:5075.

## Customizing

[Program.cs](Program.cs) is one line: `GatewayApp.Create(args, app => app.UseWebHopErrorPages()).Run()`. The callback runs before the gateway's middleware, so it's the place for further host-level additions. To serve your own content next to the gateway instead, embed the middleware in your own app; see [WebHop.Gateway](../WebHop.Gateway/README.md#in-an-existing-app).
