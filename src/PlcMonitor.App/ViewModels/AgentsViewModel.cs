using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Services;
using PlcMonitor.Deploy;
using Renci.SshNet.Common;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Gestión de agentes: perfiles (cómo conectar) y, para los equipos accesibles por SSH, instalar, actualizar,
/// consultar, ver el log y desinstalar el agente. Las contraseñas SSH solo viven mientras dura la operación.
/// </summary>
public sealed partial class AgentsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Func<SshTarget, CancellationToken, Task<ISshSession>> _sshFactory;

    public AgentsViewModel(MainViewModel main, Func<SshTarget, CancellationToken, Task<ISshSession>> sshFactory)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _sshFactory = sshFactory;
        PackageFolder = main.AgentPackagesFolder;
        Selected = main.SelectedProfile ?? main.Profiles.FirstOrDefault();
    }

    public ObservableCollection<AgentProfile> Profiles => _main.Profiles;

    /// <summary>Mensajes de la última operación (progreso de la instalación, estado, log).</summary>
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial AgentProfile? Selected { get; set; }

    // Campos del formulario (copia del perfil seleccionado; se aplican con Guardar o al instalar).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial string Thumbprint { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManaged))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand))]
    public partial string SshHost { get; set; } = "";

    [ObservableProperty]
    public partial int SshPort { get; set; } = 22;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand))]
    public partial string SshUser { get; set; } = "";

    [ObservableProperty]
    public partial string SshKeyFile { get; set; } = "";

    [ObservableProperty]
    public partial int AgentPort { get; set; } = 5311;

    /// <summary>Contraseña SSH (y de sudo). No se guarda.</summary>
    [ObservableProperty]
    public partial string SshPassword { get; set; } = "";

    /// <summary>Contraseña de sudo si es distinta. No se guarda.</summary>
    [ObservableProperty]
    public partial string SudoPassword { get; set; } = "";

    [ObservableProperty]
    public partial bool RegenerateCredentials { get; set; }

    [ObservableProperty]
    public partial bool KeepConfiguration { get; set; } = true;

    /// <summary>Carpeta con los paquetes del agente; vacía = la de la release (agents/).</summary>
    [ObservableProperty]
    public partial string PackageFolder { get; set; } = "";

    [ObservableProperty]
    public partial string Info { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand), nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsManaged => !string.IsNullOrWhiteSpace(SshHost);

    partial void OnPackageFolderChanged(string value) => _main.AgentPackagesFolder = value;

    partial void OnSelectedChanged(AgentProfile? value)
    {
        Log.Clear();
        SshPassword = "";
        SudoPassword = "";
        RegenerateCredentials = false;
        LoadForm(value);
    }

    /// <summary>Carga el formulario desde el perfil (sin tocar el log ni las contraseñas).</summary>
    private void LoadForm(AgentProfile? value)
    {
        Name = value?.Name ?? "";
        Url = value?.Url ?? "";
        Token = value?.Token ?? "";
        Thumbprint = value?.CertificateThumbprint ?? "";
        SshHost = value?.SshHost ?? "";
        SshPort = value?.SshPort ?? 22;
        SshUser = value?.SshUser ?? "";
        SshKeyFile = value?.SshKeyFile ?? "";
        AgentPort = value?.AgentPort ?? 5311;
        Info = value is null ? "" : Describe(value);
    }

    [RelayCommand]
    private void New()
    {
        var profile = new AgentProfile { Name = "Nuevo agente", Url = "", SshUser = Environment.UserName };
        Profiles.Add(profile);
        Selected = profile;
    }

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Selected is not { } profile)
            return;
        Apply(profile);
        _main.SaveProfiles();
        RefreshSelected();
        _main.Report($"Agente «{profile.Name}» guardado");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } profile)
            return;
        if (!await _main.Dialogs.ConfirmAsync("Quitar agente", $"¿Quitar «{profile.Name}» de la lista?\nNo se desinstala nada del equipo."))
            return;
        Profiles.Remove(profile);
        if (_main.SelectedProfile == profile)
            _main.SelectedProfile = Profiles.FirstOrDefault();
        _main.SaveProfiles();
        Selected = Profiles.FirstOrDefault();
    }

    private bool CanUseSsh() => !IsBusy && !string.IsNullOrWhiteSpace(SshHost) && !string.IsNullOrWhiteSpace(SshUser);

    /// <summary>Instala o actualiza el agente en el equipo y guarda en el perfil la URL, el token y la huella.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task InstallAsync() => RunSshAsync("Instalando", async (deployer, profile, ct) =>
    {
        var folders = string.IsNullOrWhiteSpace(PackageFolder) ? AgentPackages.DefaultFolders().ToArray() : [PackageFolder.Trim()];
        var result = await deployer.InstallAsync(
            SshHost.Trim(),
            new InstallOptions(folders, AgentPort, RegenerateCredentials, NullIfEmpty(SudoPassword)),
            NullIfEmpty(SshPassword),
            ct);
        profile.Url = result.Url;
        if (result.Token is not null)
        {
            profile.Token = result.Token;
            profile.CertificateThumbprint = result.CertificateThumbprint;
        }
        else if (string.IsNullOrEmpty(profile.Token))
        {
            AddLog("⚠ El equipo ya tenía credenciales y este perfil no las conoce: marca «Regenerar credenciales» y vuelve a instalar.");
        }
        profile.Platform = result.Platform.Description;
        profile.InstalledAt = DateTimeOffset.Now;
        RegenerateCredentials = false;
        _main.Report($"Agente instalado en {SshHost} ({result.Url})");
    });

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task StatusAsync() => RunSshAsync("Consultando", async (deployer, profile, ct) =>
    {
        var status = await deployer.GetStatusAsync(ct);
        profile.Platform = status.Platform.Description;
        AddLog(!status.Installed ? "El agente no está instalado en este equipo."
            : status.Running ? $"El agente está en marcha{(status.Since is null ? "" : $" desde {status.Since}")}."
            : $"El agente está instalado pero no en marcha (estado: {status.ServiceState}).");
    });

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task LogsAsync() => RunSshAsync("Leyendo el log", async (deployer, _, ct) =>
    {
        var text = await deployer.GetLogsAsync(NullIfEmpty(SshPassword), NullIfEmpty(SudoPassword), 200, ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            AddLog(line.TrimEnd('\r'));
    });

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private async Task UninstallAsync()
    {
        if (!await _main.Dialogs.ConfirmAsync("Desinstalar agente",
                $"¿Desinstalar el agente de {SshHost}?\nSe para el servicio y se borran sus ficheros{(KeepConfiguration ? " (se guarda una copia de agent.json)" : "")}."))
            return;
        await RunSshAsync("Desinstalando", async (deployer, profile, ct) =>
        {
            await deployer.UninstallAsync(KeepConfiguration, NullIfEmpty(SshPassword), NullIfEmpty(SudoPassword), AgentPort, ct);
            profile.InstalledAt = null;
            profile.Token = null;
            profile.CertificateThumbprint = null;
            _main.Report($"Agente desinstalado de {SshHost}");
        });
    }

    /// <summary>Abre la sesión SSH, ejecuta la operación y guarda el perfil. Las contraseñas se borran al terminar.</summary>
    private async Task RunSshAsync(string action, Func<AgentDeployer, AgentProfile, CancellationToken, Task> operation)
    {
        if (Selected is not { } profile)
            return;
        Apply(profile);
        IsBusy = true;
        Log.Clear();
        AddLog($"{action}: conectando por SSH con {SshUser}@{SshHost}:{SshPort}…");
        try
        {
            var target = new SshTarget(SshHost.Trim(), SshPort, SshUser.Trim(), NullIfEmpty(SshPassword), NullIfEmpty(SshKeyFile), ExpectedHostKey: profile.SshHostKey);
            using var session = await _sshFactory(target, CancellationToken.None);
            if (profile.SshHostKey is null)
                AddLog($"Huella SSH del equipo (queda fijada): SHA256:{session.HostKeyFingerprint}");
            profile.SshHostKey = session.HostKeyFingerprint;
            var deployer = new AgentDeployer(session, new InlineProgress(AddLog));
            await operation(deployer, profile, CancellationToken.None);
            AddLog("✔ Hecho");
        }
        catch (Exception ex) when (ex is DeployException or SshException or SshHostKeyMismatchException or IOException
                                       or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            AddLog($"✖ {Explain(ex)}");
            _main.Report(Explain(ex), isError: true);
        }
        finally
        {
            SshPassword = "";
            SudoPassword = "";
            IsBusy = false;
            _main.SaveProfiles();
            RefreshSelected();
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        SshAuthenticationException => "El equipo rechaza el usuario, la clave o la contraseña SSH.",
        System.Net.Sockets.SocketException => $"No se puede abrir la conexión SSH: {ex.Message}",
        SshOperationTimeoutException or TimeoutException => "El equipo no responde por SSH (tiempo agotado).",
        _ => ex.Message,
    };

    private void Apply(AgentProfile profile)
    {
        profile.Name = Name.Trim();
        profile.Url = Url.Trim();
        profile.Token = NullIfEmpty(Token);
        profile.CertificateThumbprint = NullIfEmpty(Thumbprint);
        profile.SshHost = NullIfEmpty(SshHost);
        profile.SshPort = SshPort;
        profile.SshUser = NullIfEmpty(SshUser);
        profile.SshKeyFile = NullIfEmpty(SshKeyFile);
        profile.AgentPort = AgentPort;
    }

    /// <summary>Refresca el formulario con lo que hay en el perfil (tras guardar o instalar). El perfil es observable:
    /// las listas se actualizan solas y no se pierde la selección.</summary>
    private void RefreshSelected() => LoadForm(Selected);

    private void AddLog(string line) => Log.Add(line);

    private static string Describe(AgentProfile p)
    {
        var parts = new List<string>();
        parts.Add(p.CertificateThumbprint is null ? "Sin TLS" : "TLS con certificado fijado");
        parts.Add(string.IsNullOrEmpty(p.Token) ? "sin token" : "con token");
        if (p.Platform is not null)
            parts.Add(p.Platform);
        if (p.InstalledAt is { } at)
            parts.Add($"instalado el {at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Entrega el progreso en el momento y en orden (Progress&lt;T&gt; lo encola en otro hilo si no hay contexto).</summary>
internal sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
