// PLC-Monitor: resolución de las librerías nativas de OpenSSL 3 según el sistema operativo.
// Orden de búsqueda:
//   1. Directorio indicado en la variable de entorno PLCMONITOR_OPENSSL_DIR.
//   2. Directorio de la aplicación y su subcarpeta runtimes/<rid>/native.
//   3. Búsqueda estándar del sistema operativo (PATH, ld.so, ...).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenSsl
{
    public static class OpenSslLibraryResolver
    {
        public const string DirectoryEnvironmentVariable = "PLCMONITOR_OPENSSL_DIR";

        [ModuleInitializer]
        internal static void Register()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }

        /// <summary>Nombres de fichero candidatos para un nombre lógico ("libcrypto" o "libssl").</summary>
        public static IReadOnlyList<string> CandidateFileNames(string logicalName, OSPlatform platform)
        {
            if (logicalName != Native.DLLNAME && logicalName != Native.SSLDLLNAME)
                return Array.Empty<string>();

            if (platform == OSPlatform.Windows)
                return new[] { logicalName + "-3-x64.dll" };
            if (platform == OSPlatform.OSX)
                return new[] { logicalName + ".3.dylib" };
            return new[] { logicalName + ".so.3" };
        }

        public static OSPlatform CurrentPlatform =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : OSPlatform.Linux;

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            var names = CandidateFileNames(libraryName, CurrentPlatform);
            if (names.Count == 0)
                return IntPtr.Zero;

            foreach (var dir in SearchDirectories())
            {
                foreach (var name in names)
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                        return handle;
                }
            }
            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        }

        private static IEnumerable<string> SearchDirectories()
        {
            var env = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
                yield return env;
            var baseDir = AppContext.BaseDirectory;
            yield return baseDir;
            yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        }
    }
}
