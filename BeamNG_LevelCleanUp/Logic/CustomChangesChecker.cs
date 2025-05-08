using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class CustomChangesChecker
    {
        string _levelName;
        string _levelFolderPathChanges;
        string _unpackedPath;

        internal CustomChangesChecker(string levelName, string unpackedPath)
        {
            _levelName = levelName;
            _unpackedPath = unpackedPath;
        }


        internal bool HasCustomChanges()
        {
            // Pfad zum Benutzerordner
            string userFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeamNG.drive");

            // Überprüfen, ob der Benutzerordner existiert
            if (!Directory.Exists(userFolderPath))
            {
                return false;
            }

            // Alle Unterordner abrufen und die höchste Versionsnummer finden
            var versionFolders = Directory.GetDirectories(userFolderPath)
                .Select(Path.GetFileName)
                .Where(folderName => Version.TryParse(folderName, out _)) // Nur gültige Versionsordner
                .OrderByDescending(folderName => Version.Parse(folderName)) // Absteigend sortieren
                .ToList();

            if (!versionFolders.Any())
            {
                return false; // Keine gültigen Versionsordner gefunden
            }

            // Höchste Versionsnummer abrufen
            string highestVersionFolder = Path.Combine(userFolderPath, versionFolders.First());

            // Überprüfen, ob der Ordner "/levels/[_levelName]" existiert
            _levelFolderPathChanges = Path.Combine(highestVersionFolder, "levels", _levelName);
            return Directory.Exists(_levelFolderPathChanges);
        }

        internal bool CopyCustomChangesToUnpacked()
        {
            if (HasCustomChanges())
            {
                // Dateien und Unterordner kopieren
                CopyDirectory(_levelFolderPathChanges, _unpackedPath);
                return true;
            }
            return false;
        }

        internal void CopyChangesToUnpacked()
        {
            CopyDirectory(_levelFolderPathChanges, Path.Combine(_unpackedPath, _levelName));
        }

        internal string GetLevelFolderPathChanges()
        {
            return _levelFolderPathChanges;
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException($"The source directory '{sourceDir}' was not found.");
            }

            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true); // Überschreibt vorhandene Dateien
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir); // Rekursiver Aufruf
            }
        }

    }
}
