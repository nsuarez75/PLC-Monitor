using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un símbolo encontrado al explorar. <see cref="IsAdded"/> indica que ya es un tag del dispositivo.</summary>
public sealed partial class BrowseItemViewModel(string address, string nativeType, string dataType) : ObservableObject
{
    public string Address { get; } = address;

    public string NativeType { get; } = nativeType;

    public string DataType { get; } = dataType;

    [ObservableProperty]
    public partial bool IsAdded { get; set; }
}

/// <summary>Buscador de símbolos del dispositivo seleccionado y alta de tags a partir de ellos.</summary>
public sealed partial class BrowseViewModel(IAgentContext context, Func<DeviceViewModel?> device) : LocalizedObservableObject
{
    public ObservableCollection<BrowseItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial int MaxResults { get; set; } = 200;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(RefreshCommand))]
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

    /// <summary>Busca en la caché de símbolos del agente.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => BrowseAsync(refresh: false);

    /// <summary>Vuelve a explorar la CPU (tras cargar un programa nuevo).</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task RefreshAsync() => BrowseAsync(refresh: true);

    private async Task BrowseAsync(bool refresh)
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        IsBusy = true;
        Summary = Loc.T(refresh ? "Explorando la CPU…" : "Buscando…");
        try
        {
            var reply = await client.BrowseAsync(new BrowseRequest { Device = target.Name, Filter = Filter, MaxResults = MaxResults, Refresh = refresh });
            Results.Clear();
            var existing = target.Tags.Select(t => t.Address).ToHashSet(StringComparer.Ordinal);
            foreach (var item in reply.Items)
            {
                Results.Add(new BrowseItemViewModel(item.Address, item.NativeType, item.DataType.ToString())
                {
                    IsAdded = existing.Contains(item.Address),
                });
            }
            Summary = Loc.F("{0} mostrados de {1} coincidencias · {2} símbolos en {3}", reply.Items.Count, reply.TotalMatches, reply.TotalSymbols, target.Name);
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
        if (item is null || item.IsAdded || context.Client is not { } client || device() is not { } target)
            return;
        try
        {
            var result = await TagAdder.AddAsync(client, target.Name, item.Address, null, GroupName, IntervalMs, AddWritable);
            item.IsAdded = true;
            context.Report(result.Message);
        }
        catch (RpcException ex)
        {
            context.Report(context.Describe(ex), isError: true);
        }
    }
}
