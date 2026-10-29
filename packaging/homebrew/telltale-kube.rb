# Homebrew cask (lives in a tap, e.g. aneillans/homebrew-tap). The distribute workflow bumps version + sha256.
cask "telltale-kube" do
  arch arm: "arm64", intel: "x64"

  version "0.1.0"
  sha256 arm:   "0000000000000000000000000000000000000000000000000000000000000000",
         intel: "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/aneillans/telltale-kube/releases/download/v#{version}/uk.co.neillans.telltalekube-osx-#{arch}-Portable.zip"
  name "Telltale Kube"
  desc "Lightweight, secure Kubernetes cluster management"
  homepage "https://github.com/aneillans/telltale-kube"

  livecheck do
    url :url
    strategy :github_latest
  end

  auto_updates true
  depends_on macos: ">= :ventura"

  app "TelltaleKube.app"

  zap trash: [
    "~/Library/Application Support/TelltaleKube",
  ]
end
