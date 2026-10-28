param(
  [string]$InputPath = $env:BANNERSAGE_PREVIEW_INPUT,
  [string]$OutputPath = $env:BANNERSAGE_PREVIEW_OUTPUT,
  [int]$MaxBytes = 196608
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile($InputPath)
try {
  $edge = 1280
  $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg' | Select-Object -First 1
  while ($edge -ge 160) {
    $scale = [Math]::Min([double]1.0, $edge / [double][Math]::Max($source.Width, $source.Height))
    $width = [Math]::Max(1, [int][Math]::Round($source.Width * $scale))
    $height = [Math]::Max(1, [int][Math]::Round($source.Height * $scale))
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $parameters = New-Object System.Drawing.Imaging.EncoderParameters(1)
    try {
      $graphics.Clear([System.Drawing.Color]::White)
      $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $graphics.DrawImage($source, 0, 0, $width, $height)
      $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]75)
      $stream = New-Object System.IO.MemoryStream
      try {
        $bitmap.Save($stream, $codec, $parameters)
        if ($stream.Length -le $MaxBytes) {
          [System.IO.File]::WriteAllBytes($OutputPath, $stream.ToArray())
          exit 0
        }
      } finally { $stream.Dispose() }
    } finally { $parameters.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    $edge = [int]($edge * 0.75)
  }
  throw 'Cannot produce a bounded capture preview'
} finally { $source.Dispose() }
