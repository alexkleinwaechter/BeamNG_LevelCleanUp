namespace BeamNG_LevelCleanUp.Utils
{
    public static class FileUtils
    {
        public static FileInfo ResolveImageFileName(string filePath)
        {
            //to Do: check if filepath has image extension, if not attach png
            var imageextensions = new List<string> { ".dds", ".png", ".jpg", ".jpeg", "*.tga" };
            if (!imageextensions.Any(x => filePath.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            {
                filePath = filePath + ".dds";
            }

            var fileInfo = new FileInfo(filePath);
            var fileToCheck = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                return fileInfo;
            }

            foreach (var ext in imageextensions)
            {
                var ddsPath = Path.ChangeExtension(filePath, ext);
                fileToCheck = new FileInfo(ddsPath);
                if (fileToCheck.Exists)
                {
                    return fileToCheck;
                }
            }

            return fileInfo;
        }

        public static void DeleteLinesFromFile(string filePath, List<int> lineNumbersToDelete)
        {
            // Read all lines from the file into an array.
            string[] lines = File.ReadAllLines(filePath);

            // Use LINQ to filter out lines with line numbers not in lineNumbersToDelete.
            lines = lines.Where((line, index) => !lineNumbersToDelete.Contains(index + 1)).ToArray();

            // Write the updated lines back to the file, overwriting its contents.
            File.WriteAllLines(filePath, lines);

            if (lines.Length == 0)
            {
                var name = Directory.GetParent(filePath).Name;
                var parent = Path.Combine(Directory.GetParent(Path.GetDirectoryName(filePath)).FullName, Path.GetFileName(filePath));
                File.Delete(filePath);
                Directory.Delete(Path.GetDirectoryName(filePath));
                DeleteLineByName(parent, name);
            }
        }

        public static void DeleteLineByName(string filePath, string name)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            name = $"name\":\"{name}\"";
            // Read all lines from the file into an array.
            string[] lines = File.ReadAllLines(filePath);

            // Use LINQ to filter out lines with line numbers not in lineNumbersToDelete.
            var newlines = lines.Where(l => !l.ToLowerInvariant().Contains(name.ToLowerInvariant())).ToArray();
            if (lines.Count() - newlines.Count() != 1)
            {
                return;
            }
            // Write the updated lines back to the file, overwriting its contents.
            File.WriteAllLines(filePath, newlines);

            if (lines.Length == 0)
            {
                File.Delete(filePath);
                Directory.Delete(Path.GetDirectoryName(filePath));
            }
        }
    }
}
