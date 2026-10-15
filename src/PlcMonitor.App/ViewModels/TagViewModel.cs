using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un tag configurado con su último valor.</summary>
public sealed partial class TagViewModel : ObservableObject
{
    private readonly IAgentContext _context;

    public TagViewModel(IAgentContext context, Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _context = context;
        Device = tag.Device;
        Name = tag.Name;
        Address = tag.Address;
        Group = $"{tag.Group} ({tag.IntervalMs} ms)";
        Writable = tag.Writable;
        if (tag.Value is not null)
            Apply(tag.Value);
    }

    public string Device { get; }

    public string Name { get; }

    public string Address { get; }

    public string Group { get; }

    public bool Writable { get; }

    [ObservableProperty]
    public partial string ValueText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGood), nameof(IsUncertain), nameof(IsBad))]
    public partial Quality Quality { get; set; } = Quality.BadNotConnected;

    [ObservableProperty]
    public partial string DataType { get; set; } = "";

    [ObservableProperty]
    public partial string Time { get; set; } = "";

    /// <summary>Valor que el usuario quiere escribir.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    public partial string NewValue { get; set; } = "";

    public bool IsGood => Quality == Quality.Good;

    public bool IsUncertain => Quality == Quality.Uncertain;

    public bool IsBad => !IsGood && !IsUncertain;

    public void Apply(TagValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Quality = value.Quality;
        ValueText = value.Quality is Quality.Good or Quality.Uncertain ? value.Text : value.Error;
        DataType = value.DataType == Agent.Contracts.DataType.Unknown ? "" : value.DataType.ToString();
        Time = value.Timestamp?.ToDateTime().ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";
    }

    private bool CanWrite() => Writable && !string.IsNullOrEmpty(NewValue);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (_context.Client is not { } client)
            return;
        var value = NewValue;
        if (!await _context.Dialogs.ConfirmAsync("Escribir en el PLC", $"¿Escribir «{value}» en {Device}/{Name} ({Address})?\nActual: {ValueText}"))
            return;
        try
        {
            var reply = await client.WriteAsync(new WriteRequest { Device = Device, Tag = Name, Value = value, User = Environment.UserName });
            if (reply.Success)
            {
                NewValue = "";
                _context.Report($"{Device}/{Name} = {value}");
            }
            else
            {
                _context.Report($"No se escribió {Device}/{Name} ({reply.Quality}): {reply.Error}", isError: true);
            }
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_context.Client is not { } client)
            return;
        if (!await _context.Dialogs.ConfirmAsync("Quitar tag", $"¿Quitar el tag {Name} ({Address}) de {Device}?"))
            return;
        try
        {
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Device });
            foreach (var group in device.ScanGroups.ToArray())
            {
                foreach (var t in group.Tags.Where(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == Name).ToArray())
                    group.Tags.Remove(t);
                if (group.Tags.Count == 0)
                    device.ScanGroups.Remove(group);
            }
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true });
            _context.Report($"Tag {Device}/{Name} quitado");
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }
}
