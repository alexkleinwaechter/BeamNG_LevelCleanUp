using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Utils
{
    public static class FileUtils
    {
        public static FileInfo CheckIfImageFileExists(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileToCheck = new FileInfo(filePath);
            if (!fileToCheck.Exists)
            {
                var ddsPath = Path.ChangeExtension(filePath, ".dds");
                fileToCheck = new FileInfo(ddsPath);
            }
            if (!fileToCheck.Exists)
            {
                var ddsPath = Path.ChangeExtension(filePath, ".png");
                fileToCheck = new FileInfo(ddsPath);
            }
            if (!fileToCheck.Exists)
            {
                var ddsPath = Path.ChangeExtension(filePath, ".jpg");
                fileToCheck = new FileInfo(ddsPath);
            }
            if (!fileToCheck.Exists)
            {
                var ddsPath = Path.ChangeExtension(filePath, ".jpeg");
                fileToCheck = new FileInfo(ddsPath);
            }
            if (fileToCheck.Exists)
            {
                fileInfo = fileToCheck;
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
        }
    }
}
