param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$PublishDir = Join-Path $Root "dist\publish"
$ReleasesDir = Join-Path $Root "dist\releases"
$IconPath = Join-Path $Root "src\KitBrowser\Resources\kitbrowser.ico"

Write-Host "==> KitBrowser v$Version Velopack Delta Surum Paketi hazirlaniyor..." -ForegroundColor Cyan

# Calisan KitBrowser varsa kapat
Stop-Process -Name "KitBrowser" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# 1. vpk aracini kontrol et
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    throw "Velopack CLI (vpk) bulunamadi. Kurmak icin: dotnet tool install -g vpk"
}

# 2. KitBrowser'i yayinla (self-contained win-x64)
if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Write-Host "==> KitBrowser derleniyor (v$Version, win-x64 self-contained)..." -ForegroundColor Cyan
dotnet publish (Join-Path $Root "src\KitBrowser\KitBrowser.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "KitBrowser publish basarisiz oldu."
}

# Lisans dosyalarini pakete dahil et
Copy-Item (Join-Path $Root "LICENSE") $PublishDir -Force
Copy-Item (Join-Path $Root "THIRD-PARTY-NOTICES.txt") $PublishDir -Force

# 3. Velopack ile Setup.exe + Full .nupkg + Delta .nupkg + RELEASES olustur
# Not: dist\releases klasorunde onceki surumler durdugu surece Velopack otomatik olarak
# kucuk Delta (.nupkg) fark paketlerini de uretir!
Write-Host "==> Velopack paketleniyor ($ReleasesDir)..." -ForegroundColor Cyan
vpk pack `
    --packId "KitBrowser" `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe "KitBrowser.exe" `
    --packTitle "KitBrowser" `
    --packAuthors "KitBrowser Contributors" `
    --runtime win-x64 `
    --icon $IconPath `
    --outputDir $ReleasesDir

if ($LASTEXITCODE -ne 0) {
    throw "Velopack paketleme basarisiz oldu."
}

# 4. Gecici publish klasorunu temizle
Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> Hazir! Yayin dosyalari: $ReleasesDir" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="MB";Expression={[Math]::Round($_.Length / 1MB, 2)}} | Format-Table -AutoSize
