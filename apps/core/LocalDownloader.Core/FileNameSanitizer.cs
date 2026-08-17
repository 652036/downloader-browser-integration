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

        var sanitized = new string(fileName.Select(ch => IsInvalidFileNameChar(ch) ? '_' : ch).ToArray());
        sanitized = sanitized.Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(sanitized) || IsReservedDeviceName(sanitized))
        {
            sanitized = fallbackName;
        }

        return sanitized;
    }

    private static bool IsInvalidFileNameChar(char ch)
    {
        // Always apply the Windows set: this product writes Windows paths, and
        // Path.GetInvalidFileNameChars() is OS-specific (Linux allows '<' etc.).
        if (ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
        {
            return true;
        }

        return ch < 32 || Path.GetInvalidFileNameChars().Contains(ch);
    }

    private static bool IsReservedDeviceName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return ReservedDeviceNames.Contains(stem);
    }
}
