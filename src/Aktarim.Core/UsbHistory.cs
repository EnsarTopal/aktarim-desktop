using System.Security.Cryptography;
using System.Text.Json;

namespace Aktarim.Core;

public sealed record UsbHistoryEntry(string? SourceKey, long Length, string Destination, string Path, string Sha256, DateTime CompletedUtc);

public sealed class UsbHistory
{
    private readonly string directory;
    private readonly string file;
    private static readonly SemaphoreSlim gate = new(1, 1);
    private List<UsbHistoryEntry>? cached;
    private DateTime cachedWrite;
    private long cachedLength;

    public UsbHistory(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aktarim", "UsbHistory");
        file = Path.Combine(this.directory, "history.json");
    }

    private async Task<FileStream> LockAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 300) { await Task.Delay(100); }
        }
    }

    private async Task<List<UsbHistoryEntry>> ReadAsync()
    {
        var info = new FileInfo(file);
        if (!info.Exists) { cached = []; cachedWrite = default; cachedLength = 0; return cached; }
        if (cached != null && cachedWrite == info.LastWriteTimeUtc && cachedLength == info.Length) return cached;
        cached = JsonSerializer.Deserialize<List<UsbHistoryEntry>>(await File.ReadAllTextAsync(file)) ?? throw new IOException("Invalid USB history.");
        cachedWrite = info.LastWriteTimeUtc; cachedLength = info.Length;
        return cached;
    }

    public async Task<IReadOnlyList<UsbHistoryEntry>> EntriesAsync()
    {
        await gate.WaitAsync();
        try { using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory); using var processLock = await LockAsync(); return (await ReadAsync()).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<bool> IsBackedUpAsync(string? sourceKey, long length, string destination)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;
        destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        await gate.WaitAsync();
        try
        {
            using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory);
            using var processLock = await LockAsync();
            foreach (var entry in (await ReadAsync()).Where(x => x.SourceKey == sourceKey && x.Length == length && string.Equals(x.Destination, destination, comparison)).Reverse())
            {
                try
                {
                    string path = Path.GetFullPath(entry.Path);
                    string relative = Path.GetRelativePath(destination, path);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)) continue;
                    using var targetGuard = SafeDirectories.Open(Path.GetDirectoryName(path)!);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && input.Length == length &&
                        Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            return false;
        }
        finally { gate.Release(); }
    }

    public async Task AddAsync(string? sourceKey, long length, string destination, string path, string sha256)
    {
        await gate.WaitAsync();
        try
        {
            using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory);
            using var processLock = await LockAsync();
            var entries = new List<UsbHistoryEntry>(await ReadAsync());
            entries.Add(new(sourceKey, length, Path.GetFullPath(destination), Path.GetFullPath(path), sha256, DateTime.UtcNow));
            string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                { await JsonSerializer.SerializeAsync(output, entries); await output.FlushAsync(); output.Flush(true); }
                File.Move(temp, file, true);
                var info = new FileInfo(file); cached = entries; cachedWrite = info.LastWriteTimeUtc; cachedLength = info.Length;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }
}
