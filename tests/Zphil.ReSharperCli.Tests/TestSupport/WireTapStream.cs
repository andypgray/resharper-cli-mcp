namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     A write-only <see cref="Stream" /> decorator that forwards to <paramref name="inner" /> and copies every
///     byte on its way past into <paramref name="log" />. Wrapped around the server's end of the harness's pipe
///     pair, it is what turns "what the server emitted, in what order" from an inference into a reading — see
///     <see cref="WireLog" /> for why the client's end cannot answer that.
/// </summary>
/// <remarks>
///     Every write path is overridden rather than only the <see cref="ReadOnlyMemory{T}" /> one
///     <c>StreamServerTransport</c> uses today, and each override forwards to the matching member on
///     <paramref name="inner" /> rather than to a sibling override — so no write can reach the pipe unlogged,
///     and none is logged twice. The read and seek members throw, mirroring what
///     <see cref="System.IO.Pipelines.PipeWriter.AsStream" /> already offers.
/// </remarks>
internal sealed class WireTapStream(Stream inner, WireLog log) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        log.Append(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        log.Append(buffer);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        log.Append(buffer.AsSpan(offset, count));

        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        log.Append(buffer.Span);

        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();

        base.Dispose(disposing);
    }
}