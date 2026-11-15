using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vytrix.Core;

namespace Vytrix.Services;

/// <summary>
/// Where VYTRIX accounts live: the project owner's Supabase project. The anon key is public by
/// design; Row Level Security (server/supabase/schema.sql) keeps every account's data private.
/// Leave both empty and the Account page explains that accounts aren't set up yet.
/// </summary>
public static class AccountConfig
{
    public const string SupabaseUrl = "https://inlogccnzhtxxasmcjiq.supabase.co";
    /// <summary>Supabase "publishable" key: public by design; Row Level Security protects the data.</summary>
    public const string SupabaseAnonKey = "sb_publishable_1AkeH7Y86c1Bdj4OXIDBjQ_bwPnnr5z";

    public static string Url => Environment.GetEnvironmentVariable("VYTRIX_ACCOUNTS_URL") is { Length: > 0 } u ? u.TrimEnd('/') : SupabaseUrl.TrimEnd('/');
    public static string AnonKey => Environment.GetEnvironmentVariable("VYTRIX_ACCOUNTS_KEY") is { Length: > 0 } k ? k : SupabaseAnonKey;
    public static bool Configured => Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || Url.StartsWith("http://127.0.0.1", StringComparison.Ordinal) ? AnonKey.Length > 0 : false;
}

/// <summary>A signed-in account. Only the refresh token is stored (encrypted); access tokens live in memory.</summary>
public sealed record AccountSession(string UserId, string Email, string Provider, string RefreshToken)
{
    public string AccessToken { get; init; } = "";
    public DateTime AccessExpires { get; init; }
}

/// <summary>A sign-in method the server has turned on.</summary>
public sealed record SignInMethod(string Id, string Label);

/// <summary>What's stored for an account: this PC's setup, sent on purpose by the user.</summary>
public sealed record SyncedSetup(TweakSetupFile Tweaks, JsonObject Appearance, string Device, DateTime SavedAt);

public sealed class AccountException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class AccountService
{
    public static AccountService Instance { get; } = new(AccountConfig.Url, AccountConfig.AnonKey, new StoredKey("account-session.bin", "Account session"));

    /// <summary>Methods VYTRIX knows how to offer, in display order. Supabase calls Microsoft "azure".</summary>
    public static readonly (string Id, string Label, string Scopes)[] KnownProviders =
    [
        ("google", "Google", ""),
        ("azure", "Microsoft", "email"),
        ("github", "GitHub", ""),
        ("discord", "Discord", ""),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _url, _key;
    private readonly StoredKey _store;
    private AccountSession? _session;

    public event Action? Changed;

    internal AccountService(string url, string anonKey, StoredKey store)
    {
        _url = url.TrimEnd('/');
        _key = anonKey;
        _store = store;
        _session = LoadStored();
    }

    public AccountSession? Session => _session;
    public bool SignedIn => _session is not null;

    // ───────────────────────── Session storage

    private AccountSession? LoadStored()
    {
        try
        {
            var json = _store.Load();
            return json is null ? null : JsonSerializer.Deserialize<AccountSession>(json, Json.Options);
        }
        catch { return null; }
    }

    private void Keep(AccountSession s)
    {
        _session = s;
        _store.Save(JsonSerializer.Serialize(s with { AccessToken = "", AccessExpires = default }, Json.Options));
        Changed?.Invoke();
    }

    private void Forget()
    {
        _session = null;
        _store.Delete();
        Changed?.Invoke();
    }

    // ───────────────────────── Sign-in

    /// <summary>The sign-in methods turned on in the Supabase project (from its public /auth/v1/settings).</summary>
    public async Task<(List<SignInMethod> Methods, bool EmailEnabled, bool SignupDisabled)> MethodsAsync(CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, "/auth/v1/settings", null, auth: false, ct);
        var ext = root?["external"] as JsonObject;
        bool On(string id) => ext?[id]?.GetValueKind() == JsonValueKind.True;
        var methods = KnownProviders.Where(p => On(p.Id)).Select(p => new SignInMethod(p.Id, p.Label)).ToList();
        return (methods, On("email"), root?["disable_signup"]?.GetValueKind() == JsonValueKind.True);
    }

    /// <summary>Signs in through the browser with Google, Microsoft, GitHub or Discord (OAuth with PKCE and a loopback redirect).</summary>
    public async Task SignInWithProviderAsync(string provider, CancellationToken ct = default)
    {
        var p = KnownProviders.FirstOrDefault(x => x.Id == provider);
        if (p.Id is null) throw new AccountException("That sign-in method isn't supported.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        int port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/vytrix-callback/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        var url = $"{_url}/auth/v1/authorize?provider={Uri.EscapeDataString(p.Id)}&redirect_to={Uri.EscapeDataString(redirect)}" +
                  $"&code_challenge={challenge}&code_challenge_method=s256" + (p.Scopes.Length > 0 ? $"&scopes={Uri.EscapeDataString(p.Scopes)}" : "");
        OpenBrowser(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? code = null, error = null;
        while (code is null && error is null)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("Sign-in timed out. Try again."); }
            code = ctx.Request.QueryString["code"];
            error = ctx.Request.QueryString["error_description"] ?? ctx.Request.QueryString["error"];
            if (code is null && error is null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }   // e.g. favicon
            await RespondAsync(ctx, error is null);
        }
        if (error is not null) throw new AccountException($"Sign-in was cancelled or refused: {error}");

        var tokens = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=pkce", new JsonObject { ["auth_code"] = code, ["code_verifier"] = verifier }, auth: false, ct);
        Keep(FromTokens(tokens, provider));
        Log.Info("account", $"Signed in with {p.Label}.");
    }

    public async Task SignInWithEmailAsync(string email, string password, CancellationToken ct = default)
    {
        var tokens = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", new JsonObject { ["email"] = email.Trim(), ["password"] = password }, auth: false, ct);
        Keep(FromTokens(tokens, "email"));
        Log.Info("account", "Signed in with email.");
    }

    /// <summary>Creates an email account. Returns true when signed in straight away, false when a confirmation email was sent.</summary>
    public async Task<bool> SignUpWithEmailAsync(string email, string password, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/auth/v1/signup", new JsonObject { ["email"] = email.Trim(), ["password"] = password }, auth: false, ct);
        if (r?["access_token"] is not null) { Keep(FromTokens(r, "email")); return true; }
        return false;
    }

    public Task SendPasswordResetAsync(string email, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/v1/recover", new JsonObject { ["email"] = email.Trim() }, auth: false, ct);

    public async Task SignOutAsync()
    {
        try { if (_session is not null) await SendAsync(HttpMethod.Post, "/auth/v1/logout?scope=local", null, auth: true, CancellationToken.None); }
        catch (Exception ex) { Log.Warn("account", $"Sign-out request failed ({ex.GetType().Name}); signing out locally."); }
        Forget();
        Log.Info("account", "Signed out.");
    }

    private static AccountSession FromTokens(JsonNode? t, string provider)
    {
        var access = t?["access_token"]?.GetValue<string>() ?? throw new AccountException("The account server didn't return a session.");
        var refresh = t?["refresh_token"]?.GetValue<string>() ?? throw new AccountException("The account server didn't return a session.");
        int expiresIn = t?["expires_in"]?.GetValue<int>() ?? 3600;
        var user = t?["user"];
        var actualProvider = user?["app_metadata"]?["provider"]?.GetValue<string>() ?? provider;
        return new AccountSession(user?["id"]?.GetValue<string>() ?? "", user?["email"]?.GetValue<string>() ?? "", actualProvider, refresh)
        {
            AccessToken = access,
            AccessExpires = DateTime.UtcNow.AddSeconds(expiresIn - 60),
        };
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var s = _session ?? throw new AccountException("You're signed out.");
        if (s.AccessToken.Length > 0 && DateTime.UtcNow < s.AccessExpires) return s.AccessToken;
        JsonNode? t;
        try { t = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", new JsonObject { ["refresh_token"] = s.RefreshToken }, auth: false, ct); }
        catch (AccountException a) when (a.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            Forget();
            throw new AccountException("Your sign-in has expired. Please sign in again.", a.Status);
        }
        var fresh = FromTokens(t, s.Provider);
        Keep(fresh);
        return fresh.AccessToken;
    }

    /// <summary>
    /// Checks a saved session with the account server. Returns false (and signs out) when the
    /// server rejects it, e.g. the account was deleted or the session was revoked. Network
    /// problems leave the session as it is, so VYTRIX still opens offline.
    /// </summary>
    public async Task<bool> VerifySessionAsync(CancellationToken ct = default)
    {
        if (_session is null) return false;
        try { await AccessTokenAsync(ct); return true; }
        catch (AccountException a) when (a.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) { return false; }
        catch (Exception ex) { Log.Info("account", $"Couldn't check the saved session ({ex.GetType().Name}); keeping it."); return _session is not null; }
    }

    // ───────────────────────── Sync

    /// <summary>Builds what gets synced: the tweaks that are on, the Custom profile's plan, and appearance settings.</summary>
    public static SyncedSetup Capture()
    {
        var s = Settings.Current;
        var appearance = new JsonObject
        {
            ["theme"] = s.Theme, ["accent"] = s.Accent, ["musicVolume"] = s.MusicVolume, ["showFallingPetals"] = s.ShowFallingPetals,
            ["reduceMotion"] = s.ReduceMotion, ["seasonalThemes"] = s.SeasonalThemes,
        };
        // A friendly label so you can tell your PCs apart, without sending the computer's name.
        var gpu = SystemInfo.PrimaryGpu?.Name?.Trim();
        var device = (gpu is { Length: > 0 } ? $"PC with {gpu}" : "Windows PC");
        return new SyncedSetup(TweakSetup.Current(), appearance, device.Length > 80 ? device[..80] : device, DateTime.UtcNow);
    }

    public async Task UploadAsync(SyncedSetup setup, CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(ct);
        var data = new JsonObject
        {
            ["format"] = "vytrix-sync", ["version"] = 1,
            ["tweaks"] = JsonSerializer.SerializeToNode(setup.Tweaks, Json.Options),
            ["appearance"] = setup.Appearance.DeepClone(),
        };
        var row = new JsonObject
        {
            ["user_id"] = _session!.UserId, ["data"] = data, ["device"] = setup.Device, ["updated_at"] = setup.SavedAt.ToString("o"),
        };
        await SendAsync(HttpMethod.Post, "/rest/v1/vytrix_sync", row, auth: true, ct, token, prefer: "resolution=merge-duplicates,return=minimal");
        Log.Info("account", $"Uploaded setup ({setup.Tweaks.Tweaks.Count} tweaks).");
    }

    /// <summary>The setup saved to this account, or null when nothing has been synced yet.</summary>
    public async Task<SyncedSetup?> DownloadAsync(CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(ct);
        var rows = await SendAsync(HttpMethod.Get, "/rest/v1/vytrix_sync?select=data,device,updated_at&limit=1", null, auth: true, ct, token) as JsonArray;
        if (rows is not { Count: > 0 }) return null;
        var row = rows[0]!;
        var data = row["data"] as JsonObject ?? throw new AccountException("The synced setup is damaged.");
        if (data["format"]?.GetValue<string>() != "vytrix-sync") throw new AccountException("The synced setup isn't in a format this VYTRIX understands.");
        if ((data["version"]?.GetValue<int>() ?? 1) > 1) throw new AccountException("Your synced setup came from a newer VYTRIX. Update VYTRIX on this PC first.");
        var tweaks = data["tweaks"].Deserialize<TweakSetupFile>(Json.Options) ?? new TweakSetupFile();
        var appearance = data["appearance"] as JsonObject ?? [];
        var saved = DateTime.TryParse(row["updated_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;
        return new SyncedSetup(tweaks with { Format = TweakSetupFile.FormatName }, (JsonObject)appearance.DeepClone(), row["device"]?.GetValue<string>() ?? "", saved);
    }

    public async Task DeleteSyncedDataAsync(CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(ct);
        await SendAsync(HttpMethod.Delete, $"/rest/v1/vytrix_sync?user_id=eq.{Uri.EscapeDataString(_session!.UserId)}", null, auth: true, ct, token);
        Log.Info("account", "Deleted synced setup.");
    }

    public async Task DeleteAccountAsync(CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(ct);
        await SendAsync(HttpMethod.Post, "/rest/v1/rpc/delete_my_account", new JsonObject(), auth: true, ct, token);
        Forget();
        Log.Info("account", "Account deleted.");
    }

    /// <summary>Applies synced appearance settings to this PC (the tweaks go to the Custom profile separately, for review).</summary>
    public static void ApplyAppearance(JsonObject a)
    {
        var s = Settings.Current;
        if (a["theme"]?.GetValue<string>() is { } theme && Vytrix.Controls.ThemeManager.Themes.Contains(theme)) { s.Theme = theme; s.NewYearThemeAutomatic = false; }
        if (a["accent"]?.GetValue<string>() is { } accent && Vytrix.Controls.ThemeManager.Accents.Any(x => x.Name == accent)) s.Accent = accent;
        if (a["musicVolume"]?.GetValue<double>() is { } v && v is >= 0 and <= 1) s.MusicVolume = v;
        if (a["showFallingPetals"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False) s.ShowFallingPetals = a["showFallingPetals"]!.GetValue<bool>();
        if (a["reduceMotion"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False) s.ReduceMotion = a["reduceMotion"]!.GetValue<bool>();
        if (a["seasonalThemes"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False) s.SeasonalThemes = a["seasonalThemes"]!.GetValue<bool>();
        Settings.Save();
    }

    // ───────────────────────── HTTP

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, bool auth, CancellationToken ct, string? token = null, string? prefer = null)
    {
        if (_url.Length == 0 || _key.Length == 0) throw new AccountException("Accounts aren't set up in this version of VYTRIX yet.");
        using var req = new HttpRequestMessage(method, _url + path);
        req.Headers.Add("apikey", _key);
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _session?.AccessToken ?? _key);
        if (prefer is not null) req.Headers.Add("Prefer", prefer);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new AccountException("Couldn't reach the account server. Check your internet connection."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("The account server took too long to answer. Try again."); }
        using var _ = resp;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AccountException(Friendly(resp.StatusCode, text), resp.StatusCode);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>Plain-language errors from the account server (its own wording is kept when it's already readable).</summary>
    internal static string Friendly(HttpStatusCode status, string body)
    {
        string? msg = null;
        try
        {
            var n = JsonNode.Parse(body);
            msg = n?["error_description"]?.GetValue<string>() ?? n?["msg"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
        }
        catch { }
        if (msg is not null && msg.Contains("Invalid login credentials", StringComparison.OrdinalIgnoreCase)) return "That email and password don't match an account.";
        if (msg is not null && msg.Contains("Email not confirmed", StringComparison.OrdinalIgnoreCase)) return "Confirm your email first: open the link we sent you, then sign in.";
        if (msg is not null && msg.Contains("already registered", StringComparison.OrdinalIgnoreCase)) return "There's already an account with that email. Sign in instead.";
        if (status == HttpStatusCode.TooManyRequests) return "Too many attempts. Wait a minute and try again.";
        if (status == HttpStatusCode.Unauthorized) return "Your sign-in has expired. Please sign in again.";
        if ((int)status >= 500) return "The account server is having trouble. Try again in a minute.";
        return msg is { Length: > 0 and < 200 } ? msg : $"The account server refused the request ({(int)status}).";
    }

    private static async Task RespondAsync(HttpListenerContext ctx, bool ok)
    {
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>VYTRIX</title>
            <style>body{font-family:Segoe UI,sans-serif;background:#0d0d11;color:#f2f2f5;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
            div{text-align:center}h1{font-weight:600}p{color:#a1a1aa}</style></head>
            <body><div><h1>{{(ok ? "You're signed in to VYTRIX" : "Sign-in didn't finish")}}</h1>
            <p>{{(ok ? "You can close this tab and go back to VYTRIX." : "Go back to VYTRIX and try again.")}}</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    internal static Action<string> OpenBrowser { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
