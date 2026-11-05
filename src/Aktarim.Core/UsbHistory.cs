using System.Security.Cryptography;
using System.Text.Json;

namespace Aktarim.Core;

public sealed record UsbHistoryEntry(string? SourceKey, long Length, string Destination, string Path, string Sha256, DateTime CompletedUtc, string? SourceLocator = null);
public sealed class UsbHistoryCorruptException : IOException
{
    public UsbHistoryCorruptException(Exception? inner = null) : base("Invalid USB history.", inner) { }
}

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
        cached = null;
        try
        {
            var entries = JsonSerializer.Deserialize<List<UsbHistoryEntry>>(await File.ReadAllTextAsync(file));
            if (entries == null || entries.Any(x => x == null || x.Length < 0 || string.IsNullOrWhiteSpace(x.Destination) || string.IsNullOrWhiteSpace(x.Path) || x.Sha256 == null || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit))) throw new UsbHistoryCorruptException();
            cached = entries;
        }
        catch (JsonException e) { cached = null; throw new UsbHistoryCorruptException(e); }
        cachedWrite = info.LastWriteTimeUtc; cachedLength = info.Length;
        return cached;
    }

    public async Task<string?> RecoverCorruptAsync()
    {
        await gate.WaitAsync();
        try
        {
            using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory);
            using var processLock = await LockAsync();
            cached = null;
            try { await ReadAsync(); return null; }
            catch (UsbHistoryCorruptException) { }
            string archive = Path.Combine(directory, "history.corrupt-" + Guid.NewGuid().ToString("N") + ".json");
            File.Move(file, archive, false);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(output, Array.Empty<UsbHistoryEntry>());
            await output.FlushAsync(); output.Flush(true);
            cached = null;
            return archive;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<UsbHistoryEntry>> EntriesAsync()
    {
        await gate.WaitAsync();
        try { using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory); using var processLock = await LockAsync(); return (await ReadAsync()).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<bool> IsBackedUpAsync(string? sourceKey, long length, string destination)
        => await CheckAsync(sourceKey, length, destination) == "verified";

    public async Task<string> CheckAsync(string? sourceKey, long length, string destination, string? sourceLocator = null, string? sourceSha256 = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) sourceKey = null;
        if (string.IsNullOrWhiteSpace(sourceKey) && string.IsNullOrWhiteSpace(sourceLocator)) return "identity-unknown";
        destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory);
            using var processLock = await LockAsync();
            var matching = (await ReadAsync()).Where(x => (sourceKey != null ? x.SourceKey == sourceKey : x.SourceLocator == sourceLocator) && x.Length == length && string.Equals(x.Destination, destination, comparison)).Reverse().ToArray();
            if (matching.Length == 0) return "no-record";
            if (sourceKey == null && sourceSha256 == null) return "content-required";
            if (sourceKey == null) matching = matching.Where(x => string.Equals(x.Sha256, sourceSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0) return "source-changed";
            foreach (var entry in matching)
            {
                try
                {
                    string path = Path.GetFullPath(entry.Path);
                    string relative = Path.GetRelativePath(destination, path);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)) continue;
                    using var targetGuard = SafeDirectories.Open(Path.GetDirectoryName(path)!);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && input.Length == length &&
                        Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return "verified";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            return "copy-missing-or-changed";
        }
        finally { gate.Release(); }
    }

    public async Task AddAsync(string? sourceKey, long length, string destination, string path, string sha256, string? sourceLocator = null)
    {
        await gate.WaitAsync();
        try
        {
            using var guard = SafeDirectories.Open(directory, create: true, privateFrom: directory);
            using var processLock = await LockAsync();
            var entries = new List<UsbHistoryEntry>(await ReadAsync());
            entries.Add(new(sourceKey, length, Path.GetFullPath(destination), Path.GetFullPath(path), sha256, DateTime.UtcNow, sourceLocator));
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
