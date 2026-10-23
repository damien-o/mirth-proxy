using System.Net;

namespace MirthProxy;

public sealed class ProxyOptions
{
    public HttpListenerOptions HttpListener { get; set; } = new();
    public List<ChannelOptions> Channels { get; set; } = [];
}

public sealed class HttpListenerOptions
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string Scheme { get; set; } = "http";
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}

public sealed class ChannelOptions
{
    public string? Id { get; set; }
    public MllpSourceOptions? MllpSource { get; set; }
    public HttpTargetOptions? HttpTarget { get; set; }
    public HttpSourceOptions? HttpSource { get; set; }
    public MllpTargetOptions? MllpTarget { get; set; }
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxMessageBytes { get; set; } = 10 * 1024 * 1024;

    public string ChannelId(int index) =>
        string.IsNullOrWhiteSpace(Id) ? $"channel-{index + 1}" : Id;
}

public sealed class MllpSourceOptions
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; }
}

public sealed class HttpTargetOptions
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
}

public sealed class HttpSourceOptions
{
    public string Path { get; set; } = "";
    public string Method { get; set; } = "POST";
}

public sealed class MllpTargetOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public static class ProxyOptionsValidator
{
    public static void Validate(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Channels.Count == 0)
            throw new InvalidOperationException("At least one channel must be configured.");
        if (options.HttpListener.Port is < 1 or > 65535)
            throw new InvalidOperationException("HttpListener.Port must be between 1 and 65535.");
        if (options.HttpListener.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("HttpListener.Scheme must be 'http' or 'https'.");
        if (string.IsNullOrWhiteSpace(options.HttpListener.Host))
            throw new InvalidOperationException("HttpListener.Host is required.");
        if (options.HttpListener.Scheme == "https" &&
            string.IsNullOrWhiteSpace(options.HttpListener.CertificatePath))
            throw new InvalidOperationException("HttpListener.CertificatePath is required for HTTPS.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mllpPorts = new HashSet<int>();
        var httpRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Channels.Count; i++)
        {
            var channel = options.Channels[i];
            var channelId = channel.ChannelId(i);

            if (!ids.Add(channelId))
                throw new InvalidOperationException($"Duplicate ChannelId '{channelId}'.");
            if ((channel.MllpSource is null) == (channel.HttpSource is null))
                throw new InvalidOperationException(
                    $"Channel '{channelId}' must configure exactly one of MllpSource or HttpSource.");
            if ((channel.HttpTarget is null) == (channel.MllpTarget is null))
                throw new InvalidOperationException(
                    $"Channel '{channelId}' must configure exactly one of HttpTarget or MllpTarget.");
            if (channel.ConnectionTimeout <= TimeSpan.Zero || channel.ResponseTimeout <= TimeSpan.Zero)
                throw new InvalidOperationException($"Channel '{channelId}' timeouts must be positive.");
            if (channel.MaxMessageBytes <= 0)
                throw new InvalidOperationException($"Channel '{channelId}' MaxMessageBytes must be positive.");

            if (channel.MllpSource is { } source)
            {
                ValidatePort(source.Port, $"Channels[{i}].MllpSource.Port");
                if (!mllpPorts.Add(source.Port))
                    throw new InvalidOperationException($"Multiple MLLP sources cannot bind port {source.Port}.");
                if (string.IsNullOrWhiteSpace(source.Host))
                    throw new InvalidOperationException($"Channel '{channelId}' MllpSource.Host is required.");
                if (!IPAddress.TryParse(source.Host, out _))
                    throw new InvalidOperationException($"Channel '{channelId}' MllpSource.Host must be an IP address.");
                if (source.Port == options.HttpListener.Port)
                    throw new InvalidOperationException(
                        $"Channel '{channelId}' MllpSource.Port cannot match HttpListener.Port.");
            }

            if (channel.HttpTarget is { } httpTarget)
            {
                if (!Uri.TryCreate(httpTarget.Url, UriKind.Absolute, out var targetUri) ||
                    targetUri.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException($"Channel '{channelId}' has an invalid HttpTarget.Url.");
                ValidateMethod(httpTarget.Method, $"Channel '{channelId}' HttpTarget.Method");
            }

            if (channel.HttpSource is { } httpSource)
            {
                if (string.IsNullOrWhiteSpace(httpSource.Path) ||
                    !httpSource.Path.StartsWith('/') ||
                    httpSource.Path.Contains('?') ||
                    httpSource.Path.Contains('#'))
                    throw new InvalidOperationException($"Channel '{channelId}' HttpSource.Path must be an absolute route path.");
                if (!httpRoutes.Add(httpSource.Path))
                    throw new InvalidOperationException($"Multiple HTTP sources cannot use path '{httpSource.Path}'.");
                ValidateMethod(httpSource.Method, $"Channel '{channelId}' HttpSource.Method");
            }

            if (channel.MllpTarget is { } target)
            {
                ValidatePort(target.Port, $"Channels[{i}].MllpTarget.Port");
                if (string.IsNullOrWhiteSpace(target.Host))
                    throw new InvalidOperationException($"Channel '{channelId}' MllpTarget.Host is required.");
            }
        }
    }

    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new InvalidOperationException($"{name} must be between 1 and 65535.");
    }

    private static void ValidateMethod(string method, string name)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"{name} must be a valid HTTP method token.");
    }
}
