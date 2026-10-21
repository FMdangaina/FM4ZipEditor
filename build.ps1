# Builds FM4ZipEditor.exe (x86, .NET Framework 4.x - already present in Windows 10/11).
# By default xcompress32.dll (placed next to this script) is embedded in the exe, so the exe is self-contained.
# With -NoEmbed the exe is built without it and looks for xcompress32.dll next to itself at runtime.
param([switch]$NoEmbed)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path 'app.ico')) {
    $sizes = 256,48,32,16
    $pngs = foreach ($s in $sizes) {
        $b = New-Object System.Drawing.Bitmap $s,$s
        $g = [System.Drawing.Graphics]::FromImage($b)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $p = [int]($s*0.06)
        $body = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = [int]($s*0.18); $x=$p; $y=$p; $w=$s-2*$p; $h=$s-2*$p
        $body.AddArc($x,$y,$r,$r,180,90); $body.AddArc($x+$w-$r,$y,$r,$r,270,90)
        $body.AddArc($x+$w-$r,$y+$h-$r,$r,$r,0,90); $body.AddArc($x,$y+$h-$r,$r,$r,90,90); $body.CloseFigure()
        $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)),([System.Drawing.Point]::new(0,$s)),([System.Drawing.Color]::FromArgb(255,230,70,40)),([System.Drawing.Color]::FromArgb(255,150,25,15))
        $g.FillPath($br,$body)
        # cerniera
        $cx = $s/2; $tw = [Math]::Max(2,[int]($s*0.07))
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $row = 0
        for ($yy = $s*0.12; $yy -lt $s*0.62; $yy += $tw*1.4) {
            $off = if ($row % 2 -eq 0) { -$tw } else { 0 }
            $g.FillRectangle($white, $cx+$off, $yy, $tw, $tw*0.9); $row++
        }
        $g.FillEllipse($white, $cx-$s*0.12, $s*0.62, $s*0.24, $s*0.24)
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,150,25,15))
        $g.FillEllipse($dark, $cx-$s*0.05, $s*0.70, $s*0.10, $s*0.10)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
        ,@($s,$ms.ToArray())
    }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
    $offset = 6 + 16*$pngs.Count
    foreach ($e in $pngs) {
        $s = $e[0]; $d = $e[1]
        $bw.Write([byte]($(if ($s -ge 256) {0} else {$s}))); $bw.Write([byte]($(if ($s -ge 256) {0} else {$s})))
        $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
    }
    foreach ($e in $pngs) { $bw.Write($e[1]) }
    [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
}

if (-not $NoEmbed -and -not (Test-Path 'xcompress32.dll')) {
    throw "xcompress32.dll not found next to build.ps1. It is Microsoft's Xbox 360 XMem library and is not included in this repository: copy it here from your own Forza tools / Xbox 360 SDK, or build with -NoEmbed."
}

$csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
$cscArgs = @('/nologo', '/target:winexe', '/platform:x86', '/optimize+', '/out:FM4ZipEditor.exe', '/win32icon:app.ico',
          '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')
if (-not $NoEmbed) { $cscArgs += '/resource:xcompress32.dll,xcompress32.dll' }
$cscArgs += @('ForZip.cs', 'App.cs')
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "OK -> $PSScriptRoot\FM4ZipEditor.exe"
