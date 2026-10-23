using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.Http.Headers;
using Serilog.Context;

namespace MirthProxy;

public sealed class ChannelSupervisor(
    ProxyOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<ChannelSupervisor> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Channels
            .Select((channel, index) => (channel, index))
            .Where(item => item.channel.MllpSource is not null)
            .Select(item =>
                RunIsolatedAsync(item.channel, item.index, stoppingToken)));

    private async Task RunIsolatedAsync(
        ChannelOptions channel,
        int index,
        CancellationToken stoppingToken)
    {
        using var channelContext = LogContext.PushProperty("ChannelId", channel.ChannelId(index));
        var runner = new ChannelRunner(channel, httpClientFactory, logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Channel stopped unexpectedly; restarting in 5 seconds.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}

internal sealed class ChannelRunner(
    ChannelOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger logger)
{
    private readonly SemaphoreSlim _connections = new(128, 128);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (options.MllpSource is not { } source)
            return;

        var listener = new TcpListener(System.Net.IPAddress.Parse(source.Host), source.Port);
        listener.Start();
        logger.LogInformation("MLLP listener started on {Host}:{Port}", source.Host, source.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                try
                {
                    await _connections.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    client.Dispose();
                    throw;
                }
                _ = HandleClientSafelyAsync(client, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            await HandleMllpClientAsync(client, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MLLP client connection failed.");
        }
        finally
        {
            client.Dispose();
            _connections.Release();
        }
    }

    private async Task HandleMllpClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        await using var stream = client.GetStream();
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                receiveTimeout.CancelAfter(options.ConnectionTimeout);
                var frame = await MllpFraming.ReadFrameAsync(reader, options.MaxMessageBytes, receiveTimeout.Token);
                if (frame is null)
                    return;

                var response = await ForwardAsync(frame.Payload, stoppingToken);
                if ((int)response.StatusCode is < 200 or >= 300)
                    throw new HttpRequestException(
                        $"HTTP target returned status code {(int)response.StatusCode}.");
                await MllpFraming.WriteFrameAsync(stream, response.Payload, stoppingToken);
                logger.LogDebug(
                    "MLLP to HTTP message completed in {TransitTimeMs:F2} ms ({MessageBytes} bytes)",
                    Stopwatch.GetElapsedTime(frame.ReceivedTimestamp).TotalMilliseconds,
                    frame.Payload.Length);
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    internal async Task<ProxyResponse> ForwardAsync(byte[] payload, CancellationToken stoppingToken)
    {
        if (options.HttpTarget is { } httpTarget)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(options.ResponseTimeout);
            using var request = new HttpRequestMessage(new HttpMethod(httpTarget.Method), httpTarget.Url)
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/hl7-v2");

            using var response = await httpClientFactory.CreateClient("proxy").SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var responsePayload = await ReadBoundedAsync(
                responseStream, options.MaxMessageBytes, timeout.Token);
            return new ProxyResponse(
                response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                responsePayload);
        }

        var mllpPayload = await SendToMllpAsync(payload, stoppingToken);
        return new ProxyResponse(
            System.Net.HttpStatusCode.OK,
            "application/hl7-v2",
            mllpPayload);
    }

    public async Task<byte[]> SendToMllpAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var target = options.MllpTarget!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectionTimeout);

        using var client = new TcpClient();
        await client.ConnectAsync(target.Host, target.Port, timeout.Token);
        await using var stream = client.GetStream();
        await MllpFraming.WriteFrameAsync(stream, payload, timeout.Token);

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(options.ResponseTimeout);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            var frame = await MllpFraming.ReadFrameAsync(reader, options.MaxMessageBytes, responseTimeout.Token);
            return frame?.Payload ?? throw new EndOfStreamException("The MLLP target closed without a response.");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maxBytes)
                throw new InvalidDataException($"Message exceeds the {maxBytes}-byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

internal sealed record ProxyResponse(
    System.Net.HttpStatusCode StatusCode,
    string? ContentType,
    byte[] Payload);
