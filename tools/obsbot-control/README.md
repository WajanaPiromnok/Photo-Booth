# OBSBOT Control Helper

Small CLI wrapper around OBSBOT `libdev` for hardware run-state control.

This controls the OBSBOT device state, not the OBSBOT Center virtual camera
toggle.

## Build on macOS

```bash
scripts/build-obsbot-control.sh
```

By default the script uses:

```text
tools/obsbot-control/libdev_v2.1.0_8
```

You can override it with:

```bash
OBSBOT_SDK_PATH=/path/to/libdev_v2.1.0_8 scripts/build-obsbot-control.sh
```

The script writes:

```text
tools/obsbot-control/bin/obsbot-control
tools/obsbot-control/bin/libdev.dylib
```

## Manual Test

```bash
tools/obsbot-control/bin/obsbot-control status --timeout-ms 8000
tools/obsbot-control/bin/obsbot-control sleep --device-name OBSBOT --timeout-ms 8000
tools/obsbot-control/bin/obsbot-control wake --device-name OBSBOT --timeout-ms 8000
```

## Unity Integration

`BoothFrontendController` optionally calls this helper:

- entering `Capture`: `wake`
- leaving `Capture`: `sleep`

If the executable is missing, Unity logs a warning once and continues the booth
flow without OBSBOT power control.
