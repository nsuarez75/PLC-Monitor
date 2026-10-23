using Avalonia.Controls;
using Avalonia.VisualTree;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Localization;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>El idioma es estado global: estos tests no pueden solaparse con los de interfaz.</summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationGroup;

[Collection("Localization")]
public class LocalizationTests
{
    [Fact]
    public void Options_follow_the_language()
    {
        var choice = new Choice<TimeSpan>("Últimos 15 min", TimeSpan.FromMinutes(15));
        var option = new SettingOption("auto", "Automático");
        try
        {
            Loc.SetLanguage(Loc.Spanish);
            choice.Text.ShouldBe("Últimos 15 min");
            option.Label.ShouldBe("Automático");

            Loc.SetLanguage(Loc.English);
            choice.Text.ShouldBe("Last 15 min");
            option.Label.ShouldBe("Automatic");

            Loc.SetLanguage(Loc.Spanish);
            choice.Text.ShouldBe("Últimos 15 min");
            option.Label.ShouldBe("Automático");
        }
        finally
        {
            Loc.SetLanguage(Loc.Spanish);
        }
    }

    [Fact]
    public void Tag_details_previews_follow_the_language()
    {
        try
        {
            Loc.SetLanguage(Loc.Spanish);
            var details = TagDetails();
            details.TransformPreview.ShouldStartWith("Las transformaciones se crean");
            details.DisplayPreview.ShouldContain("Los decimales se aplican");
            details.ScalingPreview.ShouldBe("El valor se usa tal como lo lee el PLC.");

            Loc.SetLanguage(Loc.English);
            details.TransformPreview.ShouldStartWith("Transforms are created");
            details.DisplayPreview.ShouldContain("Decimals apply");
            details.ScalingPreview.ShouldBe("The value is used as read from the PLC.");
        }
        finally
        {
            Loc.SetLanguage(Loc.Spanish);
        }
    }

    [Fact]
    public Task Tag_details_window_shows_the_options_in_english() => HeadlessApp.RunAsync(() =>
    {
        Loc.SetLanguage(Loc.English);
        try
        {
            var window = new TagDetailsWindow { DataContext = TagDetails() };
            try
            {
                window.Show();
                window.UpdateLayout();

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                texts.ShouldContain("Automatic");
                texts.ShouldContain("Untransformed value");
                texts.ShouldContain("All");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Loc.SetLanguage(Loc.Spanish);
        }
    });

    private static TagDetailsViewModel TagDetails()
    {
        var vm = MainWindowTests.ViewModelWithData();
        var tag = vm.Devices[0].Tags.Single(t => t.Name == "peso");
        var config = new DeviceConfig { Name = "casa1200" };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 250 };
        group.Tags.Add(new TagConfig { Name = "peso", Address = "Viruta_Datos.Viruta.Peso" });
        config.ScanGroups.Add(group);
        return new TagDetailsViewModel(vm, tag, config);
    }

    [Fact]
    public Task Language_button_switches_the_interface_and_is_remembered() => HeadlessApp.RunAsync(() =>
    {
        Loc.SetLanguage(Loc.Spanish);
        try
        {
            var settingsPath = Path.GetTempFileName();
            var vm = MainWindowTests.ViewModelWithData(settingsPath);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            window.UpdateLayout();

            Texts(window).ShouldContain("Conectar");
            Texts(window).ShouldContain("Dispositivos");

            vm.ToggleLanguageCommand.Execute(null);
            window.UpdateLayout();

            Texts(window).ShouldContain("Connect");
            Texts(window).ShouldContain("Devices");
            AppSettings.Load(settingsPath).Language.ShouldBe(Loc.English);

            vm.ToggleLanguageCommand.Execute(null);
            window.UpdateLayout();
            Texts(window).ShouldContain("Conectar");
            AppSettings.Load(settingsPath).Language.ShouldBe(Loc.Spanish);
        }
        finally
        {
            Loc.SetLanguage(Loc.Spanish);
        }

        static string?[] Texts(Window window) =>
            [.. window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];
    });
}
