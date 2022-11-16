﻿using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class GenericCsFileScanner
    {
        private FileInfo _csFile { get; set; }
        private string _levelPath { get; set; }
        private List<string> _excludeFiles = new List<string>();

        internal GenericCsFileScanner(FileInfo csFile, string levelPath, List<string> excludeFiles)
        {
            _csFile = csFile;
            _levelPath = levelPath;
            _excludeFiles = excludeFiles;
        }
        internal void ScanForFilesToExclude()
        {
            foreach (string line in File.ReadLines(_csFile.FullName))
            {
                var nameParts = line.Split('"');
                if (nameParts.Length > 1)
                {
                    var name = nameParts[1];
                    //if (name.Contains("slabs_huge_d")) Debugger.Break();
                    if (name.StartsWith("."))
                    {
                        name = name.Remove(0, 1);
                    }
                    if (name.Count(c => c == '/') == 0)
                    {
                        name = Path.Join(_csFile.Directory.FullName, name);
                    }
                    var toCheck = PathResolver.ResolvePath(_levelPath, name, false);
                    var checkForFile = new FileInfo(toCheck);
                    if (!checkForFile.Exists)
                    {
                        checkForFile = CheckMissingExtensions(checkForFile);
                    }
                    if (checkForFile.Exists)
                    {
                        _excludeFiles.Add(checkForFile.FullName);
                    }
                    else
                    {
                        toCheck = PathResolver.ResolvePathBasedOnCsFilePath(_csFile, name);
                        checkForFile = new FileInfo(toCheck);
                        if (!checkForFile.Exists)
                        {
                            checkForFile = CheckMissingExtensions(checkForFile);
                        }
                        if (checkForFile.Exists)
                        {
                            _excludeFiles.Add(checkForFile.FullName);
                        }
                    }
                }
            }
        }

        internal FileInfo CheckMissingExtensions(FileInfo fileInfo)
        {

            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".dds");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".png");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpg");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpeg");
                fileInfo = new FileInfo(ddsPath);
            }
            return fileInfo;
        }
    }
}
