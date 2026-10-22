# Annotator for Homebrew

This is the Homebrew tap for [Annotator](https://getannotator.app/), a free,
open-source app for drawing on your screen as you explain: circles, arrows,
highlights, spotlight, zoom and blur over any app.

## Install

```sh
brew install --cask dorlugasigal/annotator/annotator
```

Homebrew adds this tap the first time you run it. Annotator needs macOS 14 or
later, on an Apple Silicon or Intel Mac. It runs from the menu bar, not the Dock.

## Update

Save your boards and quit Annotator, then run:

```sh
brew update
brew upgrade --cask dorlugasigal/annotator/annotator
```

## Uninstall

```sh
brew uninstall --cask dorlugasigal/annotator/annotator
```

Your boards and settings stay on your Mac.

## About this tap

Each release writes `Casks/annotator.rb` from the signed and notarized download
on [GitHub releases](https://github.com/dorlugasigal/annotator/releases), so
this repository doesn't take pull requests. The app's source, issues and
contributions are in [dorlugasigal/annotator](https://github.com/dorlugasigal/annotator).
