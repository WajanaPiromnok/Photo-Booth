param(
    [Parameter(Mandatory = $true)]
    [string] $ImagePath,

    [Parameter(Mandatory = $true)]
    [string] $PrinterName,

    [int] $Copies = 1,
    [int] $PaperWidthHundredths = 600,
    [int] $PaperHeightHundredths = 400,
    [string] $PaperName = "(6x4)",
    [switch] $Landscape,
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
    $document.DefaultPageSettings.Margins = New-Object System.Drawing.Printing.Margins(0, 0, 0, 0)
    $selectedPaperSize = $null
    foreach ($paperSize in $document.PrinterSettings.PaperSizes) {
        if ($paperSize.PaperName -eq $PaperName) {
            $selectedPaperSize = $paperSize
            break
        }

        if (-not $selectedPaperSize -and $paperSize.PaperName -like "*6x4*") {
            $selectedPaperSize = $paperSize
        }
    }

    if ($selectedPaperSize) {
        $document.DefaultPageSettings.PaperSize = $selectedPaperSize
        $paperIsLandscape = $selectedPaperSize.Width -gt $selectedPaperSize.Height
        if (-not $paperIsLandscape) {
            $document.DefaultPageSettings.Landscape = $Landscape -or (-not $Portrait -and $PaperWidthHundredths -gt $PaperHeightHundredths)
        }
        Write-Host "Using printer paper size: $($selectedPaperSize.PaperName) $($selectedPaperSize.Width)x$($selectedPaperSize.Height)"
    } else {
        Write-Warning "Paper size '$PaperName' was not reported by printer '$PrinterName'. Keeping the driver's default paper size."
    }
    $orientationName = if ($document.DefaultPageSettings.Landscape) { "Landscape" } else { "Portrait" }
    Write-Host "Using printer orientation: $orientationName"

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
