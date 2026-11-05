// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Adbc;

/// <summary>A minimal protobuf writer: enough to build and extend Substrait plans without a protobuf library.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _buffer = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        Tag(field, 0);
        WriteVarint(value);
        return this;
    }

    /// <summary>A signed integer field (int32/int64): negative values are written as 64-bit two's complement.</summary>
    public ProtoWriter Signed(int field, long value) => Varint(field, unchecked((ulong)value));

    public ProtoWriter Fixed32(int field, uint value)
    {
        Tag(field, 5);
        for (int i = 0; i < 4; i++)
            _buffer.WriteByte((byte)(value >> (8 * i)));
        return this;
    }

    public ProtoWriter Fixed64(int field, ulong value)
    {
        Tag(field, 1);
        for (int i = 0; i < 8; i++)
            _buffer.WriteByte((byte)(value >> (8 * i)));
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> bytes)
    {
        Tag(field, 2);
        WriteVarint((ulong)bytes.Length);
        _buffer.Write(bytes.ToArray(), 0, bytes.Length);
        return this;
    }

    /// <summary>Appends bytes that already are encoded fields (tag included), as read by <see cref="ProtoReader"/>.</summary>
    public ProtoWriter Raw(ReadOnlySpan<byte> encoded)
    {
        _buffer.Write(encoded.ToArray(), 0, encoded.Length);
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();

    private void Tag(int field, int wireType) => WriteVarint(((ulong)field << 3) | (uint)wireType);

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _buffer.WriteByte((byte)value);
    }
}

/// <summary>A minimal protobuf reader over one message: walks its fields and hands out their raw encodings.</summary>
internal sealed class ProtoReader
{
    private readonly byte[] _buffer;
    private int _position;
    private readonly int _end;

    public ProtoReader(byte[] buffer) : this(buffer, 0, buffer.Length)
    {
    }

    private ProtoReader(byte[] buffer, int start, int end)
    {
        _buffer = buffer;
        _position = start;
        _end = end;
    }

    /// <summary>The next field: its number, wire type and the bytes of the whole field (tag included).</summary>
    public bool Next(out int field, out int wireType, out int fieldStart)
    {
        fieldStart = _position;
        if (_position >= _end)
        {
            field = 0;
            wireType = 0;
            return false;
        }
        ulong tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return true;
    }

    public ReadOnlySpan<byte> Encoded(int fieldStart) => new(_buffer, fieldStart, _position - fieldStart);

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (int shift = 0; ; shift += 7)
        {
            if (_position >= _end)
                throw new InvalidDataException("adbc: truncated Substrait plan");
            byte b = _buffer[_position++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
                return value;
        }
    }

    /// <summary>A length-delimited field as a reader of its own.</summary>
    public ProtoReader Message()
    {
        int length = (int)ReadVarint();
        var inner = new ProtoReader(_buffer, _position, _position + length);
        _position += length;
        return inner;
    }

    /// <summary>A length-delimited field's payload.</summary>
    public ReadOnlySpan<byte> Payload()
    {
        int length = (int)ReadVarint();
        var payload = new ReadOnlySpan<byte>(_buffer, _position, length);
        _position += length;
        return payload;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _position += 8; break;
            case 2:
            {
                // not `_position += (int)ReadVarint()`: that reads _position BEFORE the varint moves it
                int length = (int)ReadVarint();
                _position += length;
                break;
            }
            case 5: _position += 4; break;
            default: throw new InvalidDataException($"adbc: unsupported protobuf wire type {wireType}");
        }
    }
}
