using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public sealed class TrackersViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<TrackerModel> _models;
    private readonly DispatcherTimer _statusTimer;
    private bool _disposed;

    public TrackersViewModel(TrackerManager trackerManager)
    {
        _models = trackerManager.Trackers;
        TrackerViewModels = new ObservableCollection<TrackerViewModel>(
            _models.Select(m => new TrackerViewModel(m, _models)));

        _models.CollectionChanged += OnModelsChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    public ObservableCollection<TrackerViewModel> TrackerViewModels { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        _models.CollectionChanged -= OnModelsChanged;

        foreach (var vm in TrackerViewModels)
        {
            vm.Dispose();
        }
        TrackerViewModels.Clear();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        foreach (var vm in TrackerViewModels)
        {
            vm.RefreshStatus();
        }
    }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (TrackerModel m in e.NewItems)
            {
                TrackerViewModels.Add(new TrackerViewModel(m, _models));
            }
        }

        if (e.OldItems != null)
        {
            foreach (TrackerModel m in e.OldItems)
            {
                var vm = TrackerViewModels.FirstOrDefault(tv => tv.Model == m);
                if (vm is null)
                {
                    continue;
                }

                TrackerViewModels.Remove(vm);
                vm.Dispose();
            }
        }
    }
}
