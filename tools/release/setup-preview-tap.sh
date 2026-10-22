#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1/annotator-dev.rb" || ! -f "$1/annotator-dev-manifest.json" ]]; then
    printf '%s\n' 'Usage: bash tools/release/setup-preview-tap.sh /absolute/package-output-directory' >&2
    exit 1
fi
package="$(cd "$1" && pwd)"
export HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_ANALYTICS=1
brew --version >/dev/null
tap=dorlugasigal/annotator-preview
destination="$(brew --repository)/Library/Taps/dorlugasigal/homebrew-annotator-preview"
if [[ -e "$destination/Casks/annotator-dev.rb" ]]; then
    printf '%s\n' "A preview cask already exists at $destination/Casks/annotator-dev.rb. Review it before replacing it." >&2
    exit 1
fi
python3 - "$package" "$(cd "$(dirname "$0")" && pwd)/package-app.py" <<'PY'
import hashlib, importlib.util, json, sys
from pathlib import Path
directory = Path(sys.argv[1])
manifest = json.loads((directory / "annotator-dev-manifest.json").read_text())
if manifest.get("channel") != "preview" or manifest.get("published") is not False:
    raise SystemExit("Only an unpublished local preview is accepted.")
if (manifest.get("token"),manifest.get("identifier"),manifest.get("name")) != (
        "annotator-dev","com.dorlugasigal.annotator.dev","Annotator Dev.app"):
    raise SystemExit("The preview identity is invalid.")
archive = directory / manifest["archive"]
if archive.parent != directory or manifest.get("downloadURL") != archive.as_uri():
    raise SystemExit("The preview archive must be local to this package directory.")
with archive.open("rb") as file:
    if hashlib.file_digest(file, "sha256").hexdigest() != manifest["sha256"]:
        raise SystemExit("The preview archive checksum does not match.")
spec = importlib.util.spec_from_file_location("package_app",sys.argv[2])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
expected = module.cask(manifest,manifest["downloadURL"],manifest["sha256"],
                       manifest.get("homepage","https://dorlugasigal.github.io/annotator/"),"preview")
if (directory/"annotator-dev.rb").read_text() != expected:
    raise SystemExit("The cask differs from the verified packager output. Regenerate it rather than installing custom scripts.")
PY
prior_mode="$(brew developer state)"
restore_mode() {
    if [[ "$prior_mode" == *disabled* ]]; then brew developer off >/dev/null; fi
}
trap restore_mode EXIT
if [[ ! -d "$destination" ]]; then brew tap-new --no-git "$tap"; fi
mkdir -p "$destination/Casks"
cp "$package/annotator-dev.rb" "$destination/Casks/annotator-dev.rb"
brew info --cask "$tap/annotator-dev"
brew install --cask --dry-run "$tap/annotator-dev"
printf '\n%s\n' 'No app was installed. Save drawings and quit all running copies before using:'
printf '%s\n' "brew install --cask $tap/annotator-dev"
