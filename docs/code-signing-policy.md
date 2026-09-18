# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Signed releases

Official Windows release assets are built from the tagged source in the public [`sumingwang233/GameLibrary`](https://github.com/sumingwang233/GameLibrary) repository. The release pipeline signs GameLibrary executable files and the installer with Authenticode and an RFC 3161 SHA-256 timestamp. A release must not be published when signing or signature verification fails.

Users can verify a downloaded executable in Windows PowerShell:

```powershell
Get-AuthenticodeSignature .\GameLibrary-Setup-v1.0.0.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

The expected status is `Valid`. Release asset hashes are published in `GameLibrary-v<version>-SHA256SUMS.txt`.

## Team roles

- Committer and reviewer: [sumingwang233](https://github.com/sumingwang233)
- Signing approver: [sumingwang233](https://github.com/sumingwang233)

## Privacy

GameLibrary has no telemetry and does not automatically transfer the user's game library, scan results, configuration, or usage data to any networked system. It operates on local files and local inter-process communication. An external program launched at the user's explicit request may have its own network behavior and privacy policy.

Security issues should be reported privately to the repository owner rather than disclosed in a public issue before a fix is available.
