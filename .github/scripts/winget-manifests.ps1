# Writes the 3 winget manifests (version / installer / defaultLocale) for a release.
# Usage: pwsh .github/scripts/winget-manifests.ps1 -Version 1.2.3 -Exe out/WeatherWidget.exe -Out winget-out
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'

$id   = 'ThanhNguyenVN93.WeatherWidget'
$repo = 'https://github.com/ThanhNguyenVN93/weather-widget'
$url  = "$repo/releases/download/v$Version/WeatherWidget.exe"
$sha  = (Get-FileHash $Exe -Algorithm SHA256).Hash
$dir  = Join-Path $Out "manifests/t/ThanhNguyenVN93/WeatherWidget/$Version"
New-Item -ItemType Directory -Force $dir | Out-Null

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.19041.0
InstallerType: portable
Commands:
- weatherwidget
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Nguyen Thanh
PublisherUrl: https://github.com/ThanhNguyenVN93
PublisherSupportUrl: $repo/issues
PackageName: Weather Widget
PackageUrl: $repo
License: GPL-3.0-or-later
LicenseUrl: $repo/blob/main/LICENSE
ShortDescription: Tray weather widget for Windows with forecast, air quality, rain alerts and auto-update.
Description: Small Windows tray widget — current weather, 24-hour and 6-day forecast, AQI, rain and air-quality alerts, Vietnamese lunar date. Free data sources with automatic fallback; Vietnamese and English UI.
Tags:
- weather
- widget
- forecast
- tray
- aqi
ReleaseNotesUrl: $repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.9.0
"@ | Set-Content (Join-Path $dir "$id.locale.en-US.yaml") -Encoding utf8

$dir
