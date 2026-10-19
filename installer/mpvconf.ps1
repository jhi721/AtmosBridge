# Adds or removes the AtmosBridge block in an mpv / mpv.net mpv.conf (UTF-8 without BOM, markers below).
param(
    [Parameter(Mandatory)][ValidateSet('add', 'remove')][string]$Action,
    [Parameter(Mandatory)][string]$Path
)

$begin = '# >>> AtmosBridge (managed by the AtmosBridge installer, removed on uninstall)'
$end = '# <<< AtmosBridge'
$block = @(
    $begin
    '[default]'
    'ao=pcm,wasapi'
    'audio-spdif=eac3,truehd,dts-hd'
    'ao-pcm-file=\\.\pipe\atmos'
    'audio-samplerate=48000'
    'audio-channels=auto' # the bridge takes any layout; a fixed list (7.1,5.1,...) would fold heights down
    'input-ipc-server=\\.\pipe\mpv-atmos'
    ''
    '[atmos-bridge-delay]'
    'profile-cond=get("current-ao") == "pcm"'
    'profile-restore=copy'
    'audio-delay=-0.26' # measured end to end, see README (How timing works)
    $end
)

if (-not (Test-Path -LiteralPath $Path)) {
    if ($Action -eq 'remove') { exit 0 }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $lines = @()
} else {
    $lines = @([IO.File]::ReadAllText($Path) -split '\r?\n')
}

# Drop an existing block (also makes 'add' idempotent)
$kept = [Collections.Generic.List[string]]::new()
$inside = $false
foreach ($line in $lines) {
    if ($line.StartsWith('# >>> AtmosBridge')) { $inside = $true; continue }
    if ($inside) { if ($line.StartsWith($end)) { $inside = $false }; continue }
    $kept.Add($line)
}
while ($kept.Count -gt 0 -and $kept[$kept.Count - 1] -eq '') { $kept.RemoveAt($kept.Count - 1) }

if ($Action -eq 'add') {
    $backup = "$Path.bak-atmosbridge"
    if ((Test-Path -LiteralPath $Path) -and -not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $Path $backup
    }
    if ($kept.Count -gt 0) { $kept.Add('') }
    $kept.AddRange([string[]]$block)
}

# The block opens with [default], so it is safe after any profile section; later options override earlier ones
[IO.File]::WriteAllText($Path, (($kept -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))

# Companion script (skips passthrough for 1-2 channel AC-3/E-AC-3), next to mpv.conf in scripts\
$script = Join-Path (Split-Path $Path) 'scripts\atmosbridge.lua'
if ($Action -eq 'add') {
    New-Item -ItemType Directory -Force (Split-Path $script) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'atmosbridge.lua') $script -Force
} else {
    Remove-Item -LiteralPath $script -ErrorAction SilentlyContinue
}