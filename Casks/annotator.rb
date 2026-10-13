cask "annotator" do
  version "0.1.0-beta.5,23"
  sha256 "7948a9ae6a91487aa3784967c3969b7573b87c368cea5c0407c29a00c5f5dc3d"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.5/annotator-0.1.0-beta.5-23-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
