using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

public static class RptEmbeddedIconInspector
{
    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr context);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNameProc callback, IntPtr context);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    private static byte[] ReadResource(IntPtr module, IntPtr name, int type)
    {
        IntPtr resource = FindResource(module, name, new IntPtr(type));
        if (resource == IntPtr.Zero)
            throw new InvalidDataException("Missing Win32 icon resource type " + type + ".");
        IntPtr handle = LoadResource(module, resource);
        IntPtr raw = handle == IntPtr.Zero ? IntPtr.Zero : LockResource(handle);
        int size = checked((int)SizeofResource(module, resource));
        if (raw == IntPtr.Zero || size < 6)
            throw new InvalidDataException("Malformed Win32 icon resource.");
        var bytes = new byte[size];
        Marshal.Copy(raw, bytes, 0, size);
        return bytes;
    }

    private static Dictionary<int, byte[]> ReadIco(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using (var reader = new BinaryReader(new MemoryStream(data)))
        {
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                throw new InvalidDataException("Source is not a Windows .ico file.");
            int count = reader.ReadUInt16();
            if (count < 1 || count > 32) throw new InvalidDataException("Invalid .ico frame count.");
            var frames = new Dictionary<int, byte[]>();
            for (int index = 0; index < count; index++)
            {
                int width = reader.ReadByte(); if (width == 0) width = 256;
                int height = reader.ReadByte(); if (height == 0) height = 256;
                reader.ReadByte(); reader.ReadByte(); // color count and reserved
                reader.ReadUInt16(); reader.ReadUInt16(); // planes and bpp
                int length = checked((int)reader.ReadUInt32());
                int offset = checked((int)reader.ReadUInt32());
                if (width != height || length < 24 || offset < 0 || (long)offset + length > data.Length)
                    throw new InvalidDataException("Malformed .ico frame at index " + index + ".");
                var bytes = new byte[length];
                Array.Copy(data, offset, bytes, 0, length);
                // PNG IHDR confirms that the embedded picture is really the advertised size.
                if (!bytes.Take(8).SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}))
                    throw new InvalidDataException("Expected PNG-compressed ICO frames.");
                int pngWidth = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int pngHeight = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (pngWidth != width || pngHeight != height || frames.ContainsKey(width))
                    throw new InvalidDataException("Duplicate or inconsistent icon image size " + width + ".");
                frames.Add(width, bytes);
            }
            return frames;
        }
    }

    private static Dictionary<int, byte[]> ReadExeGroup(IntPtr module, IntPtr groupName)
    {
        byte[] group = ReadResource(module, groupName, 14); // RT_GROUP_ICON
        if (BitConverter.ToUInt16(group, 0) != 0 || BitConverter.ToUInt16(group, 2) != 1)
            throw new InvalidDataException("Malformed EXE group icon directory.");
        int count = BitConverter.ToUInt16(group, 4);
        if (count < 1 || group.Length != 6 + count * 14)
            throw new InvalidDataException("Malformed EXE icon group entries.");
        var frames = new Dictionary<int, byte[]>();
        for (int index = 0; index < count; index++)
        {
            int offset = 6 + index * 14;
            int width = group[offset]; if (width == 0) width = 256;
            int height = group[offset + 1]; if (height == 0) height = 256;
            int imageLength = checked((int)BitConverter.ToUInt32(group, offset + 8));
            int id = BitConverter.ToUInt16(group, offset + 12);
            byte[] raw = ReadResource(module, new IntPtr(id), 3); // RT_ICON
            if (width != height || raw.Length != imageLength || frames.ContainsKey(width))
                throw new InvalidDataException("Malformed EXE icon resource " + id + ".");
            frames.Add(width, raw);
        }
        return frames;
    }

    public static string Verify(string executablePath, string expectedIcoPath)
    {
        var expected = ReadIco(expectedIcoPath);
        int[] sizes = {16, 20, 24, 32, 40, 48, 64, 96, 128, 256};
        if (expected.Count != sizes.Length || sizes.Any(size => !expected.ContainsKey(size)))
            throw new InvalidDataException("Generated .ico lacks one or more required Windows DPI sizes.");

        IntPtr module = LoadLibraryEx(executablePath, IntPtr.Zero,
            LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            throw new InvalidDataException("Could not load EXE icon resources (Win32 error " +
                Marshal.GetLastWin32Error() + ").");
        try
        {
            var groupNames = new List<IntPtr>();
            EnumNameProc callback = (handle, type, name, context) => {
                groupNames.Add(name);
                return true;
            };
            if (!EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero) || groupNames.Count == 0)
                throw new InvalidDataException("The published EXE has no RT_GROUP_ICON resource.");

            var errors = new List<string>();
            foreach (IntPtr name in groupNames)
            {
                var embedded = ReadExeGroup(module, name);
                if (embedded.Count != expected.Count || sizes.Any(size => !embedded.ContainsKey(size)))
                {
                    errors.Add("EXE icon group has incomplete sizes: " + string.Join(",", embedded.Keys.OrderBy(x => x)));
                    continue;
                }
                var mismatches = sizes.Where(size => !expected[size].SequenceEqual(embedded[size])).ToArray();
                if (mismatches.Length == 0)
                    return "PASS: published EXE embeds all 10 exact brand icon frames (" +
                        string.Join(", ", sizes) + " px).";
                errors.Add("EXE icon group differs from generated artwork at " +
                    string.Join(", ", mismatches) + " px.");
            }
            throw new InvalidDataException(string.Join(" ", errors));
        }
        finally { FreeLibrary(module); }
    }
}
