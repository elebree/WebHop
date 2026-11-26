namespace WebHop.Gateway
{
    /// <summary>
    /// The gateway can't work as configured, such as without a usable key set. Thrown at startup, so a
    /// misconfigured gateway stops instead of turning every origin away; the message says what to fix.
    /// </summary>
    public sealed class GatewayConfigurationException(string message) : Exception(message);
}
