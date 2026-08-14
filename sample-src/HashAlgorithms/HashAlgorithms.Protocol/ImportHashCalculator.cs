using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HashAlgorithms;

public sealed record ImportHashResult(string Digest, int ImportCount, IReadOnlyList<string> Imports);

public static class ImportHashCalculator
{
    private sealed record Section(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawOffset);

    public static bool TryCompute(string path, out ImportHashResult? result, out string? error)
    {
        result = null;
        error = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x40 || ReadUInt16(bytes, 0) != 0x5A4D) throw new InvalidDataException("文件缺少 DOS MZ 标头。");
            var peOffset = checked((int)ReadUInt32(bytes, 0x3C));
            Ensure(bytes, peOffset, 24);
            if (ReadUInt32(bytes, peOffset) != 0x00004550) throw new InvalidDataException("文件缺少 PE 标头。");

            var sectionCount = ReadUInt16(bytes, peOffset + 6);
            var optionalSize = ReadUInt16(bytes, peOffset + 20);
            var optionalOffset = peOffset + 24;
            Ensure(bytes, optionalOffset, optionalSize);
            var magic = ReadUInt16(bytes, optionalOffset);
            var isPe32Plus = magic switch
            {
                0x10B => false,
                0x20B => true,
                _ => throw new InvalidDataException($"不支持的 PE Optional Header：0x{magic:X}。"),
            };
            var directoryOffset = optionalOffset + (isPe32Plus ? 112 : 96);
            Ensure(bytes, directoryOffset + 8, 8);
            var importRva = ReadUInt32(bytes, directoryOffset + 8);
            var importSize = ReadUInt32(bytes, directoryOffset + 12);
            if (importRva == 0 || importSize == 0) throw new InvalidDataException("PE 文件没有导入表。");

            var sectionOffset = optionalOffset + optionalSize;
            var sections = new List<Section>(sectionCount);
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = sectionOffset + index * 40;
                Ensure(bytes, offset, 40);
                sections.Add(new Section(
                    ReadUInt32(bytes, offset + 12),
                    ReadUInt32(bytes, offset + 8),
                    ReadUInt32(bytes, offset + 16),
                    ReadUInt32(bytes, offset + 20)));
            }

            var imports = new List<string>();
            var descriptorOffset = RvaToOffset(importRva, sections, bytes.Length);
            for (var descriptorIndex = 0; descriptorIndex < 4096; descriptorIndex++, descriptorOffset += 20)
            {
                Ensure(bytes, descriptorOffset, 20);
                var originalThunkRva = ReadUInt32(bytes, descriptorOffset);
                var nameRva = ReadUInt32(bytes, descriptorOffset + 12);
                var firstThunkRva = ReadUInt32(bytes, descriptorOffset + 16);
                if (originalThunkRva == 0 && nameRva == 0 && firstThunkRva == 0) break;
                if (nameRva == 0) throw new InvalidDataException("导入描述符缺少 DLL 名称。");

                var library = NormalizeLibrary(ReadAscii(bytes, RvaToOffset(nameRva, sections, bytes.Length)));
                var thunkRva = originalThunkRva == 0 ? firstThunkRva : originalThunkRva;
                var thunkOffset = RvaToOffset(thunkRva, sections, bytes.Length);
                var pointerSize = isPe32Plus ? 8 : 4;
                for (var thunkIndex = 0; thunkIndex < 65536; thunkIndex++, thunkOffset += pointerSize)
                {
                    Ensure(bytes, thunkOffset, pointerSize);
                    var thunk = isPe32Plus ? ReadUInt64(bytes, thunkOffset) : ReadUInt32(bytes, thunkOffset);
                    if (thunk == 0) break;
                    var ordinalMask = isPe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                    string function;
                    if ((thunk & ordinalMask) != 0)
                    {
                        function = $"ord{thunk & 0xFFFF}";
                    }
                    else
                    {
                        var nameOffset = RvaToOffset(checked((uint)thunk), sections, bytes.Length);
                        Ensure(bytes, nameOffset, 2);
                        function = ReadAscii(bytes, nameOffset + 2).ToLowerInvariant();
                    }
                    imports.Add($"{library}.{function}");
                }
            }
            if (imports.Count == 0) throw new InvalidDataException("PE 导入表没有可计算的函数项。");
            var source = string.Join(',', imports);
            var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            result = new ImportHashResult(digest, imports.Count, imports);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string NormalizeLibrary(string value)
    {
        var lower = value.ToLowerInvariant();
        foreach (var extension in new[] { ".dll", ".sys", ".ocx" })
        {
            if (lower.EndsWith(extension, StringComparison.Ordinal)) return lower[..^extension.Length];
        }
        return lower;
    }

    private static int RvaToOffset(uint rva, IReadOnlyList<Section> sections, int fileLength)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || (ulong)rva >= (ulong)section.VirtualAddress + span) continue;
            var offset = checked((long)section.RawOffset + rva - section.VirtualAddress);
            if (offset < 0 || offset >= fileLength) break;
            return checked((int)offset);
        }
        if (rva < fileLength) return checked((int)rva);
        throw new InvalidDataException($"RVA 0x{rva:X} 无法映射到文件偏移。");
    }

    private static string ReadAscii(byte[] bytes, int offset)
    {
        Ensure(bytes, offset, 1);
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0 && end - offset < 4096) end++;
        if (end == bytes.Length || end - offset >= 4096) throw new InvalidDataException("PE 字符串未正常终止。");
        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        Ensure(bytes, offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        Ensure(bytes, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        Ensure(bytes, offset, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
    }

    private static void Ensure(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("PE 数据结构越出文件边界。");
    }
}
