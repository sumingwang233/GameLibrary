# Code signing policy

GameLibrary intends to use free code signing provided by [SignPath.io](https://signpath.io/), with a certificate issued through the [SignPath Foundation](https://signpath.org/).

## Current status

The SignPath Foundation application is still under review. The v1.1.0 Windows binaries are therefore **unsigned** and Windows SmartScreen may identify them as coming from an unknown publisher.

Unsigned releases are identified in their release notes and include a `GameLibrary-v<version>-SHA256SUMS.txt` file. Users should download packages from the official [GitHub Releases](https://github.com/sumingwang233/GameLibrary/releases) page and verify the published hash.

## Signed releases

After trusted signing becomes available, release assets will be built from tagged source in the public [`sumingwang233/GameLibrary`](https://github.com/sumingwang233/GameLibrary) repository. GameLibrary executable files and the installer will be signed with Authenticode and an RFC 3161 SHA-256 timestamp. A release described as signed must pass signature verification before publication.

Users can inspect a downloaded installer in Windows PowerShell:

```powershell
Get-AuthenticodeSignature .\GameLibrary-Setup-v1.1.0.exe |
    Format-List Status,SignerCertificate,TimeStamperCertificate
```

For a signed release, the expected status is `Valid`. For v1.1.0, the expected status is `NotSigned`.

## Team roles

- Committer and reviewer: [sumingwang233](https://github.com/sumingwang233)
- Signing approver: [sumingwang233](https://github.com/sumingwang233)

## Privacy

GameLibrary has no telemetry and does not transfer the user's game library, scan results, configuration, or usage data to a networked service. The desktop app reads the latest public GitHub Release metadata for update notifications. An external program launched at the user's request may have its own network behavior and privacy policy.

Security issues should be reported privately to the repository owner before public disclosure.
