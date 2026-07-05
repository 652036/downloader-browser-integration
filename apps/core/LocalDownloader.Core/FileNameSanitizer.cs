namespace LocalDownloader.Core;

public static class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public static string Sanitize(string? untrustedName, string fallbackName)
    {
        var normalizedName = (untrustedName ?? string.Empty).Replace('\\', Path.DirectorySeparatorChar);
        var fileName = Path.GetFileName(normalizedName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallbackName;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        sanitized = sanitized.Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(sanitized) || IsReservedDeviceName(sanitized))
        {
            sanitized = fallbackName;
        }

        return sanitized;
    }

    private static bool IsReservedDeviceName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return ReservedDeviceNames.Contains(stem);
    }
}
