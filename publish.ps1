# Собирает релиз ICDK: dist\ICDK.exe (лаунчер + движок) и dist\ICDK_Setup_<версия>.exe (установщик).
# Запуск: powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$ver = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version
if (-not $ver) { $ver = "1.0.0" }
Write-Host "== ICDK $ver : publish ==" -ForegroundColor DarkYellow

if (-not (Test-Path "bin\Source2Viewer-CLI.exe")) {
    throw "нет bin\Source2Viewer-CLI.exe — положи движок в bin/ перед сборкой"
}

# 0. Оригинальное Rust-ядро конструктора предметов (mm-core) отдельным CLI.
$env:CARGO_BUILD_JOBS = "4"
Write-Host "собираю Rust-ядро (mm-core)..." -ForegroundColor DarkGray
rustup run stable-x86_64-pc-windows-gnu cargo build --release --manifest-path src\Wibe.RustBuilder\Cargo.toml
if ($LASTEXITCODE -ne 0) { throw "Rust core build failed" }
$rustExe = "src\Wibe.RustBuilder\target\release\wibe-rust-builder.exe"
if (-not (Test-Path $rustExe)) { throw "нет $rustExe" }
$rustMb = [math]::Round((Get-Item $rustExe).Length / 1MB, 1)
Write-Host "Rust-ядро: $rustMb МБ" -ForegroundColor DarkGray

# 1. Движок заворачиваем в zip: он вшивается в exe и распаковывается при первом запуске.
if (-not (Test-Path scratch)) { New-Item -ItemType Directory scratch | Out-Null }
if (Test-Path scratch\engine.zip) { Remove-Item scratch\engine.zip -Force }
Compress-Archive -Path bin\* -DestinationPath scratch\engine.zip -CompressionLevel Optimal
$engineMb = [math]::Round((Get-Item scratch\engine.zip).Length / 1MB, 1)
Write-Host "движок упакован: $engineMb МБ" -ForegroundColor DarkGray

if (Test-Path dist) { Remove-Item dist -Recurse -Force }

# 2. Один exe: сервер + www внутри + движок как ресурс.
dotnet publish src/Wibe.Server/Wibe.Server.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:EngineZip="$root\scratch\engine.zip" `
    -o dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish app failed" }

# 3. В dist остаётся ровно один exe-лаунчер с именем ICDK.exe.
if (Test-Path dist\www) { Remove-Item dist\www -Recurse -Force }
Get-ChildItem dist -File | Where-Object { $_.Extension -ne '.exe' } | Remove-Item -Force
if (Test-Path dist\ICDK.exe) { Remove-Item dist\ICDK.exe -Force }
Move-Item dist\WibeFittingRoom.exe dist\ICDK.exe -Force

# 4. Установщик: наш собственный тёмный бутстраппер (без Inno Setup).
Write-Host "собираю установщик..." -ForegroundColor DarkGray
$setup = "dist\ICDK_Setup_v$ver.exe"
dotnet publish src\Wibe.Installer\Wibe.Installer.csproj -c Release -r win-x64 -p:Version=$ver -p:AssemblyVersion=$ver.0 -p:FileVersion=$ver.0 -o scratch\setup | Out-Null
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
$bootstrapper = Get-ChildItem scratch\setup -Filter *.exe | Select-Object -First 1
if (-not $bootstrapper) { throw "bootstrapper not found" }
Copy-Item $bootstrapper.FullName $setup -Force

# payload: байты ICDK.exe напрямую в хвост + трейлер (int64 длина + magic ICDKRAW1)
$payload = [System.IO.File]::Open("dist\ICDK.exe", [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
$target = [System.IO.File]::Open($setup, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
$start = $target.Position
$payload.CopyTo($target, 1MB)
$length = $target.Position - $start
$trailer = [System.BitConverter]::GetBytes([int64]$length)
$magic = [System.Text.Encoding]::ASCII.GetBytes("ICDKRAW1")
$target.Write($trailer, 0, 8)
$target.Write($magic, 0, 8)
$target.Dispose()
$payload.Dispose()
Write-Host "payload: $([math]::Round($length/1MB,1)) МБ напрямую" -ForegroundColor DarkGray

Write-Host ""
Get-ChildItem dist | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} | Format-Table
Write-Host "Done: dist\ICDK.exe + dist\ICDK_Setup_$ver.exe" -ForegroundColor Green
