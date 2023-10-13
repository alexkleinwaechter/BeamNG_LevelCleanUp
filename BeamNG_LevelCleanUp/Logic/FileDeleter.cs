using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class FileDeleter
    {
        private List<FileInfo> _fileList;
        private string _levelPath;
        private string _summaryFileName;
        private bool _dryRun { get; set; }
        internal FileDeleter(List<FileInfo> fileList, string levelPath, string summaryFileName, bool dryRun)
        {
            _fileList = fileList;
            _levelPath = levelPath;
            _summaryFileName = summaryFileName;
            _dryRun = dryRun;
        }
        public void Delete()
        {
            var textLines = new List<string>();
            var textLinesNotFound = new List<string>();
            int counterFound = 0;
            int counterNotFound = 0;
            foreach (var file in _fileList)
            {
                if (file.Exists)
                {
                    if (!_dryRun)
                    {
                        File.Delete(file.FullName);
                    }
                    textLines.Add(file.FullName);
                    counterFound++;
                }
                else
                {
                    textLinesNotFound.Add(file.FullName);
                    counterNotFound++;
                }
            }
            var dryrunText = _dryRun ? "_dry_run_not_deleted" : string.Empty;
            File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}{dryrunText}.txt"), textLines);
            if (textLinesNotFound.Count > 0)
            {
                File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}_files_not_found.txt"), textLinesNotFound);
            }
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"{counterFound} files deleted. {counterNotFound} files not found. Dry Run: {_dryRun}. See directory {_levelPath} for logfiles.");
        }
    }
}
