# shotd.ps1 - DSH screenshot hotkey daemon.
#
# Resident PowerShell process that registers a global hotkey (RegisterHotKey, message-queue
# based, no window needed) and, on each trigger, captures the screen through the same
# in-process Add-Type overlay used by dsh-shot.ps1 (DshShot.cs is shared read-only; this
# script never writes to it). Emits one JSON object per line on stdout:
#   {"event":"ready","hotkey":...,"spool":...,"selftest":...}   on startup (after hotkey is registered)
#   {"event":"hotkey"}                                          every hotkey trigger
#   {"event":"shot","path":...,"width":N,"height":N}            successful capture
#   {"event":"cancelled"}                                       user dismissed the overlay
#   {"event":"error","message":...}                             failure (also: exit code 2 when the hotkey is taken)
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File shotd.ps1 --hotkey "Ctrl+Alt+A" --spool "%TEMP%\dsh-shot\spool" [--selftest]
# Selftest mode uses a scripted auto-accept: hover-preset the window under the cursor
# (or the whole virtual screen when there is no window) and auto-accepts after 800 ms,
# so no human interaction is needed.

$ErrorActionPreference = "Stop"

# --- arg parsing (manual, so both -x and --x forms bind; no param() block) ---
$Hotkey = "Ctrl+Alt+A"
$Spool = Join-Path $env:TEMP "dsh-shot\spool"
$Selftest = $false
$Banner = "DSH SHOT OVERLAY"

$i = 0
while ($i -lt $args.Count) {
    $a = [string]$args[$i]
    if ($a -like "-*") {
        $k = $a.TrimStart('-').ToLowerInvariant()
        switch ($k) {
            "hotkey"   { $i++; $Hotkey  = [string]$args[$i] }
            "spool"    { $i++; $Spool   = [string]$args[$i] }
            "banner"   { $i++; $Banner  = [string]$args[$i] }
            "selftest" { $Selftest = $true }
        }
    }
    $i++
}

# UTF-8 without BOM on stdout, so the Node side can decode every line as utf8.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Emit([hashtable]$obj) {
    $line = $obj | ConvertTo-Json -Compress -Depth 3
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

function Fail([string]$msg, [int]$code) {
    Emit @{ event = "error"; message = $msg }
    exit $code
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- hotkey plumbing: register on the thread message queue, pump with GetMessage ---
$shotdSrc = @"
using System;
using System.Runtime.InteropServices;

public static class ShotDaemon
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP_SHOTD_QUIT = 0x8000 + 0x5EE; // WM_APP-based private quit message

    // Blocks on the calling thread's message queue until WM_HOTKEY arrives (returns true)
    // or a quit message / WM_QUIT arrives (returns false).
    public static bool WaitForHotKey()
    {
        while (true)
        {
            MSG m;
            int r = GetMessage(out m, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) return false;          // WM_QUIT or error
            if (m.message == WM_HOTKEY) return true;
            if (m.message == WM_APP_SHOTD_QUIT) return false;
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    public static void RequestQuit()
    {
        PostThreadMessage(GetCurrentThreadId(), WM_APP_SHOTD_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
"@
Add-Type -TypeDefinition $shotdSrc -ReferencedAssemblies System.Windows.Forms, System.Drawing

# --- parse "Ctrl+Alt+A" into modifiers + virtual key ---
$mods = [uint32]0
$vk = [uint32]0
foreach ($p in ($Hotkey.Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $l = $p.ToLowerInvariant()
    if ($l -in @("ctrl", "control"))   { $mods = $mods -bor 0x2 }
    elseif ($l -eq "alt")              { $mods = $mods -bor 0x1 }
    elseif ($l -eq "shift")            { $mods = $mods -bor 0x4 }
    elseif ($l -in @("win", "windows", "meta")) { $mods = $mods -bor 0x8 }
    elseif ($l -match '^f(\d+)$')      { $vk = [uint32](0x70 + [int]$Matches[1] - 1) }
    elseif ($l.Length -eq 1 -and $l -ge 'a' -and $l -le 'z') { $vk = [uint32][char]($l.ToUpper()) }
    elseif ($l.Length -eq 1 -and [char]::IsDigit($l, 0)) { $vk = [uint32][char]$l }
    else { Fail "unknown key component '$p' in hotkey '$Hotkey'" 2 }
}
if ($vk -eq 0) { Fail "hotkey '$Hotkey' has no usable key" 2 }

# --- register the hotkey before anything else; taken combos must fail loudly ---
if (-not [ShotDaemon]::RegisterHotKey([IntPtr]::Zero, 1, $mods, $vk)) {
    Fail "hotkey '$Hotkey' could not be registered (combination already in use by another process)" 2
}

if (-not (Test-Path -LiteralPath $Spool)) { New-Item -ItemType Directory -Path $Spool -Force | Out-Null }

# --- overlay capture: same in-process Add-Type of DshShot.cs as dsh-shot.ps1 ---
$src = Get-Content -LiteralPath (Join-Path $PSScriptRoot "DshShot.cs") -Raw -Encoding UTF8
if (-not ("DshShot" -as [type])) {
    Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing
}

function Invoke-Shot {
    try {
        $name = "shot-{0:yyyy-MM-dd-HHmmss-fff}.png" -f (Get-Date)
        $out = Join-Path $Spool $name
        $json = $null
        if ($Selftest) {
            # scripted auto-confirm: hover-preset the window under the cursor, else whole
            # virtual screen; auto-accept after 800 ms instead of waiting for Enter.
            $vs = [DshShot]::VirtualScreen()
            $hover = New-Object System.Drawing.Rectangle(0, 0, 0, 0)
            $hasHover = [DshShot]::TryHoverWindow($vs, [ref]$hover)
            if (-not ($hasHover -and $hover.Width -gt 1 -and $hover.Height -gt 1)) {
                $hover = New-Object System.Drawing.Rectangle($vs.X, $vs.Y, $vs.Width, $vs.Height)
            }
            $json = [DshShot]::Run($out, $hover.X, $hover.Y, $hover.Width, $hover.Height, 800, "", 0, $Banner)
        }
        else {
            # interactive: hover-preset + wait for the user's Enter (60 s guard inside Run)
            $json = [DshShot]::Run($out, 0, 0, 0, 0, 0, "", 0, $Banner)
        }
        $res = $json | ConvertFrom-Json
        if ($res.ok) {
            $action = "session"
            if ($res.PSObject.Properties.Name -contains "action") { $action = [string]$res.action }
            Emit @{ event = "shot"; path = [string]$res.path; width = [int]$res.width; height = [int]$res.height; action = $action }
        }
        else {
            Emit @{ event = "cancelled" }
        }
    }
    catch {
        Emit @{ event = "error"; message = "capture failed: $($_ | Out-String)".Trim() }
    }
}

Emit @{ event = "ready"; hotkey = $Hotkey; spool = $Spool; selftest = [bool]$Selftest }

try {
    while ([ShotDaemon]::WaitForHotKey()) {
        Emit @{ event = "hotkey" }
        Invoke-Shot
    }
}
finally {
    [void][ShotDaemon]::UnregisterHotKey([IntPtr]::Zero, 1)
}
