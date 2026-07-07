using System.IO;
using System.Text.RegularExpressions;

namespace LocalDownloader.App.Services;

/// <summary>
/// Pure logic for the clipboard watcher: given arbitrary clipboard text and the configured
/// intercept-extension list, finds the first http/https URL whose path extension matches one of
/// the types the app would otherwise intercept from a browser download. Contains no Win32 or
/// clipboard API calls so it is trivially unit-testable; <see cref="ClipboardWatcherService"/>
/// is the thin shell that feeds it real clipboard text.
/// </summary>
public static partial class ClipboardUrlDetector
{
    // Matches a bare http/https URL token inside arbitrary surrounding text (e.g. pasted chat
    // messages, "下载地址: https://.../file.zip 提取码: 1234").
    [GeneratedRegex(@"https?://[^\s""'<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>
    /// Returns the first URL in <paramref name="clipboardText"/> whose extension matches
    /// <paramref name="interceptExtensions"/>, or null if there is no such URL (including when
    /// the clipboard held no URL at all, or only URLs of uninteresting types).
    /// </summary>
    public static string? FindDownloadUrl(string? clipboardText, IReadOnlyCollection<string> interceptExtensions)
    {
        if (string.IsNullOrWhiteSpace(clipboardText) || interceptExtensions.Count == 0)
        {
            return null;
        }

        foreach (Match match in UrlPattern().Matches(clipboardText))
        {
            var candidate = TrimTrailingPunctuation(match.Value);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var extension = GetExtension(uri);
            if (extension is null)
            {
                continue;
            }

            foreach (var candidateExtension in interceptExtensions)
            {
                if (string.Equals(extension, candidateExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? GetExtension(Uri uri)
    {
        var path = uri.AbsolutePath;
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? null : extension;
    }

    private static string TrimTrailingPunctuation(string url)
    {
        // Strip common trailing punctuation picked up when a URL is embedded in prose, e.g.
        // "see https://example.com/file.zip." or "(https://example.com/file.zip)".
        var end = url.Length;
        while (end > 0 && ".,;:!?)]}\"'".IndexOf(url[end - 1]) >= 0)
        {
            end--;
        }

        return url[..end];
    }
}
