# Draws the dashboard's icon: a dark panel of four tiles with one of them lit, which is
# what the panel itself looks like from across a desk.
#
# The icon is drawn by a script rather than kept as an image nobody can edit, so the
# design lives in one place and every size comes out of it. Run it after changing
# anything here; the files it writes are committed, because the build needs them and a
# build shouldn't depend on PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File icon.ps1
#
#   icon.ico                  the exe, and so the taskbar and Explorer
#   Remote/Web/icon-512.png   the manifest's large icon, which is what Android installs from
#   Remote/Web/icon-192.png   the manifest's small icon, and the browser tab
#   Remote/Web/icon-180.png   apple-touch-icon, which is what an iPad puts on its home screen
#
# The three web icons sit beside the page that names them, and are embedded in the exe
# from there; the .ico is the project's own and stays at the root, where the csproj's
# ApplicationIcon expects it.
#
# Everything is laid out on a 512 grid and scaled, so the proportions hold at 16px.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# The panel's own colours, lightened where they had to be. #2d2d30 on #1e1e1e is the
# real contrast between a tile and its group, and at 16px that reads as one dark blob:
# an icon is looked at from further away than the thing it stands for.
$edge    = [Drawing.Color]::FromArgb(255, 0x3F, 0x3F, 0x46)
$backTop = [Drawing.Color]::FromArgb(255, 0x28, 0x28, 0x2D)
$backEnd = [Drawing.Color]::FromArgb(255, 0x16, 0x16, 0x1A)
$tile    = [Drawing.Color]::FromArgb(255, 0x55, 0x55, 0x5E)
$litTop  = [Drawing.Color]::FromArgb(255, 0x1A, 0x93, 0xE8)
$litEnd  = [Drawing.Color]::FromArgb(255, 0x00, 0x6C, 0xB8)

function Add-Rounded {
  param([Drawing.Drawing2D.GraphicsPath]$Path, [single]$X, [single]$Y,
        [single]$W, [single]$H, [single]$R)

  $d = [single]($R * 2)
  $Path.AddArc($X, $Y, $d, $d, 180, 90)
  $Path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
  $Path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
  $Path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
  $Path.CloseFigure()
}

# $Inset is how far in from the edge the tiles start, in 512-space. The web icons keep a
# wide margin because Android crops a maskable icon to whatever shape the launcher likes
# - anything outside a circle 80% of the width can be cut - while the .ico is never
# cropped and can afford to fill more of its square.
function New-Icon {
  param([int]$Size, [single]$Inset)

  $bmp = New-Object Drawing.Bitmap($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $s = [single]($Size / 512.0)
  $g.ScaleTransform($s, $s)

  # The panel. Rounded because every platform this lands on draws app icons rounded, and
  # the ones that mask it into something else cut the corners off anyway.
  $panel = New-Object Drawing.Drawing2D.GraphicsPath
  Add-Rounded -Path $panel -X 6 -Y 6 -W 500 -H 500 -R 104
  $top = New-Object Drawing.PointF -ArgumentList ([single]0), ([single]0)
  $bottom = New-Object Drawing.PointF -ArgumentList ([single]0), ([single]512)
  $fill = New-Object Drawing.Drawing2D.LinearGradientBrush -ArgumentList $top, $bottom, $backTop, $backEnd
  $g.FillPath($fill, $panel)

  # A hairline at 16px, a visible edge at 256. Either is better than the icon dissolving
  # into a dark taskbar.
  $pen = New-Object Drawing.Pen($edge, [single]6)
  $g.DrawPath($pen, $panel)

  # Two by two, with the top-left one lit: the smallest arrangement that still reads as
  # "a grid of buttons" rather than as a logo.
  $gap  = [single]26
  $span = [single](512 - ($Inset * 2))
  $side = [single](($span - $gap) / 2)
  $radius = [single]([Math]::Max(3, $side * 0.18))

  for ($row = 0; $row -lt 2; $row++) {
    for ($column = 0; $column -lt 2; $column++) {
      $x = [single]($Inset + $column * ($side + $gap))
      $y = [single]($Inset + $row * ($side + $gap))

      $shape = New-Object Drawing.Drawing2D.GraphicsPath
      Add-Rounded -Path $shape -X $x -Y $y -W $side -H $side -R $radius

      if ($row -eq 0 -and $column -eq 0) {
        # The two points are built up first: PowerShell reads `$x, $y + $side` as one
        # array of three rather than as two arguments.
        $bottom = [single]($y + $side)
        $from = New-Object Drawing.PointF -ArgumentList $x, $y
        $to = New-Object Drawing.PointF -ArgumentList $x, $bottom
        $brush = New-Object Drawing.Drawing2D.LinearGradientBrush -ArgumentList $from, $to, $litTop, $litEnd
      } else {
        $brush = New-Object Drawing.SolidBrush($tile)
      }

      $g.FillPath($brush, $shape)
      $brush.Dispose()
      $shape.Dispose()
    }
  }

  $pen.Dispose(); $fill.Dispose(); $panel.Dispose(); $g.Dispose()
  return $bmp
}

function Save-Png {
  param([int]$Size, [single]$Inset, [string]$Name)

  $bmp = New-Icon -Size $Size -Inset $Inset
  $path = Join-Path $here $Name
  $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "  $Name"
}

function Get-PngBytes {
  param([int]$Size, [single]$Inset)

  $bmp = New-Icon -Size $Size -Inset $Inset
  $stream = New-Object IO.MemoryStream
  $bmp.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $bytes = $stream.ToArray()
  $stream.Dispose()

  # The leading comma matters: PowerShell unrolls an array returned from a function into
  # the pipeline, so `return $bytes` hands back several thousand separate bytes and every
  # directory entry below ends up claiming to be one byte long. Windows accepts that file
  # and quietly draws the generic application icon instead.
  return ,$bytes
}

Write-Host "drawing"

Save-Png -Size 512 -Inset 112 -Name "Remote\Web\icon-512.png"
Save-Png -Size 192 -Inset 112 -Name "Remote\Web\icon-192.png"
Save-Png -Size 180 -Inset 112 -Name "Remote\Web\icon-180.png"

# An .ico is a container: a short header, one directory entry per size, then the images
# themselves. The images are PNGs rather than bitmaps - supported since Vista, and it
# keeps the file a tenth of the size.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add((Get-PngBytes -Size $size -Inset 84)) }

$ico = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($ico)
$writer.Write([uint16]0)            # reserved
$writer.Write([uint16]1)            # 1 = icon, as opposed to a cursor
$writer.Write([uint16]$sizes.Count)

# Each entry says where its image starts, so the offsets have to be worked out before any
# of the images are written.
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
  # 256 doesn't fit in a byte and is written as 0, which is the format's way of saying it.
  $side = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
  $writer.Write([byte]$side)        # width
  $writer.Write([byte]$side)        # height
  $writer.Write([byte]0)            # colours in the palette: none, it's true colour
  $writer.Write([byte]0)            # reserved
  $writer.Write([uint16]1)          # colour planes
  $writer.Write([uint16]32)         # bits per pixel
  $writer.Write([uint32]$images[$i].Length)
  $writer.Write([uint32]$offset)
  $offset += $images[$i].Length
}

foreach ($image in $images) { $writer.Write($image) }

$writer.Flush()
$bytes = $ico.ToArray()
$writer.Dispose()
[IO.File]::WriteAllBytes((Join-Path $here "icon.ico"), $bytes)
Write-Host "  icon.ico ($($sizes.Count) sizes, $([Math]::Round($bytes.Length / 1kb, 1)) kB)"
