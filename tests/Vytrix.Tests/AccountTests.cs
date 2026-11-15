using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vytrix.Core;
using Vytrix.Services;
using Xunit;

namespace Vytrix.Tests;

/// <summary>Accounts and sync, driven against a fake of Supabase's Auth and REST APIs.</summary>
[Collection("accounts")]
public class AccountTests
{
    private const string AnonKey = "anon-test-key";

    private static string Tokens(int expiresIn = 3600, string access = "access-1", string refresh = "refresh-1", string provider = "email") => JsonSerializer.Serialize(new
    {
        access_token = access, refresh_token = refresh, token_type = "bearer", expires_in = expiresIn,
        user = new { id = "11111111-2222-3333-4444-555555555555", email = "gamer@example.com", app_metadata = new { provider } },
    });

    private static (AccountService Svc, StoredKey Store) New(string url)
    {
        var store = new StoredKey($"test-session-{Guid.NewGuid():N}.bin", "test");
        return (new AccountService(url, AnonKey, store), store);
    }

    [Fact]
    public async Task Sign_in_methods_come_from_the_server_settings()
    {
        using var fake = new AssistantTests.FakeClaude((200, """{"external":{"google":true,"azure":true,"github":false,"discord":false,"email":true},"disable_signup":false,"mailer_autoconfirm":false}"""));
        var (svc, _) = New(fake.Url);
        var (methods, email, signupOff) = await svc.MethodsAsync();
        Assert.Equal(["Google", "Microsoft"], methods.Select(m => m.Label));
        Assert.True(email);
        Assert.False(signupOff);
        Assert.Equal(AnonKey, fake.Requests[0].Headers["apikey"]);
        Assert.Equal("GET /auth/v1/settings", fake.Paths[0]);
    }

    [Fact]
    public async Task Email_sign_in_keeps_only_an_encrypted_refresh_token()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens()));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync(" gamer@example.com ", "correct horse battery");

        Assert.Equal("/auth/v1/token?grant_type=password", fake.Paths[0].Replace("POST ", ""));
        var body = fake.Requests[0].Body.RootElement;
        Assert.Equal("gamer@example.com", body.GetProperty("email").GetString());
        Assert.True(svc.SignedIn);
        Assert.Equal("gamer@example.com", svc.Session!.Email);

        var raw = File.ReadAllBytes(Directory.GetFiles(AppPaths.Root, "test-session-*.bin").First(f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1)));
        Assert.DoesNotContain("refresh-1", Encoding.UTF8.GetString(raw));   // DPAPI-encrypted on disk
        var restored = new AccountService(fake.Url, AnonKey, store);       // a restart signs you back in
        Assert.Equal("refresh-1", restored.Session!.RefreshToken);
        Assert.Equal("", restored.Session.AccessToken);                    // access tokens are never stored
        store.Delete();
    }

    [Fact]
    public async Task A_reset_code_sets_the_new_password_and_signs_in()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens(access: "recovery-access")), (200, """{"id":"user-1"}"""));
        var (svc, store) = New(fake.Url);
        await svc.ResetPasswordWithCodeAsync(" gamer@example.com ", " 123 456 ", "new password 1");

        Assert.Equal("/auth/v1/verify", fake.Paths[0]);
        var verify = fake.Requests[0].Body.RootElement;
        Assert.Equal("recovery", verify.GetProperty("type").GetString());
        Assert.Equal("gamer@example.com", verify.GetProperty("email").GetString());
        Assert.Equal("123456", verify.GetProperty("token").GetString());         // spaces typed in the code are ignored
        Assert.Equal("/auth/v1/user", fake.Paths[1]);
        Assert.Equal("Bearer recovery-access", fake.Requests[1].Headers["authorization"]);
        Assert.Equal("new password 1", fake.Requests[1].Body.RootElement.GetProperty("password").GetString());
        Assert.True(svc.SignedIn);
        store.Delete();
    }

    [Fact]
    public async Task A_wrong_or_expired_code_is_explained_and_does_not_sign_in()
    {
        using var fake = new AssistantTests.FakeClaude((403, """{"code":403,"error_code":"otp_expired","msg":"Token has expired or is invalid"}"""));
        var (svc, store) = New(fake.Url);
        var ex = await Assert.ThrowsAsync<AccountException>(() => svc.ResetPasswordWithCodeAsync("gamer@example.com", "111111", "new password 1"));
        Assert.Equal(AccountService.BadCodeMessage, ex.Message);
        Assert.Single(fake.Paths);                                               // the password is never sent
        Assert.False(svc.SignedIn);
        await Assert.ThrowsAsync<AccountException>(() => svc.ConfirmSignUpAsync("gamer@example.com", "12"));   // too short: not sent
        Assert.Single(fake.Paths);
        store.Delete();
    }

    [Fact]
    public async Task A_signup_code_confirms_the_account_and_a_new_code_can_be_sent()
    {
        using var fake = new AssistantTests.FakeClaude((200, "{}"), (200, Tokens()));
        var (svc, store) = New(fake.Url);
        await svc.ResendSignUpCodeAsync("gamer@example.com");
        Assert.Equal("/auth/v1/resend", fake.Paths[0]);
        Assert.Equal("signup", fake.Requests[0].Body.RootElement.GetProperty("type").GetString());

        await svc.ConfirmSignUpAsync("gamer@example.com", "654321");
        Assert.Equal("signup", fake.Requests[1].Body.RootElement.GetProperty("type").GetString());
        Assert.True(svc.SignedIn);
        store.Delete();
    }

    [Fact]
    public void Signing_in_before_confirming_points_to_the_code()
    {
        var msg = AccountService.Friendly(System.Net.HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Email not confirmed"}""");
        Assert.Equal(AccountService.NotConfirmedMessage, msg);
    }

    [Fact]
    public void Sync_fingerprint_ignores_time_device_and_key_order()
    {
        var tweaks = new TweakSetupFile { Format = TweakSetupFile.FormatName, Tweaks = ["b", "a"], PowerPlan = "performance" };
        var a = new SyncedSetup(tweaks, new JsonObject { ["theme"] = "Christmas", ["musicVolume"] = 0.15 }, "PC with RTX", DateTime.UtcNow);
        var b = new SyncedSetup(tweaks with { Tweaks = ["a", "b"], Exported = DateTime.Now.AddDays(-3) },
                                new JsonObject { ["musicVolume"] = 0.150, ["theme"] = "Christmas" }, "Windows PC", DateTime.UtcNow.AddDays(-1));
        Assert.Equal(AutoSync.Fingerprint(a), AutoSync.Fingerprint(b));
        var c = b with { Appearance = new JsonObject { ["musicVolume"] = 0.15, ["theme"] = "Graphite" } };
        Assert.NotEqual(AutoSync.Fingerprint(a), AutoSync.Fingerprint(c));
        var d = b with { Tweaks = tweaks with { Tweaks = ["a"] } };
        Assert.NotEqual(AutoSync.Fingerprint(a), AutoSync.Fingerprint(d));
    }

    [Fact]
    public async Task A_saved_session_the_server_rejects_is_signed_out_at_start()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens()), (400, """{"error":"invalid_grant","error_description":"Invalid Refresh Token"}"""));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");
        var restarted = new AccountService(fake.Url, AnonKey, store);   // saved session, no access token yet
        bool changed = false;
        restarted.Changed += () => changed = true;

        Assert.False(await restarted.VerifySessionAsync());
        Assert.False(restarted.SignedIn);
        Assert.True(changed);                                            // the app listens for this to show the sign-in screen
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task A_saved_session_is_kept_when_the_server_cannot_be_reached()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens()));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");
        var offline = new AccountService("http://127.0.0.1:9", AnonKey, store);   // nothing listens on port 9

        Assert.True(await offline.VerifySessionAsync());
        Assert.True(offline.SignedIn);
        store.Delete();
    }

    [Fact]
    public async Task Upload_and_download_round_trip_with_row_level_security_headers()
    {
        var syncRow = new JsonArray(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["format"] = "vytrix-sync", ["version"] = 1,
                ["tweaks"] = new JsonObject { ["format"] = "vytrix-tweak-setup", ["version"] = 1, ["tweaks"] = new JsonArray("gaming.gamemode", "input.stickykeys"), ["powerPlan"] = "performance" },
                ["appearance"] = new JsonObject { ["theme"] = "New Year", ["accent"] = "Gold" },
            },
            ["device"] = "PC with NVIDIA GeForce RTX 5060",
            ["updated_at"] = "2026-11-15T12:00:00+00:00",
        });
        using var fake = new AssistantTests.FakeClaude((200, Tokens()), (201, ""), (200, syncRow.ToJsonString()));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");

        var local = AccountService.Capture();
        await svc.UploadAsync(local);
        var (h, b) = fake.Requests[1];
        Assert.Equal("/rest/v1/vytrix_sync", fake.Paths[1]);
        Assert.Equal("Bearer access-1", h["authorization"]);
        Assert.Contains("resolution=merge-duplicates", h["prefer"]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", b.RootElement.GetProperty("user_id").GetString());
        Assert.Equal("vytrix-sync", b.RootElement.GetProperty("data").GetProperty("format").GetString());
        Assert.DoesNotContain(Environment.MachineName, b.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, b.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var remote = await svc.DownloadAsync();
        Assert.NotNull(remote);
        Assert.Equal(["gaming.gamemode", "input.stickykeys"], remote!.Tweaks.Tweaks);
        Assert.Equal("New Year", remote.Appearance["theme"]!.GetValue<string>());
        Assert.Equal(new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc), remote.SavedAt);
        Assert.StartsWith("GET /rest/v1/vytrix_sync?select=", fake.Paths[2]);
        store.Delete();
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_before_syncing()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens(expiresIn: 0)), (200, Tokens(access: "access-2", refresh: "refresh-2")), (200, "[]"));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");
        Assert.Null(await svc.DownloadAsync());
        Assert.Equal("/auth/v1/token?grant_type=refresh_token", fake.Paths[1].Replace("POST ", ""));
        Assert.Equal("refresh-1", fake.Requests[1].Body.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("Bearer access-2", fake.Requests[2].Headers["authorization"]);
        Assert.Equal("refresh-2", svc.Session!.RefreshToken);   // rotated token is kept
        store.Delete();
    }

    [Fact]
    public async Task A_rejected_refresh_signs_you_out_with_a_clear_message()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens(expiresIn: 0)), (400, """{"error":"invalid_grant","error_description":"Invalid Refresh Token: Already Used"}"""));
        var (svc, store) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");
        var ex = await Assert.ThrowsAsync<AccountException>(() => svc.DownloadAsync());
        Assert.Contains("sign in again", ex.Message);
        Assert.False(svc.SignedIn);
        store.Delete();
    }

    [Fact]
    public async Task Browser_sign_in_uses_pkce_and_a_loopback_redirect()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens(provider: "google")));
        var (svc, store) = New(fake.Url);
        string? opened = null;
        var old = AccountService.OpenBrowser;
        AccountService.OpenBrowser = url =>
        {
            opened = url;
            // Play the browser: Supabase redirects back to VYTRIX's loopback address with a code.
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = Task.Run(async () => { using var http = new HttpClient(); await http.GetAsync(q["redirect_to"] + "?code=auth-code-123"); });
        };
        try { await svc.SignInWithProviderAsync("google"); }
        finally { AccountService.OpenBrowser = old; }

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(opened!).Query);
        Assert.StartsWith(fake.Url + "/auth/v1/authorize?", opened);
        Assert.Equal("google", query["provider"]);
        Assert.Equal("s256", query["code_challenge_method"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_to"]);

        var exchange = fake.Requests[0].Body.RootElement;
        Assert.Equal("/auth/v1/token?grant_type=pkce", fake.Paths[0].Replace("POST ", ""));
        Assert.Equal("auth-code-123", exchange.GetProperty("auth_code").GetString());
        var verifier = exchange.GetProperty("code_verifier").GetString()!;
        Assert.Equal(AccountService.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), query["code_challenge"]);   // challenge matches the verifier
        Assert.Equal("google", svc.Session!.Provider);
        store.Delete();
    }

    [Fact]
    public async Task Deleting_the_account_calls_the_server_and_signs_out()
    {
        using var fake = new AssistantTests.FakeClaude((200, Tokens()), (204, ""));
        var (svc, _) = New(fake.Url);
        await svc.SignInWithEmailAsync("gamer@example.com", "correct horse battery");
        await svc.DeleteAccountAsync();
        Assert.Equal("/rest/v1/rpc/delete_my_account", fake.Paths[1]);
        Assert.False(svc.SignedIn);
    }

    [Fact]
    public async Task Without_a_configured_server_nothing_is_sent()
    {
        var svc = new AccountService("", "", new StoredKey($"test-session-{Guid.NewGuid():N}.bin", "test"));
        var ex = await Assert.ThrowsAsync<AccountException>(() => svc.MethodsAsync());
        Assert.Contains("aren't set up", ex.Message);
    }

    [Theory]
    [InlineData(400, """{"error":"invalid_grant","error_description":"Invalid login credentials"}""", "don't match")]
    [InlineData(400, """{"error_description":"Email not confirmed"}""", "Confirm your email")]
    [InlineData(422, """{"msg":"User already registered"}""", "already an account")]
    [InlineData(429, "{}", "Too many attempts")]
    [InlineData(503, "", "having trouble")]
    public void Server_errors_are_explained_in_plain_words(int status, string body, string expected) =>
        Assert.Contains(expected, AccountService.Friendly((System.Net.HttpStatusCode)status, body));
}
