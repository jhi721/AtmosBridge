# Adds or removes the AtmosBridge blocks in an mpv mpv.conf and the input.conf next to it (UTF-8 without BOM, markers below).
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

# Adds ($Action add) or drops the marked block in one mpv config file; first add keeps a .bak-atmosbridge copy
function Update-Block([string]$Action, [string]$File, [string[]]$Lines) {
    if (-not (Test-Path -LiteralPath $File)) {
        if ($Action -eq 'remove') { return }
        New-Item -ItemType Directory -Force (Split-Path $File) | Out-Null
        $old = @()
    } else {
        $old = @([IO.File]::ReadAllText($File) -split '\r?\n')
    }

    # Drop an existing block (also makes 'add' idempotent)
    $kept = [Collections.Generic.List[string]]::new()
    $inside = $false
    foreach ($line in $old) {
        if ($line.StartsWith('# >>> AtmosBridge')) { $inside = $true; continue }
        if ($inside) { if ($line.StartsWith($end)) { $inside = $false }; continue }
        $kept.Add($line)
    }
    while ($kept.Count -gt 0 -and $kept[$kept.Count - 1] -eq '') { $kept.RemoveAt($kept.Count - 1) }

    if ($Action -eq 'add') {
        $backup = "$File.bak-atmosbridge"
        if ((Test-Path -LiteralPath $File) -and -not (Test-Path -LiteralPath $backup)) {
            Copy-Item -LiteralPath $File $backup
        }
        if ($kept.Count -gt 0) { $kept.Add('') }
        $kept.AddRange($Lines)
    }
    [IO.File]::WriteAllText($File, (($kept -join "`r`n") + "`r`n"), [Text.UTF8Encoding]::new($false))
}

# The block opens with [default], so it is safe after any profile section; later options override earlier ones
Update-Block $Action $Path $block

# i shows mpv's stats and the bridge's "what plays" line together (input.conf: the last binding of a key wins)
Update-Block $Action (Join-Path (Split-Path $Path) 'input.conf') @(
    $begin
    'i script-binding stats/display-stats; script-binding atmosbridge/info'
    $end
)

# Companion script atmosbridge.lua (what mpv passes through, pcm AO options, the i overlay), next to mpv.conf in scripts\
$script = Join-Path (Split-Path $Path) 'scripts\atmosbridge.lua'
if ($Action -eq 'add') {
    New-Item -ItemType Directory -Force (Split-Path $script) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'atmosbridge.lua') $script -Force
} else {
    Remove-Item -LiteralPath $script -ErrorAction SilentlyContinue
}