using MirthProxy;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var proxyOptions = builder.Configuration.Get<ProxyOptions>()
    ?? throw new InvalidOperationException("Proxy configuration could not be loaded.");

ProxyOptionsValidator.Validate(proxyOptions);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .MinimumLevel.Debug()
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "MirthProxy")
        .WriteTo.Console());

builder.Services.AddSingleton(proxyOptions);
builder.Services.AddHttpClient("proxy", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHostedService<ChannelSupervisor>();

var listener = proxyOptions.HttpListener;
builder.WebHost.UseUrls($"{listener.Scheme}://{listener.Host}:{listener.Port}");
if (listener.Scheme == "https")
{
    builder.WebHost.ConfigureKestrel(serverOptions =>
        serverOptions.ConfigureEndpointDefaults(endpoint =>
            endpoint.UseHttps(listener.CertificatePath!, listener.CertificatePassword)));
}

var app = builder.Build();
HttpChannelEndpoints.Map(app, proxyOptions);

try
{
    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
