using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class BeamFileReader
    {
        static System.Collections.Specialized.StringCollection log = new System.Collections.Specialized.StringCollection();
        private static string _path { get; set; }
        public static List<Asset> Assets { get; set; } = new List<Asset>();
        internal BeamFileReader(string path)
        {
            _path = path;
        }
        internal void ReadMissionGroup()
        {
            var dirInfo = new DirectoryInfo(_path);
            if (dirInfo != null)
            {
                WalkDirectoryTree(dirInfo);
                Console.WriteLine("Files with restricted access:");
                foreach (string s in log)
                {
                    Console.WriteLine(s);
                }
            }
        }

        static void WalkDirectoryTree(DirectoryInfo root)
        {
            FileInfo[] files = null;
            DirectoryInfo[] subDirs = null;

            // First, process all the files directly under this folder
            try
            {
                files = root.GetFiles("items.level.json");
            }
            // This is thrown if even one of the files requires permissions greater
            // than the application provides.
            catch (UnauthorizedAccessException e)
            {
                // This code just writes out the message and continues to recurse.
                // You may decide to do something different here. For example, you
                // can try to elevate your privileges and access the file again.
                log.Add(e.Message);
            }

            catch (DirectoryNotFoundException e)
            {
                Console.WriteLine(e.Message);
            }

            if (files != null)
            {
                foreach (FileInfo fi in files)
                {
                    // In this example, we only access the existing FileInfo object. If we
                    // want to open, delete or modify the file, then
                    // a try-catch block is required here to handle the case
                    // where the file has been deleted since the call to TraverseTree().
                    //Console.WriteLine(fi.FullName);
                    //von hie Klassen aufrufen, die file inhalt bearbeiten
                    var materialScanner = new MissionGroupScanner(fi.FullName, _path, Assets);
                    materialScanner.scanMissionGroupFile();
                }

                // Now find all the subdirectories under this directory.
                subDirs = root.GetDirectories();

                foreach (DirectoryInfo dirInfo in subDirs)
                {
                    // Resursive call for each subdirectory.
                    WalkDirectoryTree(dirInfo);
                }
            }
        }
    }
}
