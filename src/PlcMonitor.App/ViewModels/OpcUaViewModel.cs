using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Servidor OPC UA del agente conectado: activarlo, puerto, tercera llave de escritura, usuarios y certificados de los
/// clientes (Ignition, UaExpert…). Los ajustes se editan aquí y se envían juntos al guardar; el estado y los
/// certificados se leen al abrir, con «Actualizar» y solos cada pocos segundos mientras la pestaña está a la vista (así
/// un cliente nuevo aparece pendiente sin pulsar nada).
/// </summary>
public sealed partial class OpcUaViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _autoRefresh;

    /// <summary>Cada cuánto se vuelven a leer el estado y los clientes con la pestaña a la vista.</summary>
    internal static TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    public OpcUaViewModel(MainViewModel main)
    {
        _main = main;
    }

    public ObservableCollection<OpcUaUserItem> Users { get; } = [];

    public ObservableCollection<OpcUaCertificateItem> Certificates { get; } = [];

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial int Port { get; set; } = 4840;

    /// <summary>Tercera llave: sin ella no se escribe nada por OPC UA.</summary>
    [ObservableProperty]
    public partial bool AllowWrites { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsError))]
    public partial OpcUaState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string EndpointUrl { get; set; } = "";

    [ObservableProperty]
    public partial string CertificateThumbprint { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Errors { get; set; } = "";

    /// <summary>Con el token de solo lectura se ve todo pero no se puede cambiar nada.</summary>
    public bool CanEdit => !_main.ReadOnlyAccess;

    public bool IsRunning => State == OpcUaState.Running;

    public bool IsError => State == OpcUaState.Error;

    public bool HasPending => Certificates.Any(c => !c.Trusted);

    public bool HasCertificates => Certificates.Count > 0;

    public string StateText => State switch
    {
        OpcUaState.Running => "En marcha",
        OpcUaState.Starting => "Arrancando",
        OpcUaState.Error => "Error",
        _ => "Parado",
    };

    partial void OnEnabledChanged(bool value) => IsDirty = true;

    partial void OnPortChanged(int value) => IsDirty = true;

    partial void OnAllowWritesChanged(bool value) => IsDirty = true;

    internal void MarkDirty() => IsDirty = true;

    private bool CanRefresh() => !IsBusy;

    /// <summary>Lee del agente ajustes, estado y certificados (descarta lo que no se haya guardado).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (_main.Client is not { } client)
            return;
        IsBusy = true;
        try
        {
            Apply(await client.GetOpcUaAsync(new Empty()));
            Errors = "";
        }
        catch (RpcException ex)
        {
            Errors = _main.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Empieza a leer el estado y los clientes cada <see cref="AutoRefreshInterval"/> (pestaña a la vista). No toca
    /// lo que se esté editando.
    /// </summary>
    public void StartAutoRefresh()
    {
        if (_autoRefresh is not null)
            return;
        _autoRefresh = new CancellationTokenSource();
        _ = AutoRefreshAsync(_autoRefresh.Token);
    }

    /// <summary>Deja de leer solo (otra pestaña, desconexión).</summary>
    public void StopAutoRefresh()
    {
        _autoRefresh?.Cancel();
        _autoRefresh?.Dispose();
        _autoRefresh = null;
    }

    private async Task AutoRefreshAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(AutoRefreshInterval, token);
                if (IsBusy || _main.Client is not { } client)
                    continue;
                try
                {
                    Apply(await client.GetOpcUaAsync(new Empty(), cancellationToken: token), keepEdits: true);
                }
                catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
                {
                    // Sin conexión con el agente lo avisa la barra de estado; se reintenta en el siguiente ciclo.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Pestaña cerrada o desconexión.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // Cancelada a mitad de una lectura.
        }
    }

    public void Dispose() => StopAutoRefresh();

    /// <summary>Muestra lo que devuelve el agente; los ajustes solo si no hay cambios sin guardar.</summary>
    internal void Apply(OpcUaInfo info, bool keepEdits = false)
    {
        State = info.State;
        Detail = info.Detail;
        EndpointUrl = info.EndpointUrl;
        CertificateThumbprint = info.CertificateThumbprint;
        Certificates.Clear();
        foreach (var certificate in info.Certificates.OrderBy(c => c.Trusted).ThenBy(c => c.Subject, StringComparer.CurrentCultureIgnoreCase))
            Certificates.Add(new OpcUaCertificateItem(this, certificate));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasCertificates));
        if (keepEdits && IsDirty)
            return;
        Enabled = info.Enabled;
        Port = info.Port == 0 ? 4840 : info.Port;
        AllowWrites = info.AllowWrites;
        Users.Clear();
        foreach (var user in info.Users)
            Users.Add(new OpcUaUserItem(this, user.Name, user.Role == OpcUaRole.Write, hasPassword: true));
        IsDirty = false;
    }

    [RelayCommand]
    private void AddUser()
    {
        var name = "usuario";
        for (var n = 2; Users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"usuario{n}";
        Users.Add(new OpcUaUserItem(this, name, writer: false, hasPassword: false));
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveUser(OpcUaUserItem? user)
    {
        if (user is null)
            return;
        Users.Remove(user);
        IsDirty = true;
    }

    private bool CanDiscard() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync()
    {
        IsDirty = false;
        await RefreshAsync();
    }

    private bool CanSave() => IsDirty && !IsBusy && CanEdit;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_main.Client is not { } client)
            return;
        var missing = Users.FirstOrDefault(u => !u.HasPassword && string.IsNullOrEmpty(u.Password));
        if (missing is not null)
        {
            Errors = $"Escribe una contraseña para «{missing.Name}».";
            return;
        }
        if (AllowWrites && !await _main.Dialogs.ConfirmAsync("Escritura por OPC UA",
                "¿Permitir escribir en los PLC desde OPC UA?\nSolo podrán los usuarios con permiso de escritura, en los tags marcados como escribibles de los dispositivos con escritura activada."))
            return;
        var request = new PutOpcUaRequest { Enabled = Enabled, Port = Port, AllowWrites = AllowWrites };
        request.Users.AddRange(Users.Select(u => new OpcUaUser
        {
            Name = u.Name.Trim(),
            Role = u.Writer ? OpcUaRole.Write : OpcUaRole.Read,
            Password = u.Password,
        }));
        IsBusy = true;
        Errors = "";
        try
        {
            var info = await client.PutOpcUaAsync(request);
            IsDirty = false;
            Apply(info);
            _main.Report(info.State switch
            {
                OpcUaState.Running => $"Servidor OPC UA en marcha: {info.EndpointUrl}",
                OpcUaState.Error => $"Ajustes OPC UA guardados, pero el servidor no arranca: {info.Detail}",
                _ => "Ajustes OPC UA guardados (servidor parado)",
            }, isError: info.State == OpcUaState.Error);
        }
        catch (RpcException ex)
        {
            Errors = ex.StatusCode == StatusCode.InvalidArgument ? ex.Status.Detail : _main.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task TrustAsync(OpcUaCertificateItem certificate)
    {
        if (_main.Client is not { } client)
            return;
        if (!await _main.Dialogs.ConfirmAsync("Aceptar cliente OPC UA",
                $"¿Aceptar el certificado de «{certificate.Name}»?\nHuella: {certificate.Thumbprint}\nComprueba que coincide con la que muestra el cliente."))
            return;
        await RunAsync(() => client.TrustOpcUaCertificateAsync(new OpcUaCertificateRequest { Thumbprint = certificate.Thumbprint }).ResponseAsync,
            $"Cliente OPC UA «{certificate.Name}» aceptado");
    }

    internal async Task RemoveAsync(OpcUaCertificateItem certificate)
    {
        if (_main.Client is not { } client)
            return;
        if (!await _main.Dialogs.ConfirmAsync("Quitar cliente OPC UA", $"¿Quitar el certificado de «{certificate.Name}»?\nEse cliente dejará de poder conectar."))
            return;
        await RunAsync(() => client.DeleteOpcUaCertificateAsync(new OpcUaCertificateRequest { Thumbprint = certificate.Thumbprint }).ResponseAsync,
            $"Cliente OPC UA «{certificate.Name}» quitado");
    }

    private async Task RunAsync(Func<Task<OpcUaInfo>> call, string done)
    {
        IsBusy = true;
        try
        {
            Apply(await call(), keepEdits: true);
            Errors = "";
            _main.Report(done);
        }
        catch (RpcException ex)
        {
            Errors = _main.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Usuario OPC UA en edición. La contraseña solo se escribe para ponerla o cambiarla.</summary>
public sealed partial class OpcUaUserItem(OpcUaViewModel owner, string name, bool writer, bool hasPassword) : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    /// <summary>Rol de escritura (si no, solo lectura).</summary>
    [ObservableProperty]
    public partial bool Writer { get; set; } = writer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    public partial string Password { get; set; } = "";

    /// <summary>El agente ya tiene una contraseña para este usuario (vacía = se conserva).</summary>
    public bool HasPassword { get; } = hasPassword;

    public string PasswordHint => HasPassword ? "(sin cambios)" : "contraseña";

    partial void OnNameChanged(string value) => owner.MarkDirty();

    partial void OnWriterChanged(bool value) => owner.MarkDirty();

    partial void OnPasswordChanged(string value) => owner.MarkDirty();
}

/// <summary>Certificado de un cliente OPC UA: pendiente de aceptar o ya aceptado.</summary>
public sealed partial class OpcUaCertificateItem(OpcUaViewModel owner, OpcUaCertificate certificate)
{
    public string Thumbprint { get; } = certificate.Thumbprint;

    public bool Trusted { get; } = certificate.Trusted;

    /// <summary>Nombre corto: el CN del certificado.</summary>
    public string Name { get; } = CommonName(certificate.Subject);

    public string ApplicationUri { get; } = certificate.ApplicationUri;

    public string StateText => Trusted ? "Aceptado" : "Pendiente de aceptar";

    public string Details => string.Join(" · ", new[]
    {
        string.IsNullOrEmpty(certificate.ApplicationUri) ? null : certificate.ApplicationUri,
        certificate.NotAfter is null ? null : $"caduca el {certificate.NotAfter.ToDateTime().ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}",
    }.Where(p => p is not null));

    [RelayCommand]
    private Task TrustAsync() => owner.TrustAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAsync(this);

    private static string CommonName(string subject)
    {
        foreach (var part in subject.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..];
        }
        return subject;
    }
}
