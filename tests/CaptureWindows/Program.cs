using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;
using MeetingCompanion.Desktop;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowsSources.Rect rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine($"PASS {name}"); }
    [STAThread]
    public static int Main(string[] args)
    {
        if (!args.Contains("--smoke") && !args.Contains("--fixture")) { Console.WriteLine("Opt in with --smoke on an interactive Windows desktop. Displays synthetic windows, moves cursor, retains no screenshots."); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--fixture"))
        {
            var fixture = new Window { Title = "MC-008 synthetic red fixture", Background = Brushes.Red, Left = 80, Top = 80, Width = 500, Height = 400, Topmost = true };
            fixture.Show();
            Console.WriteLine(new WindowInteropHelper(fixture).Handle);
            Console.Out.Flush();
            return app.Run();
        }
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            Process? fixture = null;
            MainWindow? shell = null;
            try
            {
                fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                nint target = nint.Parse((await fixture!.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
                SetForegroundWindow(target);
                GetWindowRect(target, out var initialRect);
                SetCursorPos(initialRect.Left + 100, initialRect.Top + 100);
                mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
                await Task.Delay(250);
                Check(WindowsSources.GetForegroundWindow() == target, "fixture has actual foreground focus");
                shell = new MainWindow { Left = 650, Top = 80 };
                shell.Show();
                await Task.Delay(300);
                var hwnd = new WindowInteropHelper(shell).Handle;
                SetForegroundWindow(hwnd);
                await Task.Delay(100);
                Check(WindowsSources.GetForegroundWindow() == hwnd, "companion has focus before snapshot");
                Console.WriteLine($"OS={Environment.OSVersion.Version} monitors={WindowsSources.Monitors().Count} DPI={GetDpiForWindow(hwnd)}");
                var button = (Button)shell.FindName("CaptureButton");
                var image = (Image)shell.FindName("Preview");
                var status = (TextBlock)shell.FindName("CaptureStatus");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => button.IsEnabled);
                Check(image.Source != null, "one-click last external foreground window: " + status.Text);
                Check(Red((System.Windows.Media.Imaging.BitmapSource)image.Source!), "foreground pixel comparison");
                var mode = (ComboBox)shell.FindName("SourceMode");
                var picker = (ComboBox)shell.FindName("WindowPicker");
                picker.SelectedItem = picker.Items.Cast<WindowSource>().Single(x => x.Handle == target);
                mode.SelectedIndex = 1;
                ((TextBox)shell.FindName("HotkeyKey")).Text = "Z";
                // Exercise native registration, keyboard routing, and shell dispatch.
                ((Button)shell.FindName("HotkeyApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((TextBlock)shell.FindName("HotkeyStatus")).Text.StartsWith("Registered:"), "native global hotkey registration");
                using (var collision = new SnapshotHotkey(new WindowsHotkeyApi(), 0))
                    Check(!collision.Rebind(6, 'Z'), "native hotkey collision");
                var previousImage = image.Source;
                try { keybd_event(0x11, 0, 0, 0); keybd_event(0x10, 0, 0, 0); keybd_event((byte)'Z', 0, 0, 0); }
                finally { keybd_event((byte)'Z', 0, 2, 0); keybd_event(0x10, 0, 2, 0); keybd_event(0x11, 0, 2, 0); }
                await Until(() => image.Source != previousImage && image.Source != null && button.IsEnabled);
                Check(Red((System.Windows.Media.Imaging.BitmapSource)image.Source!), "injected Ctrl+Shift+Z reaches selected-window capture");
                mode.SelectedIndex = 3;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => button.IsEnabled);
                Check(image.Source != null, "one-click selected monitor capture");
                var capture = new SnapshotCapture(new WindowsSnapshotBackend(), SnapshotCapture.NewClock());
                var selected = new SnapshotTarget(SnapshotSource.SelectedWindow, target, "Synthetic fixture");
                var monitor = WindowsSources.Monitors()[0];
                var display = await capture.CaptureAsync(new(SnapshotSource.Display, monitor.Handle, monitor.Label), default);
                Check(display.Metadata.Width == monitor.Bounds.Width && display.Metadata.Height == monitor.Bounds.Height, "actual monitor dimensions");
                GetWindowRect(target, out var rect);
                var crop = new PixelRegion(rect.Left - monitor.Bounds.X + 80, rect.Top - monitor.Bounds.Y + 80, 100, 100);
                var region = await capture.CaptureAsync(new(SnapshotSource.Region, monitor.Handle, "Synthetic region", crop), default);
                Check(region.Metadata.Width == 100 && Red(region.Preview), "actual monitor crop pixel comparison");
                var overlay = new Window { Background = Brushes.Lime, WindowStyle = WindowStyle.None, Left = rect.Left + 80, Top = rect.Top + 80, Width = 100, Height = 100, Topmost = true };
                overlay.Show();
                WindowsSources.SetWindowPos(new WindowInteropHelper(overlay).Handle, -1, rect.Left + 80, rect.Top + 80, 100, 100, 0x40);
                await Task.Delay(250);
                var control = await capture.CaptureAsync(new(SnapshotSource.Region, monitor.Handle, "Visible overlay fixture", crop), default);
                Check(Green(control.Preview), "green overlay visible before exclusion (positive control)");
                Check(WindowsSources.Exclude(new WindowInteropHelper(overlay).Handle), "overlay exclusion configured and read back");
                await Task.Delay(250);
                var excluded = await capture.CaptureAsync(new(SnapshotSource.Region, monitor.Handle, "Exclusion fixture", crop), default);
                Check(Red(excluded.Preview), "overlay exclusion verified with red pixel beneath green overlay");
                overlay.Close();
                // Use real selector and physical mouse coordinates on the selected monitor.
                mode.SelectedIndex = 2;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) => { timer.Stop(); SetCursorPos(rect.Left + 80, rect.Top + 80); mouse_event(2, 0, 0, 0, 0); SetCursorPos(rect.Left + 180, rect.Top + 180); mouse_event(4, 0, 0, 0, 0); };
                timer.Start();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => button.IsEnabled);
                Check(image.Source != null && image.Source.Width == 100 && image.Source.Height == 100 && Red((System.Windows.Media.Imaging.BitmapSource)image.Source), "region drag UI produces physical 100x100 red crop: " + status.Text);
                var cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                cancelTimer.Tick += (_, _) => { cancelTimer.Stop(); keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0); };
                cancelTimer.Start();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => button.IsEnabled);
                Check(image.Source == null && status.Text.StartsWith("Capture cancelled"), "Esc cancels region UI and clears stale preview");
                WindowsSources.SetWindowPos(target, 0, -50, 100, 450, 350, 0x14);
                await Task.Delay(150);
                var moved = await capture.CaptureAsync(selected, default);
                Check(Red(moved.Preview), "moved/resized window with negative desktop X");
                WindowsSources.SetWindowPos(target, 0, 80, 80, 500, 400, 0x14);
                ShowWindow(target, 6);
                await Expect("minimized window", () => capture.CaptureAsync(selected, default));
                ShowWindow(target, 9);
                await Task.Delay(150);
                for (int i = 0; i < 5; i++) await capture.CaptureAsync(selected, default);
                Check(true, "five repeat captures after failure");
                using (var cancelling = new CancellationTokenSource(20))
                {
                    try { await capture.CaptureAsync(selected, cancelling.Token); throw new Exception("in-flight cancel failed"); }
                    catch (OperationCanceledException) { Check(true, "in-flight native capture cancellation"); }
                }
                await capture.CaptureAsync(selected, default);
                Check(true, "capture recovery after cancellation");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try { await capture.CaptureAsync(selected, cancelled.Token); throw new Exception("cancel failed"); }
                    catch (OperationCanceledException) { Check(true, "pre-cancelled capture"); }
                }
                var protectedWindow = new Window { Background = Brushes.Red, Width = 200, Height = 200 };
                protectedWindow.Show();
                nint protectedHandle = new WindowInteropHelper(protectedWindow).Handle;
                Check(SetWindowDisplayAffinity(protectedHandle, 1), "synthetic protected window affinity");
                await Expect("protected window access denied", () => capture.CaptureAsync(new(SnapshotSource.SelectedWindow, protectedHandle, "Protected fixture"), default), "access_denied");
                protectedWindow.Close();
                fixture.Kill(); fixture.WaitForExit();
                await Expect("destroyed HWND", () => capture.CaptureAsync(selected, default));
                shell.Close(); shell = null;
                Check(new WindowsHotkeyApi().Register(0, 0x4C08, 6, 'Z'), "hotkey released on shell exit");
                new WindowsHotkeyApi().Unregister(0, 0x4C08);
                Console.WriteLine($"WINDOWS_SMOKE_PASS checks={checks}; no image files written; multi-monitor/mixed-DPI/HDR/RDP/DRM NOT VERIFIED");
            }
            catch (Exception e) { Console.WriteLine("WINDOWS_SMOKE_FAIL " + e); exit = 1; }
            finally { shell?.Close(); if (fixture is { HasExited: false }) { fixture.Kill(); fixture.WaitForExit(); } fixture?.Dispose(); app.Shutdown(); }
        };
        app.Run();
        return exit;
    }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready()) { if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("UI capture did not complete"); await Task.Delay(25); }
    }
    private static bool Red(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[2] > 240 && pixel[0] < 15 && pixel[1] < 15;
    }
    private static bool Green(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[1] > 240 && pixel[0] < 15 && pixel[2] < 15;
    }
    private static async Task Expect(string name, Func<Task<SnapshotResult>> action, string? code = null)
    {
        try { await action(); throw new Exception("Expected capture failure: " + name); }
        catch (SnapshotFailure e) { Check(code == null || e.Code == code, name + " (" + e.Code + ")"); }
    }
}
