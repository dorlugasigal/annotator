cask "annotator" do
  version "0.1.0-beta.4,22"
  sha256 "1d00c4fe6e8bfbe560b5bd1bae3249af40c1a21dc16cf9ff0a75822c0d7ba345"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.4/annotator-0.1.0-beta.4-22-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
