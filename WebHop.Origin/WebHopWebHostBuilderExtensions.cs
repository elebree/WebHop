using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebHop.Origin;

// Same namespace as UseKestrel, so builder.WebHost.UseWebHop() needs no extra using
namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHopWebHostBuilderExtensions
    {
        /// <summary>Configuration section bound to <see cref="WebHopServerOptions"/> (e.g. WebHop:AuthToken).</summary>
        public const string ConfigurationSection = "WebHop";

        /// <summary>
        /// Serves the app through a WebHop gateway instead of Kestrel. The token (WebHop:AuthToken or
        /// WEBHOP_TOKEN) says where to connect; --urls / applicationUrl, if set, overrides the gateway's
        /// scheme, host and port. Options are read from the "WebHop" configuration section, then
        /// <paramref name="configure"/> is applied.
        /// </summary>
        public static IWebHostBuilder UseWebHop(this IWebHostBuilder builder, Action<WebHopServerOptions>? configure = null)
        {
            return builder.ConfigureServices((context, services) =>
            {
                var options = services.AddOptions<WebHopServerOptions>()
                    .Bind(context.Configuration.GetSection(ConfigurationSection));
                if (configure is not null)
                    options.Configure(configure);

                services.AddSingleton<IServer>(sp => new WebHopServer(
                    sp.GetRequiredService<IOptions<WebHopServerOptions>>().Value,
                    sp.GetRequiredService<ILoggerFactory>()));
            });
        }

        /// <summary>
        /// Serves the app through the WebHop gateway at <paramref name="gatewayUrl"/>, which takes the scheme,
        /// host and port only, instead of the one the token names.
        /// </summary>
        public static IWebHostBuilder UseWebHop(this IWebHostBuilder builder, string gatewayUrl, Action<WebHopServerOptions>? configure = null)
        {
            return builder.UseWebHop(configure).UseUrls(gatewayUrl);
        }
    }
}
