using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un dispositivo del agente con su estado y sus tags.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private readonly IAgentContext _context;

    public DeviceViewModel(IAgentContext context, Device device)
    {
        _context = context;
        Name = device?.Name ?? throw new ArgumentNullException(nameof(device));
        Apply(device);
        Tags.CollectionChanged += (_, e) =>
        {
            foreach (var tag in e.NewItems?.OfType<TagViewModel>() ?? [])
                tag.PropertyChanged += OnTagChanged;
            foreach (var tag in e.OldItems?.OfType<TagViewModel>() ?? [])
                tag.PropertyChanged -= OnTagChanged;
            NotifyCounts();
        };
    }

    /// <summary>Tags con calidad buena.</summary>
    public int GoodCount => Tags.Count(t => t.IsGood);

    /// <summary>Tags con calidad mala o incierta.</summary>
    public int BadCount => Tags.Count(t => !t.IsGood);

    public bool HasBadTags => BadCount > 0;

    public bool HasTags => Tags.Count > 0;

    public string TagCountText => Tags.Count == 1 ? "1 tag" : $"{Tags.Count} tags";

    private void OnTagChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagViewModel.Quality))
            NotifyCounts();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(GoodCount));
        OnPropertyChanged(nameof(BadCount));
        OnPropertyChanged(nameof(HasBadTags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagCountText));
    }

    public string Name { get; }

    public ObservableCollection<TagViewModel> Tags { get; } = [];

    [ObservableProperty]
    public partial string Driver { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool WritesEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsFaulted), nameof(IsConnecting), nameof(StateText))]
    public partial ConnectionState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string Since { get; set; } = "";

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsFaulted => State == ConnectionState.Faulted;

    public bool IsConnecting => State == ConnectionState.Connecting;

    public string StateText => State switch
    {
        ConnectionState.Connected => "Conectado",
        ConnectionState.Connecting => "Conectando…",
        ConnectionState.Faulted => "En fallo",
        _ => Enabled ? "Desconectado" : "Desactivado",
    };

    public string ToggleText => Enabled ? "Desactivar" : "Activar";

    public void Apply(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Driver = device.Driver;
        Enabled = device.Enabled;
        WritesEnabled = device.WritesEnabled;
        State = device.State;
        var detail = device.Detail;
        if (device.NextRetry is not null)
            detail = $"{detail} · reintento a las {device.NextRetry.ToDateTime().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}".Trim(' ', '·');
        Detail = detail;
        Since = device.Since?.ToDateTime().ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
        OnPropertyChanged(nameof(StateText));
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (_context.Client is not { } client)
            return;
        try
        {
            // El estado lo trae la suscripción: aplicar aquí la respuesta podría pisar un estado más reciente.
            var device = await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = Name, Enabled = !Enabled, Persist = true });
            _context.Report($"{Name} {(device.Enabled ? "activado" : "desactivado")}");
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }
}
