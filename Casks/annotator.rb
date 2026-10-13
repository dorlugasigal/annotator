cask "annotator" do
  version "0.1.0-beta.7,25"
  sha256 "7b031e13d9e670c0bf4d90c0147b280b198538812ded1eca68569b99832ad2ad"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-beta.7/annotator-0.1.0-beta.7-25-universal.zip"
  name "Annotator"
  desc "Native live desktop annotation"
  homepage "https://getannotator.app"

  depends_on macos: :sonoma

  conflicts_with cask: "annotator-preview"

  app "Annotator.app"

  uninstall quit: "com.dorlugasigal.annotator"

  caveats "Testing prerelease. Read the release notes and known limits. Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
end
