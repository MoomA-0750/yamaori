# 開発中の Yamaori を、この PC の本人のアカウントに入れる（管理者権限は要らない）。
# 動いているものを止め、発行し、ログオン時に起こすよう登録して、起こす。
# 使い方: リポジトリの一番上で  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-dev.ps1
# 外すとき:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-dev.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Yamaori'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process Yamaori -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty -Path $runKey -Name Yamaori -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
    Write-Output "外した（データ $env:LOCALAPPDATA\Yamaori は残してある）"
    return
}

dotnet publish src\Yamaori -c Release -r win-x64 --self-contained false -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "発行に失敗した" }
$exe = Join-Path $dest 'Yamaori.exe'
Set-ItemProperty -Path $runKey -Name Yamaori -Value "`"$exe`""
Start-Process $exe
Write-Output "入れて起こした: $exe"
