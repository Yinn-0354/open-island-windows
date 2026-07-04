# Open Island deploy script.
# Stops the running app, builds Release, stages the hooks binary next to
# OpenIsland.exe, forces hook reinstall on next launch (so settings.json
# points at the freshly built hooks.exe), and relaunches.

$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot -Parent
$sln       = Join-Path $root 'OpenIsland.sln'
$config    = 'Release'
# TFM 目录必须跟 OpenIsland.App.csproj 的 <TargetFramework> 一致。改成
# net8.0-windows10.0.19041.0（为 WinRT SMTC/SDK API 加的 Windows SDK 版本后缀）后，
# 构建产物落到 bin\$config\net8.0-windows10.0.19041.0\；若这里仍写 net8.0-windows，
# 脚本会把 hooks 塞进旧目录并启动旧的 OpenIsland.exe（静默跑上一版，改动全都不生效）。
$appDir    = Join-Path $root "src\OpenIsland.App\bin\$config\net8.0-windows10.0.19041.0"
$appExe    = Join-Path $appDir 'OpenIsland.exe'
$hooksSrc  = Join-Path $root "src\OpenIsland.Hooks\bin\$config\net8.0\open-island-hooks.exe"
$hooksDest = Join-Path $appDir 'open-island-hooks.exe'
$manifest  = Join-Path $env:USERPROFILE '.claude\open-island-manifest.claude.json'

# 1. Stop running instance so we can overwrite the binary.
Get-Process -Name OpenIsland -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping OpenIsland (PID $($_.Id))..."
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

# 2. Release build.
Write-Host "Building $config..."
& dotnet build $sln -c $config --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

# 3. Stage the entire hooks runtime next to OpenIsland.exe.
#    .NET 8 apphost (open-island-hooks.exe) needs its sibling .dll/.deps.json/
#    .runtimeconfig.json AND every dependency the App doesn't already pull in
#    (e.g. System.CommandLine.dll plus its localized resource folders).
#    Copying just the .exe leaves a hook that fails silently — claude.exe writes
#    "Could not load file or assembly System.CommandLine" to stderr and the
#    bridge never sees the event.
if (-not (Test-Path $hooksSrc)) { throw "Hooks binary not found at $hooksSrc" }
$hooksSrcDir = Split-Path $hooksSrc -Parent
Write-Host "Staging entire hooks runtime tree from $hooksSrcDir to $appDir"
Get-ChildItem $hooksSrcDir -Force | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $appDir $_.Name) -Recurse -Force
}

# 4. Remove manifest. SetupService.CheckAndAutoInstallAsync skips when it exists,
#    so deleting it forces InstallAsync on next launch and rewrites settings.json
#    with the newly staged hooks.exe path.
if (Test-Path $manifest) {
    Write-Host "Removing manifest to force hook reinstall on launch..."
    Remove-Item $manifest -Force
}

# 5. Launch.
Write-Host "Launching $appExe"
Start-Process $appExe

Write-Host ""
Write-Host "Deploy done. Hooks reinstall asynchronously a moment after launch;"
Write-Host "verify with: Get-Content `$env:USERPROFILE\.claude\settings.json"
