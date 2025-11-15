using System.IO.Compression;
using System.Text;

namespace BeamNG_LevelCleanUp.Utils;

public static class ZipReader
{
    static ZipReader()
    {
        // Register code page encoding provider for .NET 9
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string ExtractFile(string zipPath, string extractPath, string filePathEnd)
    {
        // Normalizes the path.
        extractPath = Path.GetFullPath(extractPath);
        filePathEnd = filePathEnd.Replace(@"\", "/");
        // Ensures that the last character on the extraction path
        // is the directory separator char.
        // Without this, a malicious zip file could try to traverse outside of the expected
        // extraction path.
        if (!extractPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            extractPath += Path.DirectorySeparatorChar;

        // Try to detect the correct encoding by attempting UTF-8 first, then fallback to default
        var encoding = DetectZipEncoding(zipPath);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, encoding))
        {
            string retVal = null;
            foreach (var entry in archive.Entries)
                if (entry.FullName.EndsWith(filePathEnd, StringComparison.OrdinalIgnoreCase))
                {
                    // Gets the full path to ensure that relative segments are removed.
                    var destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));

                    // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that
                    // are case-insensitive.
                    if (destinationPath.StartsWith(extractPath, StringComparison.Ordinal))
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    entry.ExtractToFile(destinationPath, true);
                    retVal = destinationPath;
                }

            return retVal;
        }
    }

    public static bool FileExists(string zipPath, string filePathEnd)
    {
        var retVal = false;
        // Normalizes the path.
        filePathEnd = filePathEnd.Replace(@"\", "/");

        // Try to detect the correct encoding
        var encoding = DetectZipEncoding(zipPath);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, encoding))
        {
            foreach (var entry in archive.Entries)
                if (entry.FullName.EndsWith(filePathEnd, StringComparison.OrdinalIgnoreCase))
                {
                    retVal = true;
                    break;
                }

            return retVal;
        }
    }

    private static Encoding DetectZipEncoding(string zipPath)
    {
        // Try UTF-8 first and check if entry names contain valid UTF-8 characters
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.UTF8))
            {
                foreach (var entry in archive.Entries)
                    // Check if the entry name contains replacement characters which indicate encoding issues
                    if (entry.FullName.Contains('\uFFFD'))
                        // UTF-8 failed, try common fallback encodings
                        // Try code page 850 (Western European - commonly used by 7-Zip)
                        try
                        {
                            return Encoding.GetEncoding(850);
                        }
                        catch
                        {
                            // Fallback to code page 437 (IBM PC)
                            try
                            {
                                return Encoding.GetEncoding(437);
                            }
                            catch
                            {
                                // Last resort: use Latin1/ISO-8859-1
                                return Encoding.Latin1;
                            }
                        }

                return Encoding.UTF8;
            }
        }
        catch
        {
            // If UTF-8 fails completely, try fallback encodings
            try
            {
                return Encoding.GetEncoding(850);
            }
            catch
            {
                try
                {
                    return Encoding.GetEncoding(437);
                }
                catch
                {
                    return Encoding.Latin1;
                }
            }
        }
    }
}