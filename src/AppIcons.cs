using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LocalTrafficLedger {
internal sealed class IconCacheStats {
    public bool initialized;
    public long requests, extractions, memoryHits, diskHits, memoryBytes;
    public int entries;
}

// Constructed only on the first authenticated image request, never by the collector.
internal sealed class AppIcons {
    private sealed class Entry {
        public string Version;
        public byte[] Png;
        public long CheckedAt, LastUse;
    }
    private const int MaximumPngBytes = 65536;
    private const int MaximumDiskFiles = 256;
    private const long MaximumDiskBytes = 8 * 1024 * 1024;
    private readonly object gate = new object();
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;
    private readonly Func<string, byte[]> extract;
    private readonly Func<long> clock;
    private readonly int maximumEntries;
    private readonly long maximumMemory;
    private long sequence, requests, extractions, memoryHits, diskHits, memoryBytes, lastPrune;
    private int entryCount;

    public AppIcons(string directory, Func<string, byte[]> extractor = null, int capacity = 128, long byteBudget = 2 * 1024 * 1024, Func<long> clock = null) {
        this.directory = directory;
        extract = extractor ?? ExtractNative;
        this.clock = clock ?? (() => Clock.Now);
        maximumEntries = Math.Max(1, capacity);
        maximumMemory = Math.Max(1, byteBudget);
    }

    internal static string LocalExecutable(string path) {
        try {
            // Reject UNC, device paths, URLs, alternate streams, relative paths and network/removable drives before touching the file.
            if (string.IsNullOrWhiteSpace(path) || path.Length >= 260 || path.Length < 4 ||
                !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return null;
            string full = Path.GetFullPath(path);
            if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase) || full.Substring(2).Contains(":")) return null;
            if (new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Fixed) return null;
            return full;
        } catch { return null; }
    }

    public byte[] Get(string path) {
        Interlocked.Increment(ref requests);
        string local = LocalExecutable(path);
        if (local == null) return null;
        lock (gate) {
            long now = clock();
            Entry old;
            if (entries.TryGetValue(local, out old) && now - old.CheckedAt < (old.Png == null ? 60 : 30)) {
                old.LastUse = ++sequence;
                Interlocked.Increment(ref memoryHits);
                return old.Png;
            }
            string version = FileVersion(local);
            if (old != null && old.Png != null && version != null && old.Version == version) {
                old.CheckedAt = now; old.LastUse = ++sequence;
                Interlocked.Increment(ref memoryHits);
                return old.Png;
            }
            byte[] png = null;
            if (version != null) {
                string cached = Path.Combine(directory, version + ".png");
                try {
                    var info = new FileInfo(cached);
                    if (info.Exists && info.Length <= MaximumPngBytes) {
                        var bytes = File.ReadAllBytes(cached);
                        if (ValidPng(bytes)) { png = bytes; Interlocked.Increment(ref diskHits); }
                    }
                } catch { }
                if (png == null) {
                    try {
                        Interlocked.Increment(ref extractions);
                        var bytes = extract(local);
                        if (ValidPng(bytes)) png = bytes;
                    } catch { }
                    if (png != null) try {
                        Directory.CreateDirectory(directory);
                        File.WriteAllBytes(cached, png);
                        if (now - lastPrune >= 30) { PruneDisk(); lastPrune = now; }
                    } catch { /* A read-only/full cache must not prevent the icon or collector from working. */ }
                }
            }
            if (old != null && old.Png != null) memoryBytes -= old.Png.Length;
            entries[local] = new Entry { Version = version, Png = png, CheckedAt = now, LastUse = ++sequence };
            if (png != null) memoryBytes += png.Length;
            while (entries.Count > maximumEntries || memoryBytes > maximumMemory) {
                var victim = entries.OrderBy(p => p.Value.LastUse).First();
                if (victim.Value.Png != null) memoryBytes -= victim.Value.Png.Length;
                entries.Remove(victim.Key);
            }
            entryCount = entries.Count;
            return png;
        }
    }

    private static string FileVersion(string local) {
        try {
            var file = new FileInfo(local);
            if (!file.Exists) return null;
            // Include a renderer version, canonical path and executable metadata; a program update invalidates the disk cache.
            string value = "png-v1|" + local.ToUpperInvariant() + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length;
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        } catch { return null; }
    }

    internal static bool ValidPng(byte[] data) {
        if (data == null || data.Length < 33 || data.Length > MaximumPngBytes) return false;
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        for (int i = 0; i < signature.Length; i++) if (data[i] != signature[i]) return false;
        if (Encoding.ASCII.GetString(data, 12, 4) != "IHDR") return false;
        uint width = BigEndian(data, 16), height = BigEndian(data, 20);
        return width > 0 && width <= 64 && height > 0 && height <= 64;
    }
    private static uint BigEndian(byte[] bytes, int start) { return ((uint)bytes[start] << 24) | ((uint)bytes[start + 1] << 16) | ((uint)bytes[start + 2] << 8) | bytes[start + 3]; }

    private void PruneDisk() {
        var files = new DirectoryInfo(directory).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long size = 0;
        for (int i = 0; i < files.Length; i++) {
            size += files[i].Length;
            if (i >= MaximumDiskFiles || size > MaximumDiskBytes) try { files[i].Delete(); } catch { }
        }
    }

    public IconCacheStats Stats() {
        return new IconCacheStats { initialized = true, requests = Interlocked.Read(ref requests), extractions = Interlocked.Read(ref extractions),
            memoryHits = Interlocked.Read(ref memoryHits), diskHits = Interlocked.Read(ref diskHits), memoryBytes = Interlocked.Read(ref memoryBytes), entries = Volatile.Read(ref entryCount) };
    }

    private static byte[] ExtractNative(string path) {
        int com = CoInitializeEx(IntPtr.Zero, 0);
        var info = new ShellFileInfo();
        try {
            if (SHGetFileInfoW(path, 0, out info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            using (var icon = Icon.FromHandle(info.Icon))
            using (var source = icon.ToBitmap())
            using (var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var stream = new MemoryStream()) {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, 48, 48));
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        } finally {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            if (com >= 0) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
}
