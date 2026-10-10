using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeetingCompanion.Contracts;
using MeetingCompanion.Desktop;

namespace MeetingCompanion.Session.Tests;

public sealed class DesktopTests
{
    [Fact]
    public Task RealWpfHandlersRenderTypedTranscriptsControlLifecycleAndCloseAfterCleanup() => OnDispatcher(async () =>
    {
        var fixture = new Fixture();
        var coordinator = fixture.Create();
        var window = new MainWindow(coordinator, new(), _ => { });
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            Click("DiscoverButton");
            await CoordinatorTests.Until(() => Control<ComboBox>("AppPicker").Items.Count == 1);
            Assert.Equal(-1, Control<ComboBox>("AppPicker").SelectedIndex);
            Control<ComboBox>("AppPicker").SelectedIndex = 0;
            Control<ComboBox>("MicPicker").SelectedIndex = 0;
            Control<ComboBox>("ProviderPicker").SelectedIndex = 1;
            Click("StartButton");
            await CoordinatorTests.Until(() => Control<TextBlock>("SessionStatus").Text.Contains("Listening"));
            Assert.Contains("TEST MODE", Control<TextBlock>("SessionStatus").Text);
            Assert.True(Control<Button>("PauseButton").IsEnabled);
            Assert.False(Control<ComboBox>("AppPicker").IsEnabled);
            Assert.True(Control<Button>("StopButton").IsEnabled);
            fixture.Factory.Latest.Emit(CoordinatorTests.Transcript(coordinator.View.SessionId!.Value, Guid.NewGuid(), AudioStreamId.RemoteApp, true, "question"));
            await CoordinatorTests.Until(() => Control<TextBox>("Transcript").Text.Contains("REMOTE · final: question"));
            Click("PauseButton");
            await CoordinatorTests.Until(() => Control<Button>("ResumeButton").IsEnabled);
            Assert.Contains("Paused", Control<TextBlock>("SessionStatus").Text);
            Click("ResumeButton");
            await CoordinatorTests.Until(() => fixture.Factory.Created.Count == 2 && Control<Button>("PauseButton").IsEnabled);
            Click("StopButton");
            await CoordinatorTests.Until(() => Control<Button>("StartButton").IsEnabled);
            Assert.Contains("No transcript", Control<TextBox>("Transcript").Text);
            Click("StartButton");
            await CoordinatorTests.Until(() => coordinator.View.State == SessionState.Listening);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SessionState.Stopped, coordinator.View.State);
            Assert.All(fixture.Inventory.Leases, lease => Assert.Equal(1, lease.Disposals));
            Assert.All(fixture.Factory.Created, runtime => Assert.Equal(1, runtime.Disposals));
        }
        finally
        {
            await coordinator.StopAsync();
            if (!closed.Task.IsCompleted) { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    });

    private static Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = Run();
            Dispatcher.Run();
            async Task Run()
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
