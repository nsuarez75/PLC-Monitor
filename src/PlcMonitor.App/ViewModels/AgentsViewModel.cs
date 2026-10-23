using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Localization;
using PlcMonitor.App.Services;
using PlcMonitor.Deploy;
using Renci.SshNet.Common;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Gestión de agentes: perfiles (cómo conectar) y, para este equipo o los accesibles por SSH, instalar, actualizar,
/// consultar, ver el log y desinstalar el agente. Las contraseñas solo viven mientras dura la operación.
/// </summary>
public sealed partial class AgentsViewModel : LocalizedObservableObject
{
    private readonly MainViewModel _main;
    private readonly Func<SshTarget, CancellationToken, Task<ISshSession>> _sshFactory;
    private readonly Func<ISshSession> _localFactory;

    public AgentsViewModel(MainViewModel main, Func<SshTarget, CancellationToken, Task<ISshSession>> sshFactory, Func<ISshSession> localFactory)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _sshFactory = sshFactory;
        _localFactory = localFactory;
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
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand), nameof(RenewCredentialsCommand))]
    public partial string SshHost { get; set; } = "";

    [ObservableProperty]
    public partial int SshPort { get; set; } = 22;

    /// <summary>Gestionar el agente de este equipo sin SSH.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManaged), nameof(IsSsh), nameof(ShowSudoPassword), nameof(MachineHelp), nameof(SudoPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand), nameof(RenewCredentialsCommand))]
    public partial bool LocalMachine { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand), nameof(RenewCredentialsCommand))]
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

    /// <summary>Al generar credenciales (instalación nueva, regenerar o renovar), crear también un token de solo lectura.</summary>
    [ObservableProperty]
    public partial bool CreateReadOnlyToken { get; set; }

    [ObservableProperty]
    public partial bool KeepConfiguration { get; set; } = true;

    /// <summary>Carpeta con los paquetes del agente; vacía = la de la release (agents/).</summary>
    [ObservableProperty]
    public partial string PackageFolder { get; set; } = "";

    [ObservableProperty]
    public partial string Info { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(StatusCommand), nameof(LogsCommand), nameof(SaveCommand), nameof(RenewCredentialsCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsManaged => LocalMachine || !string.IsNullOrWhiteSpace(SshHost);

    public bool IsSsh => !LocalMachine;

    /// <summary>En Windows, este equipo pide permiso con UAC: no hace falta contraseña.</summary>
    public bool ShowSudoPassword => !LocalMachine || !OperatingSystem.IsWindows();

    public string MachineHelp => !LocalMachine
        ? Loc.T("Para instalar, actualizar o desinstalar el agente. Linux (con sudo) o Windows con OpenSSH (usuario administrador).")
        : OperatingSystem.IsWindows()
            ? Loc.T("El agente se instala en este equipo como servicio de Windows. Windows pedirá permiso de administrador (UAC).")
            : Loc.T("El agente se instala en este equipo como servicio systemd. Si no indicas la contraseña de sudo, se pedirá con la ventana del sistema.");

    public string SudoPlaceholder => Loc.T(LocalMachine ? "vacía: la pide el sistema" : "solo si es distinta");

    /// <summary>Equipo de la operación, para los mensajes.</summary>
    private string TargetName => LocalMachine ? Loc.T("este equipo") : SshHost.Trim();

    partial void OnPackageFolderChanged(string value) => _main.AgentPackagesFolder = value;

    partial void OnSelectedChanged(AgentProfile? value)
    {
        Log.Clear();
        SshPassword = "";
        SudoPassword = "";
        RegenerateCredentials = false;
        CreateReadOnlyToken = false;
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
        LocalMachine = value?.LocalMachine ?? false;
        SshPort = value?.SshPort ?? 22;
        SshUser = value?.SshUser ?? "";
        SshKeyFile = value?.SshKeyFile ?? "";
        AgentPort = value?.AgentPort ?? 5311;
        Info = value is null ? "" : Describe(value);
    }

    [RelayCommand]
    private void New()
    {
        var profile = new AgentProfile { Name = Loc.T("Nuevo agente"), Url = "", SshUser = Environment.UserName };
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
        _main.Report(Loc.F("Agente «{0}» guardado", profile.Name));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } profile)
            return;
        if (!await _main.Dialogs.ConfirmAsync(Loc.T("Quitar agente"), Loc.F("¿Quitar «{0}» de la lista?\nNo se desinstala nada del equipo.", profile.Name)))
            return;
        Profiles.Remove(profile);
        if (_main.SelectedProfile == profile)
            _main.SelectedProfile = Profiles.FirstOrDefault();
        _main.SaveProfiles();
        Selected = Profiles.FirstOrDefault();
    }

    private bool CanUseSsh() => !IsBusy && (LocalMachine || (!string.IsNullOrWhiteSpace(SshHost) && !string.IsNullOrWhiteSpace(SshUser)));

    /// <summary>Instala o actualiza el agente en el equipo y guarda en el perfil la URL, el token y la huella.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task InstallAsync() => RunSshAsync(Loc.T("Instalando"), async (deployer, profile, ct) =>
    {
        var folders = string.IsNullOrWhiteSpace(PackageFolder) ? AgentPackages.DefaultFolders().ToArray() : [PackageFolder.Trim()];
        var result = await deployer.InstallAsync(
            LocalMachine ? "127.0.0.1" : SshHost.Trim(),
            new InstallOptions(folders, AgentPort, RegenerateCredentials, NullIfEmpty(SudoPassword), CreateReadOnlyToken),
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
            AddLog(Loc.T("⚠ El equipo ya tenía credenciales y este perfil no las conoce: marca «Regenerar credenciales» y vuelve a instalar."));
        }
        profile.Platform = result.Platform.Description;
        profile.InstalledAt = DateTimeOffset.Now;
        RegenerateCredentials = false;
        ShowReadOnlyToken(result.ReadOnlyToken);
        _main.Report(Loc.F("Agente instalado en {0} ({1})", TargetName, result.Url));
    });

    /// <summary>
    /// Token y certificado nuevos sin reinstalar el agente: los anteriores dejan de valer (para revocar un token que se ha
    /// dado a alguien). El token de lectura se crea si está marcado; si no, el que hubiera queda revocado.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private async Task RenewCredentialsAsync()
    {
        if (!await _main.Dialogs.ConfirmAsync(Loc.T("Renovar credenciales"),
                Loc.F("¿Renovar el token y el certificado del agente de {0}?\nLos anteriores dejan de valer: los demás equipos y la CLI que los usen perderán el acceso.", TargetName)
                + (CreateReadOnlyToken ? Loc.T("\nSe crea un token de solo lectura nuevo.") : Loc.T("\nSi había token de solo lectura, también deja de valer."))))
            return;
        await RunSshAsync(Loc.T("Renovando credenciales"), async (deployer, profile, ct) =>
        {
            var result = await deployer.RenewCredentialsAsync(
                LocalMachine ? "127.0.0.1" : SshHost.Trim(),
                new RenewOptions(AgentPort, CreateReadOnlyToken, NullIfEmpty(SudoPassword)),
                NullIfEmpty(SshPassword),
                ct);
            profile.Url = result.Url;
            profile.Token = result.Token;
            profile.CertificateThumbprint = result.CertificateThumbprint;
            ShowReadOnlyToken(result.ReadOnlyToken);
            CreateReadOnlyToken = false;
            _main.Report(Loc.F("Credenciales del agente de {0} renovadas", TargetName));
        });
    }

    /// <summary>El token de lectura no se guarda en la app (ella usa el completo): se enseña una vez para copiarlo.</summary>
    private void ShowReadOnlyToken(string? token)
    {
        if (token is null)
            return;
        AddLog(Loc.T("Token de solo lectura (cópialo ahora: la app no lo guarda):"));
        AddLog(token);
    }

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task StatusAsync() => RunSshAsync(Loc.T("Consultando"), async (deployer, profile, ct) =>
    {
        var status = await deployer.GetStatusAsync(ct);
        profile.Platform = status.Platform.Description;
        AddLog(!status.Installed ? Loc.T("El agente no está instalado en este equipo.")
            : status.Running ? Loc.F("El agente está en marcha{0}.", status.Since is null ? "" : Loc.F(" desde {0}", status.Since))
            : Loc.F("El agente está instalado pero no en marcha (estado: {0}).", status.ServiceState));
    });

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private Task LogsAsync() => RunSshAsync(Loc.T("Leyendo el log"), async (deployer, _, ct) =>
    {
        var text = await deployer.GetLogsAsync(NullIfEmpty(SshPassword), NullIfEmpty(SudoPassword), 200, ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            AddLog(line.TrimEnd('\r'));
    });

    [RelayCommand(CanExecute = nameof(CanUseSsh))]
    private async Task UninstallAsync()
    {
        if (!await _main.Dialogs.ConfirmAsync(Loc.T("Desinstalar agente"),
                Loc.F("¿Desinstalar el agente de {0}?\nSe para el servicio y se borran sus ficheros{1}.", TargetName, KeepConfiguration ? Loc.T(" (se guarda una copia de agent.json)") : "")))
            return;
        await RunSshAsync(Loc.T("Desinstalando"), async (deployer, profile, ct) =>
        {
            await deployer.UninstallAsync(KeepConfiguration, NullIfEmpty(SshPassword), NullIfEmpty(SudoPassword), AgentPort, ct);
            profile.InstalledAt = null;
            profile.Token = null;
            profile.CertificateThumbprint = null;
            _main.Report(Loc.F("Agente desinstalado de {0}", TargetName));
        });
    }

    /// <summary>Abre la sesión (SSH o este equipo), ejecuta la operación y guarda el perfil. Las contraseñas se borran al terminar.</summary>
    private async Task RunSshAsync(string action, Func<AgentDeployer, AgentProfile, CancellationToken, Task> operation)
    {
        if (Selected is not { } profile)
            return;
        Apply(profile);
        IsBusy = true;
        Log.Clear();
        AddLog(LocalMachine ? Loc.F("{0}: en este equipo (sin SSH)…", action) : Loc.F("{0}: conectando por SSH con {1}@{2}:{3}…", action, SshUser, SshHost, SshPort));
        try
        {
            using var session = LocalMachine ? _localFactory() : await OpenSshAsync(profile);
            var deployer = new AgentDeployer(session, new InlineProgress(AddLog));
            await operation(deployer, profile, CancellationToken.None);
            AddLog(Loc.T("✔ Hecho"));
        }
        catch (Exception ex) when (ex is DeployException or SshException or SshHostKeyMismatchException or IOException
                                       or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            AddLog("✖ " + Explain(ex));
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

    private async Task<ISshSession> OpenSshAsync(AgentProfile profile)
    {
        var target = new SshTarget(SshHost.Trim(), SshPort, SshUser.Trim(), NullIfEmpty(SshPassword), NullIfEmpty(SshKeyFile), ExpectedHostKey: profile.SshHostKey);
        var session = await _sshFactory(target, CancellationToken.None);
        if (profile.SshHostKey is null)
            AddLog(Loc.F("Huella SSH del equipo (queda fijada): SHA256:{0}", session.HostKeyFingerprint));
        profile.SshHostKey = session.HostKeyFingerprint;
        return session;
    }

    private static string Explain(Exception ex) => ex switch
    {
        SshAuthenticationException => Loc.T("El equipo rechaza el usuario, la clave o la contraseña SSH."),
        System.Net.Sockets.SocketException => Loc.F("No se puede abrir la conexión SSH: {0}", ex.Message),
        SshOperationTimeoutException or TimeoutException => Loc.T("El equipo no responde por SSH (tiempo agotado)."),
        _ => ex.Message,
    };

    private void Apply(AgentProfile profile)
    {
        profile.Name = Name.Trim();
        profile.Url = Url.Trim();
        profile.Token = NullIfEmpty(Token);
        profile.CertificateThumbprint = NullIfEmpty(Thumbprint);
        profile.SshHost = NullIfEmpty(SshHost);
        profile.LocalMachine = LocalMachine;
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
        if (p.LocalMachine)
            parts.Add(Loc.T("Este equipo"));
        parts.Add(Loc.T(p.CertificateThumbprint is null ? "Sin TLS" : "TLS con certificado fijado"));
        parts.Add(Loc.T(string.IsNullOrEmpty(p.Token) ? "sin token" : "con token"));
        if (p.Platform is not null)
            parts.Add(p.Platform);
        if (p.InstalledAt is { } at)
            parts.Add(Loc.F("instalado el {0}", at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)));
        return string.Join(" · ", parts);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Entrega el progreso en el momento y en orden (Progress&lt;T&gt; lo encola en otro hilo si no hay contexto).</summary>
internal sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
