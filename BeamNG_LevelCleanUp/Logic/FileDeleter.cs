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
        private bool _dryRun { get; set; }
        internal FileDeleter(List<string> filePaths, string levelPath, string summaryFileName, bool dryRun) { 
            _filePaths = filePaths;
            _levelPath = levelPath;
            _summaryFileName = summaryFileName;
            _dryRun = dryRun;
        }
        public void Delete() {
            var textLines = new List<string>();
            var textLinesNotFound = new List<string>();
            foreach (var file in _filePaths)
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    if (!_dryRun)
                    {
                        File.Delete(file);
                    }
                    textLines.Add(info.FullName);
                }
                else
                {
                    textLinesNotFound.Add(info.FullName);
                }
            }
            var dryrunText = _dryRun ? "_dry_run_not_deleted" : string.Empty;
            File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}{dryrunText}.txt"), textLines);
            if (textLinesNotFound.Count > 0) {
                File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}_files_not_found.txt"), textLinesNotFound);
            }
        }
    }
}
