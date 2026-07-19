using System.Buffers.Binary;
using System.Security.Cryptography;
using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class InMemoryTransientAudio : ITransientAudio
{
    public const string WaveContentType = "audio/wav";
    private byte[]? _ownedBuffer;

    private InMemoryTransientAudio(byte[] ownedBuffer)
    {
        _ownedBuffer = ownedBuffer;
    }

    public long Length =>
        Volatile.Read(ref _ownedBuffer)?.LongLength ??
        throw new ObjectDisposedException(nameof(InMemoryTransientAudio));

    public string ContentType => WaveContentType;

    public static InMemoryTransientAudio FromPcm16(
        ReadOnlySpan<byte> pcm,
        int sampleRate,
        int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var buffer = new byte[checked(44 + pcm.Length)];
        WriteWaveHeader(buffer, pcm.Length, sampleRate, channels);
        pcm.CopyTo(buffer.AsSpan(44));
        return new(buffer);
    }

    public ValueTask<Stream> OpenReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = Volatile.Read(ref _ownedBuffer);
        ObjectDisposedException.ThrowIf(buffer is null, this);
        Stream stream = new MemoryStream(
            buffer,
            index: 0,
            count: buffer.Length,
            writable: false,
            publiclyVisible: false);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DisposeAsync()
    {
        var buffer = Interlocked.Exchange(ref _ownedBuffer, null);
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        return ValueTask.CompletedTask;
    }

    private static void WriteWaveHeader(
        Span<byte> destination,
        int pcmLength,
        int sampleRate,
        int channels)
    {
        "RIFF"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[4..],
            checked(36 + pcmLength));
        "WAVEfmt "u8.CopyTo(destination[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(destination[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[22..],
            checked((short)channels));
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[28..],
            checked(sampleRate * channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[32..],
            checked((short)(channels * 2)));
        BinaryPrimitives.WriteInt16LittleEndian(destination[34..], 16);
        "data"u8.CopyTo(destination[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], pcmLength);
    }
}
