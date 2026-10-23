using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace PlcMonitor.App.Localization;

/// <summary>Marca un texto de XAML para que siga el idioma elegido: <c>Text="{loc:T 'Conectar'}"</c>.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedValue.Value))
        {
            Source = Loc.Value(Text),
            Mode = BindingMode.OneWay,
        };
}
