cask "screenx" do
  version "0.2.1"
  sha256 "2ab5240b77c1d1ece1000832b2280f20f18da6e124306d3c4b63579a9cdb92a9"

  url "https://github.com/BrandonDoster/ScreenX/releases/download/v#{version}/ScreenX_#{version}_universal.dmg"
  name "ScreenX"
  desc "Local screen capture and annotation tool"
  homepage "https://github.com/BrandonDoster/ScreenX"

  app "ScreenX.app"

  # ScreenX keeps one settings file. Screenshots live in a folder the user
  # chose, so they are never removed here.
  zap trash: "~/Library/Application Support/ScreenX"
end
