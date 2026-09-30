cask "annotator" do
  version "0.1.0-alpha.8,16"
  sha256 "bb839114665d830603b0d50c765431fc6c44873c76e056c8cfe6dd313316af85"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.8/annotator-0.1.0-alpha.8-16-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
