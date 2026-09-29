namespace Aevo.CoreApi.Security;

public sealed class AppSessionReader(IConfiguration configuration)
{
    private static readonly IReadOnlyDictionary<string, string> DefaultCookieNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HUB"] = "aevo_hub_session",
        ["ADMIN"] = "aevo_admin_session",
        ["GO"] = "aevo_go_session",
        ["PLAY"] = "aevo_play_session",
        ["POS"] = "aevo_pos_session",
        ["KIOSK"] = "aevo_kiosk_session",
        ["QUEUE"] = "aevo_queue_session",
        ["DIGITAL_SIGN"] = "aevo_digital_sign_session"
    };

    private readonly string defaultCookieName = "aevo_admin_session";
    private readonly IReadOnlyDictionary<string, string> cookieNames = ReadCookieNames(configuration);
    private readonly Dictionary<string, string> csrfCookieNames = ReadCookieNames(
        configuration,
        "AEVO_CSRF_COOKIE_NAMES",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HUB"] = "aevo_csrf",
            ["ADMIN"] = "aevo_admin_csrf",
            ["GO"] = "aevo_go_csrf",
            ["PLAY"] = "aevo_play_csrf",
            ["POS"] = "aevo_pos_csrf",
            ["KIOSK"] = "aevo_kiosk_csrf",
            ["QUEUE"] = "aevo_queue_csrf",
            ["DIGITAL_SIGN"] = "aevo_digital_sign_csrf"
        });

    public bool HasSessionCookie(HttpContext context, string? applicationCode = null)
    {
        return ReadSessionCookie(context, applicationCode) is not null;
    }

    public string? ReadSessionCookie(HttpContext context, string? applicationCode = null)
    {
        if (applicationCode is not null)
        {
            return ReadValidCookie(context, CookieName(applicationCode));
        }

        // A generic endpoint may inspect a session without knowing the app in
        // advance, but it must never pick one arbitrarily when a browser sends
        // more than one first-party session cookie. Ambiguous cookies fail
        // closed instead of allowing app-boundary confusion.
        string? match = null;
        foreach (var name in cookieNames.Values.Distinct(StringComparer.Ordinal))
        {
            var value = ReadValidCookie(context, name);
            if (value is null) continue;
            if (match is not null) return null;
            match = value;
        }
        return match;
    }

    public string CookieName(string? applicationCode = null)
    {
        var normalized = applicationCode?.Trim().ToUpperInvariant();
        return normalized is not null && cookieNames.TryGetValue(normalized, out var configuredName)
            ? configuredName
            : defaultCookieName;
    }

    public string CsrfCookieName(string? applicationCode = null)
    {
        var normalized = applicationCode?.Trim().ToUpperInvariant();
        return normalized is not null && csrfCookieNames.TryGetValue(normalized, out var configuredName)
            ? configuredName
            : "aevo_admin_csrf";
    }

    public bool IsSessionStoreConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["AEVO_DATABASE_URL"])
            && !string.IsNullOrWhiteSpace(configuration["AEVO_SESSION_SECRET"]);
    }

    private static string? ReadValidCookie(HttpContext context, string name)
    {
        if (!context.Request.Cookies.TryGetValue(name, out var sessionCookie)) return null;
        var value = sessionCookie.Trim();
        return value.Length is >= 40 and <= 4096 ? value : null;
    }

    private static Dictionary<string, string> ReadCookieNames(
        IConfiguration configuration,
        string key = "AEVO_SESSION_COOKIE_NAMES",
        IReadOnlyDictionary<string, string>? defaults = null)
    {
        var result = (defaults ?? DefaultCookieNames).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (configuration[key] is not { Length: > 0 } configured) return result;

        foreach (var entry in configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1) continue;
            var application = entry[..separator].Trim().ToUpperInvariant();
            var name = entry[(separator + 1)..].Trim();
            if (application.Length > 0 && name.Length > 0) result[application] = name;
        }
        return result;
    }
}
