cask "annotator" do
  version "0.1.0-alpha.6,14"
  sha256 "3d36239e19001f942576dbd0a904bc477db16d46755e466af065b2bfc6dad60d"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.6/annotator-0.1.0-alpha.6-14-arm64.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://annotator-desktop.netlify.app"

  depends_on arch: :arm64
  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
