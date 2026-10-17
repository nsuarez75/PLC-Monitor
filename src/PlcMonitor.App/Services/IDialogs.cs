namespace PlcMonitor.App.Services;

/// <summary>Diálogos que los ViewModels piden a la vista.</summary>
public interface IDialogs
{
    /// <summary>Pregunta sí/no. Devuelve true si el usuario acepta.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Pide dónde guardar un fichero. Devuelve la ruta o null si se cancela.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);

    /// <summary>Muestra la ventana de detalle de un tag hasta que se cierra.</summary>
    Task ShowTagDetailsAsync(ViewModels.TagDetailsViewModel details);

    /// <summary>Pide un fichero que abrir. Devuelve la ruta o null si se cancela.</summary>
    Task<string?> OpenFileAsync(string title, string extension);
}
