using System.Security.Cryptography;

namespace Aktarim.Core;

public static class UsbCopy
{
    // The source hash verifies the local write, not the original bytes on the device.
    public static async Task<string> CopyAsync(Stream source, long expectedLength, string destination, string fileName, CancellationToken cancellationToken = default, IProgress<long>? progress = null, Action? verifying = null)
    {
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.EndsWith('.') || fileName.EndsWith(' ') || fileName.Length > 180)
            throw new ArgumentException("Invalid filename.", nameof(fileName));
        string stemName = fileName.Split('.')[0];
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stemName, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Reserved filename.", nameof(fileName));
        destination = Path.GetFullPath(destination);
        using var guard = SafeDirectories.Open(destination, create: true);
        string temp = Path.Combine(destination, ".aktarim-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            long count = 0;
            byte[] buffer = new byte[1024 * 1024];
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = SafeDirectories.CreateStaged(temp))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    count = checked(count + read);
                    if (count > expectedLength) throw new InvalidDataException("Source exceeded expected length.");
                    sourceHash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(count);
                }
                if (count != expectedLength) throw new EndOfStreamException("Source ended before expected length.");
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
                byte[] expectedHash = sourceHash.GetHashAndReset();
                verifying?.Invoke();
                output.Position = 0;
                byte[] actualHash = await SHA256.HashDataAsync(output, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) throw new InvalidDataException("Local copy hash mismatch.");
                string stem = Path.GetFileNameWithoutExtension(fileName), extension = Path.GetExtension(fileName);
                for (int i = 0; ; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(destination, i == 0 ? fileName : $"{stem} ({i}){extension}");
                    try { SafeDirectories.Publish(output, temp, target); return target; }
                    catch (IOException) when (File.Exists(target)) { }
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
