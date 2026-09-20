using System.IO;

namespace AIHub.Services.LiteraryImport;

public sealed class ImportCaptureStream(Stream source, Stream target) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = source.Read(buffer, offset, count); target.Write(buffer, offset, read); target.Flush(); return read; }
    public override int Read(Span<byte> buffer)
    { var read = source.Read(buffer); target.Write(buffer[..read]); target.Flush(); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        // Persist received bytes even if the caller cancels between read and write.
        await target.WriteAsync(buffer[..read], CancellationToken.None).ConfigureAwait(false);
        await target.FlushAsync(CancellationToken.None).ConfigureAwait(false); return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => target.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
