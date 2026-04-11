param(
    [string]$ProjectPath = ".\src\WindowWorks.App\WindowWorks.App.csproj"
)

Write-Host "Stopping running WindowWorks processes (if any)..."
Get-Process -Name WindowWorks.App,WindowWorks -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        Write-Host "Stopping PID=$($_.Id) Name=$($_.ProcessName)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "Failed to stop process PID=$($_.Id) - $_"
    }
}

Write-Host "Building project: $ProjectPath"
& dotnet build $ProjectPath

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } else { exit 0 }
