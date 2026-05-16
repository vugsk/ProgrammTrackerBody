using System;
using System.Collections.ObjectModel;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public sealed class SkeletonViewModel : ViewModelBase
{
    private double _heightCm = 170.0;
    private bool _showTrackerAxes;

    public SkeletonViewModel(TrackerManager trackerManager)
    {
        Trackers = trackerManager.Trackers;
        trackerManager.CalibrationApplied += () => CalibrationApplied?.Invoke(this, EventArgs.Empty);
    }

    // Relays TrackerManager.CalibrationApplied so the view can briefly snap
    // the skeleton to T-pose after any Yaw/Full/Mounting reset.
    public event EventHandler? CalibrationApplied;

    public ObservableCollection<TrackerModel> Trackers { get; }

    public double HeightCm
    {
        get => _heightCm;
        set
        {
            if (SetField(ref _heightCm, value))
            {
                OnPropertyChanged(nameof(HeightMeters));
                SceneInvalidated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool ShowTrackerAxes
    {
        get => _showTrackerAxes;
        set
        {
            if (SetField(ref _showTrackerAxes, value))
            {
                SceneInvalidated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double HeightMin => 100.0;
    public double HeightMax => 220.0;

    public double HeightMeters => _heightCm / 100.0;

    // Code-behind subscribes to this to rebuild 3D scene on height / overlay changes.
    public event EventHandler? SceneInvalidated;

    internal void RaiseSceneInvalidated() => SceneInvalidated?.Invoke(this, EventArgs.Empty);
}
