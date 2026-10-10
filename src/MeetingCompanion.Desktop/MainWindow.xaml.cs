using System.Windows;
using System.Windows.Interop;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Desktop;

public partial class MainWindow : Window
{
    private readonly SnapshotCapture capture = new(new WindowsSnapshotBackend(), SnapshotCapture.NewClock());
    private ForegroundTracker? foreground;
    private SnapshotHotkey? hotkey;
    private HwndSource? hwndSource;
    private CancellationTokenSource? pending;
    private SnapshotResult? snapshot;
    private bool excluded;
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        try { foreground = new(); }
        catch (SnapshotFailure e) { CaptureStatus.Text = e.Message; }
        RefreshSources();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            excluded = WindowsSources.Exclude(hwnd);
            if (!excluded) CaptureStatus.Text = "Overlay exclusion failed. Snapshot controls are disabled; restart and retry.";
            CaptureButton.IsEnabled = excluded;
            hwndSource = HwndSource.FromHwnd(hwnd);
            hwndSource.AddHook(WindowMessage);
            hotkey = new(new WindowsHotkeyApi(), hwnd);
            ApplyHotkey();
        };
        Closed += (_, _) =>
        {
            closed = true;
            pending?.Cancel();
            hotkey?.Dispose();
            foreground?.Dispose();
            hwndSource?.RemoveHook(WindowMessage);
            snapshot = null;
            Preview.Source = null;
        };
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (hotkey?.IsMessage(message, wParam) == true)
        {
            handled = true;
            _ = TakeSnapshotAsync();
        }
        return 0;
    }
    private void RefreshSources()
    {
        WindowPicker.ItemsSource = WindowsSources.Windows();
        MonitorPicker.ItemsSource = WindowsSources.Monitors();
        WindowPicker.SelectedIndex = 0;
        MonitorPicker.SelectedIndex = 0;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();
    private async void Snapshot_Click(object sender, RoutedEventArgs e) => await TakeSnapshotAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => pending?.Cancel();
    private void Clear_Click(object sender, RoutedEventArgs e) { snapshot = null; Preview.Source = null; CaptureStatus.Text = "Preview cleared."; }
    private void Hotkey_Click(object sender, RoutedEventArgs e) => ApplyHotkey();
    private void ApplyHotkey()
    {
        string key = HotkeyKey.Text.ToUpperInvariant();
        if (key.Length != 1 || key[0] is < 'A' or > 'Z') { HotkeyStatus.Text = "Enter a letter A–Z."; return; }
        HotkeyStatus.Text = hotkey?.Rebind(6, key[0]) == true
            ? $"Registered: Ctrl+Shift+{key}. Captures the currently chosen source."
            : "Hotkey unavailable or already registered by another app. Choose another letter; Snapshot button remains available.";
    }
    private async Task TakeSnapshotAsync()
    {
        if (pending != null || closed || !excluded) return;
        pending = new();
        CaptureButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        snapshot = null;
        Preview.Source = null;
        CaptureStatus.Text = "Capturing…";
        try
        {
            SnapshotTarget target;
            if (SourceMode.SelectedIndex is 0 or 1)
            {
                nint current = WindowsSources.GetForegroundWindow();
                nint hwnd = SourceMode.SelectedIndex == 0
                    ? WindowsSources.IsExternal(current) ? current : foreground?.LastWindow ?? 0
                    : (WindowPicker.SelectedItem as WindowSource)?.Handle ?? 0;
                if (!WindowsSources.IsExternal(hwnd) || WindowsSources.IsIconic(hwnd))
                    throw new SnapshotFailure("invalid_window", "No available external window. Activate a window or choose one, then retry.");
                target = new(SourceMode.SelectedIndex == 0 ? SnapshotSource.ForegroundWindow : SnapshotSource.SelectedWindow, hwnd, WindowsSources.Title(hwnd));
            }
            else
            {
                if (MonitorPicker.SelectedItem is not MonitorSource monitor) throw new SnapshotFailure("missing_monitor", "Select a monitor.");
                PixelRegion? region = null;
                if (SourceMode.SelectedIndex == 2)
                {
                    CaptureStatus.Text = "Select a region. Esc cancels.";
                    var selector = new RegionSelector(monitor) { Owner = this };
                    using var registration = pending.Token.Register(() => Dispatcher.BeginInvoke(() => { if (selector.IsVisible) selector.Close(); }));
                    selector.ShowDialog();
                    if (selector.Failure != null) throw selector.Failure;
                    region = selector.Selection;
                    if (region == null) throw new OperationCanceledException();
                    WindowsSources.DwmFlush();
                }
                if (!WindowsSources.Monitors().Any(x => x.Handle == monitor.Handle && x.Bounds == monitor.Bounds))
                    throw new SnapshotFailure("monitor_changed", "The display layout changed. Refresh sources and retry.");
                target = new(region == null ? SnapshotSource.Display : SnapshotSource.Region, monitor.Handle, monitor.Label, region);
            }
            snapshot = await capture.CaptureAsync(target, pending.Token);
            if (closed) { snapshot = null; return; }
            Preview.Source = snapshot.Preview;
            CaptureStatus.Text = $"Captured {snapshot.Metadata.Width}×{snapshot.Metadata.Height} · {snapshot.Metadata.SourceLabel} · {snapshot.Metadata.CapturedAtMs} ms. No upload.";
        }
        catch (OperationCanceledException) { if (!closed) CaptureStatus.Text = "Capture cancelled. No image retained."; }
        catch (SnapshotFailure e) { if (!closed) CaptureStatus.Text = $"{e.Code}: {e.Message}"; }
        catch (Exception) { if (!closed) CaptureStatus.Text = "Capture failed. Choose another source and retry."; }
        finally
        {
            pending.Dispose();
            pending = null;
            if (!closed) { CaptureButton.IsEnabled = excluded; CancelButton.IsEnabled = false; }
        }
    }
}
