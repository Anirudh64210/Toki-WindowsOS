using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace Toki.Audio;

/// <summary>
/// Captures the audio rendered by a target process AND its child processes
/// (WASAPI Application Loopback) to a WAV — the "them" stream. This is what taps
/// a browser meeting: Meet's audio lives in a renderer child process, and
/// INCLUDE_TARGET_PROCESS_TREE captures the whole tree. No permission prompt.
///
/// Windows 10 build 19041+. Interop mirrors Microsoft's ApplicationLoopback sample.
/// </summary>
public sealed class ProcessLoopbackCapture
{
    // Fixed capture format: 44.1kHz 16-bit stereo PCM (widely playable).
    const int SampleRate = 44100;
    const int Channels = 2;
    const int BitsPerSample = 16;

    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const string VirtualDevicePath = "VAD\\Process_Loopback";
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public float PeakLevel { get; private set; }
    public long BytesWritten { get; private set; }

    /// <summary>The fixed capture format (44.1 kHz 16-bit stereo) — for wiring the live-transcription tap.</summary>
    public static WaveFormat CaptureFormat => new(SampleRate, BitsPerSample, Channels);

    /// <summary>Optional passive tap: raw capture bytes as they arrive, for live transcription.
    /// Observes the buffer already being written to WAV — must never affect capture.</summary>
    public Action<byte[], int>? OnBuffer;

    readonly ManualResetEventSlim _stopRequested = new(false);
    Thread? _thread;
    Exception? _threadError;

    /// <summary>
    /// Blocks for <paramref name="seconds"/> capturing the target process tree
    /// to <paramref name="wavPath"/> (used by --loopbacktest).
    /// </summary>
    public void Record(uint targetPid, string wavPath, int seconds)
    {
        Start(targetPid, wavPath);
        Thread.Sleep(seconds * 1000);
        Stop();
    }

    /// <summary>Begin open-ended capture; call <see cref="Stop"/> to finish.</summary>
    public void Start(uint targetPid, string wavPath)
    {
        _stopRequested.Reset();
        _thread = new Thread(() =>
        {
            try { CaptureLoop(targetPid, wavPath); }
            catch (Exception ex) { _threadError = ex; App.Log($"loopback capture error: {ex.Message}"); }
        });
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested.Set();
        // The loop re-checks the stop flag at least every 200ms, so a healthy thread exits
        // almost instantly — a long timeout only matters when a COM call wedged. If it DOES
        // wedge, say so loudly: them.wav is still open and its header unfinalized, and the
        // caller is about to transcribe it.
        var t = _thread;
        if (t is not null && !t.Join(TimeSpan.FromSeconds(10)))
            App.Log("loopback stop: capture thread did not exit in 10s — them.wav may be incomplete");
        _thread = null;
        if (_threadError != null) { var e = _threadError; _threadError = null; throw e; }
    }

    void CaptureLoop(uint targetPid, string wavPath)
    {
        // Activation params: process loopback, include the whole process tree.
        var loopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            TargetProcessId = targetPid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.INCLUDE_TARGET_PROCESS_TREE,
        };
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = loopbackParams,
        };

        var activationPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        var propVariant = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(activation, activationPtr, false);

            // PROPVARIANT wrapping the activation blob (VT_BLOB).
            propVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
            var pv = new PROPVARIANT
            {
                vt = (ushort)VT_BLOB,
                blobSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                blobData = activationPtr,
            };
            Marshal.StructureToPtr(pv, propVariant, false);

            var handler = new ActivationHandler();
            var hr = ActivateAudioInterfaceAsync(VirtualDevicePath, IID_IAudioClient, propVariant, handler, out _);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Completed.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete.");
            Marshal.ThrowExceptionForHR(handler.ActivateHr);
            var audioClient = handler.Client ?? throw new InvalidOperationException("No IAudioClient returned.");

            var format = new WaveFormat(SampleRate, BitsPerSample, Channels);
            var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatStruct>());
            try
            {
                Marshal.StructureToPtr(WaveFormatStruct.Pcm(SampleRate, BitsPerSample, Channels), formatPtr, false);

                // 200ms buffer, event-driven.
                hr = audioClient.Initialize(
                    AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                    2_000_000, 0, formatPtr, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(hr);

                using var sampleReady = new AutoResetEvent(false);
                Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()));

                var captureGuid = typeof(IAudioCaptureClient).GUID;
                Marshal.ThrowExceptionForHR(audioClient.GetService(captureGuid, out var captureObj));
                var captureClient = (IAudioCaptureClient)captureObj;

                using var writer = new WaveFileWriter(wavPath, format);

                Marshal.ThrowExceptionForHR(audioClient.Start());
                var frameBytes = Channels * (BitsPerSample / 8);
                var lastHeaderFlush = Environment.TickCount64;
                while (!_stopRequested.IsSet)
                {
                    sampleReady.WaitOne(200);
                    // Re-finalize the RIFF header every ~5s so a crash mid-meeting leaves a
                    // playable (truncated) file, not a zero-length header (see MicCapture).
                    var now = Environment.TickCount64;
                    if (now - lastHeaderFlush > 5000)
                    {
                        lastHeaderFlush = now;
                        try { writer.Flush(); } catch { /* best-effort */ }
                    }
                    while (true)
                    {
                        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out var packetFrames));
                        if (packetFrames == 0) break;

                        Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                            out var data, out var framesAvailable, out var flags, out _, out _));
                        var byteCount = (int)framesAvailable * frameBytes;
                        var buffer = new byte[byteCount];
                        // AUDCLNT_BUFFERFLAGS_SILENT = 0x2 → emit zeros, don't read the buffer.
                        if ((flags & 0x2) == 0 && data != IntPtr.Zero)
                        {
                            Marshal.Copy(data, buffer, 0, byteCount);
                            var p = MicCapture.PeakOf(buffer, byteCount, format);
                            if (p > PeakLevel) PeakLevel = p;
                        }
                        writer.Write(buffer, 0, byteCount);
                        BytesWritten += byteCount;
                        try { OnBuffer?.Invoke(buffer, byteCount); } catch { /* a tap must never break capture */ }
                        Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(framesAvailable));
                    }
                }

                Marshal.ThrowExceptionForHR(audioClient.Stop());
                Marshal.ReleaseComObject(captureClient);
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
                Marshal.ReleaseComObject(audioClient);
            }
        }
        finally
        {
            if (propVariant != IntPtr.Zero) Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(activationPtr);
        }
    }

    // ---- interop ----

    const uint VT_BLOB = 65;
    const int AUDCLNT_SHAREMODE_SHARED = 0;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    enum AUDIOCLIENT_ACTIVATION_TYPE { DEFAULT = 0, PROCESS_LOOPBACK = 1 }
    enum PROCESS_LOOPBACK_MODE { INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE_TARGET_PROCESS_TREE = 1 }

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint blobSize;
        public IntPtr blobData;
        public IntPtr pad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WaveFormatStruct
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;

        public static WaveFormatStruct Pcm(int rate, int bits, int channels)
        {
            var blockAlign = (ushort)(channels * (bits / 8));
            return new WaveFormatStruct
            {
                FormatTag = 1, // WAVE_FORMAT_PCM
                Channels = (ushort)channels,
                SamplesPerSec = (uint)rate,
                AvgBytesPerSec = (uint)(rate * blockAlign),
                BlockAlign = blockAlign,
                BitsPerSample = (ushort)bits,
                Size = 0,
            };
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesAvailable,
            out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
    }

    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEvent Completed = new(false);
        public int ActivateHr;
        public IAudioClient? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out ActivateHr, out var iface);
                if (ActivateHr == 0) Client = (IAudioClient)iface;
            }
            catch (Exception) { ActivateHr = Marshal.GetHRForException(new COMException()); }
            finally { Completed.Set(); }
        }
    }
}
