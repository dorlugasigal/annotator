# Homebrew packaging

People install Annotator with `brew install --cask dorlugasigal/annotator/annotator`.
That cask lives in the [`dorlugasigal/homebrew-annotator`](https://github.com/dorlugasigal/homebrew-annotator)
tap; the Release workflow writes it from the newest published release
([docs/releasing.md](../../docs/releasing.md)). Its version is the release's
plain version, such as `0.2.0`.

`tools/release/package-app.py` writes the cask with the release's ZIP and its
SHA-256. It needs a Release `Annotator.app` signed with Developer ID and
stapled, plus the HTTPS download and homepage URLs, and it refuses development
or ad-hoc signatures. The Release workflow runs it; see
[docs/maintainers/apple-signing.md](../../docs/maintainers/apple-signing.md).

To try a development build on your own Mac, build it with `make signed-dev`
(or `make app`) and open it from `build/`. It is called Annotator Dev, so it
runs beside an installed Annotator.
