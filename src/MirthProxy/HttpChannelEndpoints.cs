using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog.Context;

namespace MirthProxy;

public static class HttpChannelEndpoints
{
    public static void Map(WebApplication app, ProxyOptions options)
    {
        foreach (var (channel, index) in options.Channels.Select((channel, index) => (channel, index)))
        {
            if (channel.HttpSource is not { } source)
                continue;

            var runner = new ChannelRunner(
                channel,
                app.Services.GetRequiredService<IHttpClientFactory>(),
                app.Logger);

            app.MapMethods(source.Path, [source.Method], async context =>
            {
                using var channelContext = LogContext.PushProperty("ChannelId", channel.ChannelId(index));
                var started = Stopwatch.GetTimestamp();

                try
                {
                    var payload = await ChannelRunner.ReadBoundedAsync(
                        context.Request.Body,
                        channel.MaxMessageBytes,
                        context.RequestAborted);
                    var response = await runner.ForwardAsync(payload, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    if (response.ContentType is not null)
                        context.Response.ContentType = response.ContentType;
                    context.Response.ContentLength = response.Payload.Length;
                    await context.Response.Body.WriteAsync(response.Payload, context.RequestAborted);
                    app.Logger.LogDebug(
                        "HTTP to MLLP message completed in {TransitTimeMs:F2} ms ({MessageBytes} bytes)",
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        payload.Length);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    app.Logger.LogDebug("HTTP to MLLP request was cancelled by the client.");
                }
                catch (OperationCanceledException exception)
                {
                    app.Logger.LogWarning(exception, "HTTP to MLLP request timed out.");
                    context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                }
                catch (InvalidDataException exception)
                {
                    app.Logger.LogWarning(exception, "HTTP request exceeded the configured message size limit.");
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                }
                catch (Exception exception) when (
                    exception is SocketException or IOException or HttpRequestException)
                {
                    app.Logger.LogWarning(exception, "HTTP to MLLP proxy request failed.");
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }
            });
        }
    }
}
