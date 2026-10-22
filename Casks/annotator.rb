cask "annotator" do
  version "0.1.0-beta.11,29"
  sha256 "ff01228ab87f5a764adc8d55f38c582ca7cb3d947bb205eba14a9f7e304aff0a"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.11/annotator-0.1.0-beta.11-29-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
