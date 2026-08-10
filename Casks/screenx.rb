cask "screenx" do
  version "0.3.0"
  sha256 "d69fcc42a5d785dea5dee5fcbc799367983d17f74678966a899b85c64f640f2b"

  url "https://github.com/BrandonDoster/ScreenX/releases/download/v#{version}/ScreenX_universal.dmg"
  name "ScreenX"
  desc "Local screen capture and annotation tool"
  homepage "https://github.com/BrandonDoster/ScreenX"

  app "ScreenX.app"

  # ScreenX keeps one settings file. Screenshots live in a folder the user
  # chose, so they are never removed here.
  zap trash: "~/Library/Application Support/ScreenX"
end
