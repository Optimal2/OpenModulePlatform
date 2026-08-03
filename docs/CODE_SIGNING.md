# Code signing with Azure Trusted Signing

Official Optimal2 releases sign every first-party `.exe`/`.dll` with
[Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
before the binaries are zipped into artifact packages. Signed binaries carry an
`Optimal2 AB` signature with an RFC 3161 timestamp, are trusted by SmartScreen
and Smart App Control, and remain valid after certificate rotation.

Signing is opt-in: without a configuration file the packaging pipeline runs
exactly as before and produces unsigned developer builds.

## One-time Azure setup (operator steps)

1. **Azure subscription** — use or create a subscription owned by Optimal2 AB.
2. **Create the Trusted Signing account** — Azure Portal → *Trusted Signing
   Accounts* → *Create*. Pick a nearby region (West Europe), Basic SKU
   ($9.99/month, 5 000 signatures/month).
3. **Identity validation** — in the Trusted Signing account, create a new
   *Identity validation* of type *Public*. Enter Optimal2 AB's legal name,
   organisation number, and a verifiable company email. Validation typically
   completes within a few days; respond promptly if the validation team emails.
4. **Certificate profile** — once validation is complete, create a
   *Certificate profile* of type *Public Trust* bound to the validated
   identity. The profile name goes into the config below.
5. **Grant the signer role** — on the Trusted Signing account, assign the role
   `Trusted Signing Certificate Profile Signer` to whoever signs:
   - your own user account for interactive signing (`az login`), and/or
   - a service principal (app registration) for unattended/CI signing; set
     `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` in the build
     environment.
6. **Install the Windows SDK signing tools** on the build machine (the
   "Windows SDK Signing Tools for Desktop Apps" feature provides
   `signtool.exe`). The Trusted Signing client library is downloaded from
   NuGet automatically on first use.

## Enable signing in the packaging pipeline

Copy `scripts/deployment/trusted-signing.sample.json` to
`scripts/deployment/trusted-signing.json` (git-ignored) and fill in your
values:

```json
{
  "Endpoint": "https://weu.codesigning.azure.net",
  "CodeSigningAccountName": "<your Trusted Signing account name>",
  "CertificateProfileName": "<your certificate profile name>"
}
```

The endpoint host depends on the account region (`weu` = West Europe; see the
account overview page for the exact URI).

`package-hostagent-first.ps1` then signs every component's publish output and
the Bootstrapper automatically before zipping. Alternatives:

- pass `-CodeSigningConfigPath <path>` explicitly, or
- set the `OMP_TRUSTED_SIGNING_CONFIG` environment variable.

Sign something manually (for example a hotfix binary):

```powershell
.\scripts\deployment\sign-artifacts.ps1 -Path C:\path\to\publish-folder
```

Authentication uses `DefaultAzureCredential`: run `az login` first for
interactive use, or set the `AZURE_*` service principal variables.

## What gets signed

Only first-party, not-yet-signed binaries matching the module name patterns in
`sign-artifacts.ps1` (OpenModulePlatform.*, IbsPackager.*, EArkivChecker.*,
LogSearch.*, iKrock2.*, VajSkrivare.*, ODVGateway.*, Dokumentbibliotek.*).
Microsoft and third-party dependencies already carry valid signatures and are
left untouched, which also keeps the signature volume well inside the Basic
tier quota.

## Verifying a release

```powershell
Get-AuthenticodeSignature C:\path\to\OpenModulePlatform.HostAgent.WindowsService.exe
signtool verify /pa /v C:\path\to\OpenModulePlatform.HostAgent.WindowsService.exe
```

A valid official release shows `Status: Valid` with an `Optimal2 AB` signer
certificate issued by Microsoft's public trust CA and a timestamp from
`timestamp.acs.microsoft.com`.
