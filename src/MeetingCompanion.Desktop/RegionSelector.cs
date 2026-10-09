using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MeetingCompanion.Capture;
using MeetingCompanion.Contracts;

namespace MeetingCompanion.Desktop;

internal sealed class RegionSelector : Window
{
    private readonly Canvas canvas = new() { Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) };
    private readonly Rectangle rectangle = new() { Stroke = Brushes.Lime, StrokeThickness = 2 };
    private Point? start;
    public PixelRegion? Selection { get; private set; }
    public SnapshotFailure? Failure { get; private set; }
    public RegionSelector(MonitorSource monitor)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Content = canvas;
        canvas.Children.Add(new TextBlock { Text = "Drag a region · Esc cancels", Foreground = Brushes.White, Background = Brushes.Black, FontSize = 20 });
        canvas.Children.Add(rectangle);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!WindowsSources.Exclude(hwnd))
                Failure = new("overlay_exclusion", "Windows could not exclude the region selector. Capture cancelled; restart and retry.");
            else if (!WindowsSources.SetWindowPos(hwnd, -1, monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height, 0x40))
                Failure = new("region_position", "Windows could not position the region selector. Refresh sources and retry.");
            if (Failure != null) Dispatcher.BeginInvoke(Close);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); canvas.CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (start is not Point origin) return;
            var end = e.GetPosition(canvas);
            Canvas.SetLeft(rectangle, Math.Min(origin.X, end.X));
            Canvas.SetTop(rectangle, Math.Min(origin.Y, end.Y));
            rectangle.Width = Math.Abs(end.X - origin.X);
            rectangle.Height = Math.Abs(end.Y - origin.Y);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (start is not Point origin) return;
            var a = canvas.PointToScreen(origin);
            var b = canvas.PointToScreen(e.GetPosition(canvas));
            Selection = RegionGeometry.FromScreenPoints(monitor.Bounds, a.X, a.Y, b.X, b.Y);
            canvas.ReleaseMouseCapture();
            Close();
        };
    }
}
