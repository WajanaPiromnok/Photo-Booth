$ErrorActionPreference = "Stop"

$envFile = if ($env:PHOTO_BOOTH_PRINT_BRIDGE_ENV) {
    $env:PHOTO_BOOTH_PRINT_BRIDGE_ENV
} else {
    Join-Path $env:USERPROFILE ".photo-booth\print-bridge.env"
}

if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith("#") -or -not $line.Contains("=")) {
            return
        }

        $name, $value = $line.Split("=", 2)
        [Environment]::SetEnvironmentVariable($name.Trim(), $value.Trim().Trim('"'), "Process")
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$defaultBridgeDir = Resolve-Path (Join-Path $scriptRoot "..\backend\print-bridge")
$printBridgeDir = if ($env:PRINT_BRIDGE_DIR) { $env:PRINT_BRIDGE_DIR } else { $defaultBridgeDir.Path }
$nodeBin = if ($env:NODE_BIN) { $env:NODE_BIN } else { "node" }

if (-not $env:PORT) {
    $env:PORT = "18080"
}

if (-not $env:DEFAULT_PRINTER_NAME) {
    $printerPreference = if ($env:PRINT_BRIDGE_PRINTER_PREFERENCE) {
        $env:PRINT_BRIDGE_PRINTER_PREFERENCE
    } else {
        "DS-RX1 4x6 Cut,DS-RX1"
    }

    $preferredNames = $printerPreference.Split(",") |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ }

    $printers = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -and
        $_.Name -notlike "Microsoft *" -and
        $_.Name -notlike "*PDF*" -and
        $_.Name -notlike "*XPS*" -and
        $_.Name -notlike "*OneNote*"
    })

    $selectedPrinter = $null
    foreach ($preferredName in $preferredNames) {
        $selectedPrinter = $printers | Where-Object { $_.Name -eq $preferredName } | Select-Object -First 1
        if ($selectedPrinter) {
            break
        }

        $selectedPrinter = $printers | Where-Object { $_.Name -like "*$preferredName*" } | Select-Object -First 1
        if ($selectedPrinter) {
            break
        }
    }

    $allowAnyPrinterFallback = $env:PRINT_BRIDGE_ALLOW_ANY_PRINTER_FALLBACK -and
        $env:PRINT_BRIDGE_ALLOW_ANY_PRINTER_FALLBACK -notin @("0", "false", "False", "no", "off")

    if (-not $selectedPrinter -and $allowAnyPrinterFallback -and $printers.Count -gt 0) {
        $selectedPrinter = $printers | Select-Object -First 1
    }

    if (-not $selectedPrinter) {
        throw "No preferred photo printer was found. Connect DS-RX1, set DEFAULT_PRINTER_NAME manually, or set PRINT_BRIDGE_ALLOW_ANY_PRINTER_FALLBACK=true for testing."
    }

    $env:DEFAULT_PRINTER_NAME = $selectedPrinter.Name
}

if (-not $env:ALLOWED_PRINTER_NAMES) {
    $env:ALLOWED_PRINTER_NAMES = $env:DEFAULT_PRINTER_NAME
}

if (-not $env:OVERRIDE_REQUESTED_PRINTER) {
    $env:OVERRIDE_REQUESTED_PRINTER = "true"
}

if (-not $env:PRINT_PLATFORM) {
    $env:PRINT_PLATFORM = "win32"
}

if (-not $env:PRINT_COMMAND) {
    $env:PRINT_COMMAND = "powershell.exe"
}

Write-Host "Photo Booth PrintBridge starting on http://127.0.0.1:$env:PORT"
Write-Host "Printer: $env:DEFAULT_PRINTER_NAME"
Write-Host "Bridge: $printBridgeDir"
if ($env:PRINT_BRIDGE_DRY_RUN -and $env:PRINT_BRIDGE_DRY_RUN -notin @("0", "false", "False", "no", "off")) {
    Write-Host "Mode: preview only - print jobs will not be sent to the printer"
    if ($env:PRINT_BRIDGE_OPEN_PREVIEW -and $env:PRINT_BRIDGE_OPEN_PREVIEW -notin @("0", "false", "False", "no", "off")) {
        Write-Host "Preview: enabled - generated print images will open automatically"
    }
} else {
    Write-Host "Mode: live print"
}

Push-Location $printBridgeDir
try {
    & $nodeBin src/server.js
} finally {
    Pop-Location
}
