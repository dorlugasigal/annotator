cask "annotator" do
  version "0.1.0-beta.2,20"
  sha256 "0e636c6e7a1b5e94aee0ddc46d05f393a575f583a49ff264abe24dbf6820957e"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.2/annotator-0.1.0-beta.2-20-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
