using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public sealed class TrackersViewModel : ViewModelBase
{
    private readonly ObservableCollection<TrackerModel> _models;
    private readonly DispatcherTimer _statusTimer;

    public TrackersViewModel(TrackerManager trackerManager)
    {
        _models = trackerManager.Trackers;
        TrackerViewModels = new ObservableCollection<TrackerViewModel>(
            _models.Select(m => new TrackerViewModel(m, _models)));

        _models.CollectionChanged += OnModelsChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) =>
        {
            foreach (var vm in TrackerViewModels)
            {
                vm.RefreshStatus();
            }
        };
        _statusTimer.Start();
    }

    public ObservableCollection<TrackerViewModel> TrackerViewModels { get; }

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
