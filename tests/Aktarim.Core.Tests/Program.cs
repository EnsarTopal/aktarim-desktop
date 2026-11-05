using Aktarim.Core;
using System.Security.Cryptography;

if (args.Length == 3 && args[0] == "--attempt-junction-swap")
{
    try { Directory.Move(args[1], args[1] + "-moved"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Environment.ExitCode = 17; return; }
    var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "/c", "mklink", "/J", args[1], args[2] }) command.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(command) ?? throw new Exception("Cannot launch junction attempt.");
    if (!process.WaitForExit(10000)) { process.Kill(true); Environment.ExitCode = 18; return; }
    Environment.ExitCode = process.ExitCode == 0 ? 0 : 18;
    return;
}

string root = Path.Combine(Path.GetTempPath(), "aktarim-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    const int chunkSize = 4 * 1024 * 1024;
    const int count = 24;
    byte[] Chunk(int i) { var bytes = new byte[chunkSize]; new Random(i).NextBytes(bytes); return bytes; }
    string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    string identity = OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root);
    string partial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aktarim", "Partial", Hash(System.Text.Encoding.UTF8.GetBytes(identity)));
    using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (int i = 0; i < count; i++) fullHash.AppendData(Chunk(i));
    var manifest = new Manifest(Guid.NewGuid(), "video.mov", (long)chunkSize * count, Convert.ToHexString(fullHash.GetHashAndReset()), chunkSize);
    var store = new ChunkTransfer(root);
    await Throws<IOException>(() => store.CreateAsync(manifest with { Id = Guid.NewGuid(), Length = 201L * 1024 * 1024 * 1024 }), "partial storage quota rejected");
    var expired = manifest with { Id = Guid.NewGuid(), Length = 1 };
    await store.CreateAsync(expired);
    File.SetLastWriteTimeUtc(Path.Combine(partial, expired.Id.ToString("N"), "manifest.json"), DateTime.UtcNow.AddDays(-8));
    await store.CleanExpiredAsync();
    Check(!File.Exists(Path.Combine(partial, expired.Id.ToString("N"), "manifest.json")), "startup removes expired partial");
    await store.CreateAsync(expired);
    var expiredDir = Path.Combine(partial, expired.Id.ToString("N"));
    await File.WriteAllTextAsync(Path.Combine(expiredDir, "stale.chunk"), "stale");
    File.SetLastWriteTimeUtc(Path.Combine(expiredDir, "manifest.json"), DateTime.UtcNow.AddDays(-8));
    await store.CreateAsync(expired);
    Check(!File.Exists(Path.Combine(expiredDir, "stale.chunk")), "reselection discards expired chunks");
    using (await store.CreateAsync(expired, true))
    {
        await File.WriteAllTextAsync(Path.Combine(expiredDir, "active.chunk"), "active");
        File.SetLastWriteTimeUtc(Path.Combine(expiredDir, "manifest.json"), DateTime.UtcNow.AddDays(-8));
        await new ChunkTransfer(root).CleanExpiredAsync();
        Check(File.Exists(Path.Combine(expiredDir, "active.chunk")), "active transfer survives cleanup");
    }
    await store.CleanExpiredAsync();
    Check(!File.Exists(Path.Combine(expiredDir, "manifest.json")), "inactive expired transfer removed");
    await store.CreateAsync(manifest);
    for (int i = 0; i < 8; i++) { var chunk = Chunk(i); await store.PutAsync(manifest.Id, i, chunk, Hash(chunk)); }
    store = new ChunkTransfer(root);
    var status = await store.StatusAsync(manifest.Id);
    Check(status.Count == 8, "verified chunks survive restart");
    Check(!File.Exists(Path.Combine(root, manifest.Name)), "interrupted file is not published");
    var first = Chunk(0);
    if (OperatingSystem.IsWindows())
    {
        var transferDir = Path.Combine(partial, manifest.Id.ToString("N"));
        var paused = new PausingStream(first);
        var put = store.PutAsync(manifest.Id, 0, paused, first.Length, Hash(first));
        await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { ThrowsSync<IOException>(() => Directory.Move(transferDir, transferDir + "-moved"), "transfer directory cannot be replaced during write"); }
        finally { paused.Release.TrySetResult(); await put; }
        using (SafeDirectories.Open(root))
            ThrowsSync<IOException>(() => Directory.Move(root, root + "-moved"), "destination directory cannot be replaced while guarded");
        var link = root + "-link";
        try
        {
            Directory.CreateSymbolicLink(link, root);
            ThrowsSync<IOException>(() => new ChunkTransfer(link), "destination symlink rejected");
            await Throws<IOException>(() => UsbCopy.CopyAsync(new MemoryStream(first), first.Length, Path.Combine(link, "nested"), "usb.mov"), "USB parent symlink rejected");
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        var junction = root + "-junction";
        try
        {
            var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", junction, root }) command.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(command) ?? throw new Exception("Cannot create junction fixture.");
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "junction fixture created");
            ThrowsSync<IOException>(() => new ChunkTransfer(junction), "destination junction rejected");
            await Throws<IOException>(() => UsbCopy.CopyAsync(new MemoryStream(first), first.Length, Path.Combine(junction, "nested"), "usb.mov"), "USB parent junction rejected");
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction); }
    }
    await store.PutAsync(manifest.Id, 0, first, Hash(first));
    var wrong = Chunk(25);
    await Throws<InvalidDataException>(() => store.PutAsync(manifest.Id, 8, new MemoryStream(wrong[..100]), wrong.Length, Hash(wrong)), "truncated streamed chunk rejected");
    await Throws<InvalidDataException>(() => store.PutAsync(manifest.Id, 8, new MemoryStream(wrong), wrong.Length - 1, Hash(wrong)), "wrong declared chunk size rejected");
    Check((await store.StatusAsync(manifest.Id)).Count == 8, "truncated streamed chunk not acknowledged");
    await Throws<InvalidOperationException>(() => store.PutAsync(manifest.Id, 0, wrong, Hash(wrong)), "conflicting retry");
    await Throws<InvalidDataException>(() => store.PutAsync(manifest.Id, 8, wrong, new string('0', 64)), "incorrect chunk hash");
    await Throws<InvalidOperationException>(() => store.CreateAsync(manifest with { Sha256 = new string('0', 64) }), "different source cannot resume");
    await Throws<ArgumentException>(() => store.CreateAsync(manifest with { Id = Guid.NewGuid(), Name = "../escape" }), "path traversal");
    for (int i = 8; i < count; i++) { var chunk = Chunk(i); await store.PutAsync(manifest.Id, i, chunk, Hash(chunk)); }
    var chunkFile = Path.Combine(partial, manifest.Id.ToString("N"), "0000000003.chunk");
    await File.WriteAllBytesAsync(chunkFile, wrong);
    Check((await store.StatusAsync(manifest.Id)).Count == count - 1, "corrupt chunk not acknowledged");
    await Throws<InvalidDataException>(() => store.CommitAsync(manifest.Id), "corrupt file not committed");
    Check(!File.Exists(Path.Combine(root, manifest.Name)), "corrupt file not published");
    var repair = Chunk(3);
    await store.PutAsync(manifest.Id, 3, repair, Hash(repair));
    Check((await store.StatusAsync(manifest.Id)).Count == count, "corrupt chunk replaced on retry");
    await File.WriteAllTextAsync(Path.Combine(root, manifest.Name), "existing");
    var oldAssembly = Path.Combine(partial, manifest.Id.ToString("N"), "assembled.tmp");
    await File.WriteAllTextAsync(oldAssembly, "preserve");
    string result = await store.CommitAsync(manifest.Id);
    Check(!Directory.EnumerateFiles(Path.Combine(partial, manifest.Id.ToString("N")), "*.chunk*").Any(), "published chunks removed");
    Check(await store.CommitAsync(manifest.Id) == result, "repeated commit returns same file");
    Check(await new ChunkTransfer(root).CompletedAsync(manifest.Id) == result, "completed commit survives restart");
    Check(await File.ReadAllTextAsync(oldAssembly) == "preserve", "existing assembly path preserved");
    Check(File.ReadAllText(Path.Combine(root, manifest.Name)) == "existing", "existing file preserved");
    await using (var output = File.OpenRead(result))
        Check(Convert.ToHexString(await SHA256.HashDataAsync(output)) == manifest.Sha256, "96 MiB resumed file hash");
    var small = System.Text.Encoding.UTF8.GetBytes("completion verification");
    var completion = new Manifest(Guid.NewGuid(), "completion.bin", small.Length, Hash(small), 1024);
    await store.CreateAsync(completion);
    await store.PutAsync(completion.Id, 0, small, Hash(small));
    var completed = await store.CommitAsync(completion.Id);
    File.Move(completed, completed + ".moved");
    Check(await store.CompletedAsync(completion.Id) == null, "moved completion invalidated");
    await store.PutAsync(completion.Id, 0, small, Hash(small));
    completed = await store.CommitAsync(completion.Id);
    await File.WriteAllTextAsync(completed, "changed");
    Check(await store.CompletedAsync(completion.Id) == null, "modified completion invalidated");
    await store.PutAsync(completion.Id, 0, small, Hash(small));
    var replacement = await store.CommitAsync(completion.Id);
    Check(replacement != completed && await File.ReadAllTextAsync(completed) == "changed", "modified destination preserved on retransfer");
    File.Delete(replacement);
    Check(await store.CompletedAsync(completion.Id) == null, "deleted completion invalidated");
    await store.PutAsync(completion.Id, 0, small, Hash(small));
    Check(Hash(await File.ReadAllBytesAsync(await store.CommitAsync(completion.Id))) == completion.Sha256, "deleted completion retransfer verified");
    if (OperatingSystem.IsWindows())
    {
        var outside = root + "-outside";
        Directory.CreateDirectory(outside);
        var externalFile = Path.Combine(outside, "race (1).bin");
        var existingFile = Path.Combine(root, "race.bin");
        await File.WriteAllTextAsync(externalFile, "outside sentinel");
        await File.WriteAllTextAsync(existingFile, "existing sentinel");
        var externalHash = Hash(await File.ReadAllBytesAsync(externalFile));
        var existingHash = Hash(await File.ReadAllBytesAsync(existingFile));
        var bytes = System.Text.Encoding.UTF8.GetBytes("verified transfer");
        var race = new Manifest(Guid.NewGuid(), "race.bin", bytes.Length, Hash(bytes), 1024);
        await store.CreateAsync(race);
        await store.PutAsync(race.Id, 0, bytes, Hash(bytes));
        int attempt = -1;
        store.BeforePublishForTest = () =>
        {
            string executable = Environment.ProcessPath ?? throw new Exception("Test executable unavailable.");
            var child = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                child.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--attempt-junction-swap", root, outside }) child.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(child) ?? throw new Exception("Cannot launch swap process.");
            if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Swap process timed out."); }
            attempt = process.ExitCode;
        };
        string? published = null;
        try
        {
            try { published = await store.CommitAsync(race.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            store.BeforePublishForTest = null;
            if (Directory.Exists(root + "-moved"))
            {
                if (Directory.Exists(root)) Directory.Delete(root);
                Directory.Move(root + "-moved", root);
            }
        }
        try
        {
            Check(attempt is 0 or 17, "separate process attempted junction replacement");
            Console.WriteLine($"Junction attempt exit: {attempt}; output: {published ?? "rejected"}; target entries: {string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName))}; outside entries: {string.Join(", ", Directory.GetFiles(outside).Select(Path.GetFileName))}");
            Check(Hash(await File.ReadAllBytesAsync(externalFile)) == externalHash, "outside sentinel hash unchanged");
            Check(Directory.GetFiles(outside).Length == 1, "no file created outside destination");
            Check(Hash(await File.ReadAllBytesAsync(existingFile)) == existingHash, "existing destination hash unchanged");
            if (published != null)
            {
                Check(Path.GetDirectoryName(published) == root, "published to selected directory");
                Check(Hash(await File.ReadAllBytesAsync(published)) == race.Sha256, "race output SHA-256 matches");
            }
            else Console.WriteLine("PASS race transfer safely rejected");
            int usbAttempt = -1;
            string? usbRace = null;
            try
            {
                usbRace = await UsbCopy.CopyAsync(new MemoryStream(bytes), bytes.Length, root, "race.bin", verifying: () =>
                {
                    string executable = Environment.ProcessPath ?? throw new Exception("Test executable unavailable.");
                    var child = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        child.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    foreach (var arg in new[] { "--attempt-junction-swap", root, outside }) child.ArgumentList.Add(arg);
                    using var process = System.Diagnostics.Process.Start(child) ?? throw new Exception("Cannot launch swap process.");
                    if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Swap process timed out."); }
                    usbAttempt = process.ExitCode;
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally
            {
                if (Directory.Exists(root + "-moved"))
                {
                    if (Directory.Exists(root)) Directory.Delete(root);
                    Directory.Move(root + "-moved", root);
                }
            }
            Check(usbAttempt is 0 or 17, "USB separate process attempted junction replacement");
            Check(Hash(await File.ReadAllBytesAsync(externalFile)) == externalHash, "USB outside sentinel unchanged");
            Check(Directory.GetFiles(outside).Length == 1, "USB created no file outside destination");
            Check(Hash(await File.ReadAllBytesAsync(existingFile)) == existingHash, "USB existing file unchanged during race");
            if (usbRace != null) Check(Hash(await File.ReadAllBytesAsync(usbRace)) == Hash(bytes), "USB race output hash matches");
            else Console.WriteLine("PASS USB race safely rejected");
        }
        finally { Directory.Delete(outside, true); }
    }
    await Throws<EndOfStreamException>(() => UsbCopy.CopyAsync(new MemoryStream(first), first.Length + 1, root, "usb.mov"), "USB interrupted source");
    Check(!File.Exists(Path.Combine(root, "usb.mov")) && !Directory.GetFiles(root, "*.partial").Any(), "USB incomplete file removed");
    var usbPath = await UsbCopy.CopyAsync(new MemoryStream(first), first.Length, root, "usb.mov");
    Check(Hash(await File.ReadAllBytesAsync(usbPath)) == Hash(first), "USB local read-back hash");
    var secondBytes = Chunk(1);
    var usbSecond = await UsbCopy.CopyAsync(new MemoryStream(secondBytes), secondBytes.Length, root, "usb.mov");
    Console.WriteLine($"USB first: {usbPath}; second: {usbSecond}; entries: {string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName))}");
    Check(usbPath != usbSecond && Hash(await File.ReadAllBytesAsync(usbPath)) == Hash(first), "USB existing file preserved");
    string usbParts = Path.Combine(root, "usb-parts"), usbResumeTarget = Path.Combine(root, "usb-resume");
    await Throws<EndOfStreamException>(() => UsbResumableCopy.CopyAsync(new MemoryStream(first[..(first.Length / 2)]), first.Length, usbResumeTarget, "resume.mov", usbParts, "source-a"), "USB resumable interruption");
    var usbPartial = Directory.GetFiles(usbParts, "*.partial").Single();
    Check(new FileInfo(usbPartial).Length == first.Length / 2 && !File.Exists(Path.Combine(usbResumeTarget, "resume.mov")), "USB partial saved privately without publication");
    bool checkedPrefix = false;
    var resumedUsb = await UsbResumableCopy.CopyAsync(new MemoryStream(first), first.Length, usbResumeTarget, "resume.mov", usbParts, "source-a", validatingPartial: checking => checkedPrefix |= checking);
    Check(checkedPrefix && Hash(await File.ReadAllBytesAsync(resumedUsb)) == Hash(first) && !File.Exists(usbPartial), "USB saved prefix checked and resumed copy verified");
    await Throws<EndOfStreamException>(() => UsbResumableCopy.CopyAsync(new MemoryStream(first[..(first.Length / 2)]), first.Length, usbResumeTarget, "changed.mov", usbParts, "source-b"), "USB changed partial fixture");
    usbPartial = Directory.GetFiles(usbParts, "*.partial").Single();
    await File.WriteAllBytesAsync(usbPartial, new byte[first.Length / 2]);
    await Throws<IOException>(() => UsbResumableCopy.CopyAsync(new MemoryStream(first), first.Length, usbResumeTarget, "changed.mov", usbParts, "source-b"), "USB changed prefix rejected");
    Check(!File.Exists(Path.Combine(usbResumeTarget, "changed.mov")) && new FileInfo(usbPartial).Length == 0, "USB changed partial reset without publication");
    var retriedUsb = await UsbResumableCopy.CopyAsync(new MemoryStream(first), first.Length, usbResumeTarget, "changed.mov", usbParts, "source-b");
    Check(Hash(await File.ReadAllBytesAsync(retriedUsb)) == Hash(first), "USB changed prefix safely restarted");
    var sentinel = Path.Combine(root, "partial-sentinel.bin");
    await File.WriteAllBytesAsync(sentinel, secondBytes);
    var linkKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("source-link\n" + Path.GetFullPath(usbResumeTarget) + "\nlink.mov\n" + first.Length)));
    var partialLink = Path.Combine(usbParts, linkKey + ".partial");
    File.CreateSymbolicLink(partialLink, sentinel);
    await Throws<IOException>(() => UsbResumableCopy.CopyAsync(new MemoryStream(first), first.Length, usbResumeTarget, "link.mov", usbParts, "source-link"), "USB partial symlink rejected");
    Check(Hash(await File.ReadAllBytesAsync(sentinel)) == Hash(secondBytes), "USB partial symlink target unchanged");
    File.Delete(partialLink);
    var history = new UsbHistory(Path.Combine(root, "history"));
    var backupRoot = Path.Combine(root, "backup");
    var backupBytes = System.Text.Encoding.UTF8.GetBytes("first item");
    var otherBytes = System.Text.Encoding.UTF8.GetBytes("other item");
    async Task<bool> Backup(string? key, byte[] data)
    {
        if (await history.IsBackedUpAsync(key, data.Length, backupRoot)) return false;
        string? hash = null;
        var path = await UsbCopy.CopyAsync(new MemoryStream(data), data.Length, backupRoot, "same.jpg", verifiedHash: value => hash = value);
        await history.AddAsync(key, data.Length, backupRoot, path, hash!);
        return true;
    }
    Check(await Backup("device-object-one-version-one", backupBytes), "new USB item copied");
    Check(await history.CheckAsync(null, backupBytes.Length, backupRoot) == "identity-unknown", "uncertain source diagnostic");
    Check(await history.CheckAsync("unknown-source", backupBytes.Length, backupRoot) == "no-record", "missing record diagnostic");
    Check(await history.CheckAsync("device-object-one-version-one", backupBytes.Length, backupRoot) == "verified", "verified copy diagnostic");
    history = new UsbHistory(Path.Combine(root, "history"));
    Check(!await Backup("device-object-one-version-one", backupBytes), "verified USB backup skipped after restart");
    Check(await Backup("device-object-two-version-one", otherBytes), "same name different USB identity copied");
    var firstBackup = Path.Combine(backupRoot, "same.jpg");
    var secondBackup = Path.Combine(backupRoot, "same (1).jpg");
    Check(Hash(await File.ReadAllBytesAsync(firstBackup)) == Hash(backupBytes) && Hash(await File.ReadAllBytesAsync(secondBackup)) == Hash(otherBytes), "same named backups preserved");
    File.Delete(firstBackup);
    Check(await history.CheckAsync("device-object-one-version-one", backupBytes.Length, backupRoot) == "copy-missing-or-changed", "deleted copy diagnostic");
    Check(await Backup("device-object-one-version-one", backupBytes) && Hash(await File.ReadAllBytesAsync(firstBackup)) == Hash(backupBytes), "deleted backup recreated");
    await File.WriteAllTextAsync(firstBackup, "changed");
    Check(await Backup("device-object-one-version-one", backupBytes) && await File.ReadAllTextAsync(firstBackup) == "changed", "modified backup preserved and recopied");
    var newest = (await history.EntriesAsync()).Last(x => x.SourceKey == "device-object-one-version-one").Path;
    File.Move(newest, newest + ".moved");
    Check(await Backup("device-object-one-version-one", backupBytes), "moved backup recopied");
    Check(await Backup(null, backupBytes) && await Backup(null, backupBytes), "uncertain USB identity never skipped");
    Check(!await history.IsBackedUpAsync("device-object-one-version-one", backupBytes.Length, Path.Combine(root, "other-target")), "different destination not skipped");
    Check((await history.EntriesAsync()).Count == 7, "only successful USB copies recorded");
    var contentHistory = new UsbHistory(Path.Combine(root, "content-history"));
    var contentPath = await UsbCopy.CopyAsync(new MemoryStream(backupBytes), backupBytes.Length, Path.Combine(root, "content-target"), "photo.jpg");
    var contentTarget = Path.GetDirectoryName(contentPath)!;
    await contentHistory.AddAsync(null, backupBytes.Length, contentTarget, contentPath, Hash(backupBytes), "stable-device-location");
    Check(await contentHistory.CheckAsync(null, backupBytes.Length, contentTarget, "stable-device-location") == "content-required", "unstable WPD ID requires source content verification");
    Check(await contentHistory.CheckAsync(null, backupBytes.Length, contentTarget, "stable-device-location", Hash(backupBytes)) == "verified", "source and destination hashes allow safe skip across connections");
    Check(await contentHistory.CheckAsync(null, backupBytes.Length, contentTarget, "stable-device-location", Hash(otherBytes)) == "source-changed", "same locator and size never skip different content");
    Check(await contentHistory.CheckAsync(null, backupBytes.Length, contentTarget, "different-location", Hash(backupBytes)) == "no-record", "same content at another source location is not guessed");
    File.Delete(contentPath);
    Check(await contentHistory.CheckAsync(null, backupBytes.Length, contentTarget, "stable-device-location", Hash(backupBytes)) == "copy-missing-or-changed", "source hash does not mask deleted destination");
    var brokenDir = Path.Combine(root, "broken-history");
    Directory.CreateDirectory(brokenDir);
    var brokenPath = Path.Combine(brokenDir, "history.json");
    const string broken = "{ invalid history";
    await File.WriteAllTextAsync(brokenPath, broken);
    var brokenHistory = new UsbHistory(brokenDir);
    bool rejected = false;
    try { await brokenHistory.IsBackedUpAsync("source", 1, backupRoot); } catch (UsbHistoryCorruptException) { rejected = true; }
    Check(rejected && await File.ReadAllTextAsync(brokenPath) == broken, "corrupt history is not trusted or changed without approval");
    string? archive = await brokenHistory.RecoverCorruptAsync();
    Check(archive != null && await File.ReadAllTextAsync(archive) == broken && (await brokenHistory.EntriesAsync()).Count == 0, "approved recovery preserves corrupt history and starts empty");
    Check(!await brokenHistory.IsBackedUpAsync("source", 1, backupRoot), "recovered empty history never skips files");
    Check(await brokenHistory.RecoverCorruptAsync() == null, "recovery does not overwrite valid history");
    await File.WriteAllTextAsync(brokenPath, "[null]");
    rejected = false;
    try { await brokenHistory.EntriesAsync(); } catch (UsbHistoryCorruptException) { rejected = true; }
    Check(rejected, "structurally corrupt history rejected");
    Console.WriteLine("All tests passed.");
}
finally
{
    if (Directory.Exists(root + "-moved") && !Directory.Exists(root)) Directory.Move(root + "-moved", root);
    var identity = OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root);
    var partial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aktarim", "Partial", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))));
    if (Directory.Exists(partial)) Directory.Delete(partial, true);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS " + name);
}
static async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("FAILED: " + name);
}
static void ThrowsSync<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("FAILED: " + name);
}

sealed class PausingStream(byte[] bytes) : MemoryStream(bytes, false)
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
