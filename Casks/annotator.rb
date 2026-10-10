cask "annotator" do
  version "0.1.0-alpha.10,18"
  sha256 "2b9aef0e82a3da3c0c5ff585d0f914d834eaab6bcdedd2bbb60157adf6442cbc"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.10/annotator-0.1.0-alpha.10-18-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
