using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public enum CalibrationTarget
{
    None,
    Full,
    Yaw,
    Mounting,
    FullSetup,
}

public sealed class CalibrationViewModel : ViewModelBase, IDisposable
{
    private readonly TrackerManager _trackerManager;
    private readonly LocalizationService _localization;
    private DispatcherTimer? _countdownTimer;
    private int _countdownSeconds;
    private bool _disposed;

    private CalibrationTarget _countdownTarget = CalibrationTarget.None;
    private string _countdownText = string.Empty;
    private string _statusMessageKey = "Calibration.StatusIdle";

    public CalibrationViewModel(TrackerManager trackerManager, LocalizationService localization)
    {
        _trackerManager = trackerManager;
        _localization = localization;
        Trackers = trackerManager.Trackers;

        // Per-tracker (advanced) commands — kept for the Expander section.
        YawResetCommand      = new RelayCommand(p => InvokeReset(p, _trackerManager.RequestYawReset));
        FullResetCommand     = new RelayCommand(p => InvokeReset(p, _trackerManager.RequestFullReset));
        MountingResetCommand = new RelayCommand(p => InvokeReset(p, _trackerManager.RequestMountingReset));
        VibrateCommand = new RelayCommand(p =>
        {
            if (p is string mac && !string.IsNullOrEmpty(mac))
            {
                _ = _trackerManager.SendVibrateAsync(mac);
            }
        });

        // Three new countdown-driven global commands. CanExecute requires no
        // active countdown and at least one tracker connected.
        StartFullResetCommand     = new RelayCommand(_ => StartCountdown(CalibrationTarget.Full),      _ => CanStartCountdown());
        StartYawResetCommand      = new RelayCommand(_ => StartCountdown(CalibrationTarget.Yaw),       _ => CanStartCountdown());
        StartMountingResetCommand = new RelayCommand(_ => StartCountdown(CalibrationTarget.Mounting),  _ => CanStartCountdown());
        StartFullSetupCommand     = new RelayCommand(_ => StartCountdown(CalibrationTarget.FullSetup), _ => CanStartCountdown());

        // Re-fire status key when language changes so the bound converter re-evaluates.
        _localization.PropertyChanged += OnLocalizationPropertyChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _localization.PropertyChanged -= OnLocalizationPropertyChanged;
        if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= OnCountdownTick;
            _countdownTimer = null;
        }
    }

    private void OnLocalizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StatusMessageKey));
    }

    public ObservableCollection<TrackerModel> Trackers { get; }

    public ICommand YawResetCommand { get; }
    public ICommand FullResetCommand { get; }
    public ICommand MountingResetCommand { get; }
    public ICommand VibrateCommand { get; }

    public ICommand StartFullResetCommand { get; }
    public ICommand StartYawResetCommand { get; }
    public ICommand StartMountingResetCommand { get; }
    public ICommand StartFullSetupCommand { get; }

    public CalibrationTarget CountdownTarget
    {
        get => _countdownTarget;
        private set
        {
            if (SetField(ref _countdownTarget, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsFullCountdown));
                OnPropertyChanged(nameof(IsYawCountdown));
                OnPropertyChanged(nameof(IsMountingCountdown));
                OnPropertyChanged(nameof(IsFullSetupCountdown));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CountdownText
    {
        get => _countdownText;
        private set => SetField(ref _countdownText, value);
    }

    public string StatusMessageKey
    {
        get => _statusMessageKey;
        private set => SetField(ref _statusMessageKey, value);
    }

    public bool IsIdle => _countdownTarget == CalibrationTarget.None;
    public bool IsFullCountdown => _countdownTarget == CalibrationTarget.Full;
    public bool IsYawCountdown => _countdownTarget == CalibrationTarget.Yaw;
    public bool IsMountingCountdown => _countdownTarget == CalibrationTarget.Mounting;
    public bool IsFullSetupCountdown => _countdownTarget == CalibrationTarget.FullSetup;

    private bool CanStartCountdown() => IsIdle && Trackers.Count > 0;

    private void StartCountdown(CalibrationTarget target)
    {
        if (!IsIdle) return;

        _countdownSeconds = 3;
        CountdownTarget = target;
        CountdownText = _countdownSeconds.ToString();
        StatusMessageKey = "Calibration.StatusPreparing";

        if (_countdownTimer is null)
        {
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += OnCountdownTick;
        }

        _countdownTimer.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _countdownSeconds--;
        if (_countdownSeconds > 0)
        {
            CountdownText = _countdownSeconds.ToString();
            return;
        }

        // Countdown reached zero — apply the chosen reset to every tracker.
        _countdownTimer?.Stop();
        var target = _countdownTarget;

        foreach (var tracker in Trackers)
        {
            switch (target)
            {
                case CalibrationTarget.Full:      _trackerManager.RequestFullReset(tracker.Mac); break;
                case CalibrationTarget.Yaw:       _trackerManager.RequestYawReset(tracker.Mac); break;
                case CalibrationTarget.Mounting:  _trackerManager.RequestMountingReset(tracker.Mac); break;
                case CalibrationTarget.FullSetup: _trackerManager.RequestFullSetup(tracker.Mac); break;
            }
        }

        CountdownText = string.Empty;
        CountdownTarget = CalibrationTarget.None;
        StatusMessageKey = target switch
        {
            CalibrationTarget.Full      => "Calibration.StatusFullDone",
            CalibrationTarget.Yaw       => "Calibration.StatusYawDone",
            CalibrationTarget.Mounting  => "Calibration.StatusMountingDone",
            CalibrationTarget.FullSetup => "Calibration.StatusFullSetupDone",
            _                           => "Calibration.StatusIdle",
        };
    }

    private static void InvokeReset(object? parameter, Action<string> action)
    {
        if (parameter is string mac && !string.IsNullOrEmpty(mac))
        {
            action(mac);
        }
    }
}
