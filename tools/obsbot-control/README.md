# OBSBOT Control Helper

Small CLI wrapper around OBSBOT `libdev` for hardware run-state control.

This controls the OBSBOT device state, not the OBSBOT Center virtual camera
toggle.

Photo Booth treats OBSBOT integration as two separate layers:

- `obsbot-control` wakes/sleeps the physical OBSBOT hardware.
- OBSBOT Center owns the `OBSBOT Virtual Camera` output. Open OBSBOT Center
  and enable Virtual Camera there before starting a booth session.
- Unity automatically prefers `OBSBOT Virtual Camera` when the OS exposes it;
  if it is not visible within the configured discovery timeout, Unity falls
  back to the physical OBSBOT device or the first available camera.

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

`BoothFrontendController` now talks to a generic camera device controller. For
the current OBSBOT setup, set `cameraDeviceControllerKind` to `ObsbotCli` and
keep `obsbotControlExecutablePath` pointed at this helper. The controller calls:

- entering `VoucherEntry` / `Capture`: `wake`
- leaving `VoucherEntry` / `Capture`: `sleep`

If the executable is missing, Unity logs a warning once and continues the booth
flow without OBSBOT power control.

For non-OBSBOT cameras:

- Use `None` for a generic webcam, HDMI capture card, or Canon EOS Webcam
  Utility where Unity only needs to start/stop the `WebCamTexture` stream.
- Use `ExternalCommand` when a camera requires custom wake/sleep commands, such
  as a vendor CLI, smart relay, or future Canon tethering helper.

`BoothCameraCaptureService` selects camera input separately through
`WebCamTexture`. The default preference order is:

1. `OBSBOT Virtual Camera`
2. `OBSBOT`
3. first available camera

`BoothRuntimeBootstrap.preferredCameraDeviceDiscoveryTimeoutSeconds` controls
how long Unity waits for the virtual camera before falling back.
