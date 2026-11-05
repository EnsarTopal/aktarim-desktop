using System.IO;

namespace Aktarim.Desktop;

// Finish bounded resources before release; abandoned or failed reads require a new WPD session.
internal sealed class WpdReadStream(Stream input, long? expectedLength, Action abort) : Stream
{
    private long received;
    private bool ended, failed, disposed;
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedLength ?? throw new NotSupportedException();
    public override long Position { get => received; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0) return 0;
        try
        {
            int read = input.Read(buffer, offset, count);
            received = checked(received + read);
            ended = read == 0;
            return read;
        }
        catch (Exception e)
        {
            failed = true;
            throw new WpdReadException(e);
        }
    }

    // Keep the same lifetime tracking for both the preview's synchronous reads and async copy/hash reads.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        token.IsCancellationRequested ? Task.FromCanceled<int>(token) : Task.FromResult(Read(buffer, offset, count));

    protected override void Dispose(bool disposing)
    {
        if (!disposing || disposed) return;
        disposed = true;
        try
        {
            // Only finish bounded photos/thumbnails here; never drain a large video for a preview or window close.
            long limit = expectedLength == null ? 4 * 1024 * 1024 : 12 * 1024 * 1024;
            if (!failed && !ended && (expectedLength == null || expectedLength <= limit))
            {
                try
                {
                    var buffer = new byte[65536];
                    while (!ended && received <= limit)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit + 1 - received));
                        received += read; ended = read == 0;
                    }
                }
                catch (Exception) { failed = true; }
            }
            if (failed || !ended || expectedLength is { } length && received != length) abort();
        }
        finally
        {
            try { input.Dispose(); }
            catch { abort(); throw; }
            finally { base.Dispose(disposing); }
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class WpdReadException : IOException
{
    public WpdReadException(Exception cause) : base("WPD source read failed; the device session must be reopened. " + cause.Message, cause)
    { HResult = unchecked((int)0x8007001e); }
}
