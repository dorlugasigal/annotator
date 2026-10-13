cask "annotator" do
  version "0.1.0-beta.3,21"
  sha256 "1e4b09a6dee20d791fc229a106bc85c655d189a25df8fa6525daf4a6ed3e15ec"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.3/annotator-0.1.0-beta.3-21-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
