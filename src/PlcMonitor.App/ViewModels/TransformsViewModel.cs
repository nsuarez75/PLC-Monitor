using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Panel de transformaciones del agente: valores enteros que se muestran como texto (0 = Parado, 1 = Marcha...).
/// Los cambios se editan aquí y se envían todos juntos al guardar; el agente los valida antes de aplicarlos.
/// </summary>
public sealed partial class TransformsViewModel : LocalizedObservableObject, IDisposable
{
    private readonly MainViewModel _main;

    public TransformsViewModel(MainViewModel main)
    {
        _main = main;
        _main.TransformsChanged += OnAgentTransformsChanged;
        Load();
    }

    public ObservableCollection<TransformItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial TransformItem? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Hay cambios sin guardar.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Errors { get; set; } = "";

    /// <summary>Carga las transformaciones del agente (descarta lo que no se haya guardado).</summary>
    private void Load()
    {
        var selected = Selected?.Name;
        Items.Clear();
        foreach (var transform in _main.Transforms.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new TransformItem(this, transform, UsageOf(transform.Name)));
        Selected = Items.FirstOrDefault(i => i.Name == selected) ?? Items.FirstOrDefault();
        Errors = "";
        IsDirty = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Tags que usan una transformación (para avisar antes de borrarla o cambiarle el nombre).</summary>
    private IReadOnlyList<TagViewModel> UsageOf(string name) =>
        [.. _main.AllTags.Where(t => string.Equals(t.Transform, name, StringComparison.OrdinalIgnoreCase))];

    // Si otro equipo cambia las transformaciones mientras el panel está abierto, se recargan salvo que haya cambios sin guardar.
    private void OnAgentTransformsChanged(object? sender, EventArgs e)
    {
        if (!IsDirty && !IsBusy)
            Load();
    }

    internal void MarkDirty() => IsDirty = true;

    [RelayCommand]
    private void Add()
    {
        var name = Loc.T("Nueva transformación");
        for (var n = 2; Items.Any(i => string.Equals(i.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++)
            name = Loc.F("Nueva transformación {0}", n);
        var item = new TransformItem(this, new ValueTransform { Name = name }, [], isNew: true);
        item.Entries.Add(new TransformEntryItem(item) { Value = "0" });
        item.Entries.Add(new TransformEntryItem(item) { Value = "1" });
        Items.Add(item);
        Selected = item;
        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (Selected is not { } item)
            return;
        if (item.Usage.Count > 0)
        {
            Errors = Loc.F("«{0}» la usan {1}: quítasela antes a esos tags (doble clic en el tag → «Mostrar valor como»).", item.OriginalName, item.UsageText);
            return;
        }
        if (!await _main.Dialogs.ConfirmAsync(Loc.T("Borrar transformación"), Loc.F("¿Borrar «{0}»? Se aplica al guardar.", item.Name)))
            return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanDiscard() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() => Load();

    private bool CanSave() => IsDirty && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_main.Client is not { } client)
            return;
        var (transforms, errors) = Build();
        if (errors.Count > 0)
        {
            Errors = string.Join(Environment.NewLine, errors);
            return;
        }
        // Las transformaciones en uso a las que se cambia el nombre: los tags que las usan pasan al nombre nuevo.
        var renames = Items
            .Where(i => i.OriginalName is not null && i.Usage.Count > 0 && !string.Equals(i.OriginalName, i.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(i => i.OriginalName!, i => i.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var clash = renames.Keys.FirstOrDefault(old => transforms.Any(t => string.Equals(t.Name, old, StringComparison.OrdinalIgnoreCase)));
        if (clash is not null)
        {
            Errors = Loc.F("Otra transformación se llama ahora «{0}», que es el nombre antiguo de una que usan algunos tags: guarda primero un cambio y después el otro.", clash);
            return;
        }

        IsBusy = true;
        Errors = "";
        try
        {
            if (renames.Count > 0)
            {
                // 1) Nombres nuevos junto a los antiguos, 2) los tags al nombre nuevo, 3) fuera los antiguos.
                var both = new PutTransformsRequest { Persist = true };
                both.Transforms.AddRange(transforms);
                both.Transforms.AddRange(Items.Where(i => i.OriginalName is { } o && renames.ContainsKey(o)).Select(i => i.ToProto(i.OriginalName!)));
                await client.PutTransformsAsync(both);
                foreach (var device in _main.Devices.Where(d => d.Tags.Any(t => renames.ContainsKey(t.Transform))).Select(d => d.Name).ToArray())
                {
                    var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = device });
                    foreach (var tag in config.ScanGroups.SelectMany(g => g.Tags))
                    {
                        if (renames.TryGetValue(tag.Transform, out var renamed))
                            tag.Transform = renamed;
                    }
                    await client.PutDeviceAsync(new PutDeviceRequest { Device = config, Persist = true });
                }
            }
            var request = new PutTransformsRequest { Persist = true };
            request.Transforms.AddRange(transforms);
            var saved = await client.PutTransformsAsync(request);
            IsDirty = false;
            _main.Report(saved.Transforms.Count == 1 ? Loc.T("1 transformación guardada") : Loc.F("{0} transformaciones guardadas", saved.Transforms.Count));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Errors = ex.Status.Detail;
        }
        catch (RpcException ex)
        {
            Errors = _main.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
        // La suscripción trae las transformaciones guardadas; se recarga ya por si llegaron antes de terminar.
        if (!IsDirty)
            Load();
    }

    /// <summary>Convierte lo editado en mensajes para el agente, con los errores que se ven sin preguntarle.</summary>
    internal (List<ValueTransform> Transforms, List<string> Errors) Build()
    {
        var transforms = new List<ValueTransform>();
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in Items)
        {
            var name = item.Name.Trim();
            var where = name.Length == 0 ? Loc.T("Una transformación") : Loc.F("«{0}»", name);
            if (name.Length == 0)
                errors.Add(Loc.T("Hay una transformación sin nombre."));
            else if (!names.Add(name))
                errors.Add(Loc.F("{0}: hay otra con el mismo nombre.", where));
            var transform = new ValueTransform { Name = name };
            var values = new HashSet<long>();
            foreach (var entry in item.Entries.Where(e => !e.IsBlank))
            {
                if (!long.TryParse(entry.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    errors.Add(Loc.F("{0}: «{1}» no es un número entero.", where, entry.Value.Trim()));
                    continue;
                }
                if (!values.Add(value))
                    errors.Add(Loc.F("{0}: el valor {1} está repetido.", where, value));
                transform.Entries.Add(new TransformEntry { Value = value, Text = entry.Text.Trim() });
            }
            transforms.Add(transform);
        }
        return (transforms, errors);
    }

    public void Dispose() => _main.TransformsChanged -= OnAgentTransformsChanged;
}

/// <summary>Una transformación en edición.</summary>
public sealed partial class TransformItem : LocalizedObservableObject
{
    private readonly TransformsViewModel _owner;

    internal TransformItem(TransformsViewModel owner, ValueTransform transform, IReadOnlyList<TagViewModel> usage, bool isNew = false)
    {
        _owner = owner;
        OriginalName = isNew ? null : transform.Name;
        Name = transform.Name;
        Usage = usage;
        foreach (var entry in transform.Entries.OrderBy(e => e.Value))
            Entries.Add(new TransformEntryItem(this) { Value = entry.Value.ToString(CultureInfo.InvariantCulture), Text = entry.Text });
        Entries.CollectionChanged += (_, _) => _owner.MarkDirty();
    }

    /// <summary>Nombre con el que está guardada en el agente; null si es nueva.</summary>
    public string? OriginalName { get; private init; }

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value) => _owner.MarkDirty();

    public ObservableCollection<TransformEntryItem> Entries { get; } = [];

    /// <summary>Tags que la usan.</summary>
    public IReadOnlyList<TagViewModel> Usage { get; }

    public string UsageText => Usage.Count switch
    {
        0 => Loc.T("ningún tag"),
        1 => Loc.F("1 tag ({0}/{1})", Usage[0].Device, Usage[0].Name),
        <= 3 => Loc.F("{0} tags ({1})", Usage.Count, string.Join(", ", Usage.Select(t => $"{t.Device}/{t.Name}"))),
        _ => Loc.F("{0} tags ({1}…)", Usage.Count, string.Join(", ", Usage.Take(3).Select(t => $"{t.Device}/{t.Name}"))),
    };

    public string UsageSummary => Usage.Count == 0 ? Loc.T("Sin usar") : Usage.Count == 1 ? Loc.T("1 tag") : Loc.F("{0} tags", Usage.Count);

    [RelayCommand]
    private void AddEntry()
    {
        // Siguiente al mayor valor numérico ya puesto.
        var next = Entries.Select(e => long.TryParse(e.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
            .Max() is { } max ? max + 1 : 0;
        Entries.Add(new TransformEntryItem(this) { Value = next.ToString(CultureInfo.InvariantCulture) });
    }

    [RelayCommand]
    private void RemoveEntry(TransformEntryItem entry) => Entries.Remove(entry);

    internal void MarkDirty() => _owner.MarkDirty();

    internal ValueTransform ToProto(string name)
    {
        var (transforms, _) = _owner.Build();
        var built = transforms[_owner.Items.IndexOf(this)];
        var copy = new ValueTransform { Name = name };
        copy.Entries.AddRange(built.Entries);
        return copy;
    }
}

/// <summary>Un valor y su texto en edición.</summary>
public sealed partial class TransformEntryItem(TransformItem owner) : ObservableObject
{
    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Fila sin texto (p.ej. recién añadida, con el número ya puesto): no se guarda.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);

    partial void OnValueChanged(string value) => owner.MarkDirty();

    partial void OnTextChanged(string value) => owner.MarkDirty();
}
