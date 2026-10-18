namespace AOI.Ui.Client;

/// <summary>
/// Native save-dialog seam. Kept in the UI-independent client assembly so the export flow
/// can be unit tested without an Avalonia desktop or a real dialog.
/// Returns the absolute local path the operator chose, or null when the dialog was cancelled.
/// </summary>
public interface IReportSavePicker
{
    Task<string?> PickAsync(string suggestedFileName, CancellationToken cancellationToken = default);
}
