using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    public interface ICanonCameraBackend : IDisposable
    {
        bool IsSessionOpen { get; }
        bool IsLiveViewRunning { get; }
        string CameraName { get; }
        Task StartLiveViewAsync(CancellationToken cancellationToken = default);
        Task AutoFocusAsync(CancellationToken cancellationToken = default);
        Task<byte[]> DownloadLiveViewFrameAsync(CancellationToken cancellationToken = default);
        Task<string> CaptureStillAsync(string outputDirectory, string fileName, CancellationToken cancellationToken = default);
        Task StopLiveViewAsync(CancellationToken cancellationToken = default);
        Task CloseSessionAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Windows x64 Canon EDSDK adapter. All public operations are serialized because
    /// EDSDK camera references must not be used concurrently.
    /// </summary>
    public sealed class CanonEdsdkCameraService : ICanonCameraBackend
    {
        private const uint EdsErrOk = 0;
        private const uint EdsErrNotReady = 0x0000A102;
        private const uint PropertySaveTo = 0x0000000B;
        private const uint PropertyEvfOutputDevice = 0x00000500;
        private const uint SaveToHost = 2;
        private const uint EvfOutputDevicePc = 2;
        private const uint CameraCommandTakePicture = 0;
        private const uint ObjectEventAll = 0x00000200;
        private const uint ObjectEventDirItemRequestTransfer = 0x00000208;
        private const int FileCreateDispositionCreateAlways = 1;
        private const int AccessReadWrite = 2;
        private const uint EdsErrInternalError = 0x00000002;
        private const uint EdsErrDeviceBusy = 0x00000081;
        private const uint EdsErrTakePictureAfNg = 0x00008D01;

        private const uint CameraCommandPressShutterButton = 0x00000004;

        private const int ShutterButtonOff = 0x00000000;
        private const int ShutterButtonHalfway = 0x00000001;
        private const int ShutterButtonCompletely = 0x00000003;

        // สำรองกรณีอยาก force ถ่ายแบบไม่รอ AF
        private const int ShutterButtonCompletelyNonAf = 0x00010003;

        private readonly int captureTimeoutMs;
        private readonly EdsObjectEventHandler objectEventHandler;
        private readonly EdsdkCommandDispatcher dispatcher;

        private IntPtr camera;
        private bool sdkInitialized;
        private bool disposed;
        private IntPtr pendingCapturedItem;

        private DateTime lastAutofocusUtc = DateTime.MinValue;

        public CanonEdsdkCameraService(int captureTimeoutMs = 20000)
        {
            this.captureTimeoutMs = Math.Max(3000, captureTimeoutMs);
            objectEventHandler = HandleObjectEvent;
            dispatcher = new EdsdkCommandDispatcher("Canon EDSDK Worker");
        }

        public bool IsSessionOpen => camera != IntPtr.Zero;
        public bool IsLiveViewRunning { get; private set; }
        public string CameraName { get; private set; }

        public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                ThrowIfDisposed();
                EnsureWindows64Bit();
                OpenSessionIfNeeded();
                if (IsLiveViewRunning)
                {
                    return;
                }

                SetUInt32PropertyWithRetry(
                    PropertyEvfOutputDevice,
                    EvfOutputDevicePc,
                    "enable Live View",
                    timeoutMs: 10000,
                    delayMs: 300
                );
                IsLiveViewRunning = true;
            }, cancellationToken);
        }

        public Task AutoFocusAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                ThrowIfDisposed();
                EnsureWindows64Bit();
                OpenSessionIfNeeded();

                FocusOnceCore(cancellationToken);

            }, cancellationToken);
        }

        private void FocusOnceCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // กัน shutter state ค้าง
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(150);

                // กดครึ่งชัตเตอร์เพื่อ Auto Focus
                SendCommandWithRetry(
                    CameraCommandPressShutterButton,
                    ShutterButtonHalfway,
                    "autofocus half-press",
                    timeoutMs: 5000,
                    delayMs: 200
                );

                // ให้เลนส์มีเวลาหาโฟกัส
                PumpCameraEvents(900);

                lastAutofocusUtc = DateTime.UtcNow;
            }
            catch (CanonEdsdkException exception) when (exception.ErrorCode == EdsErrTakePictureAfNg)
            {
                throw new InvalidOperationException(
                    "Canon autofocus failed. Please increase light, improve contrast, or move the subject into focus area.",
                    exception
                );
            }
            finally
            {
                // สำคัญ: ปล่อยปุ่มทุกครั้ง
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(300);
            }
        }

        public Task<byte[]> DownloadLiveViewFrameAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSessionOpen || !IsLiveViewRunning)
                {
                    throw new InvalidOperationException("Canon Live View is not running.");
                }

                IntPtr stream = IntPtr.Zero;
                IntPtr evfImage = IntPtr.Zero;
                try
                {
                    Check(EdsCreateMemoryStream(0, out stream), "create Live View stream");
                    Check(EdsCreateEvfImageRef(stream, out evfImage), "create Live View image");
                    var downloadError = EdsDownloadEvfImage(camera, evfImage);
                    if (downloadError == EdsErrNotReady)
                    {
                        return null;
                    }

                    Check(downloadError, "download Live View frame");
                    Check(EdsGetLength(stream, out var length), "read Live View frame length");
                    Check(EdsGetPointer(stream, out var pointer), "read Live View frame pointer");
                    if (length == 0 || length > int.MaxValue || pointer == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Canon Live View returned an empty frame.");
                    }

                    var bytes = new byte[(int)length];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    Release(ref evfImage);
                    Release(ref stream);
                }
            }, cancellationToken);
        }

        public Task<string> CaptureStillAsync(
    string outputDirectory,
    string fileName,
    CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                ThrowIfDisposed();

                if (!IsSessionOpen)
                {
                    throw new InvalidOperationException("Canon camera session is not open.");
                }

                Directory.CreateDirectory(outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory)));

                var outputPath = Path.Combine(
                    outputDirectory,
                    string.IsNullOrWhiteSpace(fileName) ? "capture.jpg" : fileName
                );

                var restartLiveView = IsLiveViewRunning;

                Interlocked.Exchange(ref pendingCapturedItem, IntPtr.Zero);

                try
                {
                    // เสถียรกว่า: ถ้า Live View เปิดอยู่ ให้ปิดก่อนถ่ายเสมอ
                    if (IsLiveViewRunning)
                    {
                        StopLiveViewCore();
                        PumpCameraEvents(300);
                    }

                    var hasFreshFocus = (DateTime.UtcNow - lastAutofocusUtc).TotalSeconds <= 7;

                    if (hasFreshFocus)
                    {
                        TakePictureWithoutAutoFocus();
                    }
                    else
                    {
                        TakePictureWithAutoFocus();
                    }

                    var item = WaitForCapturedItem(cancellationToken);

                    DownloadDirectoryItem(item, outputPath);

                    // ให้กล้องหาย busy หลัง download complete
                    PumpCameraEvents(500);

                    return outputPath;
                }
                finally
                {
                    Interlocked.Exchange(ref pendingCapturedItem, IntPtr.Zero);

                    if (restartLiveView && IsSessionOpen)
                    {
                        try
                        {
                            PumpCameraEvents(500);

                            SetUInt32PropertyWithRetry(
                                PropertyEvfOutputDevice,
                                EvfOutputDevicePc,
                                "resume Live View",
                                timeoutMs: 10000,
                                delayMs: 300
                            );

                            IsLiveViewRunning = true;
                        }
                        catch (Exception exception)
                        {
                            // สำคัญ: อย่าให้ resume live view fail แล้วทำให้ capture ที่สำเร็จแล้ว fail
                            IsLiveViewRunning = false;
                            Debug.LogWarning($"Canon capture succeeded, but Live View resume failed: {exception.Message}");
                        }
                    }
                }
            }, cancellationToken);
        }

        public Task StopLiveViewAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                StopLiveViewCore();
            }, cancellationToken);
        }

        public Task CloseSessionAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(() =>
            {
                CloseSessionCore();
            }, cancellationToken);
        }

        private void InitializeSdkIfNeeded()
        {
            if (sdkInitialized)
            {
                return;
            }

            uint lastError = EdsErrOk;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                lastError = EdsInitializeSDK();

                if (lastError == EdsErrOk)
                {
                    sdkInitialized = true;
                    Debug.Log("Canon EDSDK initialized.");
                    return;
                }

                Debug.LogWarning($"Canon EDSDK initialize failed attempt {attempt}/3: 0x{lastError:X8}");

                // ถ้า EDSDK internal error ให้รอนานขึ้นหน่อย
                Thread.Sleep(lastError == EdsErrInternalError ? 1500 : 500);
            }

            Check(lastError, "initialize Canon EDSDK");
        }

        private void OpenSessionIfNeeded()
        {
            if (camera != IntPtr.Zero)
            {
                return;
            }

            try
            {
                InitializeSdkIfNeeded();

                IntPtr cameraList = IntPtr.Zero;
                try
                {
                    Check(EdsGetCameraList(out cameraList), "enumerate Canon cameras");
                    Check(EdsGetChildCount(cameraList, out var count), "count Canon cameras");
                    if (count <= 0)
                    {
                        throw new InvalidOperationException("No Canon camera was detected by EDSDK.");
                    }

                    Check(EdsGetChildAtIndex(cameraList, 0, out camera), "select Canon camera");
                }
                finally
                {
                    Release(ref cameraList);
                }

                Check(EdsOpenSession(camera), "open Canon camera session");
                Check(EdsSetObjectEventHandler(camera, ObjectEventAll, objectEventHandler, IntPtr.Zero), "register Canon capture event handler");
                SetUInt32Property(PropertySaveTo, SaveToHost, "set Canon save destination");
                var capacity = new EdsCapacity
                {
                    NumberOfFreeClusters = int.MaxValue,
                    BytesPerSector = 0x1000,
                    Reset = 1
                };
                Check(EdsSetCapacity(camera, capacity), "set Canon host capacity");
                CameraName = "Canon EDSDK Camera";
            }
            catch (DllNotFoundException exception)
            {
                CloseSessionCore();
                throw new InvalidOperationException("EDSDK.dll or one of its native dependencies could not be loaded.", exception);
            }
            catch (BadImageFormatException exception)
            {
                CloseSessionCore();
                throw new InvalidOperationException("EDSDK.dll is not compatible with the Windows x64 player.", exception);
            }
            catch (EntryPointNotFoundException exception)
            {
                CloseSessionCore();
                throw new InvalidOperationException("EDSDK.dll does not expose the required Canon SDK functions.", exception);
            }
            catch
            {
                CloseSessionCore();
                throw;
            }
        }

        private IntPtr WaitForCapturedItem(CancellationToken cancellationToken)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref pendingCapturedItem) == IntPtr.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.ElapsedMilliseconds >= captureTimeoutMs)
                {
                    throw new TimeoutException("Timed out waiting for Canon to provide the captured image.");
                }

                EdsGetEvent();
                Thread.Sleep(20);
            }

            return Interlocked.Exchange(ref pendingCapturedItem, IntPtr.Zero);
        }

        private uint HandleObjectEvent(uint eventCode, IntPtr objectRef, IntPtr context)
        {
            if (eventCode == ObjectEventDirItemRequestTransfer && objectRef != IntPtr.Zero)
            {
                if (Interlocked.CompareExchange(ref pendingCapturedItem, objectRef, IntPtr.Zero) == IntPtr.Zero)
                {
                    return EdsErrOk;
                }

                EdsDownloadCancel(objectRef);
            }

            if (objectRef != IntPtr.Zero)
            {
                EdsRelease(objectRef);
            }

            return EdsErrOk;
        }

        private void TakePictureWithAutoFocus()
        {
            try
            {
                // กัน state ค้างจากรอบก่อน
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(200);

                // Half-press = ให้กล้อง Auto Focus ก่อน
                SendCommandWithRetry(
                    CameraCommandPressShutterButton,
                    ShutterButtonHalfway,
                    "autofocus half-press",
                    timeoutMs: 5000,
                    delayMs: 200
                );

                // ให้เลนส์มีเวลาหาโฟกัส
                PumpCameraEvents(700);

                // Full-press = ถ่ายภาพ
                SendCommandWithRetry(
                    CameraCommandPressShutterButton,
                    ShutterButtonCompletely,
                    "take picture with autofocus",
                    timeoutMs: 8000,
                    delayMs: 250
                );

                PumpCameraEvents(200);
            }
            catch (CanonEdsdkException exception) when (exception.ErrorCode == EdsErrTakePictureAfNg)
            {
                // AF ไม่เข้า ต้องปล่อยปุ่ม ไม่งั้นกล้องบางรุ่นจะค้าง busy
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(500);

                throw new InvalidOperationException(
                    "Canon autofocus failed. Please increase light, improve contrast, or move the subject into focus area.",
                    exception
                );
            }
            finally
            {
                // สำคัญมาก: ปล่อยปุ่มทุกครั้ง
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(300);
            }
        }

        private void TakePictureWithoutAutoFocus()
        {
            try
            {
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(150);

                SendCommandWithRetry(
                    CameraCommandPressShutterButton,
                    ShutterButtonCompletelyNonAf,
                    "take picture without autofocus",
                    timeoutMs: 8000,
                    delayMs: 250
                );

                PumpCameraEvents(200);
            }
            finally
            {
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(300);
            }
        }

        private void TakePictureWithFallback()
        {
            try
            {
                SendCommandWithRetry(CameraCommandTakePicture, 0, "take picture");
            }
            catch (CanonEdsdkException exception) when (exception.ErrorCode == EdsErrTakePictureAfNg)
            {
                Debug.LogWarning("Canon AF failed. Retrying capture with Non-AF shutter command.");

                // ปล่อยปุ่มก่อน กัน state ค้าง
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(200);

                // กดชัตเตอร์แบบไม่รอ AF
                SendCommandWithRetry(
                    CameraCommandPressShutterButton,
                    ShutterButtonCompletelyNonAf,
                    "take picture without autofocus",
                    timeoutMs: 8000,
                    delayMs: 250
                );

                PumpCameraEvents(200);

                // ปล่อยปุ่มชัตเตอร์
                EdsSendCommand(camera, CameraCommandPressShutterButton, ShutterButtonOff);
                PumpCameraEvents(300);
            }
        }

        private static void DownloadDirectoryItem(IntPtr directoryItem, string outputPath)
        {
            IntPtr stream = IntPtr.Zero;
            try
            {
                Check(EdsGetDirectoryItemInfo(directoryItem, out var info), "read captured image information");
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                Check(EdsCreateFileStream(outputPath, FileCreateDispositionCreateAlways, AccessReadWrite, out stream), "create captured image file");
                Check(EdsDownload(directoryItem, info.Size, stream), "download captured image");
                Check(EdsDownloadComplete(directoryItem), "complete captured image download");
            }
            catch
            {
                EdsDownloadCancel(directoryItem);
                throw;
            }
            finally
            {
                Release(ref stream);
                if (directoryItem != IntPtr.Zero)
                {
                    EdsRelease(directoryItem);
                }
            }
        }

        private void StopLiveViewCore()
        {
            if (!IsLiveViewRunning)
            {
                return;
            }

            if (camera != IntPtr.Zero)
            {
                SetUInt32Property(PropertyEvfOutputDevice, 0, "disable Live View");
            }

            IsLiveViewRunning = false;
        }

        private void CloseSessionCore()
        {
            try
            {
                if (camera != IntPtr.Zero && IsLiveViewRunning)
                {
                    StopLiveViewCore();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Canon Live View cleanup failed: {exception.Message}");
            }

            if (camera != IntPtr.Zero)
            {
                EdsCloseSession(camera);
                EdsRelease(camera);
                camera = IntPtr.Zero;
            }

            CameraName = null;
            IsLiveViewRunning = false;
            //if (sdkInitialized)
            //{
            //    EdsTerminateSDK();
            //    sdkInitialized = false;
            //}
        }

        public Task ShutdownSdkAsync(CancellationToken cancellationToken = default)
        {
            return dispatcher.InvokeAsync(TerminateSdkCore, cancellationToken);
        }

        private void SetUInt32Property(uint propertyId, uint value, string action)
        {
            Check(EdsSetPropertyData(camera, propertyId, 0, sizeof(uint), ref value), action);
        }

        private static void EnsureWindows64Bit()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Canon EDSDK capture is available only on Windows.");
            }

            if (IntPtr.Size != 8)
            {
                throw new PlatformNotSupportedException("Canon EDSDK capture requires a 64-bit Unity process.");
            }
        }

        private void PumpCameraEvents(int durationMs)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < durationMs)
            {
                EdsGetEvent();
                Thread.Sleep(20);
            }
        }

        private void SetUInt32PropertyWithRetry(
            uint propertyId,
            uint value,
            string action,
            int timeoutMs = 8000,
            int delayMs = 250)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            uint lastError = EdsErrOk;

            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                lastError = EdsSetPropertyData(camera, propertyId, 0, sizeof(uint), ref value);

                if (lastError == EdsErrOk)
                {
                    return;
                }

                if (lastError != EdsErrDeviceBusy && lastError != EdsErrNotReady)
                {
                    Check(lastError, action);
                }

                EdsGetEvent();
                Thread.Sleep(delayMs);
            }

            Check(lastError, action);
        }

        private void SendCommandWithRetry(
            uint command,
            int parameter,
            string action,
            int timeoutMs = 8000,
            int delayMs = 250)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            uint lastError = EdsErrOk;

            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                lastError = EdsSendCommand(camera, command, parameter);

                if (lastError == EdsErrOk)
                {
                    return;
                }

                if (lastError != EdsErrDeviceBusy && lastError != EdsErrNotReady)
                {
                    Check(lastError, action);
                }

                EdsGetEvent();
                Thread.Sleep(delayMs);
            }

            Check(lastError, action);
        }

        private static void Check(uint error, string action)
        {
            if (error != EdsErrOk)
            {
                throw new CanonEdsdkException(action, error);
            }
        }

        private static void Release(ref IntPtr reference)
        {
            if (reference == IntPtr.Zero)
            {
                return;
            }

            EdsRelease(reference);
            reference = IntPtr.Zero;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(CanonEdsdkCameraService));
            }
        }
        private void TerminateSdkCore()
        {
            CloseSessionCore();

            if (sdkInitialized)
            {
                EdsTerminateSDK();
                sdkInitialized = false;
                Debug.Log("Canon EDSDK terminated.");
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                dispatcher.InvokeAsync(TerminateSdkCore, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                dispatcher.Dispose();
            }
        }


        private sealed class EdsdkCommandDispatcher : IDisposable
        {
            private readonly BlockingCollection<IWorkItem> queue = new();
            private readonly Thread worker;
            private bool disposed;

            public EdsdkCommandDispatcher(string name)
            {
                worker = new Thread(Run)
                {
                    IsBackground = true,
                    Name = name
                };
                worker.Start();
            }

            public Task InvokeAsync(Action action, CancellationToken cancellationToken)
            {
                return InvokeAsync(() =>
                {
                    action();
                    return true;
                }, cancellationToken);
            }

            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
            {
                if (disposed)
                {
                    return Task.FromException<T>(new ObjectDisposedException(nameof(EdsdkCommandDispatcher)));
                }

                var item = new WorkItem<T>(action, cancellationToken);
                try
                {
                    queue.Add(item, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    item.Cancel();
                }
                catch (InvalidOperationException)
                {
                    item.Fail(new ObjectDisposedException(nameof(EdsdkCommandDispatcher)));
                }

                return item.Task;
            }

            private void Run()
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    item.Execute();
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                queue.CompleteAdding();
                if (Thread.CurrentThread != worker)
                {
                    worker.Join(3000);
                }
                queue.Dispose();
            }

            private interface IWorkItem
            {
                void Execute();
            }

            private sealed class WorkItem<T> : IWorkItem
            {
                private readonly Func<T> action;
                private readonly CancellationToken cancellationToken;
                private readonly TaskCompletionSource<T> completion =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);

                public WorkItem(Func<T> action, CancellationToken cancellationToken)
                {
                    this.action = action ?? throw new ArgumentNullException(nameof(action));
                    this.cancellationToken = cancellationToken;
                }

                public Task<T> Task => completion.Task;

                public void Execute()
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Cancel();
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (OperationCanceledException)
                    {
                        Cancel();
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                    }
                }

                public void Cancel()
                {
                    completion.TrySetCanceled(cancellationToken);
                }

                public void Fail(Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint EdsObjectEventHandler(uint eventCode, IntPtr objectRef, IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct EdsCapacity
        {
            public int NumberOfFreeClusters;
            public int BytesPerSector;
            public int Reset;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct EdsDirectoryItemInfo
        {
            public ulong Size;
            public int IsFolder;
            public uint GroupId;
            public uint Option;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string FileName;
        }

        private sealed class CanonEdsdkException : InvalidOperationException
        {
            public CanonEdsdkException(string action, uint error)
                : base($"Canon EDSDK could not {action} (error 0x{error:X8}).")
            {
                ErrorCode = error;
            }

            public uint ErrorCode { get; }
        }

        private const string EdsdkLibrary = "EDSDK";

        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsInitializeSDK();
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsTerminateSDK();
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetCameraList(out IntPtr cameraList);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetChildCount(IntPtr reference, out int count);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetChildAtIndex(IntPtr reference, int index, out IntPtr child);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsOpenSession(IntPtr cameraRef);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsCloseSession(IntPtr cameraRef);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern int EdsRelease(IntPtr reference);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsSetObjectEventHandler(IntPtr cameraRef, uint eventCode, EdsObjectEventHandler handler, IntPtr context);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsSetPropertyData(IntPtr reference, uint propertyId, int parameter, int size, ref uint data);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsSetCapacity(IntPtr cameraRef, EdsCapacity capacity);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsSendCommand(IntPtr cameraRef, uint command, int parameter);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsCreateMemoryStream(ulong bufferSize, out IntPtr stream);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsCreateEvfImageRef(IntPtr stream, out IntPtr evfImage);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsDownloadEvfImage(IntPtr cameraRef, IntPtr evfImage);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetPointer(IntPtr stream, out IntPtr pointer);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetLength(IntPtr stream, out ulong length);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] private static extern uint EdsCreateFileStream(string fileName, int createDisposition, int access, out IntPtr stream);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetDirectoryItemInfo(IntPtr directoryItem, out EdsDirectoryItemInfo info);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsDownload(IntPtr directoryItem, ulong readSize, IntPtr stream);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsDownloadComplete(IntPtr directoryItem);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsDownloadCancel(IntPtr directoryItem);
        [DllImport(EdsdkLibrary, CallingConvention = CallingConvention.StdCall)] private static extern uint EdsGetEvent();
    }
}
