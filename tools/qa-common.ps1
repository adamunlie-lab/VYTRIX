# Shared helpers for VYTRIX QA scripts (dot-source this file). ASCII only.

# Creates an isolated VYTRIX data folder and points the app at it through VYTRIX_DATA_DIR,
# so QA never touches the real settings, journal or logs. With -TermsAccepted the
# Terms of Agreement are pre-accepted so pages can be tested directly.
$script:QaDirs = @()

# Deletes the data folders this script created (call at the end of a QA script, after its VYTRIX has closed).
function Remove-QaDataDirs {
    foreach ($d in $script:QaDirs) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    $script:QaDirs = @()
}

function New-QaDataDir([switch]$TermsAccepted, [string]$Theme = '', [switch]$SignedOut) {
    $dir = Join-Path $env:TEMP ("vytrix-qa-data-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $script:QaDirs += $dir
    if ($TermsAccepted) {
        # Update checks off: QA must not contact GitHub or show update prompts (the update test enables them itself).
        # The one-time theme introductions are marked done, so the requested theme isn't replaced at startup.
        $themeJson = if ($Theme) { ", `"theme`": `"$Theme`", `"seasonalThemes`": false, `"christmasIntroduced`": true, `"cherryBlossomIntroduced`": true" } else { '' }
        ('{ "acceptedTermsVersion": "1.5", "acceptedTermsAt": "2026-01-01T00:00:00", "checkUpdatesOnStart": false, "gitHubFeedIntroduced": true' + $themeJson + ' }') | Set-Content (Join-Path $dir 'settings.json') -Encoding ascii
    }
    # VYTRIX needs an account to open. QA copies get a saved test session (encrypted for this
    # Windows user, like the real one) and an account server address that refuses connections,
    # so the session check fails as "offline" and the copy opens without contacting the real server.
    # -SignedOut leaves the session out, so the copy opens on its sign-in screen.
    if (-not $SignedOut) {
        Add-Type -AssemblyName System.Security
        $session = '{ "userId": "qa-user", "email": "qa@example.test", "provider": "email", "refreshToken": "qa-refresh" }'
        $entropy = [Text.Encoding]::UTF8.GetBytes('VYTRIX assistant key v1')
        $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($session), $entropy, 'CurrentUser')
        [IO.File]::WriteAllBytes((Join-Path $dir 'account-session.bin'), $bytes)
        $env:VYTRIX_ACCOUNTS_URL = 'http://127.0.0.1:9'
    } else {
        Remove-Item Env:VYTRIX_ACCOUNTS_URL -ErrorAction SilentlyContinue
    }
    $env:VYTRIX_DATA_DIR = $dir
    # QA copies must never launch the user's Ollama: one started from a restricted test shell
    # can't serve, and it would block the user's own VYTRIX from starting Ollama properly.
    $env:VYTRIX_QA_NO_OLLAMA_START = '1'
    return $dir
}
