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

   // Check if file already exists and is valid
  if (File.Exists(targetPath))
        {
      // Quick file size check first (avoid hash calculation if size doesn't match)
 var fileInfo = new FileInfo(targetPath);
            if (fileInfo.Length != file.Size)
  {
         // Size mismatch, delete and re-download
     File.Delete(targetPath);
       }
       else
    {
      // Size matches, now verify hash
       var existingHash = await CalculateSha256Async(targetPath);
       if (existingHash.Equals(file.Sha256Hash, StringComparison.OrdinalIgnoreCase))
 {
         progress?.Report(new DownloadProgress
         {
           FileName = file.Name,
     Status = "Skipped (already exists with valid hash)",
  IsComplete = true
     });
      return true;
          }

     // File exists but hash doesn't match, delete and re-download
        File.Delete(targetPath);
         }
        }

        try
        {
 progress?.Report(new DownloadProgress
            {
      FileName = file.Name,
        Status = "Downloading...",
     TotalBytes = file.Size
       });

            await DownloadFileSingleThreadedAsync(file, targetPath, progress);

            progress?.Report(new DownloadProgress
         {
     FileName = file.Name,
 Status = "Verifying hash...",
         BytesDownloaded = file.Size,
 TotalBytes = file.Size
  });

            // Verify hash (file is now closed and can be read)
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

            progress?.Report(new DownloadProgress
      {
                FileName = file.Name,
        Status = "Complete",
     BytesDownloaded = file.Size,
        TotalBytes = file.Size,
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

    private async Task DownloadFileSingleThreadedAsync(Meta4File file, string targetPath, IProgress<DownloadProgress>? progress)
    {
        const int maxRetries = 3;
  
     for (int retry = 0; retry < maxRetries; retry++)
        {
     try
  {
           using var response = await _httpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

     long totalBytesRead = 0;

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
            TotalBytes = file.Size
      });
       }
 } // Streams are disposed here
           
     // Success - break out of retry loop
        break;
 }
       catch (Exception ex) when (retry < maxRetries - 1 && 
      (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException))
    {
         // Wait before retry with exponential backoff
    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
          
          // Delete partial file if exists
  if (File.Exists(targetPath))
  {
        try { File.Delete(targetPath); } catch { }
       }
  }
        }
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
