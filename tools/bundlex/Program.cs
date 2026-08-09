using System.IO.Compression;
using System.Text;

namespace BundleX;

/// <summary>
/// Extracts files from a .NET single-file bundle (the format produced by PublishSingleFile).
///
/// Layout: the apphost carries a 40 byte placeholder, 8 bytes bundle header offset followed by a
/// 32 byte signature. The manifest at that offset lists every embedded file with its offset, size
/// and, from bundle major version 6 on, its compressed size.
/// </summary>
internal static class Program
{
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: bundlex <single-file.exe> <output-dir> [name-filter ...]");
            Console.Error.WriteLine("       without a filter every entry is listed but nothing is written");

            return 2;
        }

        var inputPath = args[0];
        var outputDirectory = args[1];
        var filters = args.Skip(2).ToArray();

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"input not found: {inputPath}");

            return 2;
        }

        using var stream = File.OpenRead(inputPath);

        var headerOffset = FindHeaderOffset(stream);

        if (headerOffset <= 0 || headerOffset >= stream.Length)
        {
            Console.Error.WriteLine("no single-file bundle signature found, or the header offset is out of range");

            return 1;
        }

        Console.WriteLine($"bundle header offset: 0x{headerOffset:x}");

        stream.Position = headerOffset;

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var majorVersion = reader.ReadUInt32();
        var minorVersion = reader.ReadUInt32();
        var fileCount = reader.ReadInt32();
        var bundleId = reader.ReadString();

        Console.WriteLine($"bundle version:       {majorVersion}.{minorVersion}");
        Console.WriteLine($"bundle id:            {bundleId}");
        Console.WriteLine($"entries:              {fileCount}");

        if (majorVersion >= 2)
        {
            reader.ReadInt64();     // deps.json offset
            reader.ReadInt64();     // deps.json size
            reader.ReadInt64();     // runtimeconfig.json offset
            reader.ReadInt64();     // runtimeconfig.json size
            var flags = reader.ReadUInt64();

            Console.WriteLine($"flags:                0x{flags:x}");
        }

        if (fileCount is <= 0 or > 100_000)
        {
            Console.Error.WriteLine("implausible entry count, aborting");

            return 1;
        }

        var entries = new List<Entry>(fileCount);

        for (var i = 0; i < fileCount; i++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressedSize = majorVersion >= 6 ? reader.ReadInt64() : 0L;
            var type = reader.ReadByte();
            var relativePath = reader.ReadString();

            entries.Add(new Entry(relativePath, offset, size, compressedSize, type));
        }

        Console.WriteLine();

        Directory.CreateDirectory(outputDirectory);

        var written = 0;

        foreach (var entry in entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var matches = filters.Length != 0 &&
                          filters.Any(f => entry.RelativePath.Contains(f, StringComparison.OrdinalIgnoreCase));

            var marker = matches ? "->" : "  ";
            var packed = entry.CompressedSize != 0 ? $" packed {entry.CompressedSize,10:N0}" : string.Empty;

            Console.WriteLine($"{marker} {TypeName(entry.Type),-18} {entry.Size,12:N0}{packed}  {entry.RelativePath}");

            if (!matches)
            {
                continue;
            }

            var targetPath = Path.Combine(outputDirectory, Path.GetFileName(entry.RelativePath));

            Extract(stream, entry, targetPath);
            written++;
        }

        Console.WriteLine();
        Console.WriteLine($"{written} file(s) written to {outputDirectory}");

        return 0;
    }

    private static void Extract(FileStream stream, Entry entry, string targetPath)
    {
        stream.Position = entry.Offset;

        using var target = File.Create(targetPath);

        if (entry.CompressedSize != 0)
        {
            using var window = new SubStream(stream, entry.Offset, entry.CompressedSize);
            using var decompressor = new DeflateStream(window, CompressionMode.Decompress);

            decompressor.CopyTo(target);
        }
        else
        {
            var remaining = entry.Size;
            var buffer = new byte[81920];

            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                if (read <= 0)
                {
                    throw new IOException($"unexpected end of bundle while reading {entry.RelativePath}");
                }

                target.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    private static long FindHeaderOffset(Stream stream)
    {
        const int chunkSize = 1 << 20;

        var overlap = BundleSignature.Length + 8;
        var buffer = new byte[chunkSize + overlap];
        var basePosition = 0L;
        var carried = 0;

        stream.Position = 0;

        while (true)
        {
            var read = stream.Read(buffer, carried, chunkSize);

            if (read <= 0)
            {
                return -1;
            }

            var available = carried + read;
            var span = buffer.AsSpan(0, available);
            var searchFrom = 0;

            while (true)
            {
                var index = span[searchFrom..].IndexOf(BundleSignature);

                if (index < 0)
                {
                    break;
                }

                var signatureStart = searchFrom + index;

                if (signatureStart >= 8)
                {
                    var offset = BitConverter.ToInt64(span[(signatureStart - 8)..signatureStart]);

                    if (offset > 0)
                    {
                        return offset;
                    }
                }

                searchFrom = signatureStart + 1;

                if (searchFrom >= available)
                {
                    break;
                }
            }

            carried = Math.Min(overlap, available);
            buffer.AsSpan(available - carried, carried).CopyTo(buffer);
            basePosition += available - carried;
        }
    }

    private static string TypeName(byte type) => type switch
    {
        0 => "unknown",
        1 => "assembly",
        2 => "native-binary",
        3 => "deps.json",
        4 => "runtimeconfig.json",
        5 => "symbols",
        _ => $"type-{type}",
    };

    private sealed record Entry(string RelativePath, long Offset, long Size, long CompressedSize, byte Type);

    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
            _inner.Position = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;

            if (remaining <= 0)
            {
                return 0;
            }

            _inner.Position = _start + _position;

            var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
