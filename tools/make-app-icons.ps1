# Makes the programs' .ico files from their logos in Logo.xaml.
#
#   powershell -ExecutionPolicy Bypass -File tools/make-app-icons.ps1
#
# Each logo is drawn by WPF from the DrawingImage svg2xaml.py made from its
# SVG (res/Sirius Studio Plus.svg, Sirius Builder.svg, Sirius Edge.svg), at
# the sizes Windows asks an .ico for - taskbar, title bar, Explorer's views,
# 256 for large icons - and packed as PNG frames, which Windows reads since
# Vista. Run it after changing an SVG and regenerating Logo.xaml:
#
#   python tools/svg2xaml.py src/Sirius.Studio.App/Themes/Logo.xaml "res/Sirius Stu.svg" \
#       "res/Sirius Studio Plus.svg" "res/Sirius Builder.svg" "res/Sirius Edge.svg"
#
# res/Sirius Stu.ico is not made here: its small frames were drawn by hand.
# A logo whose 16 px frame needs the same care can have its .ico replaced by
# a hand-made one; nothing regenerates it unless this script is run.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$logos = Join-Path $root 'src/Sirius.Studio.App/Themes/Logo.xaml'
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

$icons = [ordered]@{
    'Icon.SiriusStudioPlus' = 'res/Sirius Studio Plus.ico'
    'Icon.SiriusBuilder'    = 'res/Sirius Builder.ico'
    'Icon.SiriusEdge'       = 'res/Sirius Edge.ico'
}

$stream = [System.IO.File]::OpenRead($logos)
try {
    $dictionary = [System.Windows.Markup.XamlReader]::Load($stream)
}
finally {
    $stream.Dispose()
}

function Render-Png([System.Windows.Media.ImageSource] $image, [int] $size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $context.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))

    $memory = New-Object System.IO.MemoryStream
    $encoder.Save($memory)
    return , $memory.ToArray()
}

foreach ($key in $icons.Keys) {
    $image = $dictionary[$key]
    if ($null -eq $image) { throw "Logo.xaml has no $key." }

    $frames = foreach ($size in $sizes) { , (Render-Png $image $size) }

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $out

    # ICONDIR
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $sizes.Count)

    # ICONDIRENTRY each, then the PNGs in the same order.
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte] $side)
        $writer.Write([byte] $side)
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $frames[$i].Length)
        $writer.Write([uint32] $offset)
        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()

    $path = Join-Path $root $icons[$key]
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    Write-Host ("{0,-28} {1} ({2} sizes, {3:N0} bytes)" -f $key, $icons[$key], $sizes.Count, $out.Length)
}
