using CommunityToolkit.Mvvm.ComponentModel;

namespace PlcMonitor.App.Localization;

/// <summary>
/// ViewModel con textos en castellano en sus propiedades calculadas: al cambiar de idioma avisa a todas sus
/// propiedades para que la vista vuelva a leerlas.
/// </summary>
public abstract class LocalizedObservableObject : ObservableObject
{
    private static readonly List<WeakReference<LocalizedObservableObject>> Instances = [];
    private static readonly object Gate = new();

    protected LocalizedObservableObject()
    {
        lock (Gate)
            Instances.Add(new WeakReference<LocalizedObservableObject>(this));
    }

    internal static void RefreshAll()
    {
        LocalizedObservableObject[] alive;
        lock (Gate)
        {
            Instances.RemoveAll(r => !r.TryGetTarget(out _));
            alive = [.. Instances.Select(r => r.TryGetTarget(out var vm) ? vm : null).OfType<LocalizedObservableObject>()];
        }
        foreach (var vm in alive)
            vm.OnPropertyChanged(string.Empty);
    }
}
