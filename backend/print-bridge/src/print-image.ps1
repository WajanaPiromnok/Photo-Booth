param(
    [Parameter(Mandatory = $true)]
    [string] $ImagePath,

    [Parameter(Mandatory = $true)]
    [string] $PrinterName,

    [int] $Copies = 1,
    [int] $PaperWidthHundredths = 600,
    [int] $PaperHeightHundredths = 400,
    [switch] $Portrait
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $ImagePath)) {
    throw "Image file was not found: $ImagePath"
}

for ($copy = 0; $copy -lt [Math]::Max(1, $Copies); $copy++) {
    $document = New-Object System.Drawing.Printing.PrintDocument
    $document.PrinterSettings.PrinterName = $PrinterName
    if (-not $document.PrinterSettings.IsValid) {
        throw "Printer is not valid: $PrinterName"
    }

    $document.DocumentName = [IO.Path]::GetFileName($ImagePath)
    $document.OriginAtMargins = $false
    $document.DefaultPageSettings.Landscape = -not $Portrait -and $PaperWidthHundredths -lt $PaperHeightHundredths
    $document.DefaultPageSettings.Margins = New-Object System.Drawing.Printing.Margins(0, 0, 0, 0)
    $document.DefaultPageSettings.PaperSize = New-Object System.Drawing.Printing.PaperSize("Photo 6x4", $PaperWidthHundredths, $PaperHeightHundredths)

    $image = [System.Drawing.Image]::FromFile($ImagePath)
    try {
        $handler = [System.Drawing.Printing.PrintPageEventHandler] {
            param($sender, $event)

            $pageBounds = $event.PageBounds
            $bounds = New-Object System.Drawing.RectangleF(0, 0, $pageBounds.Width, $pageBounds.Height)

            $event.Graphics.PageUnit = [System.Drawing.GraphicsUnit]::Display
            $event.Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $event.Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $event.Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $event.Graphics.TranslateTransform(-$event.PageSettings.HardMarginX, -$event.PageSettings.HardMarginY)
            $event.Graphics.DrawImage($image, $bounds)
            $event.HasMorePages = $false
        }

        $document.add_PrintPage($handler)
        $document.Print()
        $document.remove_PrintPage($handler)
    } finally {
        $image.Dispose()
        $document.Dispose()
    }
}
