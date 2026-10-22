# Security policy

## Supported versions

Only the latest release gets security fixes, on Mac and on Windows. Annotator
is in preview, so a fix comes in a new release, not as a patch to an older one.
Update to the [latest release](https://github.com/dorlugasigal/annotator/releases)
before you report.

## Report a security problem

Please report security problems privately, not in a public issue, discussion
or pull request. Use
[Report a vulnerability](https://github.com/dorlugasigal/annotator/security/advisories/new)
on the repository's Security tab. The report stays private between you and the
maintainers until a fix is released.

Include:

- the Annotator version and build, from Settings > About
- your OS version (macOS or Windows) and computer model
- what you did and what happened, with steps to reproduce it if you can
- what an attacker could do with the problem, if you know

Use made-up data. Don't send credentials, private screenshots or boards with
private details.

## What happens next

- Within 3 working days, we reply to say we got your report.
- Within 10 working days, we tell you whether we can reproduce the problem
  and how serious we think it is.
- We aim to release a fix within 90 days. Serious problems come first. We tell
  you if a fix will take longer, and why.
- We keep you updated in the private advisory until the fix is out.

## Disclosure

Please keep the problem private until a fixed release is out, or until 90
days have passed, whichever comes first. When the fix is released, we publish
the security advisory and request a CVE when it applies. We credit you in the
advisory unless you ask us not to.

If we decide the report is not a security problem, we tell you why. You can
then open a public issue.

## What's covered

The Annotator apps for Mac and Windows, their downloads (disk image, ZIPs,
Microsoft Store package), the update feed and signatures, the Homebrew cask,
the winget package, the release workflows in this repository and the website.

Some limits are documented and aren't security problems. Blur, Pixelate and
Distort are visual effects, not secure redaction. App or window sharing may
leave drawings out. Hiding or quitting Annotator removes covers. See
[screen-sharing limits](docs/compatibility.md).

Problems in other software, such as macOS, Windows, Homebrew or GitHub, belong
with that project. Tell us if Annotator makes such a problem worse.
