using meta4downloader;

if (args.Length < 2)
{
    Console.WriteLine("Usage: meta4downloader <path-to-meta4-file> <target-directory> [max-concurrent-downloads]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <path-to-meta4-file>  Path to the .meta4 XML file");
    Console.WriteLine("  <target-directory>          Directory where files will be downloaded");
    Console.WriteLine("  [max-concurrent-downloads]  Maximum number of files to download simultaneously (default: 4)");
    return 1;
}

var meta4FilePath = args[0];
var targetDirectory = args[1];
var maxConcurrentDownloads = 4; // Default value

if (args.Length >= 3)
{
    if (!int.TryParse(args[2], out maxConcurrentDownloads) || maxConcurrentDownloads < 1)
    {
        Console.WriteLine("Error: max-concurrent-downloads must be a positive integer.");
        return 1;
    }
}

try
{
    // Validate meta4 file exists
    if (!File.Exists(meta4FilePath))
    {
        Console.WriteLine($"Error: Meta4 file not found: {meta4FilePath}");
        return 1;
    }

    // Create target directory if it doesn't exist
    if (!Directory.Exists(targetDirectory))
    {
Console.WriteLine($"Creating target directory: {targetDirectory}");
        Directory.CreateDirectory(targetDirectory);
}

    // Parse meta4 file
    Console.WriteLine($"Parsing meta4 file: {meta4FilePath}");
    var files = Meta4Parser.Parse(meta4FilePath);

    if (files.Count == 0)
    {
  Console.WriteLine("No files found in meta4 file.");
      return 1;
    }

    Console.WriteLine($"Found {files.Count} file(s) to download.");
    Console.WriteLine($"Target directory: {targetDirectory}");
    Console.WriteLine($"Max concurrent downloads: {maxConcurrentDownloads}");
    Console.WriteLine();

    // Calculate total size
    var totalSize = files.Sum(f => f.Size);
    Console.WriteLine($"Total download size: {FormatBytes(totalSize)}");
    Console.WriteLine();
    Console.WriteLine("Starting downloads...");
    Console.WriteLine();

    // Download files
  using var downloader = new FileDownloader(targetDirectory);
    var successCount = 0;
    var skippedCount = 0;
    var failedCount = 0;
    var downloadedCount = 0;
    var completedCount = 0;
  var totalBytesDownloaded = 0L;

    // Use semaphore to limit concurrent downloads
    using var semaphore = new SemaphoreSlim(maxConcurrentDownloads);
    var lockObj = new object();
    
  // Simple progress tracking without cursor manipulation for reliability
    var fileStatus = new Dictionary<string, string>();

    var tasks = files.Select(async file =>
    {
        await semaphore.WaitAsync();

  try
        {
   var progress = new Progress<DownloadProgress>(p =>
  {
          lock (lockObj)
           {
   if (p.IsComplete)
    {
       completedCount++;

if (p.HasError)
             {
     Console.ForegroundColor = ConsoleColor.Red;
           Console.WriteLine($"✗ {p.FileName,-50} Failed: {p.Status}");
        Console.ResetColor();
          }
         else if (p.Status.StartsWith("Skipped"))
       {
                  Console.ForegroundColor = ConsoleColor.Yellow;
     Console.WriteLine($"○ {p.FileName,-50} Skipped (valid)");
      Console.ResetColor();
   }
         else
         {
      Console.ForegroundColor = ConsoleColor.Green;
           Console.WriteLine($"✓ {p.FileName,-50} Complete ({FormatBytes(p.TotalBytes)})");
          Console.ResetColor();
            totalBytesDownloaded += p.TotalBytes;
            }

     // Show overall progress
              var percent = files.Count > 0 ? (completedCount * 100.0 / files.Count) : 0;
          var bar = CreateProgressBar(percent, 30);
          Console.ForegroundColor = ConsoleColor.Cyan;
      Console.WriteLine($"Overall: {bar} {percent:F1}% ({completedCount}/{files.Count} files, {FormatBytes(totalBytesDownloaded)} downloaded)");
  Console.ResetColor();
      Console.WriteLine();
}
         else if (p.BytesDownloaded > 0)
          {
               // Update status periodically (throttled output to avoid spam)
         var percent = p.PercentComplete;
    var currentStatus = $"{percent:F1}%";
              
    if (!fileStatus.ContainsKey(file.Name) || fileStatus[file.Name] != currentStatus)
  {
         fileStatus[file.Name] = currentStatus;
     
            // Only show progress updates every 10%
             if (percent % 10 < 1)
  {
           var bar = CreateProgressBar(percent, 20);
   Console.WriteLine($"⬇ {p.FileName,-50} {bar} {percent:F1}% ({FormatBytes(p.BytesDownloaded)}/{FormatBytes(p.TotalBytes)})");
          }
        }
       }
      }
 });

       var startTime = DateTime.UtcNow;
       var result = await downloader.DownloadFileAsync(file, progress);

lock (lockObj)
            {
    if (result)
   {
      successCount++;
// Check if it was skipped or downloaded
        var fileWriteTime = File.GetLastWriteTimeUtc(Path.Combine(targetDirectory, file.Name));
             if (fileWriteTime > startTime.AddSeconds(-5))
              {
                  downloadedCount++;
         }
        else
           {
    skippedCount++;
           }
       }
      else
       {
      failedCount++;
     }
            }

        return result;
 }
        finally
        {
  semaphore.Release();
        }
    }).ToList();

    await Task.WhenAll(tasks);

    // Summary
Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine("Download Summary:");
    Console.WriteLine($"  Total files:       {files.Count}");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  Downloaded:        {downloadedCount}");
    Console.ResetColor();
Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  Skipped (valid):   {skippedCount}");
    Console.ResetColor();
    if (failedCount > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
   Console.WriteLine($"  Failed: {failedCount}");
        Console.ResetColor();
    }
    Console.WriteLine($"  Total downloaded:  {FormatBytes(totalBytesDownloaded)}");
    Console.WriteLine("═══════════════════════════════════════════════════════════");

    return failedCount > 0 ? 1 : 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {ex.Message}");
    Console.ResetColor();
  return 1;
}

static string CreateProgressBar(double percent, int width)
{
    var filled = (int)Math.Round(percent / 100.0 * width);
  var empty = width - filled;
    return $"[{new string('█', filled)}{new string('░', empty)}]";
}

static string FormatBytes(long bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB", "TB" };
    double len = bytes;
    int order = 0;
    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
}
