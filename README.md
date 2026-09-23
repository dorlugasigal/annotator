# Annotator

Draw on your Mac desktop while keeping your apps open. Mark up a design,
explain a diagram or point out a detail during a call. Your drawings stay
editable.

**All tools are free.** No paid tiers, account or subscription.

[Website](https://dorlugasigal.github.io/annotator/) |
[Watch the demo](https://dorlugasigal.github.io/annotator/#native-demo) |
[Field guide](https://dorlugasigal.github.io/annotator/docs/) |
[Releases](https://github.com/dorlugasigal/annotator/releases)

[![Annotator's native toolbar and an editable diagram over a Mac desktop window](https://dorlugasigal.github.io/annotator/demo/annotator-ad-poster.jpg)](https://dorlugasigal.github.io/annotator/#native-demo)

Real Mac app footage, with scripted actions and fictional data. Click the image
to watch the demo.

## What you can do

- Sketch with the pen and Smart Draw, then edit shapes, arrows and text labels.
- Highlight a line, zoom into a detail, or add Blur and Pixelate effects.
- Move, resize and group annotations. Save a board and return to it later.
- Switch between drawing and using the apps underneath. Hide or restore your
  drawings and toolbar together.

Annotator works over the live desktop. It is not a screen recorder or a
standalone image editor.

## Download and install

Annotator is in **alpha**. Start with the
[published releases](https://github.com/dorlugasigal/annotator/releases) and
read the notes for the build you choose. If no app ZIP is listed under Assets,
there is no public installer yet. The website being live does not mean an app
release is available.

Download the `annotator-...-arm64.zip` asset, not either **Source code** archive.
Quit any running Annotator or Annotator Dev instance, extract the ZIP, and move
**Annotator.app** to **Applications**. Save your work before replacing an
existing installation.

If macOS cannot verify the app, says it is damaged, or your organization blocks
it, stop and [report the exact message](https://github.com/dorlugasigal/annotator/issues/new?template=bug-report.yml).
Do not remove quarantine or disable Gatekeeper.

### Homebrew

After a signed alpha release and its preview cask have been published:

```bash
brew tap dorlugasigal/annotator https://github.com/dorlugasigal/annotator
brew install --cask dorlugasigal/annotator/annotator-preview
```

For later preview updates:

```bash
brew update
brew upgrade --cask dorlugasigal/annotator/annotator-preview
```

The alpha uses `annotator-preview`. The stable `annotator` cask is a separate
track; do not install both.

## Requirements and current limits

| Requirement | Details |
| --- | --- |
| Mac | Apple Silicon |
| macOS | 14 or later; compatibility across supported versions is still being tested |
| Screen Recording permission | Needed for live pixel effects |
| Accessibility permission | Needed for live zoom gestures |

VoiceOver support is incomplete. Multi-monitor compatibility and long-session
performance still need wider testing. During screen sharing, check what the
other person sees: app/window-only sharing may omit desktop annotations.

Blur and Pixelate are visual effects, **not secure redaction**. Use fictional
data for tests and demonstrations.

See the [field guide](https://dorlugasigal.github.io/annotator/docs/) for
permissions, controls and capture limits, and the
[privacy page](https://dorlugasigal.github.io/annotator/privacy/) for data handling.

## Feedback and support

[Report a problem](https://github.com/dorlugasigal/annotator/issues/new?template=bug-report.yml)
or [share feedback](https://github.com/dorlugasigal/annotator/issues/new?template=feedback.yml).
Include the app version, macOS version and steps to reproduce the problem.
Issues are public: do not attach private desktop screenshots, credentials or
unrelated documents.

If Annotator helps you, you can [buy me a beer](https://buymeacoffee.com/dorlugasigal).
Support is optional and does not unlock features.

## About this repository

Annotator is proprietary software, free to use. This repository contains its
generated website, public release assets, Homebrew casks when published, and
issue forms. Application and website authoring source are maintained privately.
