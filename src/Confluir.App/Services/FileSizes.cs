using Confluir.App.Localization;

namespace Confluir.App.Services;

public static class FileSizes
{
    /// <summary>"3,1 KB", "14,2 MB"… na cultura do idioma atual.</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size.ToString("0.#", LocalizationService.Instance.FormatCulture)} {units[unit]}";
    }
}
