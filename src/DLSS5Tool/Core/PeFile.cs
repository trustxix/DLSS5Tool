using System.Text;

namespace DLSS5Tool.Core;

/// <summary>
/// Minimal PE reader: machine type, import and delay-import DLL names, export names, subsystem.
/// Reads only the headers and the few tables it needs; never maps the whole file.
/// </summary>
public sealed class PeFile
{
    public string Path { get; private init; } = string.Empty;
    public ushort Machine { get; private init; }
    public bool Is64Bit => Machine == 0x8664 || Machine == 0xAA64;
    public string Architecture => Machine switch { 0x014C => "x86", 0x8664 => "x64", 0xAA64 => "ARM64", 0x01C4 => "ARM", _ => $"0x{Machine:X4}" };
    public ushort Subsystem { get; private init; }
    public IReadOnlyList<string> Imports { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> DelayImports { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> Exports { get; private init; } = Array.Empty<string>();

    public IEnumerable<string> AllImports => Imports.Concat(DelayImports);

    private sealed record Section(uint VirtualAddress, uint Span, uint RawPointer);

    public static PeFile? TryRead(string path, bool readExports = false)
    {
        try
        {
            return Read(path, readExports);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static PeFile? Read(string path, bool readExports)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var br = new BinaryReader(fs, Encoding.ASCII, false);
        if (fs.Length < 0x40) return null;
        if (br.ReadUInt16() != 0x5A4D) return null;
        fs.Position = 0x3C;
        var peOff = br.ReadInt32();
        if (peOff <= 0 || peOff + 24 >= fs.Length) return null;
        fs.Position = peOff;
        if (br.ReadUInt32() != 0x00004550) return null;

        var machine = br.ReadUInt16();
        var nSections = br.ReadUInt16();
        br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
        var optSize = br.ReadUInt16();
        br.ReadUInt16();

        var optOff = (long)peOff + 24;
        if (optSize < 96 || optOff + optSize > fs.Length) return new PeFile { Path = path, Machine = machine };
        fs.Position = optOff;
        var magic = br.ReadUInt16();
        long dirOff = magic switch { 0x20B => optOff + 112, 0x10B => optOff + 96, _ => -1 };
        if (dirOff < 0) return new PeFile { Path = path, Machine = machine };

        fs.Position = optOff + 68;
        var subsystem = br.ReadUInt16();

        var sections = new List<Section>();
        var secOff = optOff + optSize;
        for (var i = 0; i < nSections; i++)
        {
            var p = secOff + i * 40L;
            if (p + 40 > fs.Length) break;
            fs.Position = p + 8;
            var vsize = br.ReadUInt32();
            var vaddr = br.ReadUInt32();
            var rsize = br.ReadUInt32();
            var raw = br.ReadUInt32();
            sections.Add(new Section(vaddr, Math.Max(vsize, rsize), raw));
        }

        long RvaToOffset(uint rva)
        {
            foreach (var s in sections)
            {
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.Span)
                {
                    var o = (long)s.RawPointer + (rva - s.VirtualAddress);
                    return o >= 0 && o < fs.Length ? o : -1;
                }
            }
            return -1;
        }

        string ReadAsciiZ(long off, int max)
        {
            fs.Position = off;
            var sb = new StringBuilder();
            for (var k = 0; k < max; k++)
            {
                var b = fs.ReadByte();
                if (b <= 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        (uint rva, uint size) ReadDirectory(int index)
        {
            var p = dirOff + index * 8L;
            if (p + 8 > fs.Length) return (0, 0);
            fs.Position = p;
            return (br.ReadUInt32(), br.ReadUInt32());
        }

        var imports = new List<string>();
        var (impRva, _) = ReadDirectory(1);
        if (impRva != 0)
        {
            var io = RvaToOffset(impRva);
            for (var i = 0; io >= 0 && i < 1024; i++)
            {
                var p = io + i * 20L;
                if (p + 20 > fs.Length) break;
                fs.Position = p + 12;
                var nameRva = br.ReadUInt32();
                if (nameRva == 0) break;
                var no = RvaToOffset(nameRva);
                if (no < 0) continue;
                var name = ReadAsciiZ(no, 260);
                if (name.Length > 0) imports.Add(name.ToLowerInvariant());
            }
        }

        var delay = new List<string>();
        var (delRva, _) = ReadDirectory(13);
        if (delRva != 0)
        {
            var dOff = RvaToOffset(delRva);
            for (var i = 0; dOff >= 0 && i < 1024; i++)
            {
                var p = dOff + i * 32L;
                if (p + 32 > fs.Length) break;
                fs.Position = p;
                var attrs = br.ReadUInt32();
                var nameRva = br.ReadUInt32();
                if (nameRva == 0) break;
                // Old-style (attrs bit 0 clear) descriptors hold VAs instead of RVAs; convert when an image base is known.
                if ((attrs & 1) == 0)
                {
                    fs.Position = optOff + (magic == 0x20B ? 24 : 28);
                    var imageBase = magic == 0x20B ? br.ReadUInt64() : br.ReadUInt32();
                    if (nameRva >= imageBase) nameRva -= (uint)imageBase;
                }
                var no = RvaToOffset(nameRva);
                if (no < 0) continue;
                var name = ReadAsciiZ(no, 260);
                if (name.Length > 0) delay.Add(name.ToLowerInvariant());
            }
        }

        var exports = new List<string>();
        if (readExports)
        {
            var (expRva, _) = ReadDirectory(0);
            if (expRva != 0)
            {
                var eo = RvaToOffset(expRva);
                if (eo >= 0 && eo + 40 <= fs.Length)
                {
                    fs.Position = eo + 24;
                    var nNames = br.ReadUInt32();
                    br.ReadUInt32();
                    var namesRva = br.ReadUInt32();
                    var no = RvaToOffset(namesRva);
                    if (no >= 0 && nNames > 0)
                    {
                        nNames = Math.Min(nNames, 16384);
                        for (uint i = 0; i < nNames; i++)
                        {
                            var p = no + i * 4L;
                            if (p + 4 > fs.Length) break;
                            fs.Position = p;
                            var so = RvaToOffset(br.ReadUInt32());
                            if (so < 0) continue;
                            var n = ReadAsciiZ(so, 512);
                            if (n.Length > 0) exports.Add(n);
                        }
                    }
                }
            }
        }

        return new PeFile
        {
            Path = path,
            Machine = machine,
            Subsystem = subsystem,
            Imports = imports,
            DelayImports = delay,
            Exports = exports,
        };
    }

    /// <summary>
    /// Counts occurrences of each known graphics-API module name inside a binary (ASCII and UTF-16LE, case-insensitive).
    /// Used when the import table says nothing because the engine loads its renderer with LoadLibrary.
    /// </summary>
    public static Dictionary<string, int> ScanApiStrings(string path, long maxBytes = 256L * 1024 * 1024)
    {
        var hits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > maxBytes) return hits;
        var bytes = File.ReadAllBytes(path);
        foreach (var (module, _) in GraphicsApiInfo.ModuleMap)
        {
            var c = CountIgnoreCase(bytes, Encoding.ASCII.GetBytes(module)) + CountIgnoreCase(bytes, Encoding.Unicode.GetBytes(module));
            if (c > 0) hits[module] = c;
        }
        return hits;
    }

    private static int CountIgnoreCase(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return 0;
        var count = 0;
        var first = needle[0];
        var firstUpper = (byte)char.ToUpperInvariant((char)first);
        var last = hay.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var b = hay[i];
            if (b != first && b != firstUpper) continue;
            var ok = true;
            for (var j = 1; j < needle.Length; j++)
            {
                var h = hay[i + j];
                var n = needle[j];
                if (h == n) continue;
                if (n >= 'a' && n <= 'z' && h == n - 32) continue;
                ok = false;
                break;
            }
            if (!ok) continue;
            // Reject matches that are the tail of a longer identifier (e.g. "xd3d9.dll").
            if (i > 0)
            {
                var prev = hay[i - 1];
                var stride = needle.Length > 1 && needle[1] == 0 ? 2 : 1;
                if (stride == 1 && (char.IsLetterOrDigit((char)prev) || prev == '_' || prev == '.')) continue;
            }
            count++;
            i += needle.Length - 1;
        }
        return count;
    }
}
