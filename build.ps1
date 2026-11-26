param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$source = Join-Path $root "src\VictusFanControl.cs"
$outputDir = Join-Path $root "dist"
$output = Join-Path $outputDir "VictusFanControl.exe"
$lockedOutput = Join-Path $outputDir "VictusFanControl-new.exe"
$temporaryDir = Join-Path $outputDir ".build"
$temporaryOutput = Join-Path $temporaryDir "VictusFanControl.exe"
$frameworkDir = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$compiler = Join-Path $frameworkDir "csc.exe"

if (-not (Test-Path $compiler)) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path $compiler)) {
    throw "C# compiler tidak ditemukan. Instal .NET Framework 4.x Developer Runtime."
}

New-Item -ItemType Directory -Force $outputDir | Out-Null
New-Item -ItemType Directory -Force $temporaryDir | Out-Null

$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Management.dll",
    "System.Security.dll",
    "System.Windows.Forms.dll"
) | ForEach-Object { "/reference:$frameworkDir\$_" }

$arguments = @(
    "/nologo",
    "/target:winexe",
    "/langversion:default",
    "/win32manifest:$root\src\app.manifest",
    "/win32icon:$root\src\app.ico",
    "/out:$temporaryOutput",
    "/define:TRACE",
    $source
) + $references

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build gagal dengan exit code $LASTEXITCODE."
}

if (-not (Test-Path $temporaryOutput)) {
    throw "Build selesai, tetapi file output tidak ditemukan."
}

try {
    Copy-Item -LiteralPath (Join-Path $root "src\app.ico") `
        -Destination (Join-Path $outputDir "app.ico") -Force -ErrorAction Stop

    if (Test-Path $output) {
        Remove-Item -LiteralPath $output -Force -ErrorAction Stop
    }
    Move-Item -LiteralPath $temporaryOutput -Destination $output -Force -ErrorAction Stop
    Remove-Item -LiteralPath $temporaryDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $outputDir ".use-new-build") -Force -ErrorAction SilentlyContinue
}
catch {
    if (Test-Path $temporaryOutput) {
        Move-Item -LiteralPath $temporaryOutput -Destination $lockedOutput -Force -ErrorAction Stop
        Set-Content -LiteralPath (Join-Path $outputDir ".use-new-build") -Value "VictusFanControl-new.exe" -Encoding ASCII
        Remove-Item -LiteralPath $temporaryDir -Recurse -Force -ErrorAction SilentlyContinue
        $output = $lockedOutput
        Write-Warning "EXE utama sedang berjalan dan tidak dapat diganti. Build baru tersedia sebagai: $output"
    }
    else {
        throw "Tidak dapat mengganti $output. Tutup VictusFanControl.exe yang sedang berjalan lalu jalankan build lagi."
    }
}

$assembly = [System.Reflection.AssemblyName]::GetAssemblyName($output)
Write-Host "Build berhasil: $output" -ForegroundColor Green
Write-Host ("Nama: {0}; Versi: {1}; Ukuran: {2:N0} bytes" -f $assembly.Name, $assembly.Version, (Get-Item $output).Length)
