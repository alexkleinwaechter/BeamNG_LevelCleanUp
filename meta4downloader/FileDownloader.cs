using System.Security.Cryptography;

namespace meta4downloader;

/// <summary>
/// Downloads files and verifies their integrity
/// </summary>
public class FileDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _targetDirectory;

    public FileDownloader(string targetDirectory)
    {
        _targetDirectory = targetDirectory;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public async Task<bool> DownloadFileAsync(Meta4File file, IProgress<DownloadProgress>? progress = null)
    {
        var targetPath = Path.Combine(_targetDirectory, file.Name);

        if (file.Urls.Count == 0)
        {
            progress?.Report(new DownloadProgress
            {
                FileName = file.Name,
                Status = "No download URL available",
                IsComplete = true,
                HasError = true
            });
            return false;
        }

        // Check if file already exists and is valid.
        // Size and hash are optional in metalink files - only verify what we know.
        if (File.Exists(targetPath))
        {
            if (await IsExistingFileValidAsync(targetPath, file))
            {
                progress?.Report(new DownloadProgress
                {
                    FileName = file.Name,
                    Status = "Skipped (already exists)",
                    IsComplete = true
                });
                return true;
            }

            File.Delete(targetPath);
        }

        try
        {
            progress?.Report(new DownloadProgress
            {
                FileName = file.Name,
                Status = "Downloading...",
                TotalBytes = file.Size
            });

            var bytesDownloaded = await DownloadFileSingleThreadedAsync(file, targetPath, progress);

            // Verify hash only when the metalink provided one
            if (!string.IsNullOrEmpty(file.Sha256Hash))
            {
                progress?.Report(new DownloadProgress
                {
                    FileName = file.Name,
                    Status = "Verifying hash...",
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = bytesDownloaded
                });

                var downloadedHash = await CalculateSha256Async(targetPath);

                if (!downloadedHash.Equals(file.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new DownloadProgress
                    {
                        FileName = file.Name,
                        Status = $"Hash mismatch! Expected: {file.Sha256Hash}, Got: {downloadedHash}",
                        IsComplete = true,
                        HasError = true
                    });
                    File.Delete(targetPath);
                    return false;
                }
            }

            progress?.Report(new DownloadProgress
            {
                FileName = file.Name,
                Status = "Complete",
                BytesDownloaded = bytesDownloaded,
                TotalBytes = bytesDownloaded,
                IsComplete = true
            });

            return true;
        }
        catch (Exception ex)
        {
            progress?.Report(new DownloadProgress
            {
                FileName = file.Name,
                Status = $"Error: {ex.Message}",
                IsComplete = true,
                HasError = true
            });

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            return false;
        }
    }

    private static async Task<bool> IsExistingFileValidAsync(string targetPath, Meta4File file)
    {
        var fileInfo = new FileInfo(targetPath);

        // Quick file size check first (avoid hash calculation if size doesn't match)
        if (file.Size > 0 && fileInfo.Length != file.Size)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(file.Sha256Hash))
        {
            var existingHash = await CalculateSha256Async(targetPath);
            return existingHash.Equals(file.Sha256Hash, StringComparison.OrdinalIgnoreCase);
        }

        // No hash to verify against - accept any non-empty existing file
        return fileInfo.Length > 0;
    }

    private async Task<long> DownloadFileSingleThreadedAsync(Meta4File file, string targetPath, IProgress<DownloadProgress>? progress)
    {
        var maxAttempts = Math.Max(3, file.Urls.Count);
        long totalBytesRead = 0;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Rotate through mirror URLs across attempts
            var url = file.Urls[attempt % file.Urls.Count];

            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                // Prefer the declared metalink size, fall back to Content-Length for progress reporting
                var totalBytes = file.Size > 0 ? file.Size : (response.Content.Headers.ContentLength ?? 0);
                totalBytesRead = 0;

                // Download to file with proper stream disposal
                {
                    await using var contentStream = await response.Content.ReadAsStreamAsync();
                    // Use 64KB buffer for better performance
                    await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

                    var buffer = new byte[65536];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;

                        progress?.Report(new DownloadProgress
                        {
                            FileName = file.Name,
                            Status = "Downloading...",
                            BytesDownloaded = totalBytesRead,
                            TotalBytes = totalBytes
                        });
                    }
                } // Streams are disposed here

                // Success - break out of retry loop
                return totalBytesRead;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1 &&
                (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException))
            {
                // Wait before retry with exponential backoff
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));

                // Delete partial file if exists
                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }
            }
        }

        return totalBytesRead;
    }

    private static async Task<string> CalculateSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        // Use larger buffer (1MB) for faster hash calculation on large files
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public class DownloadProgress
{
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }

    public double PercentComplete => TotalBytes > 0 ? (BytesDownloaded * 100.0 / TotalBytes) : 0;
}
