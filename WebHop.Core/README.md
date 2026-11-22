# WebHop.Core

Shared types for [WebHop](https://github.com/elebree/WebHop): the WebSocket tunnel stream, header names, constants, and the tokens servers open tunnels with. It reads what an origin's token says (`OriginToken`, `AudienceEntry`), issues tokens from an issuer's key set, and checks them as the gateway does. It depends on nothing beyond .NET.

You don't need to reference this package directly. It comes with the packages you install:

- [WebHop.Origin](https://www.nuget.org/packages/WebHop.Origin): serve your ASP.NET Core app through a gateway.
- [WebHop.Gateway](https://www.nuget.org/packages/WebHop.Gateway): run the gateway.

It's also the library behind the token commands of [`webhop`](https://www.nuget.org/packages/webhop), so they and the gateway follow the same rules. Reference it to issue tokens from your own app, such as the admin pages of an app with an embedded gateway.

## Issuing

```csharp
using WebHop.Core;

// The private set, as webhop issuer init wrote it
var keys = KeySet.Load("_.example.com.json");

var token = keys.Issue(new TokenRequest
{
    Audience = [AudienceEntry.Parse("https://app1.example.com/")],
    Origin = "app1",
    Lifetime = TimeSpan.FromDays(7),
}, DateTimeOffset.UtcNow);

Console.WriteLine(token.Value);   // wh_eyJ…
```

`Issue` checks the request as the gateway will check the token: what it serves against the set's `allow`, its lifetime against `maxLifetime`. `KeySet.Create` makes a new set, `Rotate`, `DenyToken` and `RevokeAll` change one, and `Encode` gives the public set, `whk_…`, for the gateway.

## Checking

```csharp
var validator = new TokenValidator(KeySet.Parse(publicSet));
var result = validator.Validate(presentedToken);
if (result.IsValid && validator.CheckHost(result.Token!, "app1.example.com") is null)
{
    // result.Token.Subject is the origin id
}
```

A token is ES256-signed, typed `webhop+jwt`, and names its key by its RFC 7638 thumbprint. The validator accepts exactly that shape. The algorithm comes from the key, never from the token. The header can't bring its own key or critical extensions, and base64url is read strictly, so one signature makes one token.

## More

https://github.com/elebree/WebHop
