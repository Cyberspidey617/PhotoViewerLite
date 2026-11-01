# Builds the viewer and its installer with the C# compiler that ships with .NET Framework 4.8
# (no SDK needed). Outputs:
#   bin\PhotoViewerLite.exe         the viewer
#   dist\PhotoViewerLite-Setup.exe  the installer, with the viewer embedded
$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\FrameworkArm64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
}
$csc = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$bin = Join-Path $PSScriptRoot 'bin'
$dist = Join-Path $PSScriptRoot 'dist'
$icon = Join-Path $PSScriptRoot 'PhotoViewerLite.ico'
New-Item -ItemType Directory -Force $bin, $dist | Out-Null

function New-AppIcon([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 24, 32, 48, 64, 256
    $images = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $s = $size / 16.0
        # Photo frame
        $frame = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = [Math]::Max(1, 2.5 * $s)
        $x = 0.5 * $s; $y = 1.5 * $s; $w = 15 * $s; $h = 13 * $s
        $frame.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $frame.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
        $frame.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $frame.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
        $frame.CloseFigure()
        $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, $y), (New-Object System.Drawing.PointF 0, ($y + $h)), ([System.Drawing.Color]::FromArgb(255, 58, 140, 255)), ([System.Drawing.Color]::FromArgb(255, 30, 80, 200))
        $g.FillPath($sky, $frame)
        $g.SetClip($frame)
        # Sun and hills
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 214, 64))), 9.5 * $s, 3.5 * $s, 3.5 * $s, 3.5 * $s)
        $back = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (0 * $s), (15 * $s)), (New-Object System.Drawing.PointF (5.5 * $s), (7 * $s)), (New-Object System.Drawing.PointF (11 * $s), (15 * $s)))
        $front = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (5 * $s), (15 * $s)), (New-Object System.Drawing.PointF (10.5 * $s), (9 * $s)), (New-Object System.Drawing.PointF (16 * $s), (15 * $s)))
        $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 160, 90))), $back)
        $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 120, 70))), $front)
        $g.ResetClip()
        $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 240, 250)), ([Math]::Max(1, 1 * $s))), $frame)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    # ICO container with PNG-compressed entries
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write($img) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
}

function Invoke-Csc([string[]]$Arguments) {
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)." }
}

if (-not (Test-Path $icon)) { New-AppIcon $icon }

$wpfReferences = @(
    (Join-Path $wpf 'PresentationCore.dll'),
    (Join-Path $wpf 'PresentationFramework.dll'),
    (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Core.dll')
)

$viewer = Join-Path $bin 'PhotoViewerLite.exe'
$viewerReferences = $wpfReferences + @(
    (Join-Path $wpf 'System.Printing.dll'),
    (Join-Path $wpf 'ReachFramework.dll'),
    (Join-Path $framework 'Microsoft.VisualBasic.dll')
)
Invoke-Csc (@("/out:$viewer", "/win32icon:$icon") + ($viewerReferences | ForEach-Object { "/reference:$_" }) +
            (Join-Path $PSScriptRoot 'PhotoViewerLite.cs'))
Write-Host "Built $viewer"

$setup = Join-Path $dist 'PhotoViewerLite-Setup.exe'
Invoke-Csc (@("/out:$setup", "/win32icon:$icon", "/resource:$viewer,PhotoViewerLite.exe") +
            ($wpfReferences | ForEach-Object { "/reference:$_" }) + (Join-Path $PSScriptRoot 'Setup.cs'))
Write-Host "Built $setup"
