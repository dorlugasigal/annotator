cask "annotator" do
  version "0.1.0-alpha.9,17"
  sha256 "48d2dabe1f49dd1ab4207dd309f5b84b1b6045955f40692915858c5c9d161a4f"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.9/annotator-0.1.0-alpha.9-17-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
