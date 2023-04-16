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
    }
}
