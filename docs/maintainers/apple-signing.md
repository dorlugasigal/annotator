# Apple signing, notarization and Sparkle

Mac releases need three credentials. Create them once, keep encrypted backups
outside the repository, and store them only as GitHub secrets in the
`release-signing` environment ([names](README.md#names)).

## Developer ID certificate

1. An Apple Developer Program membership (not Enterprise) is required.
2. As the account holder or an admin, create a **Developer ID Application**
   certificate. Apple Development and Developer ID Installer certificates don't
   work for this.
3. In Keychain Access, export the certificate **with its private key** as a
   password-protected P12.
4. Set `DEVELOPER_ID_P12_BASE64` (`base64 -i cert.p12 | gh secret set
   DEVELOPER_ID_P12_BASE64 --env release-signing`), `DEVELOPER_ID_P12_PASSWORD`,
   and the variables `DEVELOPER_ID_IDENTITY` (the full identity string) and
   `APPLE_TEAM_ID`.

## Notarization key

1. In App Store Connect, create a **team API key** with the least role Apple
   allows for notarization. Note its Key ID and Issuer ID and download the P8
   once.
2. Set `NOTARY_KEY_P8_BASE64`, `NOTARY_KEY_ID` and `NOTARY_ISSUER_ID`.

The workflow uses the team-key form of `notarytool` (`--key`, `--key-id`,
`--issuer`), never an Apple ID password.

## Sparkle update key

1. Generate an Ed25519 key pair with `generate_keys` from the pinned Sparkle
   distribution in `apps/macos/Packages/SparkleDistribution`.
2. Export the private seed and keep an offline backup. Losing it breaks the
   update chain for every installed copy.
3. Set `SPARKLE_PRIVATE_KEY` and `SPARKLE_PUBLIC_KEY`.

Never generate a new key during a CI run. The public key is built into the
app; changing it needs a release signed with the old key first.

## Check a published build

```sh
codesign --verify --deep --strict --verbose=2 /Applications/Annotator.app
spctl --assess --type execute --verbose /Applications/Annotator.app
xcrun stapler validate /Applications/Annotator.app
```
