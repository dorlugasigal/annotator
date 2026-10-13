cask "annotator" do
  version "0.1.0-beta.6,24"
  sha256 "dce5e1e233f09ce0029756cecf0cba6912bda7311d4e677c23c2c2f28d318a7e"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.6/annotator-0.1.0-beta.6-24-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
