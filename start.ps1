# Starts the .NET TWAIN service and the web client (Windows only).
# Usage: .\start.ps1

$root = $PSScriptRoot

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Starting the TWAIN scanning demo" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host ""

# Load variables from .env (if present). The service and the client inherit them.
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
            $name, $value = $line.Split("=", 2)
            [Environment]::SetEnvironmentVariable($name.Trim(), $value.Trim().Trim('"'), "Process")
        }
    }
}

# Start the .NET TWAIN API in the background
Write-Host "[1/2] Starting .NET TWAIN API..." -ForegroundColor Yellow
$servicePath = Join-Path $root "gdp-process\dotnet-twain\dotnet-twain"
$dotnetJob = Start-Job -ScriptBlock {
    param($path)
    Set-Location $path
    dotnet run
} -ArgumentList $servicePath

Start-Sleep -Seconds 3

# Start the web client (Vite dev server)
Write-Host "[2/2] Starting web client..." -ForegroundColor Yellow
Set-Location (Join-Path $root "websdk-client")
if (-not (Test-Path "node_modules")) {
    npm install
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host "  .NET TWAIN API : " -NoNewline -ForegroundColor White
Write-Host "http://localhost:5000" -ForegroundColor Cyan
Write-Host "  Web client     : " -NoNewline -ForegroundColor White
Write-Host "http://localhost:6001" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Press Ctrl+C to stop." -ForegroundColor Red
Write-Host ""

try {
    # Blocking: opens the browser and serves the client until Ctrl+C
    npm run dev -- --open
}
finally {
    Stop-Job -Job $dotnetJob
    Remove-Job -Job $dotnetJob -Force
    Set-Location $root
}
