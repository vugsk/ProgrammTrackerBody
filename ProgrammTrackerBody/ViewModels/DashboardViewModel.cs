using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ProgrammTrackerBody.Networking;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly UdpTrackerServer _server;
    private readonly TrackerManager _trackerManager;
    private readonly LocalizationService _localization;
    private readonly LogsViewModel _logs;

    private string _portText;
    private TrackerSessionState _sessionState = TrackerSessionState.Stopped;
    private int _serverPort;
    private bool _isRunning;
    private bool _disposed;

    public DashboardViewModel(
        UdpTrackerServer server,
        TrackerManager trackerManager,
        LocalizationService localization,
        LogsViewModel logs)
    {
        _server = server;
        _trackerManager = trackerManager;
        _localization = localization;
        _logs = logs;
        _portText = trackerManager.Config.LastPort.ToString(CultureInfo.InvariantCulture);
        _serverPort = trackerManager.Config.LastPort;

        StartStopCommand = new RelayCommand(_ => _ = StartStopAsync());
        BroadcastCommand = new RelayCommand(_ => _ = BroadcastAsync(), _ => IsRunning);
        SetLanguageCommand = new RelayCommand(p => SetLanguage(p as string));

        _server.SessionStateChanged += OnSessionStateChanged;
        _trackerManager.PropertyChanged += OnTrackerManagerPropertyChanged;
        _localization.PropertyChanged += OnLocalizationPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _server.SessionStateChanged -= OnSessionStateChanged;
        _trackerManager.PropertyChanged -= OnTrackerManagerPropertyChanged;
        _localization.PropertyChanged -= OnLocalizationPropertyChanged;
    }

    private void OnSessionStateChanged(TrackerSessionState state)
    {
        SessionState = state;
        IsRunning = _server.IsRunning;
    }

    private void OnTrackerManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerManager.ConnectedCount))
        {
            OnPropertyChanged(nameof(ConnectedCount));
        }
    }

    private void OnLocalizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-fire so {Binding ..., Converter=ResourceLookupConverter} re-evaluates.
        OnPropertyChanged(nameof(SessionStateKey));
        OnPropertyChanged(nameof(StartStopButtonKey));
        OnPropertyChanged(nameof(CurrentLanguage));
    }

    public string PortText
    {
        get => _portText;
        set => SetField(ref _portText, value);
    }

    public TrackerSessionState SessionState
    {
        get => _sessionState;
        private set
        {
            if (SetField(ref _sessionState, value))
            {
                OnPropertyChanged(nameof(SessionStateKey));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StartStopButtonKey));
            }
        }
    }

    // Resource keys to drive {DynamicResource} bindings.
    public string SessionStateKey => SessionState switch
    {
        TrackerSessionState.Stopped => "State.Stopped",
        TrackerSessionState.Discovery => "State.Discovery",
        TrackerSessionState.SessionStart => "State.SessionStart",
        TrackerSessionState.Streaming => "State.Streaming",
        _ => "State.Stopped",
    };

    public string StartStopButtonKey => IsRunning ? "Dashboard.Stop" : "Dashboard.Start";

    public int ConnectedCount => _trackerManager.ConnectedCount;

    public string CurrentLanguage => _localization.CurrentLanguage;

    public ICommand StartStopCommand { get; }

    public ICommand BroadcastCommand { get; }

    public ICommand SetLanguageCommand { get; }

    private async System.Threading.Tasks.Task StartStopAsync()
    {
        if (_server.IsRunning)
        {
            try
            {
                await _server.StopAsync();
            }
            catch (Exception ex)
            {
                _logs.AppendSystem($"Stop error: {ex.Message}");
            }

            IsRunning = false;
            return;
        }

        if (!int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0 || port > 65535)
        {
            _logs.AppendSystem("Invalid listen port");
            return;
        }

        try
        {
            await _server.StartAsync(port);
            _serverPort = port;
            _trackerManager.Config.LastPort = port;
            await _trackerManager.PersistConfigAsync();
            IsRunning = true;
        }
        catch (Exception ex)
        {
            _logs.AppendSystem($"Start error: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task BroadcastAsync()
    {
        if (!_server.IsRunning)
        {
            _logs.AppendSystem("Start server first");
            return;
        }

        try
        {
            await _server.SendDiscoveryHandshakeAsync(_serverPort);
        }
        catch (Exception ex)
        {
            _logs.AppendSystem($"Broadcast error: {ex.Message}");
        }
    }

    private void SetLanguage(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return;
        }

        _localization.SetLanguage(languageCode);
        _trackerManager.Config.Language = _localization.CurrentLanguage;
        _ = _trackerManager.PersistConfigAsync();
        OnPropertyChanged(nameof(CurrentLanguage));
    }
}
