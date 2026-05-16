using System.Windows.Threading;
using ProgrammTrackerBody.Networking;
using ProgrammTrackerBody.Services;

namespace ProgrammTrackerBody.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public MainViewModel(
        UdpTrackerServer server,
        TrackerManager trackerManager,
        LocalizationService localization,
        Dispatcher dispatcher)
    {
        Server = server;
        TrackerManager = trackerManager;
        Localization = localization;

        Logs = new LogsViewModel(server, dispatcher);
        Dashboard = new DashboardViewModel(server, trackerManager, localization, Logs);
        Trackers = new TrackersViewModel(trackerManager);
        Calibration = new CalibrationViewModel(trackerManager, localization);
        Skeleton = new SkeletonViewModel(trackerManager);
    }

    public UdpTrackerServer Server { get; }

    public TrackerManager TrackerManager { get; }

    public LocalizationService Localization { get; }

    public DashboardViewModel Dashboard { get; }

    public TrackersViewModel Trackers { get; }

    public CalibrationViewModel Calibration { get; }

    public SkeletonViewModel Skeleton { get; }

    public LogsViewModel Logs { get; }
}
