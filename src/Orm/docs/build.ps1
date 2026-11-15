# Builds the documentation site into _site: the pages, and beside them llms-full.txt, the guide as one markdown
# file in table-of-contents order, for an assistant that reads the site.
#
#   ./src/Orm/docs/build.ps1            build (from PowerShell; elsewhere `powershell -File …`)
#   ./src/Orm/docs/build.ps1 -Serve     build and serve on http://localhost:8086
param([switch] $Serve)

$ErrorActionPreference = 'Stop'
$docs = $PSScriptRoot
$site = Join-Path $docs '_site'

# `dotnet docfx` is the local tool in the repository's manifest, found by walking up from here.
Push-Location $docs
try {
  dotnet docfx (Join-Path $docs 'docfx.json')
  if ($LASTEXITCODE -ne 0) { throw "docfx exited with $LASTEXITCODE" }
} finally {
  Pop-Location
}

# ----- llms-full.txt ---------------------------------------------------------------------------------------------
$pages = @('index.md')
foreach ($line in Get-Content (Join-Path $docs 'guide/toc.yml')) {
  if ($line -match 'href:\s*(\S+\.md)') { $pages += 'guide/' + $Matches[1] }
}
$parts = foreach ($page in $pages) {
  $text = Get-Content (Join-Path $docs $page) -Raw
  # The landing page's front matter is for the site, not the reader.
  $text = $text -replace '(?s)^---.*?---\s*', ''
  "<!-- $page -->`n`n$text"
}
Set-Content (Join-Path $site 'llms-full.txt') ($parts -join "`n`n---`n`n") -Encoding utf8
Write-Host "Written $site"

if ($Serve) {
  Push-Location $docs
  try { dotnet docfx serve $site --port 8086 } finally { Pop-Location }
}
