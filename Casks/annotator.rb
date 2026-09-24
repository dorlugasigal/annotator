cask "annotator" do
  version "0.1.0-alpha.5,13"
  sha256 "ba757c72134fb599f6c10a07bb21a3ff7146828731862d18a3fb0c7f34961b86"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.5/annotator-0.1.0-alpha.5-13-arm64.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://annotator-desktop.netlify.app"

  depends_on arch: :arm64
  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
