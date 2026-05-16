using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using ProgrammTrackerBody.Domain;

namespace ProgrammTrackerBody.ViewModels;

public sealed class TrackerViewModel : ViewModelBase, IDisposable
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(56, 142, 60));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(245, 127, 23));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(117, 117, 117));

    private readonly ObservableCollection<TrackerModel> _allTrackers;

    public TrackerViewModel(TrackerModel model, ObservableCollection<TrackerModel> allTrackers)
    {
        Model = model;
        _allTrackers = allTrackers;

        foreach (var t in allTrackers)
        {
            t.PropertyChanged += OnSiblingPropertyChanged;
        }

        allTrackers.CollectionChanged += OnTrackersCollectionChanged;
    }

    public TrackerModel Model { get; }

    public IReadOnlyList<BodyPart> AvailableBodyParts
    {
        get
        {
            var taken = new HashSet<BodyPart>(
                _allTrackers
                    .Where(t => t != Model && t.BodyPart != BodyPart.None)
                    .Select(t => t.BodyPart));

            return BodyParts.All
                .Where(bp => bp == BodyPart.None || !taken.Contains(bp))
                .ToList();
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (!Model.IsOnline) return GrayBrush;
            var elapsed = (DateTime.UtcNow - Model.LastSeenUtc).TotalSeconds;
            if (elapsed < 3) return GreenBrush;
            if (elapsed < 10) return YellowBrush;
            return GrayBrush;
        }
    }

    public string StatusTextKey
    {
        get
        {
            if (!Model.IsOnline) return "Trackers.Offline";
            var elapsed = (DateTime.UtcNow - Model.LastSeenUtc).TotalSeconds;
            if (elapsed < 10) return "Trackers.Online";
            return "Trackers.Stale";
        }
    }

    internal void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusTextKey));
    }

    public void Dispose()
    {
        _allTrackers.CollectionChanged -= OnTrackersCollectionChanged;
        foreach (var t in _allTrackers)
        {
            t.PropertyChanged -= OnSiblingPropertyChanged;
        }
    }

    private void OnSiblingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerModel.BodyPart))
        {
            OnPropertyChanged(nameof(AvailableBodyParts));
        }

        if (sender == Model && e.PropertyName is nameof(TrackerModel.IsOnline) or nameof(TrackerModel.LastSeenUtc))
        {
            RefreshStatus();
        }
    }

    private void OnTrackersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (TrackerModel t in e.NewItems)
            {
                t.PropertyChanged += OnSiblingPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (TrackerModel t in e.OldItems)
            {
                t.PropertyChanged -= OnSiblingPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(AvailableBodyParts));
    }
}
