$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path $PSScriptRoot 'MailReminder/Assets.xcassets'
$iconDirectory = Join-Path $assetRoot 'AppIcon.appiconset'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$bitmap = [Drawing.Bitmap]::new(1024, 1024, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,0), [Drawing.Point]::new(1024,1024), [Drawing.Color]::FromArgb(32,91,208), [Drawing.Color]::FromArgb(15,37,93))
$pen = [Drawing.Pen]::new([Drawing.Color]::White, 34)
$pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
try {
    $graphics.FillRectangle($background, 0, 0, 1024, 1024)
    $graphics.DrawArc($pen, 300, 220, 424, 480, 180, 180)
    $graphics.DrawLine($pen, 300, 460, 260, 700)
    $graphics.DrawLine($pen, 724, 460, 764, 700)
    $graphics.DrawLine($pen, 260, 700, 764, 700)
    $graphics.DrawArc($pen, 442, 700, 140, 128, 0, 180)
    $bitmap.Save((Join-Path $iconDirectory 'AppIcon.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $pen.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
$assetInfo = [ordered]@{ info = [ordered]@{ author = 'xcode'; version = 1 } }
$assetInfo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $assetRoot 'Contents.json') -Encoding utf8
$iconInfo = [ordered]@{ images = @([ordered]@{ filename = 'AppIcon.png'; idiom = 'universal'; platform = 'ios'; size = '1024x1024' }); info = [ordered]@{ author = 'xcode'; version = 1 } }
$iconInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $iconDirectory 'Contents.json') -Encoding utf8
$readback = Get-Content -LiteralPath (Join-Path $iconDirectory 'Contents.json') -Raw | ConvertFrom-Json
if ($readback.images -isnot [array] -or $readback.images.Count -ne 1) { throw 'App icon catalog shape failed.' }
Write-Output 'PASS iOS icon and catalog readback'
