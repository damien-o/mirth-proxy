using System.IO.Pipelines;
using MirthProxy;
using Xunit;

namespace MirthProxy.Tests;

public sealed class MllpFramingTests
{
    [Fact]
    public async Task ReadFrameAsync_ReadsPayloadWithLeadingNoiseAndFrameSplitAcrossWrites()
    {
        var pipe = new Pipe();
        var readerTask = MllpFraming.ReadFrameAsync(pipe.Reader, 1024, CancellationToken.None).AsTask();

        await pipe.Writer.WriteAsync(new byte[] { 0x01, 0x0B, (byte)'M', (byte)'S' });
        await pipe.Writer.WriteAsync(new byte[] { (byte)'H', 0x1C, 0x0D });
        await pipe.Writer.CompleteAsync();

        var frame = await readerTask;

        Assert.NotNull(frame);
        Assert.Equal("MSH", System.Text.Encoding.ASCII.GetString(frame.Payload));
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsOversizedPayload()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 0x0B, (byte)'A', (byte)'B', (byte)'C' });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MllpFraming.ReadFrameAsync(pipe.Reader, 2, CancellationToken.None));

        await pipe.Reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task WriteFrameAsync_WritesStandardMllpEnvelope()
    {
        await using var stream = new MemoryStream();

        await MllpFraming.WriteFrameAsync(stream, "ACK"u8.ToArray(), CancellationToken.None);

        Assert.Equal(new byte[] { 0x0B, (byte)'A', (byte)'C', (byte)'K', 0x1C, 0x0D }, stream.ToArray());
    }
}
