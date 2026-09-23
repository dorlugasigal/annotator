cask "annotator-preview" do
  version "0.1.0-alpha.2,10"
  sha256 "eb9dd90e177d00a543dea770507e227f42d6031dbc241dbc84889f02e34b3604"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.2/annotator-0.1.0-alpha.2-10-arm64.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://dorlugasigal.github.io/annotator"

  depends_on arch: :arm64
  depends_on macos: :sonoma

  conflicts_with cask: "annotator"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
