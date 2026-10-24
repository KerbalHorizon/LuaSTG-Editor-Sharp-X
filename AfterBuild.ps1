param (
    [string]$ProjectDir = (Get-Location).Path
)

Write-Host "Select build output path:"
Write-Host "  1) bin\Debug"
Write-Host "  2) bin\Release"
Write-Host ""

$choice = Read-Host "Enter choice (1 or 2)"

switch ($choice) {
    "1" { $OutputPath = Join-Path $ProjectDir "bin\Debug" }
    "2" { $OutputPath = Join-Path $ProjectDir "bin\Release" }
    default {
        Write-Error "Invalid choice. Aborting."
        exit 1
    }
}

if (-not (Test-Path $OutputPath)) {
    Write-Error "Output path does not exist: $OutputPath"
    exit 1
}

$LibPath = Join-Path $OutputPath "lib"

if (-not (Test-Path $LibPath)) {
    New-Item -ItemType Directory -Path $LibPath | Out-Null
}

$Patterns = @(
    "*.dll",
    "*.pdb",
    "*.xml",
    "*.dll.config"
)

foreach ($pattern in $Patterns) {
    Get-ChildItem -Path $OutputPath -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.IsReadOnly) {
                $_.IsReadOnly = $false
            }

            Move-Item `
                -Path $_.FullName `
                -Destination $LibPath `
                -Force
        }
}

Write-Host "Files moved to $LibPath"