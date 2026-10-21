cask "annotator" do
  version "0.1.0-beta.9,27"
  sha256 "7e3a39b8c60a73181b2fa5ea6e778157993ec47267e8989b9b95b07d3681b9a7"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.9/annotator-0.1.0-beta.9-27-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
