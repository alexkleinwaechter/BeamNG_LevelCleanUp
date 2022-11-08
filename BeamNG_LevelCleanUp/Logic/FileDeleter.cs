using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class FileDeleter
    {
        private List<string> _filePaths;
        private string _levelPath;
        private string _summaryFileName;
        internal FileDeleter(List<string> filePaths, string levelPath, string summaryFileName) { 
            _filePaths = filePaths;
            _levelPath = levelPath;
            _summaryFileName = summaryFileName;
        }
        public void Delete() {
            var textLines = new List<string>();
            foreach (var file in _filePaths)
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    File.Delete(file);
                    textLines.Add(info.FullName);
                }
            }
            File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}.txt"), textLines);
        }
    }
}
