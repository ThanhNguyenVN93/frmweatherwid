# Builds the GitHub release notes for one version from Changelog.cs (the same text the app shows in "What's new").
# Usage: pwsh .github/scripts/release-notes.ps1 -Version 1.2.2 [-Out notes.md]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Out
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$src  = Get-Content (Join-Path $root 'Changelog.cs') -Raw -Encoding utf8

$v = [Version]$Version
$marker = "new(new Version($($v.Major), $($v.Minor), $($v.Build)),"
$start = $src.IndexOf($marker)
if ($start -lt 0) { throw "Changelog.cs has no entry for $Version — add it before releasing." }

# The entry runs until the next version (or the end of the array).
$next = $src.IndexOf('new(new Version(', $start + $marker.Length)
$block = if ($next -ge 0) { $src.Substring($start, $next - $start) } else { $src.Substring($start) }

# Two string arrays: [ vi… ], [ en… ]
$arrays = [regex]::Matches($block, '\[(.*?)\]', 'Singleline')
if ($arrays.Count -lt 2) { throw "Couldn't read the vi/en lists of $Version in Changelog.cs." }
function Items($text) {
    [regex]::Matches($text, '"((?:[^"\\]|\\.)*)"') | ForEach-Object { '- ' + ($_.Groups[1].Value -replace '\\"', '"') }
}

$notes = @(
    "## Weather Widget v$Version"
    ''
    'Một file `WeatherWidget.exe` (Windows 10/11 x64, self-contained, **không cần cài .NET**). Máy đang chạy bản cũ (từ v1.2.0) sẽ tự cập nhật.'
    ''
    '### Tiếng Việt'
    Items $arrays[0].Groups[1].Value
    ''
    '### English'
    Items $arrays[1].Groups[1].Value
) -join "`n"

if ($Out) { Set-Content -Path $Out -Value $notes -Encoding utf8 } else { $notes }
