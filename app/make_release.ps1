param(
    [string]$Version = "1.5.5",
    [string]$Notes = "1. 쿠팡 #2 샵 검색어 인물 키워드 전면 개편:`n   - 노인, 수험생, 여성, 남성 등 일반 대상군(연령·성별) 키워드 엄격 배제`n   - 의사, 약사, 연예인, 유명인(유재석, 이만기, 송가인, 여에스더, 약사추천 등) 월간 검색량 검증 키워드 전자동 주입`n2. 건강기능식품 대표 유명인·전문가 검색어 시드 풀 및 월간검색량 일괄 조회 강화"
)

$ErrorActionPreference = "Stop"
$appDir = $PSScriptRoot
$buildDir = Join-Path $appDir "build"
$distDir = Join-Path $appDir "dist_release"
$tempUpdateDir = Join-Path $appDir "temp_update"
$tempFullDir = Join-Path $appDir "temp_full"

Write-Host "=== 판매도구 Release Builder (v$Version) ===" -ForegroundColor Cyan

# 1. Rebuild SalesTool.exe
Write-Host "[1/5] Rebuilding SalesTool.exe..." -ForegroundColor Yellow
& cmd.exe /c "cd /d `"$appDir`" && build.bat"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

# Ensure dist_release directory
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# 2. Package update.zip (Only files required for in-place self-update)
Write-Host "[2/5] Creating update.zip (In-place update package)..." -ForegroundColor Yellow
if (Test-Path $tempUpdateDir) { Remove-Item $tempUpdateDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempUpdateDir -Force | Out-Null
New-Item -ItemType Directory -Path "$tempUpdateDir\fonts" -Force | Out-Null

Copy-Item "$buildDir\SalesTool.exe" "$tempUpdateDir\판매도구.exe" -Force
Copy-Item "$buildDir\*.dll" "$tempUpdateDir\" -Force
Copy-Item "$buildDir\fonts\*" "$tempUpdateDir\fonts\" -Force

$parentScratch = (Get-Item $appDir).Parent.Parent.FullName
$extDir = Join-Path $parentScratch "extracted_tool"
$progDir = Join-Path $parentScratch "source\프로그램"

if (Test-Path "$extDir\판매도구_휴대폰앱.apk") {
    Copy-Item "$extDir\판매도구_휴대폰앱.apk" "$tempUpdateDir\" -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$updateZipPath = Join-Path $distDir "update.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($tempUpdateDir, $updateZipPath)
Remove-Item $tempUpdateDir -Recurse -Force
Write-Host "  -> Created: $updateZipPath ($((Get-Item $updateZipPath).Length) bytes)"

# 3. Create version.json
Write-Host "[3/5] Generating version.json..." -ForegroundColor Yellow
$versionInfo = @{
    version = $Version
    name = "v$Version"
    date = (Get-Date).ToString("yyyy-MM-dd")
    notes = $Notes
    update_zip = "update.zip"
    apk = "판매도구_휴대폰앱.apk"
}
$jsonContent = $versionInfo | ConvertTo-Json -Depth 4
$versionJsonPath = Join-Path $distDir "version.json"
[System.IO.File]::WriteAllText($versionJsonPath, $jsonContent, [System.Text.Encoding]::UTF8)
Write-Host "  -> Created: $versionJsonPath"

# 4. Package full distribution zip
Write-Host "[4/5] Creating full distribution package..." -ForegroundColor Yellow
if (Test-Path $tempFullDir) { Remove-Item $tempFullDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempFullDir -Force | Out-Null
New-Item -ItemType Directory -Path "$tempFullDir\fonts" -Force | Out-Null

Copy-Item "$buildDir\SalesTool.exe" "$tempFullDir\판매도구.exe" -Force
Copy-Item "$buildDir\*.dll" "$tempFullDir\" -Force
Copy-Item "$buildDir\fonts\*" "$tempFullDir\fonts\" -Force

# Copy external resources from scratch if available
$parentScratch = (Get-Item $appDir).Parent.Parent.FullName
$extDir = Join-Path $parentScratch "extracted_tool"
$progDir = Join-Path $parentScratch "source\프로그램"

if (Test-Path "$extDir\판매도구_휴대폰앱.apk") {
    Copy-Item "$extDir\판매도구_휴대폰앱.apk" "$tempFullDir\" -Force
    Copy-Item "$extDir\판매도구_휴대폰앱.apk" "$distDir\" -Force
}
if (Test-Path "$extDir\WebView2_LICENSE.txt") {
    Copy-Item "$extDir\WebView2_LICENSE.txt" "$tempFullDir\" -Force
}
if (Test-Path "$extDir\사용법(먼저 읽기).txt") {
    Copy-Item "$extDir\사용법(먼저 읽기).txt" "$tempFullDir\" -Force
}
$repoDir = (Get-Item $appDir).Parent.FullName
if (Test-Path "$repoDir\api_keys.example.json") {
    Copy-Item "$repoDir\api_keys.example.json" "$tempFullDir\api_keys.example.json" -Force
}

$fullZipPath = Join-Path $distDir "판매도구_배포용_v$Version.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($tempFullDir, $fullZipPath)
Remove-Item $tempFullDir -Recurse -Force
Write-Host "  -> Created: $fullZipPath ($((Get-Item $fullZipPath).Length) bytes)"

# 5. Summary
Write-Host "[5/5] Release packages prepared successfully!" -ForegroundColor Green
Write-Host "GitHub Release Upload List ($distDir):" -ForegroundColor Cyan
Get-ChildItem -Path $distDir | ForEach-Object {
    Write-Host "  - $($_.Name) ($([math]::Round($_.Length/1KB, 1)) KB)"
}
