$env:OPP_ORIGIN='http://localhost:65380'
$env:OPP_PORT='65380'
$proc = Start-Process dotnet -ArgumentList 'run','--project','c:\s\r\Fedi\Fedi.csproj' -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 10
try {
    $r = Invoke-WebRequest -Uri 'http://localhost:65380/live' -UseBasicParsing -TimeoutSec 5
    Write-Host ('STATUS: ' + $r.StatusCode + ' BODY: ' + $r.Content)
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message)
}
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
