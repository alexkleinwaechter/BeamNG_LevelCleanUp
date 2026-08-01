namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     Outcome of a copy operation across all selected assets. The copy pages use this to
///     show an honest final message instead of an unconditional success snackbar.
/// </summary>
public class CopyResultSummary
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }

    /// <summary>
    ///     Operations that were never attempted because an earlier fatal error aborted the batch.
    /// </summary>
    public int Skipped { get; set; }

    public List<string> FailedItems { get; } = new();

    /// <summary>
    ///     The copy was aborted before touching anything: the extracted source level no longer
    ///     exists on disk and could not be restored from the original source zip.
    /// </summary>
    public bool SourceMissing { get; set; }

    /// <summary>
    ///     The extracted source level was missing but has been re-extracted from the original
    ///     source zip before copying.
    /// </summary>
    public bool SourceRestored { get; set; }

    /// <summary>
    ///     None of the selected identifiers matched the scanned copy list (stale scan data).
    /// </summary>
    public bool NothingMatched { get; set; }

    public bool HasFailures => SourceMissing || NothingMatched || Failed > 0 || Skipped > 0;

    public void Track(bool success, string name)
    {
        if (success)
        {
            Succeeded++;
        }
        else
        {
            Failed++;
            if (!string.IsNullOrEmpty(name)) FailedItems.Add(name);
        }
    }

    /// <summary>
    ///     Builds the final user-facing message for the snackbar shown after a copy operation.
    /// </summary>
    public string BuildUserMessage(int selectedCount, string sourceName, string itemLabel)
    {
        if (SourceMissing)
            return "Nothing was copied: the extracted source level is no longer available and could not be " +
                   "restored from the source zip. Please select the source level again.";

        if (NothingMatched)
            return "Nothing was copied: the selection no longer matches the scanned data. " +
                   "Please reload the source level and select again.";

        if (Failed > 0 || Skipped > 0)
        {
            var skippedPart = Skipped > 0 ? $", {Skipped} skipped" : string.Empty;
            return $"Copy finished with problems: {Succeeded} succeeded, {Failed} failed{skippedPart}. " +
                   "See the Errors/Warnings log for details.";
        }

        var restoredPart = SourceRestored
            ? " The extracted source files had been removed and were restored from the original zip."
            : string.Empty;
        return $"Successfully copied {selectedCount} {itemLabel} from {sourceName}.{restoredPart}";
    }
}
