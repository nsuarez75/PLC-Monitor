// PLC-Monitor: sustituye las llamadas a Console.Write* de la librería original por un
// punto de enganche configurable. Por defecto no se escribe nada.
using System;

namespace S7CommPlusDriver
{
    public static class S7pLog
    {
        /// <summary>Destino de los mensajes de diagnóstico. null = descartar.</summary>
        public static Action<string> Sink { get; set; }

        public static void WriteLine(string message) => Sink?.Invoke(message);

        public static void WriteLine(string format, params object[] args) => Sink?.Invoke(string.Format(format, args));

        public static void WriteLine() => Sink?.Invoke(string.Empty);

        public static void Write(string message) => Sink?.Invoke(message);

        public static void Write(string format, params object[] args) => Sink?.Invoke(string.Format(format, args));
    }
}
