# Shared helpers for VYTRIX QA scripts (dot-source this file). ASCII only.

# Creates an isolated VYTRIX data folder and points the app at it through VYTRIX_DATA_DIR,
# so QA never touches the real settings, journal or logs. With -TermsAccepted the
# Terms of Agreement are pre-accepted so pages can be tested directly.
function New-QaDataDir([switch]$TermsAccepted) {
    $dir = Join-Path $env:TEMP ("vytrix-qa-data-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    if ($TermsAccepted) {
        # Update checks off: QA must not contact GitHub or show update prompts (the update test enables them itself).
        '{ "acceptedTermsVersion": "1.1", "acceptedTermsAt": "2026-01-01T00:00:00", "checkUpdatesOnStart": false, "gitHubFeedIntroduced": true }' | Set-Content (Join-Path $dir 'settings.json') -Encoding ascii
    }
    $env:VYTRIX_DATA_DIR = $dir
    return $dir
}
