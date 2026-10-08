# Draws app.ico: the Squish mark, an archive box pressed flat between two arrows.
# The same shape is drawn in code for the in-app logo (LogoMark in Squish.cs); keep the two in step.
# Run:  powershell -ExecutionPolicy Bypass -File make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'))
Add-Type -AssemblyName System.Drawing

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$radius) {
  $d = 2 * $radius
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return , $p
}

$sizes = 16, 24, 32, 48, 64, 256
$images = @()
foreach ($size in $sizes) {
  $bmp = New-Object Drawing.Bitmap $size, $size
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  [float]$n = $size - 1

  # Tile: violet -> pink -> orange, the two themes' colours
  $rect = New-Object Drawing.RectangleF 0, 0, $n, $n
  $brush = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::Empty), ([Drawing.Color]::Empty), 45.0
  $blend = New-Object Drawing.Drawing2D.ColorBlend
  $blend.Colors = [Drawing.Color]::FromArgb(132, 92, 255), [Drawing.Color]::FromArgb(226, 100, 244), [Drawing.Color]::FromArgb(255, 156, 66)
  $blend.Positions = 0.0, 0.55, 1.0
  $brush.InterpolationColors = $blend
  $g.FillPath($brush, (Rounded 0 0 $n $n ($n * 0.26)))

  # Arrows pressing in from above and below
  $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.4, $n * 0.085))
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
  $g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF ($n * 0.36), ($n * 0.20)), (New-Object Drawing.PointF ($n * 0.50), ($n * 0.32)), (New-Object Drawing.PointF ($n * 0.64), ($n * 0.20))))
  $g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF ($n * 0.36), ($n * 0.80)), (New-Object Drawing.PointF ($n * 0.50), ($n * 0.68)), (New-Object Drawing.PointF ($n * 0.64), ($n * 0.80))))

  # The squished box, with its handle slot
  $g.FillPath([Drawing.Brushes]::White, (Rounded ($n * 0.17) ($n * 0.41) ($n * 0.66) ($n * 0.18) ($n * 0.07)))
  if ($size -ge 32) {
    $slot = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(150, 96, 240)), ($n * 0.05)
    $slot.StartCap = 'Round'; $slot.EndCap = 'Round'
    $g.DrawLine($slot, [float]($n * 0.43), [float]($n * 0.50), [float]($n * 0.57), [float]($n * 0.50))
  }
  $g.Dispose()

  $ms = New-Object IO.MemoryStream
  if ($size -ge 256) { $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png) }
  else {
    # Classic DIB entry: header, bottom-up BGRA pixels, then an (unused) AND mask.
    $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $size; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $bw.Write((New-Object byte[] ([int]([math]::Ceiling($size / 32.0) * 4 * $size))))
    $bw.Flush()
  }
  $images += , $ms.ToArray(); $bmp.Dispose()
}

$o = New-Object IO.BinaryWriter ([IO.File]::Create($Out))
$o.Write([uint16]0); $o.Write([uint16]1); $o.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $o.Write([byte]$b); $o.Write([byte]$b); $o.Write([byte]0); $o.Write([byte]0)
  $o.Write([uint16]1); $o.Write([uint16]32); $o.Write([uint32]$images[$i].Length); $o.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $o.Write($img) }
$o.Close()
"wrote $Out"
