Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\assets"
New-Item -ItemType Directory -Force $outDir | Out-Null

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 56.0
$x = 12.0
$y = 12.0
$w = 232.0
$h = 232.0
$d = $r * 2
$path.AddArc($x, $y, $d, $d, 180, 90)
$path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
$path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
$path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(256, 256)),
    [System.Drawing.Color]::FromArgb(255, 91, 140, 255),
    [System.Drawing.Color]::FromArgb(255, 127, 91, 255))
$g.FillPath($brush, $path)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 13.0)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$mon = New-Object System.Drawing.Drawing2D.GraphicsPath
$mx = 58.0
$my = 70.0
$mw = 140.0
$mh = 88.0
$mr = 16.0
$md = $mr * 2
$mon.AddArc($mx, $my, $md, $md, 180, 90)
$mon.AddArc($mx + $mw - $md, $my, $md, $md, 270, 90)
$mon.AddArc($mx + $mw - $md, $my + $mh - $md, $md, $md, 0, 90)
$mon.AddArc($mx, $my + $mh - $md, $md, $md, 90, 90)
$mon.CloseFigure()
$g.DrawPath($pen, $mon)
$g.DrawLine($pen, 128.0, 158.0, 128.0, 184.0)
$g.DrawLine($pen, 94.0, 190.0, 162.0, 190.0)

$dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 82, 232, 146))
$g.FillEllipse($dot, 187, 79, 18, 18)

$png256 = Join-Path $outDir "OneDesk-256.png"
$bmp.Save($png256, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save((Join-Path $outDir "OneDesk.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$big = New-Object System.Drawing.Bitmap(1024, 1024)
$bg = [System.Drawing.Graphics]::FromImage($big)
$bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$bg.DrawImage($bmp, 0, 0, 1024, 1024)
$big.Save((Join-Path $outDir "OneDesk-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$bytes = [System.IO.File]::ReadAllBytes($png256)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]1)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$bytes.Length)
$bw.Write([UInt32]22)
$bw.Write($bytes)
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir "OneDesk.ico"), $ms.ToArray())

$g.Dispose()
$bmp.Dispose()
$big.Dispose()
$bg.Dispose()
$bw.Dispose()
$ms.Dispose()

Write-Host "Wrote assets/OneDesk.ico, OneDesk-256.png, OneDesk-1024.png"
