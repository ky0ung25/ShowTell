# Code signing

Unsigned builds work, but both operating systems warn the first time they open:

- **macOS:** *Apple could not verify "ShowTell" is free of malware.* Gatekeeper shows this for any app that isn't notarized by Apple.
- **Windows:** *Windows protected your PC* (SmartScreen), with "Unknown publisher".

Neither warning means something was found in the app. Both go away only with a paid signing certificate. The GitHub workflow is already set up for both: add the secrets below, push a new `v*` tag, and the release is signed. With no secrets it builds unsigned, as now.

## macOS: Developer ID + notarization (removes the warning)

Needs the [Apple Developer Program](https://developer.apple.com/programs/) (US$99/year) and a Mac for the one-time setup.

1. **Certificate.** In Xcode → Settings → Accounts → Manage Certificates, add a **Developer ID Application** certificate. Then in Keychain Access, right-click it → Export → `.p12` with a password.
2. **Notary API key.** In [App Store Connect → Users and Access → Integrations → App Store Connect API](https://appstoreconnect.apple.com/access/integrations/api), create a key with the **Developer** role. Download the `.p8` and note the Key ID and the Issuer ID.
3. **Add the repository secrets** (run in the repo folder):

   ```bash
   base64 -i DeveloperID.p12 | gh secret set MACOS_CERT_P12
   gh secret set MACOS_CERT_PASSWORD          # the .p12 password
   base64 -i AuthKey_XXXXXXXXXX.p8 | gh secret set APPLE_API_KEY_P8
   gh secret set APPLE_API_KEY_ID             # e.g. XXXXXXXXXX
   gh secret set APPLE_API_ISSUER             # the issuer UUID
   ```

The macOS job then signs with the hardened runtime, notarizes the app and the .dmg, and staples the tickets, so it opens with no warning, even offline.

Building on your own Mac instead:

```bash
SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
NOTARY_KEY_PATH=AuthKey_XXXXXXXXXX.p8 NOTARY_KEY_ID=XXXXXXXXXX NOTARY_ISSUER=<issuer uuid> \
./macos/build.sh
```

A signed app also keeps its Screen Recording, Accessibility and Input Monitoring permissions across updates. Ad-hoc signed builds may need them granted again after each update.

## Windows: Azure Artifact Signing

Needs an Azure subscription and [Artifact Signing](https://azure.microsoft.com/products/artifact-signing) (formerly Trusted Signing, about US$10/month on the Basic plan). Individual developers must be in the US or Canada and pass Microsoft's identity validation.

1. Follow the [Artifact Signing quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart): create a signing account, complete identity validation, and create a **Public Trust** certificate profile.
2. Create an app registration (Microsoft Entra ID) with a client secret, and give it the **Artifact Signing Certificate Profile Signer** role on the signing account.
3. Add the secrets and variables:

   ```bash
   gh secret set AZURE_TENANT_ID
   gh secret set AZURE_CLIENT_SECRET
   gh variable set AZURE_SIGNING_ENDPOINT --body https://eus.codesigning.azure.net/   # your account's region
   gh variable set AZURE_SIGNING_ACCOUNT --body <signing account name>
   gh variable set AZURE_CERTIFICATE_PROFILE --body <certificate profile name>
   gh secret set AZURE_CLIENT_ID              # set this last: it turns signing on
   ```

Signing shows your name as the publisher instead of "Unknown publisher". SmartScreen also weighs download reputation, so a newly signed app can still warn for its first downloads until reputation builds up.
