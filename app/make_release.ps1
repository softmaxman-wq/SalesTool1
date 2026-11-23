param(
    [string]$Version = "1.4.0",
    [string]$Notes = "1. msgood4u 계정 분리: 계정 1·2·3 개별 로그인 및 사업자별 주문 자동발주 연동`n2. 사업자별 상품명 차별화: 스타일 프리셋, 키워드 분산, 공유폴더(PC간 6개 사업자) 중복 방지`n3. 3개 계정 동시 등록 & 실패한 계정만 선별 재시도 지원 (PC 및 모바일 웹)"
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
