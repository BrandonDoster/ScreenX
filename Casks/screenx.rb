cask "screenx" do
  version "1.0.0"
  sha256 "28006f8f7defc647871551b0b0bdc185e34c1a1f9185219a12c4ee9c453ed41e"

  url "https://github.com/BrandonDoster/ScreenX/releases/download/v#{version}/ScreenX_universal.dmg"
  name "ScreenX"
  desc "Local screen capture and annotation tool"
  homepage "https://github.com/BrandonDoster/ScreenX"

  app "ScreenX.app"

  # ScreenX keeps one settings file. Screenshots live in a folder the user
  # chose, so they are never removed here.
  zap trash: "~/Library/Application Support/ScreenX"
end
