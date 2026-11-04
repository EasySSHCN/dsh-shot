# Thin launcher: compiles DshShot.cs in-process via Add-Type, then runs one operation.
# No standalone exe is produced, so the "fresh unsigned binary" AV path is avoided.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-shot.ps1 -Mode overlay -Out crop.png -Scripted "600,250,900,500" -AutoAcceptMs 3000
#   powershell ... -Mode capture -Out shot.png
#   powershell ... -Mode bench -Frames 10
param(
    [string]$Mode = "capture",
    [string]$Out = "$env:TEMP\dsh-shot\shot.png",
    [string]$Scripted = "",
    [int]$AutoAcceptMs = 0,
    [string]$ProofPath = "",
    [int]$ProofMs = 1200,
    [int]$Frames = 10,
    [string]$Banner = "DSH SHOT OVERLAY"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$src = Get-Content -LiteralPath (Join-Path $PSScriptRoot "DshShot.cs") -Raw -Encoding UTF8
if (-not ("DshShot" -as [type])) {
    Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing
}

$sel = @()
if ($Scripted -ne "") { $sel = @($Scripted.Split(',') | ForEach-Object { [int]$_.Trim() }) }

switch ($Mode) {
    "capture" { [DshShot]::CaptureOnly($Out) }
    "bench"   { [DshShot]::Bench($Frames) }
    "overlay" {
        if ($sel.Count -eq 4) {
            [DshShot]::Run($Out, $sel[0], $sel[1], $sel[2], $sel[3], $AutoAcceptMs, $ProofPath, $ProofMs, $Banner)
        } else {
            [DshShot]::Run($Out, 0, 0, 0, 0, $AutoAcceptMs, $ProofPath, $ProofMs, $Banner)
        }
    }
    default { '{"ok":false,"error":"unknown mode"}' }
}
