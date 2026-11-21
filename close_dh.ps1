param([string]$Out)
$p = Get-Process DungeonHelper -ErrorAction SilentlyContinue
if ($p) {
    $path = ($p | Select-Object -First 1).Path
    if ($path -and $Out) { Set-Content -LiteralPath $Out -Value $path -Encoding Default }
    $p | Stop-Process -Force
}
