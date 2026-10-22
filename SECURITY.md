# Security policy

## Report a security problem

Please report security problems privately, not in a public issue. Use
[Report a vulnerability](https://github.com/dorlugasigal/annotator/security/advisories/new)
on the repository's Security tab. The report stays private between you and the
maintainers until a fix is released.

Include:

- the Annotator version and build, from Settings > About
- your OS version (macOS or Windows) and computer model
- what you did and what happened, with steps to reproduce it if you can

Use made-up data. Don't send credentials, private screenshots or boards with
private details.

## What's covered

The Annotator apps for Mac and Windows, their downloads (disk image, ZIPs,
Microsoft Store package), the update feed and signatures, the Homebrew cask,
the winget package, the release workflows in this repository and the website.
Only the latest release gets fixes, so update before you report.

Some limits are documented and aren't security problems. Blur, Pixelate and
Distort are visual effects, not secure redaction. App or window sharing may
leave drawings out. Hiding or quitting Annotator removes covers. See
[screen-sharing limits](docs/compatibility.md).
