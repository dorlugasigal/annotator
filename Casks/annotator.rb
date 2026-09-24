cask "annotator" do
  version "0.1.0-alpha.4,12"
  sha256 "b38e76e931d2b2798c644bbe355ca04271acbda7fcf2840fbfe060deeee7f8a1"

  url "https://github.com/dorlugasigal/annotator/releases/download/v0.1.0-alpha.4/annotator-0.1.0-alpha.4-12-arm64.zip"
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
