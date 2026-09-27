cask "annotator" do
  version "0.1.0-alpha.7,15"
  sha256 "01539bd2b808cc363ff06f75827df95cb9a4e18d4e4ef4bc9fc5de3fad1f2bc2"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.7/annotator-0.1.0-alpha.7-15-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
