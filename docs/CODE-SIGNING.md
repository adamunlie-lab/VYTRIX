# Code signing VYTRIX

VYTRIX's installer isn't code-signed yet, so Windows SmartScreen shows "Windows protected your PC" when someone runs it for the first time. Signing proves the files come from you and haven't been changed. Over time it also stops that warning.

Getting a certificate needs **your** identity, so it's a step only the project owner can take. Once you have one, the build signs everything automatically.

## Options

| Option | Cost | Notes |
|---|---|---|
| **SignPath Foundation** | Free for open-source projects | You apply with the public GitHub repository. They review the project and sign builds through their service. |
| **Microsoft Trusted Signing** (Azure Artifact Signing) | Monthly fee | Microsoft verifies your identity, then signs from Azure. Check current eligibility for individual developers in your country. |
| **Buy an OV or EV certificate** | Yearly fee | From a certificate authority. You verify your identity, then the certificate goes on a hardware token or cloud HSM. |

Pricing and eligibility change, so check each provider's current terms before choosing.

SmartScreen also uses reputation. A newly signed app can still show the warning for a while, until enough people have downloaded and run it.

## Turning signing on in the VYTRIX build

1. Install your code-signing certificate so it appears in your Windows certificate store, or connect the hardware token.
2. Install the Windows SDK, which provides `signtool.exe`.
3. Set the certificate's SHA-1 thumbprint for the build:
   ```powershell
   $env:VYTRIX_SIGN_THUMBPRINT = "YOUR-CERTIFICATE-THUMBPRINT"
   ```
4. Optional: `$env:VYTRIX_SIGN_TIMESTAMP` sets your provider's timestamp server. The default is DigiCert's.
5. Publish as usual:
   ```powershell
   powershell -File tools\publish-release.ps1 -Version x.y.z -Notes "..."
   ```

With the thumbprint set, `build.ps1` signs `VYTRIX.exe`, `VYTRIX.dll`, `VytrixCore.dll` and the installer with SHA-256 and a timestamp. Without it, builds stay unsigned, exactly as before.

The auto-updater keeps checking each installer's SHA-256 against `latest.json` either way.
