using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;

namespace MirthProxy;

public sealed record MllpFrame(byte[] Payload, long ReceivedTimestamp);

public static class MllpFraming
{
    private const byte StartBlock = 0x0B;
    private const byte EndBlock = 0x1C;
    private const byte CarriageReturn = 0x0D;
    private static readonly byte[] StartBlockBytes = [StartBlock];
    private static readonly byte[] FrameTerminatorBytes = [EndBlock, CarriageReturn];

    public static async ValueTask<MllpFrame?> ReadFrameAsync(
        PipeReader reader,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            var sequenceReader = new SequenceReader<byte>(buffer);

            if (!sequenceReader.TryAdvanceTo(StartBlock, advancePastDelimiter: false))
            {
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                    return null;
                continue;
            }

            var startMarker = sequenceReader.Position;
            sequenceReader.Advance(1);
            var receivedTimestamp = Stopwatch.GetTimestamp();
            var payloadStart = sequenceReader.Position;

            if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> payload, EndBlock, advancePastDelimiter: true))
            {
                if (buffer.Slice(payloadStart).Length > maxMessageBytes)
                    throw new InvalidDataException($"MLLP frame exceeds the {maxMessageBytes}-byte limit.");

                reader.AdvanceTo(startMarker, buffer.End);
                if (result.IsCompleted)
                    throw new InvalidDataException("The MLLP stream ended before the frame terminator.");
                continue;
            }

            if (payload.Length > maxMessageBytes)
                throw new InvalidDataException($"MLLP frame exceeds the {maxMessageBytes}-byte limit.");
            if (!sequenceReader.TryRead(out var terminator))
            {
                reader.AdvanceTo(startMarker, buffer.End);
                if (result.IsCompleted)
                    throw new InvalidDataException("The MLLP stream ended before the carriage-return terminator.");
                continue;
            }
            if (terminator != CarriageReturn)
                throw new InvalidDataException("The MLLP end-block must be followed by a carriage return.");

            var payloadBytes = payload.ToArray();
            reader.AdvanceTo(sequenceReader.Position, sequenceReader.Position);
            return new MllpFrame(payloadBytes, receivedTimestamp);
        }
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(StartBlockBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.WriteAsync(FrameTerminatorBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
