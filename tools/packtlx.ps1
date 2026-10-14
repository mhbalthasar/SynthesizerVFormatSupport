#Requires -Version 5.0
# Pack SynthesizerVFormatSupport into a .tlx (a zip with manifest.json at the archive root).
# The project root is the parent of this script's folder (tools\).
#
# Self-contained: no Python, no external tools — only PowerShell + .NET
# (System.IO.Compression). It builds the plugin, copies bin\package into the
# archive (dropping .pdb + dev cruft), strips absolute build paths out of the
# managed DLL's CodeView debug record (privacy hardening), and writes a
# forward-slash zip. tools\packtlx.sh does the same on Linux/macOS.
#
# Environment overrides (all optional):
#   SVFS_DOTNET             dotnet CLI                  (default: dotnet on PATH)
#   SVFS_PACKAGE_DIR        where the .tlx is written   (default: <root>\packages)
#   SVFS_TLX_NAME           output file name            (default: SynthesizerVFormatSupport.tlx)
#   SVFS_NO_BUILD=1         skip the dotnet build step
#   SVFS_TLX_NO_HARDEN=1    disable the CodeView path redaction (debugging only)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = if ($env:SVFS_DOTNET) { $env:SVFS_DOTNET } else { 'dotnet' }

if ($env:SVFS_NO_BUILD -ne '1') {
    & $dotnet build (Join-Path $root 'SynthesizerVFormatSupport.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

$bin = (Resolve-Path (Join-Path $root 'bin\package')).Path
if (-not (Test-Path (Join-Path $bin 'manifest.json'))) { throw "build output not found: $bin" }

$outDir = if ($env:SVFS_PACKAGE_DIR) { $env:SVFS_PACKAGE_DIR } else { Join-Path $root 'packages' }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$tlx = Join-Path $outDir $(if ($env:SVFS_TLX_NAME) { $env:SVFS_TLX_NAME } else { 'SynthesizerVFormatSupport.tlx' })
$harden = $env:SVFS_TLX_NO_HARDEN -ne '1'

# ---- keep/skip rules (mirror tools/packtlx.sh) ----
$BackupMarkers = @('.bak', '-bak', '.prev', '.trace', '.old', '-old', '.orig', '.backup', '.stale', '.tmp', '.swp')
$Latin1 = [Text.Encoding]::GetEncoding(28591)   # 1:1 byte<->char map for binary scanning

function Test-SkipFile {
    param([string]$Rel, [string]$Name)
    $low = $Name.ToLower()
    if ($low.EndsWith('.pdb')) { return $true }
    foreach ($m in $BackupMarkers) { if ($low.Contains($m)) { return $true } }
    return $false
}

function Test-AbsolutePath {
    param([string]$P)
    if ([string]::IsNullOrEmpty($P)) { return $false }
    if ($P -match '^[A-Za-z]:[\\/]') { return $true }        # C:\ or C:/
    if ($P.StartsWith('\\')) { return $true }                # UNC
    if ($P.StartsWith('/') -and -not $P.StartsWith('/_/')) { return $true }  # POSIX abs
    return $false
}

function Get-RedactedPE {
    # Returns @{ Bytes = <new byte[] or $null>; Changes = @( @(old,new), ... ) }.
    # Only rewrites absolute CodeView (RSDS) PDB paths to '/_/<basename>', NUL-padded,
    # so the record length and the PE layout are preserved.
    param([byte[]]$Bytes)
    $s = $Latin1.GetString($Bytes)
    $out = $null
    $changes = New-Object System.Collections.ArrayList
    $i = 0
    while ($true) {
        $k = $s.IndexOf('RSDS', $i)
        if ($k -lt 0) { break }
        $ps = $k + 24                                       # RSDS + 16 GUID + 4 age
        if ($ps -lt $s.Length) {
            $z = $s.IndexOf([char]0, $ps)
            if ($z -ge 0) {
                $old = $s.Substring($ps, $z - $ps)
                if (Test-AbsolutePath $old) {
                    $base = ($old -replace '\\', '/') -replace '^.*/', ''
                    $new = "/_/$base"
                    if ($new.Length -gt ($z - $ps)) { $new = $base }
                    if ($new.Length -le ($z - $ps)) {
                        if ($null -eq $out) { $out = [byte[]]$Bytes.Clone() }
                        $nb = $Latin1.GetBytes($new)
                        [Array]::Copy($nb, 0, $out, $ps, $nb.Length)
                        for ($j = $ps + $nb.Length; $j -lt $z; $j++) { $out[$j] = 0 }
                        [void]$changes.Add(@($old, $new))
                    }
                }
            }
        }
        $i = $k + 4
    }
    return @{ Bytes = $out; Changes = $changes }
}

# ---- enumerate + filter ----
$baseLen = $bin.Length + 1
$entries = New-Object System.Collections.ArrayList
$skipped = New-Object System.Collections.ArrayList
foreach ($f in (Get-ChildItem -Path $bin -Recurse -File)) {
    $rel = $f.FullName.Substring($baseLen).Replace('\', '/')
    if (Test-SkipFile -Rel $rel -Name $f.Name) { [void]$skipped.Add($rel); continue }
    [void]$entries.Add([pscustomobject]@{ Full = $f.FullName; Rel = $rel; Name = $f.Name; MTime = $f.LastWriteTime })
}
# Ordinal sort on the forward-slash rel path, to match the Python packer
# (entries.sort(key=lambda e: e[1])). A plain Sort-Object is culture-aware and
# would reorder names differently -> a different archive.
$rels = [string[]]@($entries | ForEach-Object { $_.Rel })
[Array]::Sort($rels, [StringComparer]::Ordinal)
$byRel = @{}
foreach ($e in $entries) { $byRel[$e.Rel] = $e }
$entries = @($rels | ForEach-Object { $byRel[$_] })
if (-not ($entries | Where-Object { $_.Rel -eq 'manifest.json' })) { throw 'manifest.json is not at the archive root' }

# ---- write the zip (forward-slash names; redacted DLL bytes) ----
# Zip metadata convention mirrored from the canonical Python packer
# (zipfile.ZipInfo.from_file on Windows): external attributes = 0o100666 << 16,
# create_system = 0 (DOS), mtime = the source file's local last-write time.
# .NET's DeflateStream and CPython's zlib emit identical compressed bytes at this
# level, so matching these headers makes packtlx.ps1 byte-identical to packtlx.sh.
$ExtAttr = -2118778880   # 0x81B60000 == (0o100666 << 16)
$tmpTlx = "$tlx.tmp"
if ([IO.File]::Exists($tmpTlx)) { [IO.File]::Delete($tmpTlx) }
$fs = [IO.File]::Open($tmpTlx, [IO.FileMode]::Create)
$zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
$hardenLog = New-Object System.Collections.ArrayList
try {
    foreach ($e in $entries) {
        $data = $null
        if ($harden -and ($e.Name -match '\.(dll|exe)$')) {
            $r = Get-RedactedPE -Bytes ([IO.File]::ReadAllBytes($e.Full))
            if ($null -ne $r.Bytes) {
                $data = $r.Bytes
                foreach ($c in $r.Changes) { [void]$hardenLog.Add(@($e.Rel, $c[0], $c[1])) }
            }
        }
        if ($null -eq $data) { $data = [IO.File]::ReadAllBytes($e.Full) }
        $entry = $zip.CreateEntry($e.Rel, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]$e.MTime
        $entry.ExternalAttributes = $ExtAttr
        $st = $entry.Open(); $st.Write($data, 0, $data.Length); $st.Dispose()
    }
}
finally { $zip.Dispose(); $fs.Dispose() }
if ([IO.File]::Exists($tlx)) { [IO.File]::Delete($tlx) }
[IO.File]::Move($tmpTlx, $tlx)

# ---- verify: no absolute CodeView path survives in any packed PE ----
$leaks = New-Object System.Collections.ArrayList
$zr = [IO.Compression.ZipFile]::OpenRead($tlx)
try {
    foreach ($entry in $zr.Entries) {
        if ($entry.FullName -notmatch '\.(dll|exe)$') { continue }
        $ms = New-Object IO.MemoryStream
        $es = $entry.Open(); $es.CopyTo($ms); $es.Dispose()
        $s = $Latin1.GetString($ms.ToArray()); $ms.Dispose()
        $i = 0
        while ($true) {
            $k = $s.IndexOf('RSDS', $i); if ($k -lt 0) { break }
            $ps = $k + 24
            if ($ps -lt $s.Length) {
                $z = $s.IndexOf([char]0, $ps)
                if ($z -ge 0) {
                    $old = $s.Substring($ps, $z - $ps)
                    if (Test-AbsolutePath $old) { [void]$leaks.Add("$($entry.FullName): $old") }
                }
            }
            $i = $k + 4
        }
    }
}
finally { $zr.Dispose() }

# ---- report ----
Write-Host "packed       : $tlx"
Write-Host "entries      : $($entries.Count)"
if ($skipped.Count -gt 0) {
    Write-Host "skipped      : $($skipped.Count) file(s) (.pdb / stale artifacts)"
    foreach ($s in ($skipped | Sort-Object)) { Write-Host "               - $s" }
}
Write-Host "root manifest: True"
if ($harden) {
    if ($hardenLog.Count -gt 0) {
        Write-Host "hardened     : $($hardenLog.Count) absolute CodeView path(s) redacted"
        foreach ($c in $hardenLog) {
            Write-Host "               - $($c[0])"
            Write-Host "                   $($c[1])"
            Write-Host "                -> $($c[2])"
        }
    }
    else { Write-Host "hardened     : 0 (no absolute CodeView path found)" }
}
else { Write-Host "hardened     : DISABLED (SVFS_TLX_NO_HARDEN=1)" }
Write-Host "abs-path leak: $($leaks.Count)   (must be 0)"
foreach ($l in $leaks) { Write-Host "               ! $l" }
Write-Host "tlx bytes    : $((Get-Item $tlx).Length)"
if ($leaks.Count -gt 0) { exit 1 }
