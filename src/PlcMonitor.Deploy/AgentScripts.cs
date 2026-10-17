using System.Globalization;
using System.Text;

namespace PlcMonitor.Deploy;

/// <summary>Material de seguridad que se deja en el equipo (agent.settings.json y agent.pfx).</summary>
/// <param name="SettingsJson">Contenido de agent.settings.json (puerto, certificado y hash del token).</param>
/// <param name="Pfx">Certificado TLS con su clave.</param>
public sealed record AgentSecurityFiles(string SettingsJson, byte[] Pfx);

/// <summary>Scripts de instalación, desinstalación y estado. Se generan aquí para poder probarlos sin equipo remoto.</summary>
internal static class AgentScripts
{
    public const string LinuxDir = "/opt/plc-monitor/agent";
    public const string LinuxService = "plcmon-agent";
    public const string WindowsService = "PLCMonitorAgent";
    public const string WindowsDirExpression = @"$env:ProgramFiles\PLC-Monitor\agent";

    public static string SettingsJson(string tokenSha256, int port, string pfxPassword) => $$"""
        {
          "Agent": { "Security": { "TokenSha256": "{{tokenSha256}}" } },
          "Kestrel": {
            "Endpoints": {
              "Grpc": {
                "Url": "https://0.0.0.0:{{port.ToString(CultureInfo.InvariantCulture)}}",
                "Certificate": { "Path": "agent.pfx", "Password": "{{pfxPassword}}" }
              }
            }
          }
        }
        """;

    // ---------------- Linux (bash, se ejecuta como root) ----------------

    public static string LinuxInstall(string archive, AgentSecurityFiles? security, int port) => $$"""
        set -e
        D={{LinuxDir}}
        A='{{archive}}'
        id plcmon >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin plcmon
        systemctl stop {{LinuxService}} 2>/dev/null || true
        mkdir -p "$D"
        [ -s "$A" ] || { echo "No llegó el ejecutable del agente" >&2; exit 3; }
        # Se conserva la configuración del equipo (agent.json y la seguridad) y el histórico; lo demás (incluidos los
        # ficheros de las versiones en varios ficheros) se sustituye por el ejecutable único.
        find "$D" -mindepth 1 -maxdepth 1 ! -name agent.json ! -name agent.settings.json ! -name agent.pfx ! -name history -exec rm -rf {} +
        install -m 0755 "$A" "$D/plcmon-agent"
        rm -f "$A"
        {{(security is null ? "" : $"""
        echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(security.SettingsJson))}' | base64 -d > "$D/agent.settings.json"
        echo '{Convert.ToBase64String(security.Pfx)}' | base64 -d > "$D/agent.pfx"
        """)}}
        chown -R plcmon:plcmon "$D"
        chmod 600 "$D"/agent.settings.json "$D"/agent.pfx 2>/dev/null || true
        cat > /etc/systemd/system/{{LinuxService}}.service <<'UNIT'
        [Unit]
        Description=PLC-Monitor Agent
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=notify
        User=plcmon
        Group=plcmon
        WorkingDirectory={{LinuxDir}}
        ExecStart={{LinuxDir}}/plcmon-agent
        # El ejecutable único extrae aquí sus librerías nativas (SQLite): el servicio no tiene carpeta personal.
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR={{LinuxDir}}/.bundle
        Restart=on-failure
        RestartSec=5
        # Margen para cerrar ordenadamente (volcar el historiador): algunos sistemas bajan el valor por defecto a 5 s.
        TimeoutStopSec=15
        NoNewPrivileges=true
        ProtectSystem=strict
        ProtectHome=true
        PrivateTmp=true
        ReadWritePaths={{LinuxDir}}

        [Install]
        WantedBy=multi-user.target
        UNIT
        systemctl daemon-reload
        systemctl enable {{LinuxService}} >/dev/null 2>&1
        systemctl restart {{LinuxService}}
        if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then ufw allow {{port}}/tcp >/dev/null; echo "ufw: abierto el puerto {{port}}/tcp"; fi
        if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --add-port={{port}}/tcp >/dev/null && firewall-cmd --reload >/dev/null; echo "firewalld: abierto el puerto {{port}}/tcp"; fi
        for i in $(seq 1 30); do systemctl is-active --quiet {{LinuxService}} && break; sleep 1; done
        systemctl is-active {{LinuxService}}
        """;

    public static string LinuxHasSecurity => $"test -f {LinuxDir}/agent.settings.json && echo yes || echo no";

    public static string LinuxUninstall(bool keepConfiguration, int port) => $$"""
        set -e
        D={{LinuxDir}}
        systemctl disable --now {{LinuxService}} 2>/dev/null || true
        rm -f /etc/systemd/system/{{LinuxService}}.service
        systemctl daemon-reload
        {{(keepConfiguration ? """[ -f "$D/agent.json" ] && cp "$D/agent.json" /opt/plc-monitor/agent.json.backup && echo "Configuración guardada en /opt/plc-monitor/agent.json.backup" || true""" : "")}}
        rm -rf "$D"
        if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then ufw delete allow {{port}}/tcp >/dev/null 2>&1 || true; fi
        if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --remove-port={{port}}/tcp >/dev/null 2>&1 && firewall-cmd --reload >/dev/null || true; fi
        echo desinstalado
        """;

    public const string LinuxStatus = $$"""
        if [ -x {{LinuxDir}}/plcmon-agent ]; then echo "installed=yes"; else echo "installed=no"; fi
        S=$(systemctl is-active {{LinuxService}} 2>/dev/null)
        # Sin D-Bus (contenedores mínimos) un usuario normal no puede preguntar a systemd: se mira el proceso.
        [ -n "$S" ] || { pidof plcmon-agent >/dev/null 2>&1 && S=active || S=inactive; }
        echo "active=$S"
        echo "since=$(systemctl show {{LinuxService}} -p ActiveEnterTimestamp --value 2>/dev/null)"
        """;

    public static string LinuxLogs(int lines) => $"journalctl -u {LinuxService} -n {lines} --no-pager --output=short-iso";

    // ---------------- Windows (PowerShell, sesión de administrador) ----------------

    public static string WindowsInstall(string archive, AgentSecurityFiles? security, int port) => $$"""
        $ErrorActionPreference = 'Stop'
        $D = "{{WindowsDirExpression}}"
        $A = '{{archive}}'
        $svc = Get-Service -Name {{WindowsService}} -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name {{WindowsService}} -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
        New-Item -ItemType Directory -Force -Path $D | Out-Null
        if (-not (Test-Path $A)) { throw 'No llegó el ejecutable del agente' }
        # Se conserva la configuración del equipo, el histórico y los logs; lo demás se sustituye por el ejecutable único.
        Get-ChildItem -Path $D -Force | Where-Object { $_.Name -notin 'agent.json', 'agent.settings.json', 'agent.pfx', 'history', 'logs' } | Remove-Item -Recurse -Force
        Move-Item -Path $A -Destination (Join-Path $D 'plcmon-agent.exe') -Force
        {{(security is null ? "" : $"""
        [IO.File]::WriteAllBytes((Join-Path $D 'agent.settings.json'), [Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(security.SettingsJson))}'))
        [IO.File]::WriteAllBytes((Join-Path $D 'agent.pfx'), [Convert]::FromBase64String('{Convert.ToBase64String(security.Pfx)}'))
        """)}}
        if (-not $svc) {
          New-Service -Name {{WindowsService}} -DisplayName 'PLC-Monitor Agent' -Description 'Agente de PLC-Monitor' -BinaryPathName ('"' + (Join-Path $D 'plcmon-agent.exe') + '"') -StartupType Automatic | Out-Null
          sc.exe failure {{WindowsService}} reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
        }
        if (-not (Get-NetFirewallRule -DisplayName 'PLC-Monitor Agent' -ErrorAction SilentlyContinue)) {
          New-NetFirewallRule -DisplayName 'PLC-Monitor Agent' -Direction Inbound -Protocol TCP -LocalPort {{port}} -Action Allow | Out-Null
          'Firewall: abierto el puerto {{port}}/tcp'
        }
        Start-Service -Name {{WindowsService}}
        (Get-Service -Name {{WindowsService}}).WaitForStatus('Running', '00:00:30')
        'active'
        """;

    public const string WindowsHasSecurity = $"if (Test-Path \"{WindowsDirExpression}\\agent.settings.json\") {{ 'yes' }} else {{ 'no' }}";

    public static string WindowsUninstall(bool keepConfiguration) => $$"""
        $ErrorActionPreference = 'Stop'
        $D = "{{WindowsDirExpression}}"
        $svc = Get-Service -Name {{WindowsService}} -ErrorAction SilentlyContinue
        if ($svc) {
          if ($svc.Status -ne 'Stopped') { Stop-Service -Name {{WindowsService}} -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
          sc.exe delete {{WindowsService}} | Out-Null
        }
        Get-NetFirewallRule -DisplayName 'PLC-Monitor Agent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        {{(keepConfiguration ? """if (Test-Path "$D\agent.json") { Copy-Item "$D\agent.json" "$env:ProgramData\PLC-Monitor-agent.json.backup" -Force; "Configuración guardada en $env:ProgramData\PLC-Monitor-agent.json.backup" }""" : "")}}
        if (Test-Path $D) { Remove-Item -Recurse -Force $D }
        'desinstalado'
        """;

    public const string WindowsStatus = $$"""
        $D = "{{WindowsDirExpression}}"
        if (Test-Path (Join-Path $D 'plcmon-agent.exe')) { 'installed=yes' } else { 'installed=no' }
        $s = Get-Service -Name {{WindowsService}} -ErrorAction SilentlyContinue
        if ($s -and $s.Status -eq 'Running') { 'active=active' } elseif ($s) { 'active=' + $s.Status.ToString().ToLowerInvariant() } else { 'active=inactive' }
        """;

    /// <summary>Últimas líneas de los ficheros de log del agente (los dos últimos días, por si acaba de cambiar).</summary>
    public static string WindowsLogs(int lines) => $$"""
        $L = Join-Path "{{WindowsDirExpression}}" 'logs'
        $files = @(Get-ChildItem -Path $L -Filter 'plcmon-agent-*.log' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 2)
        if ($files.Count -eq 0) { 'nolog'; return }
        $out = @()
        foreach ($f in $files) { $out += @(Get-Content -LiteralPath $f.FullName -Tail {{lines}} -Encoding UTF8) }
        $out | Select-Object -Last {{lines}}
        """;

    /// <summary>
    /// Envuelve un script para ejecutarlo elevado con UAC (sin tuberías): su salida, línea a línea según se produce, va
    /// a <paramref name="outFile"/>, el error que lo corta a <paramref name="errFile"/> y el código de salida es 0 o 1.
    /// </summary>
    public static string ElevatedWrapper(string script, string outFile, string errFile) => $$"""
        $__lines = [System.Collections.Generic.List[string]]::new()
        $__code = 0
        try {
          & {
        {{script}}
          } *>&1 | ForEach-Object { $__lines.Add("$_") }
        } catch {
          [IO.File]::WriteAllText('{{errFile.Replace("'", "''", StringComparison.Ordinal)}}', ($_ | Out-String))
          $__code = 1
        }
        [IO.File]::WriteAllLines('{{outFile.Replace("'", "''", StringComparison.Ordinal)}}', $__lines)
        exit $__code
        """;

    /// <summary>Comando para ejecutar un script de PowerShell sin problemas de comillas (-EncodedCommand).</summary>
    public static string PowerShell(string script) =>
        "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
