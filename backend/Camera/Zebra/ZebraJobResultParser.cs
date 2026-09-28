using System.Buffers.Binary;
using System.Text;

namespace MTCVision.Backend.Camera.Zebra;

public sealed record ZebraJobResult(byte[]? Image, string? Name, string? JobId, string? Timestamp);

/// <summary>
/// Reads the FlatBuffers "JobResult" message Aurora Focus broadcasts to its web HMI after every job
/// run. The slot numbers below are the vtable offsets from the HMI's own generated accessors
/// (JobResult: jobId 8, resultImage 14, name 16, timestamp 18; ResultImage: image 10) — the format
/// is Zebra's internal one, not a published API, so a firmware update could change it.
/// </summary>
public static class ZebraJobResultParser
{
    private const int JobIdSlot = 8;
    private const int ResultImageSlot = 14;
    private const int NameSlot = 16;
    private const int TimestampSlot = 18;
    private const int ImageBytesSlot = 10;

    public static ZebraJobResult Parse(ReadOnlySpan<byte> buffer)
    {
        var table = ReadInt32(buffer, 0);

        byte[]? image = null;
        var resultImage = FieldPosition(buffer, table, ResultImageSlot);
        if (resultImage != 0)
        {
            image = ReadBytes(buffer, resultImage + ReadInt32(buffer, resultImage), ImageBytesSlot);
        }

        return new ZebraJobResult(
            image,
            ReadString(buffer, table, NameSlot),
            ReadString(buffer, table, JobIdSlot),
            ReadString(buffer, table, TimestampSlot));
    }

    /// <summary>Absolute position of a field in a table, or 0 when the message leaves it out.</summary>
    private static int FieldPosition(ReadOnlySpan<byte> buffer, int table, int slot)
    {
        var vtable = table - ReadInt32(buffer, table);
        var vtableSize = ReadUInt16(buffer, vtable);
        if (slot >= vtableSize)
        {
            return 0;
        }

        var offset = ReadUInt16(buffer, vtable + slot);
        return offset == 0 ? 0 : table + offset;
    }

    private static string? ReadString(ReadOnlySpan<byte> buffer, int table, int slot)
    {
        var field = FieldPosition(buffer, table, slot);
        if (field == 0)
        {
            return null;
        }

        var start = field + ReadInt32(buffer, field);
        var length = ReadInt32(buffer, start);
        RequireRange(buffer, start + 4, length);
        return Encoding.UTF8.GetString(buffer.Slice(start + 4, length));
    }

    private static byte[]? ReadBytes(ReadOnlySpan<byte> buffer, int table, int slot)
    {
        var field = FieldPosition(buffer, table, slot);
        if (field == 0)
        {
            return null;
        }

        var start = field + ReadInt32(buffer, field);
        var length = ReadInt32(buffer, start);
        RequireRange(buffer, start + 4, length);
        return buffer.Slice(start + 4, length).ToArray();
    }

    private static int ReadInt32(ReadOnlySpan<byte> buffer, int position)
    {
        RequireRange(buffer, position, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(position, 4));
    }

    private static int ReadUInt16(ReadOnlySpan<byte> buffer, int position)
    {
        RequireRange(buffer, position, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position, 2));
    }

    private static void RequireRange(ReadOnlySpan<byte> buffer, int position, int length)
    {
        if (position < 0 || length < 0 || position > buffer.Length - length)
        {
            throw new InvalidDataException("Zebra sonuç mesajı beklenen biçimde değil.");
        }
    }
}
