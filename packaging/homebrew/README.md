# Homebrew packaging

People install Annotator with `brew install --cask dorlugasigal/annotator/annotator`.
That cask lives in the [`dorlugasigal/homebrew-annotator`](https://github.com/dorlugasigal/homebrew-annotator)
tap; the Release workflow writes it from the published release
([docs/releasing.md](../../docs/releasing.md)). This page covers trying a cask
for a development build on your own Mac.

## Local development cask

`tools/release/package-app.py` packages a signed development app into a ZIP,
a SHA-256 manifest and a cask. `setup-preview-tap.sh` puts that cask in a
local-only `dorlugasigal/annotator-preview` tap; it creates no repository and
installs nothing.

```sh
make signed-dev                      # needs an Apple Development certificate
python3 tools/release/package-app.py --channel preview --output build/distribution/preview-<label>
bash tools/release/setup-preview-tap.sh "$PWD/build/distribution/preview-<label>"
```

Save your drawings and quit every running Annotator copy, then:

```sh
brew install --cask dorlugasigal/annotator-preview/annotator-dev
open "/Applications/Annotator Dev.app"
```

The cask points at a local file URL, so it works only on the Mac that built
it. Use a new output folder for each package. A development signature is not
notarized: if macOS blocks the app, use a notarized release instead of turning
off Gatekeeper or removing quarantine. Uninstalling the cask keeps boards,
saved shapes and settings.

## Release packages

`package-app.py --channel release` refuses development or ad-hoc signatures.
It needs a Release `Annotator.app` signed with Developer ID and stapled, plus
the HTTPS download and homepage URLs. The Release workflow runs it; see
[docs/maintainers/apple-signing.md](../../docs/maintainers/apple-signing.md).
