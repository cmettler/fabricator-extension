namespace Fabricator.Installer;

/// <summary>
/// A read-only, seekable view of a byte range of another stream, presented as if it were a
/// standalone file starting at 0. This is what lets <c>ZipArchive</c> read the payload in place,
/// without copying it out of the artifact first — the point of the polyglot layout.
/// </summary>
internal sealed class WindowStream : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _ownsInner;
    private long _position;

    internal WindowStream(Stream inner, long offset, long length, bool ownsInner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException("The underlying stream must be readable and seekable.", nameof(inner));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _inner = inner;
        _offset = offset;
        _length = length;
        _ownsInner = ownsInner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        // Seek every time: the window owns its logical position, and sharing the underlying handle
        // with anything else would otherwise be a silent correctness hazard.
        _inner.Position = _offset + _position;
        int read = _inner.Read(buffer);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("Attempted to seek before the beginning of the payload window.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
