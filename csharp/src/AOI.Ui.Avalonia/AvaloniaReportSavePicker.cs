using AOI.Ui.Client;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AOI.Ui.Avalonia;

/// <summary>Native Avalonia save dialog for the operator production CSV.</summary>
public sealed class AvaloniaReportSavePicker(Window window) : IReportSavePicker
{
    public async Task<string?> PickAsync(string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var top = TopLevel.GetTopLevel(window);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 AOI 生产报表 CSV",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("AOI 生产报表 CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"]
                }
            ]
        });
        return file?.TryGetLocalPath();
    }
}
