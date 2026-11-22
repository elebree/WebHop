using WebHop.Gateway;

WebApplication app;
try
{
    app = GatewayApp.Create(args, a => a.UseWebHopErrorPages());
}
catch (GatewayConfigurationException ex)
{
    // A gateway that can't accept tunnels serves nobody: stop, so the misconfiguration shows
    Console.Error.WriteLine($"ERROR:  {ex.Message}");
    return 1;
}

app.Run();
return 0;
