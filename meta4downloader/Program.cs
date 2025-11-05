using meta4downloader;

// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");

if (args.Length < 2)
{
    Console.WriteLine("Usage: meta4downloader <path-to-meta4-file> <target-directory>");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <path-to-meta4-file>  Path to the .meta4 XML file");
    Console.WriteLine("  <target-directory>    Directory where files will be downloaded");
    return 1;
}

var meta4FilePath = args[0];
var targetDirectory = args[1];

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
    Console.WriteLine();

    // Calculate total size
    var totalSize = files.Sum(f => f.Size);
    Console.WriteLine($"Total download size: {FormatBytes(totalSize)}");
    Console.WriteLine();

    // Download files
    using var downloader = new FileDownloader(targetDirectory);
    var successCount = 0;
    var skippedCount = 0;
    var failedCount = 0;
    var downloadedCount = 0;

    // Use semaphore to limit concurrent downloads
    var maxConcurrentDownloads = 4;
    using var semaphore = new SemaphoreSlim(maxConcurrentDownloads);
    var lockObj = new object();

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
                        if (p.HasError)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"✗ {p.FileName}: {p.Status}");
                            Console.ResetColor();
                        }
                        else if (p.Status.StartsWith("Skipped"))
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"○ {p.FileName}: {p.Status}");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"✓ {p.FileName}: {p.Status} ({FormatBytes(p.TotalBytes)})");
                            Console.ResetColor();
                        }
                    }
                }
            });

            var result = await downloader.DownloadFileAsync(file, progress);

            lock (lockObj)
            {
                if (result)
                {
                    successCount++;
                    // Check if it was skipped or downloaded
                    if (File.GetLastWriteTimeUtc(Path.Combine(targetDirectory, file.Name)) > DateTime.UtcNow.AddSeconds(-5))
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
        }
        finally
        {
            semaphore.Release();
        }
    }).ToList();

    await Task.WhenAll(tasks);

    // Summary
    Console.WriteLine();
    Console.WriteLine("Download Summary:");
    Console.WriteLine($"  Total files: {files.Count}");
    Console.WriteLine($"  Downloaded: {downloadedCount}");
    Console.WriteLine($"  Skipped (already valid): {skippedCount}");
    Console.WriteLine($"  Failed: {failedCount}");

    return failedCount > 0 ? 1 : 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {ex.Message}");
    Console.ResetColor();
    return 1;
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
