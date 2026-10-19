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
    'input-ipc-server=\\.\pipe\mpv-atmos'
    ''
    '[atmos-bridge-delay]'
    'profile-cond=get("current-ao") == "pcm"'
    'profile-restore=copy'
    'audio-delay=-0.15'
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
