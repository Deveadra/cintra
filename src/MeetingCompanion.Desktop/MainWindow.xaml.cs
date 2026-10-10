using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;
using MeetingCompanion.Platform;
using MeetingCompanion.Session;

namespace MeetingCompanion.Desktop;

public partial class MainWindow : Window
{
    private readonly MeetingCoordinator coordinator;
    private readonly DispatcherTimer uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer sourceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly SourcePreferences preferences;
    private readonly Action<SourcePreferences> savePreferences;
    private Task? snapshotWork;
    private long snapshotGeneration;
    private bool closing;
    private bool controlBusy;
    private bool approved;
    private CallSourceKind discoveredKind;
    private long shownRevision = -1;
    private Guid? shownSession;
    private SessionState shownState = SessionState.Stopped;
    private ForegroundTracker? foreground;
    private SnapshotHotkey? hotkey;
    private HwndSource? hwndSource;
    private CancellationTokenSource? pending;
    private SnapshotResult? snapshot;
    private bool excluded;
    private bool closed;

    public MainWindow() : this(null, null, null) { }
    /// <summary>Inject the session owner/settings sink for offline WPF orchestration tests.</summary>
    public MainWindow(MeetingCoordinator? session, SourcePreferences? settings, Action<SourcePreferences>? persist)
    {
        InitializeComponent();
        preferences = settings ?? SourcePreferences.Load();
        savePreferences = persist ?? (value => value.Save());
        coordinator = session ?? new(new WindowsCallSourceInventory(), new MeetingRuntimeFactory(FindHelper(),
            () => approved, () => Environment.GetEnvironmentVariable("OPENAI_API_KEY")));
        AppKind.SelectedIndex = (int)preferences.Kind;
        LastSelection.Text = preferences.LastSource is null ? "No previous source selection." : "Last selected: " + preferences.LastSource;
        uiTimer.Tick += (_, _) => RenderSession();
        sourceTimer.Tick += async (_, _) => await coordinator.PollAsync(windowLifetime.Token);
        uiTimer.Start();
        sourceTimer.Start();
        Closing += Window_Closing;
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
            uiTimer.Stop();
            sourceTimer.Stop();
            windowLifetime.Cancel();
            windowLifetime.Dispose();
            pending?.Cancel();
            hotkey?.Dispose();
            foreground?.Dispose();
            hwndSource?.RemoveHook(WindowMessage);
            ClearImage();
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
    private void Clear_Click(object sender, RoutedEventArgs e) { pending?.Cancel(); snapshotGeneration++; ClearImage(); coordinator.ClearSnapshot(); CaptureStatus.Text = "Preview cleared."; }
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
        if (snapshotWork is not null || closing || closed) return;
        snapshotWork = CaptureManualAsync();
        try { await snapshotWork; }
        finally { snapshotWork = null; }
    }
    private async Task CaptureManualAsync()
    {
        if (pending != null || closed || !excluded) return;
        pending = new();
        CaptureButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ClearImage();
        coordinator.ClearSnapshot();
        var version = snapshotGeneration;
        var ticket = coordinator.BeginSnapshot();
        var capture = new SnapshotCapture(new WindowsSnapshotBackend(), ticket?.Clock ?? SnapshotCapture.NewClock());
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
            if (closed || closing || version != snapshotGeneration ||
                ticket is not null && !coordinator.AcceptSnapshot(ticket, snapshot.Metadata, snapshot.Png)) { ClearImage(); return; }
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

    private void ClearImage()
    {
        if (snapshot is not null) Array.Clear(snapshot.Png);
        snapshot = null;
        Preview.Source = null;
    }
    private static string FindHelper()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Cintra.Audio.Native.exe");
        if (File.Exists(deployed)) return deployed;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            if (File.Exists(Path.Combine(root.FullName, "MeetingCompanion.slnx")))
                return Path.Combine(root.FullName, "artifacts", "native", "Cintra.Audio.Native.exe");
            root = root.Parent;
        }
        return deployed;
    }
    private sealed record AppChoice(CallAudioCandidate Candidate)
    {
        public string Label => $"{Candidate.AppDisplayName} · PID {Candidate.ProcessId} · {(Candidate.RenderActive ? "render active (audio unverified)" : "idle / audio unverified")}";
    }
    private async void Discover_Click(object sender, RoutedEventArgs e) => await ControlAsync(async () =>
    {
        discoveredKind = (CallSourceKind)AppKind.SelectedIndex;
        var sources = await coordinator.DiscoverAsync(discoveredKind, windowLifetime.Token);
        AppPicker.ItemsSource = sources.Applications.Select(c => new AppChoice(c)).ToArray();
        AppPicker.SelectedIndex = -1;
        MicPicker.ItemsSource = sources.Microphones;
        MicPicker.SelectedItem = sources.Microphones.FirstOrDefault(m => m.Id == preferences.MicrophoneId);
        SelectionStatus.Text = sources.Applications.Length == 0 ? "No matching processes. Open the call app or select Manual, then refresh."
            : "Choose a process explicitly. " + string.Join("; ", sources.Diagnostics);
    });
    private async void Start_Click(object sender, RoutedEventArgs e) => await ControlAsync(async () =>
    {
        if (AppKind.SelectedIndex != (int)discoveredKind || AppPicker.SelectedItem is not AppChoice app || MicPicker.SelectedItem is not AudioEndpoint mic)
            throw new InvalidOperationException("Refresh, then choose an incoming app process and a microphone.");
        var mode = (ProviderMode)ProviderPicker.SelectedIndex;
        approved = PaidApproval.IsChecked == true;
        if (mode == ProviderMode.OpenAi && !approved) throw new InvalidOperationException("Enable the explicit paid API opt-in before Start.");
        ClearImage();
        snapshotGeneration++;
        preferences.Kind = discoveredKind;
        preferences.MicrophoneId = mic.Id;
        preferences.LastSource = app.Label;
        savePreferences(preferences);
        SelectionStatus.Text = app.Candidate.SelectionHint;
        await coordinator.StartAsync(new(discoveredKind, app.Candidate, mic), mode, windowLifetime.Token);
    });
    private async void Pause_Click(object sender, RoutedEventArgs e) => await ControlAsync(() => coordinator.PauseAsync(windowLifetime.Token));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await ControlAsync(() => coordinator.ResumeAsync(windowLifetime.Token));
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        pending?.Cancel();
        snapshotGeneration++;
        ClearImage();
        try { await coordinator.StopAsync(); }
        catch { SessionStatus.Text = coordinator.View.Detail; }
        RenderSession();
    }
    private async Task ControlAsync(Func<Task> action)
    {
        if (controlBusy || closing) return;
        controlBusy = true;
        RenderSession();
        try { await action(); }
        catch (OperationCanceledException) { SessionStatus.Text = "Operation cancelled; resources are stopping."; }
        catch (CallSourceSelectionException error) { SelectionStatus.Text = error.DiagnosticCode + " — refresh and select again."; AppPicker.SelectedIndex = -1; }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
        { SelectionStatus.Text = error is FileNotFoundException ? "Native helper missing — run scripts/native-build.ps1 before Start." : error.Message; }
        catch { SelectionStatus.Text = "Operation failed. Stop, refresh sources and retry."; }
        finally { controlBusy = false; RenderSession(); }
    }
    private void RenderSession()
    {
        if (closed) return;
        var view = coordinator.View;
        bool idle = view.SessionId is null && view.State is SessionState.Stopped or SessionState.Error;
        AppKind.IsEnabled = AppPicker.IsEnabled = MicPicker.IsEnabled = ProviderPicker.IsEnabled = PaidApproval.IsEnabled = idle && !controlBusy;
        DiscoverButton.IsEnabled = StartButton.IsEnabled = idle && !controlBusy;
        PauseButton.IsEnabled = !controlBusy && view.State is SessionState.Listening or SessionState.Degraded;
        ResumeButton.IsEnabled = !controlBusy && view.State == SessionState.Paused;
        StopButton.IsEnabled = controlBusy || view.State != SessionState.Stopped;
        if (shownRevision == view.Revision) return;
        if (shownSession != view.SessionId || shownState != view.State && view.State is SessionState.Stopping or SessionState.Stopped or SessionState.Error or SessionState.Paused)
        {
            pending?.Cancel();
            snapshotGeneration++;
            ClearImage();
            if (view.State == SessionState.Error) AppPicker.SelectedIndex = -1;
        }
        shownSession = view.SessionId;
        shownState = view.State;
        shownRevision = view.Revision;
        SessionStatus.Text = $"{(view.Provider == ProviderMode.OfflineTest ? "TEST MODE · " : "")}{view.State} — {view.Detail}";
        LastSelection.Text = "Last selected: " + (view.LastSource ?? preferences.LastSource ?? "none");
        string Status(Component c) => view.Health.FirstOrDefault(h => h.Component == c) is { } h ? $"{h.Status} ({h.DiagnosticCode ?? "ok"})" : "unknown";
        LocalHealth.Text = $"YOU capture: {Status(Component.LocalCapture)} · STT: {Status(Component.LocalStt)}";
        RemoteHealth.Text = $"REMOTE capture: {Status(Component.RemoteCapture)} · STT: {Status(Component.RemoteStt)}";
        LocalLevel.Value = view.LocalPeak;
        RemoteLevel.Value = view.RemotePeak;
        Transcript.Text = view.Transcripts.Length == 0 ? "No transcript. Capture only requires no key. TEST MODE yields scripted text after a VAD turn; OpenAI requires a key and paid opt-in."
            : string.Join(Environment.NewLine, view.Transcripts.TakeLast(100).Select(t => $"{t.CapturedStartMs} ms · {(t.StreamId == AudioStreamId.LocalMic ? "YOU" : "REMOTE")} · {(t is TranscriptFinal ? "final" : "partial")}: {t.Text}"));
        PipelineEvents.Text = string.Join(Environment.NewLine, view.Gaps.TakeLast(4).Select(g => $"Gap: {g.Gap.StreamId} · {g.Gap.Reason} · {g.Gap.CapturedStartMs}–{g.Gap.CapturedEndMs} ms")
            .Concat(view.SourceChanges.TakeLast(2).Select(s => $"Source changed: {s.Change.Source.DisplayName} at {s.Change.EffectiveAtMs} ms")));
    }
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        pending?.Cancel();
        snapshotGeneration++;
        sourceTimer.Stop();
        try
        {
            if (snapshotWork is not null) await snapshotWork.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.DisposeAsync();
            closed = true;
            Close();
        }
        catch { closing = false; SessionStatus.Text = "Closure blocked — cleanup failed or timed out. Use Stop to retry; no successful shutdown reported."; }
    }
}
