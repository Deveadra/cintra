using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Capture;

public sealed record SnapshotTarget(SnapshotSource Source, nint Handle, string Label, PixelRegion? Crop = null);
/// <summary>Ownership of Bgra transfers to SnapshotCapture, which clears it after processing.</summary>
public sealed record CapturedPixels(byte[] Bgra, int Width, int Height, long QpcTicks);
public sealed record SnapshotResult(SnapshotMetadata Metadata, byte[] Png, BitmapSource Preview);
public interface ISnapshotBackend
{
    Task<CapturedPixels> CaptureAsync(SnapshotTarget target, CancellationToken cancellationToken);
}

public sealed class SnapshotFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Called only by a manual button/hotkey. Keeps no image history and performs no file/network I/O.</summary>
public sealed class SnapshotCapture(ISnapshotBackend backend, ClockMapping clock)
{
    private int busy;

    public async Task<SnapshotResult> CaptureAsync(SnapshotTarget target, CancellationToken cancellationToken)
    {
        clock.Validate();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new SnapshotFailure("busy", "A snapshot is already in progress.");
        CapturedPixels? pixels = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(target.Source) || target.Handle == 0 || string.IsNullOrWhiteSpace(target.Label))
                throw new SnapshotFailure("invalid_source", "Choose a valid window or monitor.");
            if (target.Source == SnapshotSource.Region && target.Crop is null)
                throw new SnapshotFailure("invalid_region", "Select a rectangular region.");
            pixels = await backend.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (pixels.Width is <= 0 or > 16384 || pixels.Height is <= 0 or > 16384 ||
                (long)pixels.Width * pixels.Height > 33_554_432 || pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4)
                throw new SnapshotFailure("invalid_frame", "The capture returned invalid dimensions.");
            var crop = target.Crop ?? new PixelRegion(0, 0, pixels.Width, pixels.Height);
            if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
                (long)crop.X + crop.Width > pixels.Width || (long)crop.Y + crop.Height > pixels.Height)
                throw new SnapshotFailure("invalid_region", "The source moved or resized. Select the region again.");
            var cropped = new byte[checked(crop.Width * crop.Height * 4)];
            for (int row = 0; row < crop.Height; row++)
                Buffer.BlockCopy(pixels.Bgra, ((crop.Y + row) * pixels.Width + crop.X) * 4, cropped, row * crop.Width * 4, crop.Width * 4);
            bool visible = false;
            for (int i = 0; i < cropped.Length; i += 4)
                if (cropped[i] > 3 || cropped[i + 1] > 3 || cropped[i + 2] > 3) { visible = true; break; }
            if (!visible) throw new SnapshotFailure("blank_frame", "The image is blank or protected. Choose another source and retry.");
            var image = BitmapSource.Create(crop.Width, crop.Height, 96, 96, PixelFormats.Bgra32, null, cropped, crop.Width * 4);
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var capturedMs = (long)((pixels.QpcTicks - clock.MonotonicOriginTicks) * 1000.0 / clock.TicksPerSecond);
            var metadata = new SnapshotMetadata(Guid.NewGuid(), Math.Max(0, capturedMs), target.Source,
                target.Label[..Math.Min(target.Label.Length, 256)], target.Crop, crop.Width, crop.Height);
            metadata.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return new(metadata, stream.ToArray(), image);
        }
        finally { if (pixels != null) Array.Clear(pixels.Bgra); Volatile.Write(ref busy, 0); }
    }

    public static ClockMapping NewClock() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
}

public sealed class WindowsSnapshotBackend : ISnapshotBackend
{
    [DllImport("Cintra.Snapshot.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SnapshotCapture(nint target, int monitor, nint cancelled, out nint pixels, out int width, out int height, out long qpc);

    public Task<CapturedPixels> CaptureAsync(SnapshotTarget target, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var cancelled = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var registration = cancellationToken.Register(() => cancelled.Set());
        nint buffer = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int hr = SnapshotCapture(target.Handle, target.Source is SnapshotSource.Display or SnapshotSource.Region ? 1 : 0,
                cancelled.SafeWaitHandle.DangerousGetHandle(), out buffer, out int width, out int height, out long qpc);
            cancellationToken.ThrowIfCancellationRequested();
            if (hr < 0)
            {
                var (code, message) = hr switch
                {
                    unchecked((int)0x80070005) => ("access_denied", "Windows denied capture or the window is protected."),
                    unchecked((int)0x80070102) => ("timeout", "No frame arrived within three seconds. Retry or choose another source."),
                    unchecked((int)0x80070032) => ("unsupported_hdr", "HDR capture is unsupported. Choose an SDR source."),
                    unchecked((int)0x80070057) => ("invalid_window", "The window is minimized, closed, or has invalid dimensions."),
                    unchecked((int)0x80070578) => ("closed_window", "The source window closed during capture."),
                    unchecked((int)0x800704D5) => ("resized", "The source resized during capture. Retry."),
                    _ => ("capture_failed", $"Windows capture failed (0x{hr:X8}). Choose another source or retry.")
                };
                throw new SnapshotFailure(code, message);
            }
            if (buffer == 0 || width is <= 0 or > 16384 || height is <= 0 or > 16384 || (long)width * height > 33_554_432)
                throw new SnapshotFailure("invalid_frame", "The Windows capture returned invalid dimensions.");
            int bytes = checked(width * height * 4);
            var pixels = new byte[bytes];
            Marshal.Copy(buffer, pixels, 0, bytes);
            return new CapturedPixels(pixels, width, height, qpc);
        }
        catch (DllNotFoundException) { throw new SnapshotFailure("missing_native", "The Windows capture DLL is missing. Rebuild the desktop application."); }
        finally { if (buffer != 0) Marshal.FreeCoTaskMem(buffer); }
    }, cancellationToken);
}
