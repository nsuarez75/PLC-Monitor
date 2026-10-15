using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un símbolo encontrado al explorar.</summary>
public sealed record BrowseItemViewModel(string Address, string NativeType, string DataType);

/// <summary>Buscador de símbolos del dispositivo seleccionado y alta de tags a partir de ellos.</summary>
public sealed partial class BrowseViewModel(IAgentContext context, Func<DeviceViewModel?> device) : ObservableObject
{
    public ObservableCollection<BrowseItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial int MaxResults { get; set; } = 200;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Grupo de escaneo donde se añaden los tags (se crea si no existe).</summary>
    [ObservableProperty]
    public partial string GroupName { get; set; } = "default";

    [ObservableProperty]
    public partial int IntervalMs { get; set; } = 1000;

    [ObservableProperty]
    public partial bool AddWritable { get; set; }

    [ObservableProperty]
    public partial BrowseItemViewModel? SelectedItem { get; set; }

    public void Clear()
    {
        Results.Clear();
        Summary = "";
    }

    private bool CanSearch() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(bool refresh)
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        IsBusy = true;
        Summary = refresh ? "Explorando la CPU…" : "Buscando…";
        try
        {
            var reply = await client.BrowseAsync(new BrowseRequest { Device = target.Name, Filter = Filter, MaxResults = MaxResults, Refresh = refresh });
            Results.Clear();
            foreach (var item in reply.Items)
                Results.Add(new BrowseItemViewModel(item.Address, item.NativeType, item.DataType.ToString()));
            Summary = $"{reply.Items.Count} mostrados de {reply.TotalMatches} coincidencias · {reply.TotalSymbols} símbolos en {target.Name}";
        }
        catch (RpcException ex)
        {
            Summary = "";
            context.Report(context.Describe(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Añade el símbolo como tag del dispositivo (nombre = dirección) y guarda la configuración.</summary>
    [RelayCommand]
    private async Task AddAsync(BrowseItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null || context.Client is not { } client || device() is not { } target)
            return;
        try
        {
            var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = target.Name });
            if (config.ScanGroups.SelectMany(g => g.Tags).Any(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == item.Address))
            {
                context.Report($"{item.Address} ya es un tag de {target.Name}");
                return;
            }
            var groupName = string.IsNullOrWhiteSpace(GroupName) ? "default" : GroupName.Trim();
            var group = config.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                config.ScanGroups.Add(group = new ScanGroupConfig { Name = groupName, IntervalMs = IntervalMs });
            group.Tags.Add(new TagConfig { Address = item.Address, Writable = AddWritable });
            await client.PutDeviceAsync(new PutDeviceRequest { Device = config, Persist = true });
            context.Report($"Añadido {item.Address} a {target.Name} (grupo {group.Name}, {group.IntervalMs} ms)");
        }
        catch (RpcException ex)
        {
            context.Report(context.Describe(ex), isError: true);
        }
    }
}
