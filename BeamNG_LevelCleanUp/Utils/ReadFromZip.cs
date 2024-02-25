using System.IO.Compression;

namespace BeamNG_LevelCleanUp.Utils
{
    public static class ZipReader
    {
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

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                string retVal = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(filePathEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        // Gets the full path to ensure that relative segments are removed.
                        string destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));

                        // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that
                        // are case-insensitive.
                        if (destinationPath.StartsWith(extractPath, StringComparison.Ordinal))
                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        entry.ExtractToFile(destinationPath, true);
                        retVal = destinationPath;
                    }
                }
                return retVal;
            }
        }

        public static bool FileExists(string zipPath, string filePathEnd)
        {
            bool retVal = false;
            // Normalizes the path.
            filePathEnd = filePathEnd.Replace(@"\", "/");

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(filePathEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        retVal = true;
                        break;
                    }
                }
                return retVal;
            }
        }
    }
}
