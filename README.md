# Annotator

## Draw on your Mac.

Circle a detail, connect ideas with arrows, or sketch over the app you're using.
Keep your drawings editable on the live desktop.

**All tools are free.** No account, paid tiers or subscription.

[Download Annotator](https://annotator-desktop.netlify.app/downloads/) ·
[Website](https://annotator-desktop.netlify.app/) ·
[Help](https://annotator-desktop.netlify.app/docs/) ·
[AI skill](https://annotator-desktop.netlify.app/ai/)

macOS 14 or later · Apple Silicon (M-series) · Public alpha

An alpha is an early test release. The Downloads page lists the current version
and known limits. Check Apple menu > About This Mac for your chip and macOS version.

[![Annotator's native toolbar and an editable diagram over a Mac desktop window](https://annotator-desktop.netlify.app/demo/annotator-ad-poster.jpg)](https://annotator-desktop.netlify.app/#native-demo)

**[Watch the desktop demo](https://annotator-desktop.netlify.app/#native-demo)**
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

**[Choose the current download for Apple Silicon](https://annotator-desktop.netlify.app/downloads/)**
and read its [release notes](https://github.com/dorlugasigal/annotator/releases)
before installing. This is a testing release with known limits, not a stable
release. [All releases](https://github.com/dorlugasigal/annotator/releases).

Keep a backup of your boards. Stop screen sharing before quitting Annotator:
quitting removes covers. Save your drawings and quit other Annotator copies.
Unzip the download, move
**Annotator.app** to **Applications**, then open it normally. Use the app ZIP,
not GitHub's **Source code** archives.

If macOS blocks the app, stop and [report the exact message](https://github.com/dorlugasigal/annotator/issues/new/choose).
Do not remove quarantine or disable security checks.

### Install with Homebrew

The published cask is `annotator`, including for alpha releases. Installation
and migration on another Mac still need verification.

```sh
brew tap dorlugasigal/annotator https://github.com/dorlugasigal/annotator
brew install --cask dorlugasigal/annotator/annotator
```

Save your work, stop screen sharing and quit Annotator before updating:

```sh
brew update
brew upgrade --cask dorlugasigal/annotator/annotator
```

If you installed the older `annotator-preview` cask, do not install a second
copy alongside it. Save your boards, stop sharing and quit Annotator, then
replace only the old cask:

```sh
brew uninstall --cask dorlugasigal/annotator/annotator-preview
brew install --cask dorlugasigal/annotator/annotator
```

Do not add `--zap`; keep your boards and preferences. The install name does
not change the release's alpha status. If macOS blocks installation, report
the message rather than bypassing its security checks.

## First use

Choose a tool in the floating toolbar. With Pen, drag on your screen.
Click **Use apps** to work in the app underneath while drawings stay visible.
Press **Control-Shift-2** to draw again; change this shortcut in Settings >
Shortcuts. **Control-2** shows or hides drawings and tools together, including
covers. Find Settings and Help in Annotator's menu-bar menu.

## Privacy and alpha limits

Drawing and live screen effects run locally. Drawing needs no screen access;
live zoom and pixel effects need **Screen Recording** permission. Zoom gestures
over other apps also need **Accessibility** permission. An AI app controlling
Annotator may send screenshots and text to its model provider; check that app's settings.

VoiceOver support is incomplete. Other hardware, multiple displays, long
sessions and screen-sharing apps still need wider testing. App/window-only
sharing may omit annotations. Check what the other person sees before sharing
private content.

Blur, Pixelate and Distort are visual effects, not secure redaction. Hiding or
quitting Annotator, or an app crash, removes visible covers. Remove private
details from the material you plan to share.

Editable boards keep original pasted images, including parts hidden by covers.
Do not share a board containing private details. Check any exported PNG before
sharing it; blur can still leave details readable.

[Capture and compatibility limits](https://annotator-desktop.netlify.app/docs/#limitations) ·
[Privacy](https://annotator-desktop.netlify.app/privacy/)

## Feedback and optional support

[Report a problem or share feedback](https://github.com/dorlugasigal/annotator/issues/new/choose).
Include your app version/build, macOS version and the steps to reproduce it.
Settings > About > Copy support info gathers app and system details without
drawings or screenshots. Review it before sharing.
Issues are public. Use fictional data; never attach credentials, private
desktop screenshots or unrelated documents.

**[Buy me a coffee](https://buymeacoffee.com/dorlugasigal)** if Annotator helps
you. All tools stay free whether you contribute or not.

## About this repository

Annotator is proprietary software, free to use. This public repository hosts
the generated website, app releases, release notes, Homebrew casks and issue
forms. The application and website authoring source are maintained privately.
