# Annotator

## Draw on your Mac.

Circle a detail, connect ideas with arrows, or sketch over the app you're using.
Keep your drawings editable on the live desktop.

**All tools are free.** No account, paid tiers or subscription.

[Download alpha.2](https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.2/annotator-0.1.0-alpha.2-10-arm64.zip) ·
[Website](https://dorlugasigal.github.io/annotator/) ·
[Field guide](https://dorlugasigal.github.io/annotator/docs/) ·
[AI skill](https://dorlugasigal.github.io/annotator/ai/)

macOS 14 or later · Apple Silicon · Public alpha.2, build 10

[![Annotator's native toolbar and an editable diagram over a Mac desktop window](https://dorlugasigal.github.io/annotator/demo/annotator-ad-poster.jpg)](https://dorlugasigal.github.io/annotator/#native-demo)

**[Watch the desktop demo](https://dorlugasigal.github.io/annotator/#native-demo)**
with playback controls, chapters and a text description.
Real Mac app footage with scripted, sped-up actions and fictional data.
Music: ["Chrome Funk" by Punch Deck](https://punchdeck.bandcamp.com/track/chrome-funk),
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), edited excerpt.

## Keep your app open

| What you want to do | Tools |
| --- | --- |
| Explain an idea | Pen, Smart Draw, shapes, arrows and text labels |
| Point out a detail | Highlighter, spotlight and live screen zoom |
| Refine a drawing | Move, resize, group and Undo; save an editable board |
| Get back to work | Switch between drawing and using the app underneath; hide or restore the drawings and toolbar together |

Annotator works on the live desktop. It does not record video or provide a
standalone image editor or OCR reader.

## Install the public alpha

**[Download alpha.2, build 10 for Apple Silicon](https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.2/annotator-0.1.0-alpha.2-10-arm64.zip)**
and read the [release notes](https://github.com/dorlugasigal/annotator/releases/tag/v0.1.0-alpha.2)
before installing. This is a testing release with known limits, not a stable
release. [All releases](https://github.com/dorlugasigal/annotator/releases).

Save your drawings and quit other Annotator copies. Unzip the download, move
**Annotator.app** to **Applications**, then open it normally. Use the app ZIP,
not GitHub's **Source code** archives.

If macOS blocks the app, stop and [report the exact message](https://github.com/dorlugasigal/annotator/issues/new/choose).
Do not remove quarantine or disable security checks.

### Homebrew install name

The intended Homebrew name is `annotator`, including for alpha releases.
Availability under this name has not yet been verified. Use the ZIP above
for now. Once the renamed cask is published, the install commands will be:

```sh
brew tap dorlugasigal/annotator https://github.com/dorlugasigal/annotator
brew install --cask dorlugasigal/annotator/annotator
```

After that change, save your work and quit Annotator before updating:

```sh
brew update
brew upgrade --cask dorlugasigal/annotator/annotator
```

If you already installed through Homebrew, wait for migration instructions
before changing install names. Do not install a second copy alongside it.
The shorter name does not change this build's alpha status.

## Privacy and alpha limits

Drawing and live screen effects run locally. Drawing needs no screen access;
live zoom and pixel effects need **Screen Recording** permission. Zoom gestures
over other apps also need **Accessibility** permission. An AI app controlling
Annotator may send screenshots to its model provider; check that app's settings.

VoiceOver support is incomplete. Other hardware, multiple displays, long
sessions and screen-sharing apps still need wider testing. App/window-only
sharing may omit annotations. Check what the other person sees before sharing
private content.

Blur, Pixelate and Distort are visual effects, not secure redaction. Hiding or
quitting Annotator, or an app crash, removes visible covers. Remove private
details from the material you plan to share.

[Capture and compatibility limits](https://dorlugasigal.github.io/annotator/docs/#limitations) ·
[Privacy](https://dorlugasigal.github.io/annotator/privacy/)

## Feedback and optional support

[Report a problem or share feedback](https://github.com/dorlugasigal/annotator/issues/new/choose).
Include your app version/build, macOS version and the steps to reproduce it.
Issues are public. Use fictional data; never attach credentials, private
desktop screenshots or unrelated documents.

**[Buy me a coffee](https://buymeacoffee.com/dorlugasigal)** if Annotator helps
you. All tools stay free whether you contribute or not.

## About this repository

Annotator is proprietary software, free to use. This public repository hosts
the generated website, app releases, release notes, Homebrew casks and issue
forms. The application and website authoring source are maintained privately.
