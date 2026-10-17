# Code signing with Microsoft Artifact Signing

This guide gets the pet tracker's programs signed so Windows will run them. It is written for someone who has not used
Azure or code signing before. Expect the identity check (step 4) to take the longest, from 1 to 20 business days. The rest takes
an hour or two.

Signing is **opt-in**. If you do nothing, the build works exactly as before and produces unsigned files.

## Why we sign

* **Smart App Control blocks unsigned programs.** Windows 11's Smart App Control (SAC), when it is on in enforced mode,
  only runs programs that are signed by a trusted publisher or are well known to Microsoft. A freshly built, unsigned
  `pt-research.dll` is neither, so Windows refuses to load it (error `0x800711C7`). You cannot allow-list a single file. The only
  ways round it are to turn SAC off for good (it cannot be switched back on without reinstalling Windows) or to sign the files.
* **It helps the people who download the tool.** Signed files show your verified name instead of "Unknown publisher", and
  SmartScreen and antivirus programs trust them far sooner.

What gets signed: only our own files (`PetTracker.*.dll/.exe`, `pt-research.dll/.exe`, the test assembly). Third-party
NuGet DLLs (SharpPcap, LZ4, ...) are never touched.

Until your account is ready, use the [pcapng workflow](RESEARCH-WITHOUT-SIGNING.md).

## 1. Check that you are eligible

* An individual developer (not a company) can use Artifact Signing only if they are in the **United States or Canada**.
  Organizations have wider options, but that is not covered here.
* You need an Azure account with a **billing account whose Account Type is "Individual"**. The legal name and the address on
  the billing account must match your government-issued ID (driver's licence or passport) exactly. Check it at
  Azure portal, **Cost Management + Billing**, **Properties**.
* A phone with the **Microsoft Authenticator** app (used for the ID check).

## 2. What it costs

The **Basic** tier is about **US$9.99 per month** (it includes a few thousand signatures per month, far more than this project
needs). Prices change, so confirm on the Azure pricing page for Artifact Signing before you subscribe. You can delete the
account at any time to stop the charge.

## 3. Set up Azure

Everything here is in the [Azure portal](https://portal.azure.com).

1. **Register the resource provider.** Search for **Subscriptions**, open your subscription, then **Settings**,
   **Resource providers**. Search for `Microsoft.CodeSigning` and press **Register**. Wait until it says *Registered*.
2. **Create the Artifact Signing account.** Search for **Artifact Signing Accounts** and press **Create**.
   * Subscription and resource group: your own (create a resource group such as `pettracker-signing`).
   * Account name: any unique name, for example `pettracker-signing`. Write it down.
   * **Region:** pick the one closest to you, and **write down which one**: it decides the endpoint in step 7.
   * Pricing tier: **Basic**.
3. **Give yourself the roles.** Open the new account, then **Access control (IAM)**, **Add role assignment**. Add both of these
   to your own user (the same account you use with `az login`):
   * **Artifact Signing Identity Verifier** (lets you do the identity check).
   * **Artifact Signing Certificate Profile Signer** (lets you actually sign).

   Role changes can take a few minutes to take effect.

## 4. Identity validation (portal only, 1 to 20 business days)

This proves you are a real person. It can only be done in the portal, not from the command line.

1. In your Artifact Signing account choose **Objects**, **Identity validations**, then **New identity**.
2. Choose **Individual**, then **New Identity**, then **Public** (this is the type that Windows trusts).
3. Fill in your legal name and address **exactly as on your ID**.
4. Follow the link to the identity provider (**AU10TIX**). It asks you to photograph your ID and take a selfie.
5. Finish by getting a **Verified ID** into **Microsoft Authenticator** on your phone, as the portal explains.
6. Wait. The status changes from *In progress* to *Completed*. Nothing else can be finished until it says *Completed*.

If it is rejected, the usual causes are a name or address that differs from the ID or a blurry photo. Fix it and resubmit.

## 5. Create a Public Trust certificate profile

When the identity validation is *Completed*:

1. In the account choose **Objects**, **Certificate profiles**, **Create**, **Public Trust**.
2. Profile name: for example `pettracker-public`. Write it down.
3. Pick your completed identity validation, then **Create**.

## 6. Install the tools on your PC

In a normal PowerShell window:

```powershell
winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
winget install -e --id Microsoft.AzureCLI
```

Close the window and open a new one (so the new programs are found), then sign in:

```powershell
az login
```

A browser opens. Sign in with the same account that got the roles in step 3. If you have several subscriptions,
pick the one with the signing account (`az account set --subscription "<name>"`).

The client tools package contains `signtool.exe` and `Azure.CodeSigning.Dlib.dll`. The build finds them by itself (it also
looks in Program Files, `%LOCALAPPDATA%`, the Windows Kits folder and the NuGet cache). If it cannot, pass the paths:
`-p:SignToolPath=C:\...\signtool.exe -p:ArtifactSigningDlibPath=C:\...\Azure.CodeSigning.Dlib.dll`.
Any `signtool.exe` of version **10.0.22621 or newer** works; older ones do not understand `/dlib`.

## 7. Point the repo at your account

```powershell
Copy-Item signing\metadata.sample.json signing\metadata.json
notepad signing\metadata.json
```

Fill in the three values:

| Field | Value |
| --- | --- |
| `Endpoint` | The endpoint **for the region you picked in step 3** (table below) |
| `CodeSigningAccountName` | The account name from step 3 |
| `CertificateProfileName` | The profile name from step 5 |

Leave `ExcludeCredentials` as it is. It switches off the credential types that are slow or confusing, and leaves
**AzureCliCredential** (your `az login`) and EnvironmentCredential on.

| Region of your account | Endpoint |
| --- | --- |
| Brazil South | `https://brs.codesigning.azure.net` |
| Central US | `https://cus.codesigning.azure.net` |
| East US | `https://eus.codesigning.azure.net` |
| Japan East | `https://jpe.codesigning.azure.net` |
| Korea Central | `https://krc.codesigning.azure.net` |
| North Central US | `https://ncus.codesigning.azure.net` |
| North Europe | `https://neu.codesigning.azure.net` |
| Poland Central | `https://plc.codesigning.azure.net` |
| South Central US | `https://scus.codesigning.azure.net` |
| Switzerland North | `https://swn.codesigning.azure.net` |
| West Central US | `https://wcus.codesigning.azure.net` |
| West Europe | `https://weu.codesigning.azure.net` |
| West US | `https://wus.codesigning.azure.net` |
| West US 2 | `https://wus2.codesigning.azure.net` |
| West US 3 | `https://wus3.codesigning.azure.net` |

The list can grow; the current one is in the [quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart).
**The endpoint must match the region of the account**, or signing fails with a 403.

`signing/metadata.json` is listed in `.gitignore` and is never committed. It holds no secrets (your login does), but it is
yours.

## 8. Build and sign

```powershell
dotnet build -c Release -p:SignBuild=true
```

With `signing\metadata.json` in place, a plain `dotnet build` also signs; `-p:SignBuild=false` turns signing off again.
All files of a project are signed in one go, and files that already have a valid signature are skipped, so repeated builds
do not use up signatures. A published CLI is signed the same way:

```powershell
dotnet publish src/PetTracker.Research -c Release -r win-x64 --self-contained -o artifacts/pt-research
```

To sign other files by hand: `.\scripts\sign.ps1 path\to\file.exe other.dll`.

## 9. Check the result

```powershell
signtool verify /pa /v src\PetTracker.Research\bin\Release\net10.0-windows\pt-research.dll
```

(`signtool` is in the client tools folder or under `C:\Program Files (x86)\Windows Kits\10\bin\<version>\x64\`; or right-click the
file, **Properties**, **Digital Signatures**.) You should see "Successfully verified", your name as the signer, and a
timestamp. The signature chains to a Microsoft code signing root. Smart App Control now loads the file.

## Troubleshooting

**`403 Forbidden` when signing.** Almost always a region mismatch: the `Endpoint` in `metadata.json` is not the endpoint of the
region your account lives in. Compare the account's **Location** in the portal with the table in step 7. If it matches, the
second cause is a missing role: you need **Artifact Signing Certificate Profile Signer** on the account (or the certificate
profile), and it can take several minutes to apply.

**Credential errors ("DefaultAzureCredential failed", "No credential could be used").** Run `az login` again (the token
expires), and make sure you are in the right subscription (`az account show`). Do not remove `AzureCliCredential` from
the list; `ExcludeCredentials` should only name credentials you do *not* want tried. If another credential gets in the way
(for example a stale `AZURE_*` environment variable, or a wrong Visual Studio login), add its name to `ExcludeCredentials`.

**"signtool.exe not found", "Azure.CodeSigning.Dlib.dll was not found".** Install the client tools (step 6) and open a
new terminal, or pass `-p:SignToolPath=` / `-p:ArtifactSigningDlibPath=` as shown above.

**The build fails with SIGN006 / "placeholder values".** `metadata.json` still has the `<...>` text from the sample.

**Certificates last only 3 days, and why timestamping matters.** Artifact Signing issues short-lived certificates (valid for
about 3 days) that it renews automatically; you never handle them. A signature stays valid after the certificate expires **only if
it was timestamped** while the certificate was still valid. The build always adds a timestamp (`/tr http://timestamp.acs.microsoft.com
/td SHA256`), so files you signed last month still verify. Do not remove the timestamp options, and do not sign without network
access. Signatures without a timestamp stop being valid after the certificate expires.

**Signing "does not run" in Smart App Control itself.** SAC checks signatures when a program loads, not when it is built, so
signing during the build is not blocked. If `dotnet` or `signtool` is itself blocked, that is a different problem.

**Build without signing for a while.** `dotnet build -p:SignBuild=false`, or rename/delete `signing\metadata.json`.

## Sources

* Quickstart: <https://learn.microsoft.com/azure/artifact-signing/quickstart>
* Signing integrations (signtool, dlib, metadata file, credentials): <https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations>
