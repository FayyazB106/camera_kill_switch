<#
.SYNOPSIS
    Generates app.ico (the application/exe icon) from the camera artwork that is
    embedded as base64 in CameraKillSwitch.cs. Produces a multi-size, PNG-based
    .ico (16/32/48/64/128/256) so Windows renders it crisply everywhere: the
    taskbar, desktop shortcut, Add/Remove Programs, and the installer.

    Run automatically by Build-App.ps1; can also be run on its own.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# Pull the embedded camera PNG (the plain "Enabled" artwork) out of the source.
$cs = Get-Content (Join-Path $here 'CameraKillSwitch.cs') -Raw
if ($cs -notmatch 'CameraPngBase64\s*=\s*"([A-Za-z0-9+/=]+)"') {
    throw 'Could not find CameraPngBase64 in CameraKillSwitch.cs'
}
$png   = [Convert]::FromBase64String($Matches[1])
$srcMs = New-Object System.IO.MemoryStream(,$png)
$srcImg = [System.Drawing.Image]::FromStream($srcMs)

# Render each size as a PNG frame.
$sizes  = 16, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcImg, 0, 0, $s, $s)

    # Draw the "disabled" overlay (red circle + diagonal slash), matching
    # CamArt.Render(ArtStyle.Disabled): ring r=45 @ (50,50), slash, width 10 on a
    # 100x100 canvas. Scale to the current frame size.
    $f   = $s / 100.0
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,255,0,0), (10.0 * $f))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($pen, (5.0*$f), (5.0*$f), (90.0*$f), (90.0*$f))
    $g.DrawLine($pen, (17.46*$f), (85.46*$f), (85.46*$f), (17.46*$f))
    $pen.Dispose()
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += ,($ms.ToArray())
}
$srcImg.Dispose(); $srcMs.Dispose()

# Assemble the ICO container: 6-byte header + 16-byte dir entries + PNG data.
$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type: icon
$bw.Write([UInt16]$sizes.Count)   # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s   = $sizes[$i]
    $len = $frames[$i].Length
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 = 256)
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 = 256)
    $bw.Write([Byte]0)            # palette
    $bw.Write([Byte]0)            # reserved
    $bw.Write([UInt16]1)          # color planes
    $bw.Write([UInt16]32)         # bits per pixel
    $bw.Write([UInt32]$len)       # size of PNG data
    $bw.Write([UInt32]$offset)    # offset of PNG data
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()

$icoPath = Join-Path $here 'app.ico'
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Wrote $icoPath ($([math]::Round((Get-Item $icoPath).Length/1KB,1)) KB, sizes: $($sizes -join '/'))" -ForegroundColor Green
