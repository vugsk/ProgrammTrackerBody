using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using ProgrammTrackerBody.Networking;

namespace ProgrammTrackerBody.ViewModels;

public sealed record PacketLogRow(
    string Time,
    string Direction,
    string Endpoint,
    string PacketType,
    string Summary);

public sealed class LogsViewModel : ViewModelBase
{
    private const int MaxPacketRows = 600;
    private const int MaxSystemLogChars = 12000;

    private readonly Dispatcher _dispatcher;
    private string _systemLog = string.Empty;

    public LogsViewModel(UdpTrackerServer server, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        server.PacketLogged += OnPacketLogged;
    }

    public ObservableCollection<PacketLogRow> Packets { get; } = new();

    public string SystemLog
    {
        get => _systemLog;
        private set => SetField(ref _systemLog, value);
    }

    public void AppendSystem(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        var combined = SystemLog + stamped;
        if (combined.Length > MaxSystemLogChars)
        {
            combined = combined[^MaxSystemLogChars..];
        }

        SystemLog = combined;
    }

    private void OnPacketLogged(PacketLogMessage message)
    {
        _dispatcher.Invoke(() =>
        {
            Packets.Insert(0, new PacketLogRow(
                message.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                message.Direction,
                message.Endpoint,
                message.PacketType,
                message.Summary));

            while (Packets.Count > MaxPacketRows)
            {
                Packets.RemoveAt(Packets.Count - 1);
            }

            if (message.Direction == "SYSTEM")
            {
                AppendSystem($"[{message.PacketType}] {message.Summary}");
            }
        });
    }
}
