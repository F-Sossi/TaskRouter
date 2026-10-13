namespace TaskRouter.Blazor;

/// <summary>
/// Turns a URL a host wrote into one that resolves against the app's base — the page's
/// <c>&lt;base href&gt;</c>, or an <c>HttpClient</c>'s base address.
///
/// <para>A leading slash makes a URL root-relative, which skips the base entirely, so an app
/// published under a sub-path sends it to the server's root. Trimming the slash is half of
/// the answer: a host that worked the sub-path out for itself has already put it on the
/// front, and trimming alone applies it twice. So the base's own path is taken off first, if
/// it is there, and what is left is relative either way.</para>
///
/// <para>The one thing this cannot tell apart is a page inside the app whose first segment
/// is the sub-path's own name — <c>/app/app/…</c>. That is read as carrying the base.</para>
/// </summary>
internal static class AppRelativeUrl
{
    /// <param name="url">What the host wrote. Anything that is not root-relative — already
    /// relative, absolute, or protocol-relative — is returned as it is.</param>
    /// <param name="basePath">The path of the base the result will be resolved against.
    /// Only its directory counts, as it does to a browser: <c>/app</c> is a file at the
    /// root, and <c>/app/</c> is the sub-path.</param>
    public static string From(string url, string? basePath)
    {
        if (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal))
        {
            return url;
        }

        var root = basePath is null ? string.Empty : basePath[..Math.Max(basePath.LastIndexOf('/'), 0)];

        // Case-insensitively, because the servers that publish under a sub-path mostly match
        // it that way, and the address bar holds whatever case somebody typed.
        if (root.Length > 0
            && url.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (url.Length == root.Length || url[root.Length] is '/' or '?' or '#'))
        {
            url = url[root.Length..];
        }

        return url.TrimStart('/');
    }

    /// <summary>A client's endpoint prefix, relative to its base address.</summary>
    public static string RelativePrefix(this HttpClient http, string prefix) =>
        From(prefix, http.BaseAddress?.AbsolutePath).TrimEnd('/');
}
