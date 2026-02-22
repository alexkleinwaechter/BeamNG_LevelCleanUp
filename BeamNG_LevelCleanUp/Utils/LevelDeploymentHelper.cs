using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Logic;
using MudBlazor;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
///     Shared helper for copying unpacked levels to the BeamNG user levels folder.
///     Used by CreateLevel, RenameMap, and the Generate Terrain wizard.
/// </summary>
public static class LevelDeploymentHelper
{
    /// <summary>
    ///     Copies an unpacked level to the BeamNG user levels folder.
    /// </summary>
    /// <param name="levelName">Level folder name (e.g., "my_level")</param>
    /// <param name="unpackedBasePath">Directory containing the level folder</param>
    /// <param name="dialogService">Dialog service for overwrite confirmation (optional)</param>
    /// <param name="skipConfirmation">If true, silently overwrites existing level without asking</param>
    /// <returns>The deployed level directory path on success, null if user canceled</returns>
    public static async Task<string?> CopyToLevelsFolder(
        string levelName,
        string unpackedBasePath,
        IDialogService? dialogService = null,
        bool skipConfirmation = false)
    {
        var checker = new CustomChangesChecker(levelName, unpackedBasePath);

        if (checker.TargetDirectoryExists())
        {
            if (!skipConfirmation && dialogService != null)
            {
                var options = new DialogOptions { CloseOnEscapeKey = true };
                var parameters = new DialogParameters<SimpleDialog>
                {
                    { x => x.ContentText, $"The level '{levelName}' already exists in your BeamNG levels folder. Do you want to overwrite it?" },
                    { x => x.ButtonText, "Yes, Overwrite" },
                    { x => x.Color, MudBlazor.Color.Warning }
                };

                var dialog = await dialogService.ShowAsync<SimpleDialog>("Level Already Exists", parameters, options);
                var result = await dialog.Result;

                if (result.Canceled)
                    return null;
            }

            checker.DeleteTargetDirectory();
        }

        string? targetPath = null;
        await Task.Run(() =>
        {
            ZipFileHandler.RemoveModInfo(unpackedBasePath);
            targetPath = checker.CopyUnpackedToUserFolder();
        });

        return targetPath;
    }
}
