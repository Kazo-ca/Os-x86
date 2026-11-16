using System.Buffers.Binary;

namespace OsX86.Tests.Programs;

/// <summary>Program headers built byte by byte for the tests (no binary in the repository).</summary>
internal static class TestPrograms
{
    /// <summary>Where the Portable Executable header starts in the images built here (what MinGW and MSVC write).</summary>
    public const int PeOffset = 0x80;

    /// <summary>A DOS MZ header alone: relocation table at 0x1C, no newer header.</summary>
    public static byte[] Dos(int length = 512)
    {
        byte[] bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18), 0x1C);
        return bytes;
    }

    /// <summary>
    /// A Portable Executable: DOS header pointing at 0x80, COFF header, optional header with the
    /// subsystem and the data directories (the CLR one set for <paramref name="dotNet"/>).
    /// </summary>
    public static byte[] Pe(bool pe32Plus = false, ushort subsystem = 3, bool dotNet = false, bool dll = false, ushort machine = 0x014c, int length = 1024, uint directories = 16)
    {
        byte[] bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), PeOffset);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(PeOffset));
        int coff = PeOffset + 4;
        int optional = coff + 20;
        Write16(bytes, coff, machine);
        Write16(bytes, coff + 18, (ushort)(dll ? 0x2102 : 0x0102));
        Write16(bytes, optional, (ushort)(pe32Plus ? 0x20b : 0x10b));
        Write16(bytes, optional + 68, subsystem);
        int count = optional + (pe32Plus ? 108 : 92);
        Write32(bytes, count, directories);
        if (dotNet)
        {
            int clr = count + 4 + (14 * 8);
            Write32(bytes, clr, 0x2008);
            Write32(bytes, clr + 4, 0x48);
        }

        return bytes;
    }

    /// <summary>A 16-bit Windows New Executable.</summary>
    public static byte[] Ne()
    {
        byte[] bytes = Pe();
        "NE\0\0"u8.CopyTo(bytes.AsSpan(PeOffset));
        return bytes;
    }

    /// <summary>An ELF header: 32 or 64 bits, little-endian, type and machine.</summary>
    public static byte[] Elf(int bits = 32, ushort type = 2, ushort machine = 3)
    {
        byte[] bytes = new byte[64];
        bytes[0] = 0x7F;
        "ELF"u8.CopyTo(bytes.AsSpan(1));
        bytes[4] = (byte)(bits == 64 ? 2 : 1);
        bytes[5] = 1;
        bytes[6] = 1;
        Write16(bytes, 16, type);
        Write16(bytes, 18, machine);
        return bytes;
    }

    /// <summary>The start of an OLE compound file, the container of a Windows Installer package.</summary>
    public static byte[] Ole()
    {
        byte[] bytes = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static void Write16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
