# インストーラーを作る（Windows で。Inno Setup 6 の iscc が要る: winget install JRSoftware.InnoSetup）
param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$publish = "artifacts\publish"
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
Push-Location web; npm ci --no-audit --no-fund; npm run build; Pop-Location
dotnet publish src\Yamaori -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "発行に失敗した" }
# Windows に入っている PowerShell 5.1 でも動く書き方にする
$iscc = $null
$cmd = Get-Command iscc -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
foreach ($c in @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))) {
    if (-not $iscc -and (Test-Path $c)) { $iscc = $c }
}
if (-not $iscc) { throw "Inno Setup 6 が見つからない（winget install JRSoftware.InnoSetup）" }
& $iscc "/DAppVersion=$Version" "/DPublishDir=..\$publish" installer\yamaori.iss
if ($LASTEXITCODE -ne 0) { throw "インストーラーを作れなかった" }
Get-FileHash "artifacts\Yamaori-Setup-$Version.exe" -Algorithm SHA256
