cask "annotator" do
  version "0.1.0-beta.8,26"
  sha256 "35b39f94a2eb8b7b400b07ecde549abf2658a057bc9559533818d3fc90b78a52"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.8/annotator-0.1.0-beta.8-26-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
