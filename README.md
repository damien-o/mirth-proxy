# Mirth Proxy

A .NET 10 Worker Service sidecar that proxies HL7 messages between MLLP and
HTTP/HTTPS. Each channel can use any source/target pair: MLLP→HTTP/S,
MLLP→MLLP, HTTP/S→MLLP, or HTTP/S→HTTP/S. Each configured channel runs
independently and concurrently. MLLP frame parsing uses `System.IO.Pipelines`;
logs are written as structured Serilog console events.

## Configuration

Configure channels in `src/appsettings.json` or through standard .NET
configuration providers/environment variables. A channel has one source and
one target:

- `MllpSource` + `HttpTarget`: sends the unframed payload to the configured
  HTTP method and URL, then frames the response for the MLLP client.
- `MllpSource` + `MllpTarget`: relays the payload through a second MLLP
  connection and frames the returned message for the source client.
- `HttpSource` + `MllpTarget`: wraps the HTTP request body as an MLLP message
  and returns the unframed MLLP response.
- `HttpSource` + `HttpTarget`: forwards the request body and configured HTTP
  method to the target URL, returning the target response status and body.

All HTTP sources share the `HttpListener` host and port and are routed by
`HttpSource.Path`. MLLP sources bind their own ports; because MLLP has no
path-based routing, each source must use a distinct port. The application
validates channel pairs, ports, route and channel ID uniqueness, timeouts, and
message size limits at startup.

Each channel may set `ConnectionTimeout` and `ResponseTimeout` as .NET
`TimeSpan` values (for example, `"00:00:10"` or `"00:00:02.500"`), along with
`MaxMessageBytes`. The timeout defaults are 10 seconds and 30 seconds; the
message limit defaults to 10 MiB. Channel IDs are optional; omitted IDs are
generated as `channel-1`, `channel-2`, and so on. Every channel log event
includes `ChannelId`. Debug events include message transit time.

HTTP source routes share one listener and therefore one scheme, configured by
`HttpListener.Scheme` as `http` or `https`. For HTTPS ingress, set
`HttpListener.CertificatePath` and optionally `CertificatePassword` for the
Kestrel certificate. HTTP targets independently accept `http://` or `https://`
URLs. Protect source routes with appropriate network controls or an
authenticated ingress proxy.

Configuration is loaded at startup; changing `appsettings.json` requires a
process restart.

## Run

```sh
dotnet run --project src/MirthProxy/MirthProxy.csproj
# Or open/build the complete solution:
dotnet build MirthProxy.slnx
```

## Tests

```sh
dotnet test MirthProxy.slnx
```

## Container

```sh
docker build -t mirth-proxy .
docker run --rm -p 8080:8080 -p 8081:8081 -p 8082:8082 mirth-proxy
```

Adjust published ports and channel configuration for the deployment.
