using System.Security.Cryptography;
using System.Text.Json;

namespace Aktarim.Core;

// A transfer is identified by a full-file hash and length, not by a browser file handle.
public sealed record Manifest(Guid Id, string Name, long Length, string Sha256, int ChunkSize);
public sealed record ChunkRecord(int Index, int Length, string Sha256);

public sealed class ChunkTransfer
{
    private readonly string root;
    private readonly string privateBase;
    private readonly string destination;
    internal Action? BeforePublishForTest { get; set; }
    private static readonly SemaphoreSlim gate = new(1, 1);
    private const long Quota = 200L * 1024 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    public ChunkTransfer(string destination)
    {
        this.destination = Path.GetFullPath(destination);
        using (SafeDirectories.Open(this.destination, create: true)) { }
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) throw new IOException("Local application data directory is unavailable.");
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(appData);
        using (SafeDirectories.Open(appData)) { }
        privateBase = Path.Combine(appData, "Aktarim", "Partial");
        var identity = OperatingSystem.IsWindows() ? this.destination.ToUpperInvariant() : this.destination;
        root = Path.Combine(privateBase, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))));
        using (SafeDirectories.Open(root, create: true, privateFrom: privateBase)) { }
    }
    private string Dir(Guid id) => Path.Combine(root, id.ToString("N"));
    private async Task<FileStream> LockStore(CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var guard = SafeDirectories.Open(privateBase, privateFrom: privateBase);
                return new FileStream(Path.Combine(privateBase, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 300) { await Task.Delay(100, cancellationToken); }
        }
    }
    private void CleanExpired()
    {
        using var baseGuard = SafeDirectories.Open(privateBase, privateFrom: privateBase);
        foreach (var store in Directory.EnumerateDirectories(privateBase))
        {
            using var storeGuard = SafeDirectories.Open(store, privateFrom: privateBase);
            foreach (var dir in Directory.EnumerateDirectories(store))
            {
            using var dirGuard = SafeDirectories.Open(dir, privateFrom: privateBase);
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) continue;
            if (!File.Exists(Path.Combine(dir, "completed.json")) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - Retention)
            {
                FileStream lease;
                try { lease = new FileStream(Path.Combine(dir, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { continue; }
                using (lease)
                    foreach (var entry in Directory.EnumerateFiles(dir).Where(x => Path.GetFileName(x) != ".active")) File.Delete(entry);
            }
            }
        }
    }
    private void CheckQuota(long additional)
    {
        long reserved = 0;
        using var baseGuard = SafeDirectories.Open(privateBase, privateFrom: privateBase);
        foreach (var store in Directory.EnumerateDirectories(privateBase))
        {
            using var storeGuard = SafeDirectories.Open(store, privateFrom: privateBase);
            foreach (var dir in Directory.EnumerateDirectories(store))
            {
            using var dirGuard = SafeDirectories.Open(dir, privateFrom: privateBase);
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) continue;
            var existing = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), json) ?? throw new IOException("Invalid quota manifest.");
            Validate(existing);
            if (File.Exists(Path.Combine(dir, "completed.json"))) continue;
            reserved = checked(reserved + existing.Length);
            }
        }
        if (additional > Quota - reserved) throw new IOException("Partial transfer quota exceeded.");
    }
    public async Task CleanExpiredAsync()
    {
        await gate.WaitAsync();
        try { using var processLock = await LockStore(); CleanExpired(); }
        finally { gate.Release(); }
    }
    private static bool HashValid(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void Validate(Manifest m)
    {
        if (m.Id == Guid.Empty || m.Length < 0 || m.ChunkSize <= 0 || m.ChunkSize > 16 * 1024 * 1024 || !HashValid(m.Sha256))
            throw new ArgumentException("Invalid manifest.");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name != Path.GetFileName(m.Name) || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            m.Name.EndsWith('.') || m.Name.EndsWith(' ') || m.Name.Length > 180)
            throw new ArgumentException("Invalid filename.");
        var stem = m.Name.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Reserved filename.");
    }
    private async Task<Manifest> ReadManifest(Guid id)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(Dir(id), "manifest.json")), json) ?? throw new IOException("Invalid manifest.");
        Validate(manifest);
        if (manifest.Id != id) throw new IOException("Transfer ID mismatch.");
        return manifest;
    }
    private static async Task WriteDurable(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes); await stream.FlushAsync(); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<FileStream?> CreateAsync(Manifest manifest, bool holdActive = false)
    {
        Validate(manifest);
        await gate.WaitAsync();
        try
        {
            using var processLock = await LockStore();
            var dir = Dir(manifest.Id);
            CleanExpired();
            if (!File.Exists(Path.Combine(dir, "manifest.json"))) CheckQuota(manifest.Length);
            using var guard = SafeDirectories.Open(dir, create: true, privateFrom: privateBase);
            var path = Path.Combine(dir, "manifest.json");
            if (File.Exists(path))
            {
                if (await ReadManifest(manifest.Id) != manifest) throw new InvalidOperationException("Transfer ID conflict.");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return holdActive ? new FileStream(Path.Combine(dir, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
            }
            await WriteDurable(path, JsonSerializer.SerializeToUtf8Bytes(manifest));
            return holdActive ? new FileStream(Path.Combine(dir, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
        }
        finally { gate.Release(); }
    }
    private static int ExpectedLength(Manifest m, int index)
    {
        if (index < 0 || (long)index * m.ChunkSize >= m.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return (int)Math.Min(m.ChunkSize, m.Length - (long)index * m.ChunkSize);
    }
    private static string ChunkPath(string dir, int index) => Path.Combine(dir, index.ToString("D10") + ".chunk");
    public async Task<string?> CompletedAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            using var processLock = await LockStore();
            using var guard = SafeDirectories.Open(Dir(id), privateFrom: privateBase);
            return await VerifiedCompletion(id, await ReadManifest(id));
        }
        finally { gate.Release(); }
    }
    private async Task<string?> VerifiedCompletion(Guid id, Manifest manifest)
    {
        var record = Path.Combine(Dir(id), "completed.json");
        if (!File.Exists(record)) return null;
        var path = await File.ReadAllTextAsync(record);
        try
        {
            using var targetGuard = SafeDirectories.Open(destination);
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Invalid completed path.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && file.Length == manifest.Length &&
                Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        CheckQuota(manifest.Length);
        File.Delete(record);
        return null;
    }
    public Task PutAsync(Guid id, int index, byte[] data, string sha256) => PutAsync(id, index, new MemoryStream(data, false), data.Length, sha256);
    public async Task PutAsync(Guid id, int index, Stream input, long length, string sha256, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var processLock = await LockStore(cancellationToken);
            using var guard = SafeDirectories.Open(Dir(id), privateFrom: privateBase);
            var m = await ReadManifest(id);
            if (length != ExpectedLength(m, index) || !HashValid(sha256))
                throw new InvalidDataException("Chunk length or hash mismatch.");
            var path = ChunkPath(Dir(id), index);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[65536]; long remaining = length;
                    while (remaining > 0)
                    {
                        int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) throw new InvalidDataException("Incomplete chunk.");
                        hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); remaining -= read;
                    }
                    if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Chunk length or hash mismatch.");
                    await output.FlushAsync(cancellationToken); output.Flush(true);
                }
            if (File.Exists(path))
            {
                var old = await File.ReadAllBytesAsync(path);
                var oldHash = Convert.ToHexString(SHA256.HashData(old));
                var journal = path + ".sha256";
                if (old.Length != ExpectedLength(m, index) || !File.Exists(journal) || oldHash != await File.ReadAllTextAsync(journal)) File.Move(temp, path, true);
                else if (!oldHash.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Chunk conflict.");
            }
            else File.Move(temp, path, false);
            await WriteDurable(path + ".sha256", System.Text.Encoding.ASCII.GetBytes(sha256.ToUpperInvariant()));
            File.SetLastWriteTimeUtc(Path.Combine(Dir(id), "manifest.json"), DateTime.UtcNow);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<ChunkRecord>> StatusAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            using var processLock = await LockStore();
            using var guard = SafeDirectories.Open(Dir(id), privateFrom: privateBase);
            var m = await ReadManifest(id);
            var result = new List<ChunkRecord>();
            foreach (var file in Directory.EnumerateFiles(Dir(id), "*.chunk"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var index)) continue;
                int expected;
                try { expected = ExpectedLength(m, index); } catch (ArgumentOutOfRangeException) { continue; }
                var bytes = await File.ReadAllBytesAsync(file);
                var hashFile = file + ".sha256";
                if (!File.Exists(hashFile)) continue;
                var actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (bytes.Length == expected && actual == await File.ReadAllTextAsync(hashFile)) result.Add(new(index, expected, actual));
            }
            return result.OrderBy(x => x.Index).ToArray();
        }
        finally { gate.Release(); }
    }
    public async Task<string> CommitAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            using var processLock = await LockStore();
            using var guard = SafeDirectories.Open(Dir(id), privateFrom: privateBase);
            var m = await ReadManifest(id);
            string dir = Dir(id);
            string completedPath = Path.Combine(dir, "completed.json");
            if (await VerifiedCompletion(id, m) is { } completed) return completed;
            using var targetGuard = SafeDirectories.Open(destination);
            string staged = Path.Combine(destination, ".aktarim-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using var output = SafeDirectories.CreateStaged(staged);
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    long remaining = m.Length;
                    for (int index = 0; remaining > 0; index++)
                    {
                        var bytes = await File.ReadAllBytesAsync(ChunkPath(dir, index));
                        var hashPath = ChunkPath(dir, index) + ".sha256";
                        if (bytes.Length != ExpectedLength(m, index) || !File.Exists(hashPath) ||
                            Convert.ToHexString(SHA256.HashData(bytes)) != await File.ReadAllTextAsync(hashPath))
                            throw new InvalidDataException("Chunk length or hash mismatch.");
                        hash.AppendData(bytes); await output.WriteAsync(bytes); remaining -= bytes.Length;
                    }
                    await output.FlushAsync(); output.Flush(true);
                    if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("File hash mismatch.");
                }
                output.Position = 0;
                if (!Convert.ToHexString(await SHA256.HashDataAsync(output)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Destination hash mismatch.");
                BeforePublishForTest?.Invoke();
                var name = Path.GetFileNameWithoutExtension(m.Name); var ext = Path.GetExtension(m.Name);
                string published;
                for (int n = 0; ; n++)
                {
                    var target = Path.Combine(destination, n == 0 ? m.Name : $"{name} ({n}){ext}");
                    if (File.Exists(target)) continue;
                    try { SafeDirectories.Publish(output, staged, target); published = target; break; }
                    catch (IOException) when (File.Exists(target)) { }
                }
                await WriteDurable(completedPath, System.Text.Encoding.UTF8.GetBytes(published));
                foreach (var file in Directory.EnumerateFiles(dir, "*.chunk*")) File.Delete(file);
                return published;
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        finally { gate.Release(); }
    }
}
