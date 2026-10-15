namespace PlcMonitor.App.Services;

/// <summary>Diálogos que los ViewModels piden a la vista.</summary>
public interface IDialogs
{
    /// <summary>Pregunta sí/no. Devuelve true si el usuario acepta.</summary>
    Task<bool> ConfirmAsync(string title, string message);
}
