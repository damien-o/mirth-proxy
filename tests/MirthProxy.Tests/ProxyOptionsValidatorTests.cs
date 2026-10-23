using Microsoft.Extensions.Configuration;
using MirthProxy;
using Xunit;

namespace MirthProxy.Tests;

public sealed class ProxyOptionsValidatorTests
{
    [Fact]
    public void Validate_AcceptsAllSourceAndTargetTransportCombinations()
    {
        var options = new ProxyOptions
        {
            HttpListener = new HttpListenerOptions { Scheme = "https", CertificatePath = "test.pfx" },
            Channels =
            [
                new()
                {
                    Id = "mllp-to-http",
                    MllpSource = new MllpSourceOptions { Port = 8101 },
                    HttpTarget = new HttpTargetOptions { Url = "https://example.test/hl7" }
                },
                new()
                {
                    Id = "mllp-to-mllp",
                    MllpSource = new MllpSourceOptions { Port = 8102 },
                    MllpTarget = new MllpTargetOptions { Host = "ehr.example.test", Port = 8103 }
                },
                new()
                {
                    Id = "http-to-http",
                    HttpSource = new HttpSourceOptions { Path = "/http-http" },
                    HttpTarget = new HttpTargetOptions { Url = "http://example.test/hl7" }
                },
                new()
                {
                    Id = "https-to-mllp",
                    HttpSource = new HttpSourceOptions { Path = "/https-mllp" },
                    MllpTarget = new MllpTargetOptions { Host = "ehr.example.test", Port = 8104 }
                }
            ]
        };

        var exception = Record.Exception(() => ProxyOptionsValidator.Validate(options));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_RejectsHttpListenerPortSharedWithMllpSource()
    {
        var options = new ProxyOptions
        {
            HttpListener = new HttpListenerOptions { Port = 8080 },
            Channels =
            [
                new()
                {
                    MllpSource = new MllpSourceOptions { Port = 8080 },
                    HttpTarget = new HttpTargetOptions { Url = "http://example.test/hl7" }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ProxyOptionsValidator.Validate(options));

        Assert.Contains("cannot match HttpListener.Port", exception.Message);
    }

    [Fact]
    public void Validate_RequiresCertificateForHttpsListener()
    {
        var options = new ProxyOptions
        {
            HttpListener = new HttpListenerOptions { Scheme = "https" },
            Channels =
            [
                new()
                {
                    HttpSource = new HttpSourceOptions { Path = "/proxy" },
                    HttpTarget = new HttpTargetOptions { Url = "https://example.test/hl7" }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ProxyOptionsValidator.Validate(options));

        Assert.Contains("CertificatePath", exception.Message);
    }

    [Fact]
    public void ConfigurationBinding_ParsesFractionalTimeSpanTimeouts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:0:HttpSource:Path"] = "/proxy",
                ["Channels:0:HttpTarget:Url"] = "https://example.test/hl7",
                ["Channels:0:ConnectionTimeout"] = "00:00:02.500",
                ["Channels:0:ResponseTimeout"] = "00:00:45.125"
            })
            .Build();

        var options = configuration.Get<ProxyOptions>()!;

        Assert.Equal(TimeSpan.FromMilliseconds(2500), options.Channels[0].ConnectionTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(45125), options.Channels[0].ResponseTimeout);
        ProxyOptionsValidator.Validate(options);
    }

    [Fact]
    public void Validate_RejectsNonPositiveTimeouts()
    {
        var options = new ProxyOptions
        {
            Channels =
            [
                new()
                {
                    HttpSource = new HttpSourceOptions { Path = "/proxy" },
                    HttpTarget = new HttpTargetOptions { Url = "https://example.test/hl7" },
                    ConnectionTimeout = TimeSpan.Zero
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => ProxyOptionsValidator.Validate(options));

        Assert.Contains("timeouts must be positive", exception.Message);
    }
}
