using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MirthProxy;
using Xunit;

namespace MirthProxy.Tests;

public sealed class ChannelForwardingTests
{
    [Fact]
    public async Task ForwardAsync_ForwardsMllpSourcePayloadToHttpTarget()
    {
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedBody = null;
        string? capturedContentType = null;
        var factory = new StubHttpClientFactory(async (request, _) =>
        {
            capturedMethod = request.Method;
            capturedUri = request.RequestUri;
            capturedBody = await request.Content!.ReadAsStringAsync();
            capturedContentType = request.Content.Headers.ContentType!.MediaType;
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("ACK", Encoding.ASCII)
            };
        });
        var runner = new ChannelRunner(
            new ChannelOptions
            {
                HttpTarget = new HttpTargetOptions
                {
                    Url = "https://target.example.test/hl7",
                    Method = "PUT"
                }
            },
            factory,
            NullLogger.Instance);

        var response = await runner.ForwardAsync("MSH|test"u8.ToArray(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ACK", Encoding.ASCII.GetString(response.Payload));
        Assert.Equal(HttpMethod.Put, capturedMethod);
        Assert.Equal("https://target.example.test/hl7", capturedUri!.ToString());
        Assert.Equal("MSH|test", capturedBody);
        Assert.Equal("application/hl7-v2", capturedContentType);
    }

    [Fact]
    public async Task MllpListener_ForwardsMllpSourceToHttpTarget()
    {
        var sourcePort = GetFreePort();
        var factory = new StubHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ACK", Encoding.ASCII)
            }));
        var runner = new ChannelRunner(
            new ChannelOptions
            {
                MllpSource = new MllpSourceOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = sourcePort
                },
                HttpTarget = new HttpTargetOptions { Url = "http://target.example.test/hl7" }
            },
            factory,
            NullLogger.Instance);
        using var shutdown = new CancellationTokenSource();
        var runnerTask = runner.RunAsync(shutdown.Token);
        using var client = await ConnectWithRetryAsync(sourcePort);
        await using var stream = client.GetStream();
        var responseReader = System.IO.Pipelines.PipeReader.Create(
            stream,
            new System.IO.Pipelines.StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            await MllpFraming.WriteFrameAsync(
                stream, "MSH|source"u8.ToArray(), CancellationToken.None);
            var response = await MllpFraming.ReadFrameAsync(
                responseReader, 1024, CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal("ACK", Encoding.ASCII.GetString(response.Payload));
        }
        finally
        {
            await responseReader.CompleteAsync();
            shutdown.Cancel();
            client.Dispose();
            await runnerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ForwardAsync_ForwardsHttpSourcePayloadToMllpTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var targetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var targetTask = RunMllpEchoAsync(listener);
        var runner = new ChannelRunner(
            new ChannelOptions
            {
                MllpTarget = new MllpTargetOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = targetPort
                }
            },
            new StubHttpClientFactory((_, _) => throw new InvalidOperationException("HTTP should not be used.")),
            NullLogger.Instance);

        var response = await runner.ForwardAsync("MSH|test"u8.ToArray(), CancellationToken.None);
        var receivedPayload = await targetTask;

        Assert.Equal("MSH|test", Encoding.ASCII.GetString(receivedPayload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ACK", Encoding.ASCII.GetString(response.Payload));
    }

    [Fact]
    public async Task HttpEndpoint_ForwardsHttpSourceToHttpTarget()
    {
        var factory = new StubHttpClientFactory((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("target-response", Encoding.ASCII)
            });
        });
        await using var app = await CreateTestAppAsync(
            new ChannelOptions
            {
                HttpSource = new HttpSourceOptions { Path = "/route" },
                HttpTarget = new HttpTargetOptions { Url = "http://target.example.test/hl7" }
            },
            factory);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/route", new StringContent("source-message"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("target-response", body);
    }

    [Fact]
    public async Task HttpEndpoint_ForwardsHttpSourceToMllpTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var targetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var targetTask = RunMllpEchoAsync(listener);
        var factory = new StubHttpClientFactory((_, _) => throw new InvalidOperationException("HTTP should not be used."));
        await using var app = await CreateTestAppAsync(
            new ChannelOptions
            {
                HttpSource = new HttpSourceOptions { Path = "/route" },
                MllpTarget = new MllpTargetOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = targetPort
                }
            },
            factory);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/route", new StringContent("source-message"));
        var body = await response.Content.ReadAsStringAsync();
        var receivedPayload = await targetTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ACK", body);
        Assert.Equal("source-message", Encoding.UTF8.GetString(receivedPayload));
    }

    private static async Task<WebApplication> CreateTestAppAsync(
        ChannelOptions channel,
        IHttpClientFactory factory)
    {
        var options = new ProxyOptions
        {
            Channels = [channel]
        };
        ProxyOptionsValidator.Validate(options);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHttpClientFactory>(factory);
        var app = builder.Build();
        HttpChannelEndpoints.Map(app, options);
        await app.StartAsync();
        return app;
    }

    private static async Task<byte[]> RunMllpEchoAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = client.GetStream();
        var reader = System.IO.Pipelines.PipeReader.Create(stream, new System.IO.Pipelines.StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            var frame = await MllpFraming.ReadFrameAsync(reader, 1024, CancellationToken.None)
                ?? throw new EndOfStreamException("No MLLP request received.");
            await MllpFraming.WriteFrameAsync(stream, "ACK"u8.ToArray(), CancellationToken.None);
            return frame.Payload;
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(int port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(25);
            }
        }

        throw new TimeoutException($"Could not connect to the MLLP listener on port {port}.");
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(new StubHttpMessageHandler(send));

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
