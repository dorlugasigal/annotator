cask "annotator" do
  version "0.1.0-beta.10,28"
  sha256 "56ef2d83359b674a9459cfb7d3e0c37c0afa40a869e3e0cd8f93a5d800999f83"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.10/annotator-0.1.0-beta.10-28-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
