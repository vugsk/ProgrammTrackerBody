using System;
using System.Threading.Tasks;
using System.Windows;
using ProgrammTrackerBody.Networking;
using ProgrammTrackerBody.Persistence;
using ProgrammTrackerBody.Services;
using ProgrammTrackerBody.ViewModels;
using ProgrammTrackerBody.Views;

namespace ProgrammTrackerBody;

public partial class App : Application
{
    private UdpTrackerServer? _server;
    private TrackerManager? _trackerManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configStore = new ConfigStore();
        _server = new UdpTrackerServer();
        _trackerManager = new TrackerManager(_server, configStore, Dispatcher);

        await _trackerManager.InitializeAsync();

        LocalizationService.Instance.SetLanguage(_trackerManager.Config.Language);

        var mainViewModel = new MainViewModel(_server, _trackerManager, LocalizationService.Instance, Dispatcher);
        var mainWindow = new MainWindow
        {
            DataContext = mainViewModel,
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            (MainWindow?.DataContext as IDisposable)?.Dispose();

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }

            if (_trackerManager is not null)
            {
                await _trackerManager.DisposeAsync();
            }
        }
        catch
        {
            // Best-effort cleanup on exit.
        }

        base.OnExit(e);
    }
}
