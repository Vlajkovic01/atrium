using System.Buffers.Binary;
using System.Text;
using NetVips;

namespace Atrium.Media.Tests;

/// <summary>
/// Sample media made by the tests themselves, so the repository carries no
/// binary samples of unclear origin or license.
/// </summary>
public static class Samples
{
    /// <summary>
    /// A JPEG of the given size with an EXIF block holding the capture time,
    /// its offset, the orientation, the camera and a GPS position, written
    /// byte by byte as a camera would.
    /// </summary>
    public static byte[] JpegWithExif(
        int width, int height, string dateTimeOriginal = "2024:05:01 18:30:00", string? offset = "+02:00", int orientation = 1)
    {
        using var image = Image.Black(width, height, bands: 3) + new double[] { 40, 120, 200 };
        var plain = image.Cast(Enums.BandFormat.Uchar).JpegsaveBuffer(q: 80, keep: Enums.ForeignKeep.None);
        var exif = Exif(dateTimeOriginal, offset, orientation);

        // SOI, then APP1 "Exif", then the rest of the plain JPEG.
        var app1Length = 2 + 6 + exif.Length;
        var result = new byte[plain.Length + 2 + app1Length];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), (ushort)app1Length);
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(result, 6);
        exif.CopyTo(result, 12);
        plain.AsSpan(2).CopyTo(result.AsSpan(12 + exif.Length));
        return result;
    }

    /// <summary>A PNG without any metadata, like a screenshot.</summary>
    public static byte[] Png(int width, int height)
    {
        using var image = Image.Black(width, height, bands: 3) + new double[] { 200, 60, 60 };
        return image.Cast(Enums.BandFormat.Uchar).PngsaveBuffer();
    }

    /// <summary>A two-second H.264 MP4 made with FFmpeg, with a creation time.</summary>
    public static async Task<string> Mp4Async(string directory, string creationTime = "2023-07-04T09:15:00Z")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        await ToolRunner.RunAsync(
            "ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=320x240:rate=10",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-metadata", $"creation_time={creationTime}", "-y", path],
            TimeSpan.FromMinutes(1),
            background: false,
            maxOutput: 1024,
            CancellationToken.None);
        return path;
    }

    // A little-endian TIFF structure: IFD0 with Make, Model, Orientation and
    // pointers to the EXIF and GPS IFDs.
    private static byte[] Exif(string dateTimeOriginal, string? offset, int orientation)
    {
        var data = new List<byte>();
        var make = Ascii("TestCam");
        var model = Ascii("Model 1");
        var date = Ascii(dateTimeOriginal);
        var off = offset is null ? null : Ascii(offset);

        // Layout: header(8) | IFD0 | EXIF IFD | GPS IFD | values
        const int ifd0Entries = 5;
        var exifEntries = off is null ? 1 : 2;
        const int gpsEntries = 2;
        var ifd0Offset = 8;
        var exifOffset = ifd0Offset + 2 + (ifd0Entries * 12) + 4;
        var gpsOffset = exifOffset + 2 + (exifEntries * 12) + 4;
        var valuesOffset = gpsOffset + 2 + (gpsEntries * 12) + 4;

        var values = new List<byte>();
        int Value(byte[] bytes)
        {
            var at = valuesOffset + values.Count;
            values.AddRange(bytes);
            if (values.Count % 2 == 1)
            {
                values.Add(0);
            }

            return at;
        }

        var makeAt = Value(make);
        var modelAt = Value(model);
        var dateAt = Value(date);
        var offAt = off is null ? 0 : Value(off);

        // GPS latitude 44° 49' 0", the kind of thing that must never leave the file.
        var latitude = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(0), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(8), 49);
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(latitude.AsSpan(20), 1);
        var latitudeAt = Value(latitude);

        data.AddRange("II"u8.ToArray());
        data.AddRange(U16(42));
        data.AddRange(U32((uint)ifd0Offset));

        data.AddRange(U16(ifd0Entries));
        data.AddRange(Entry(0x010F, 2, (uint)make.Length, (uint)makeAt));
        data.AddRange(Entry(0x0110, 2, (uint)model.Length, (uint)modelAt));
        data.AddRange(Entry(0x0112, 3, 1, (uint)orientation));
        data.AddRange(Entry(0x8769, 4, 1, (uint)exifOffset));
        data.AddRange(Entry(0x8825, 4, 1, (uint)gpsOffset));
        data.AddRange(U32(0));

        data.AddRange(U16((ushort)exifEntries));
        data.AddRange(Entry(0x9003, 2, (uint)date.Length, (uint)dateAt));
        if (off is not null)
        {
            data.AddRange(Entry(0x9011, 2, (uint)off.Length, (uint)offAt));
        }

        data.AddRange(U32(0));

        data.AddRange(U16(gpsEntries));
        data.AddRange(Entry(0x0001, 2, 2, 'N'));
        data.AddRange(Entry(0x0002, 5, 3, (uint)latitudeAt));
        data.AddRange(U32(0));

        data.AddRange(values);
        return [.. data];
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Entry(ushort tag, ushort type, uint count, uint value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return bytes;
    }
}
