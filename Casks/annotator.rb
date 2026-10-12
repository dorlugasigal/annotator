cask "annotator" do
  version "0.1.0-beta.1,19"
  sha256 "b97dc5f67d8454a01ec4a86d85a3db5f701090ae9e15580df1c63f43338590aa"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.1/annotator-0.1.0-beta.1-19-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
