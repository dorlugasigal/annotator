cask "annotator" do
  version "0.1.0-alpha.3,11"
  sha256 "6276a8e12859db3a9e781b99828c43639af276186b2a0b9e710c870e1ee3ba0f"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.3/annotator-0.1.0-alpha.3-11-arm64.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://dorlugasigal.github.io/annotator"

  depends_on arch: :arm64
  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
