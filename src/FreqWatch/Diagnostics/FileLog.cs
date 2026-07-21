using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace FreqWatch.Diagnostics;

/// <summary>
/// Journal de diagnostic sur DISQUE, dans <c>%LOCALAPPDATA%\FreqWatch\logs</c>.
///
/// RAISON D'ÊTRE : quand l'application « ne fait rien » au lancement — DLL manquante,
/// mauvaise architecture, runtime absent — il n'y a par définition aucune fenêtre pour
/// afficher l'erreur. Le fichier est donc le SEUL témoin exploitable.
///
/// Trois choix conséquents :
///  • l'ouverture se fait dans un <see cref="ModuleInitializerAttribute"/>, c'est-à-dire
///    AVANT le point d'entrée : même un plantage au chargement des assemblys est capturé ;
///  • rien n'est mis en tampon (<c>AutoFlush</c>) : un processus qui meurt brutalement
///    laisse quand même ses dernières lignes ;
///  • aucune méthode ne peut lever d'exception. Un journal cassé ne doit jamais empêcher
///    l'application de démarrer.
/// </summary>
public static class FileLog
{
    private const int KeepFiles = 10;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    /// <summary>Dossier des journaux (créé au besoin).</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreqWatch", "logs");

    /// <summary>Chemin du journal de la session en cours, ou null si l'écriture est impossible.</summary>
    public static string? CurrentPath { get; private set; }

    /// <summary>
    /// Installé automatiquement au chargement du module, avant toute autre ligne de code.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            Open();
            WriteHeader();
            HookGlobalHandlers();
        }
        catch { /* un journal défaillant ne doit jamais bloquer le démarrage */ }
    }

    private static void Open()
    {
        string dir = Directory;
        System.IO.Directory.CreateDirectory(dir);
        Prune(dir);

        string name = $"wilcoatc-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        CurrentPath = Path.Combine(dir, name);

        // FileShare.ReadWrite : le fichier reste lisible pendant que l'app tourne.
        var stream = new FileStream(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
    }

    /// <summary>Ne garde que les <see cref="KeepFiles"/> journaux les plus récents.</summary>
    private static void Prune(string dir)
    {
        try
        {
            var old = new DirectoryInfo(dir).GetFiles("wilcoatc-*.log")
                                            .OrderByDescending(f => f.LastWriteTimeUtc)
                                            .Skip(KeepFiles - 1);
            foreach (var f in old) { try { f.Delete(); } catch { } }
        }
        catch { }
    }

    // ------------------------------------------------------------------ écriture

    /// <summary>Écrit une ligne horodatée. Ne lève jamais.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate) _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        }
        catch { }
    }

    /// <summary>Écrit une exception avec son contexte, pile d'appels comprise.</summary>
    public static void Exception(string context, Exception? ex)
    {
        Write($"ERREUR — {context}");
        for (var e = ex; e is not null; e = e.InnerException)
        {
            Write($"    {e.GetType().FullName}: {e.Message}");
            if (!string.IsNullOrWhiteSpace(e.StackTrace)) Write("    " + e.StackTrace.Trim());
        }
    }

    // ------------------------------------------------------------------ contexte de démarrage

    private static void WriteHeader()
    {
        var asm = Assembly.GetExecutingAssembly();

        Write("======================================================================");
        Write($"WilcoATC {asm.GetName().Version}   démarrage {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write("======================================================================");
        Write($"Windows        : {Environment.OSVersion.VersionString}");
        Write($".NET           : {Environment.Version}");
        Write($"Processus      : {(Environment.Is64BitProcess ? "64 bits" : "32 BITS (anormal : SimConnect exige x64)")}");
        Write($"Dossier        : {AppContext.BaseDirectory}");
        Write($"Journal        : {CurrentPath}");
        Write("");

        Write("Dépendances attendues à côté de l'exécutable :");
        foreach (var (label, file) in new[]
                 {
                     ("SimConnect natif   ", "SimConnect.dll"),
                     ("SimConnect managé  ", "Microsoft.FlightSimulator.SimConnect.dll"),
                     ("sherpa-onnx        ", "sherpa-onnx-c-api.dll"),
                     ("onnxruntime        ", "onnxruntime.dll"),
                 })
        {
            string path = Path.Combine(AppContext.BaseDirectory, file);
            bool ok = File.Exists(path);
            string size = ok ? $"{new FileInfo(path).Length / 1024} Ko" : "ABSENT";
            Write($"    [{(ok ? "OK" : "!!")}] {label} {file,-45} {size}");
        }

        string data = Path.Combine(AppContext.BaseDirectory, "data");
        Write($"    [{(System.IO.Directory.Exists(data) ? "OK" : "!!")}] données aéro      dossier data/ " +
              (System.IO.Directory.Exists(data)
                  ? $"({System.IO.Directory.GetFiles(data, "*.csv").Length} fichiers CSV)"
                  : "ABSENT — noms de stations et fréquences indisponibles"));

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreqWatch");
        foreach (var (label, sub) in new[] { ("voix TTS", "voices"), ("modèle STT", "asr") })
        {
            string p = Path.Combine(local, sub);
            int n = System.IO.Directory.Exists(p) ? System.IO.Directory.GetDirectories(p).Length : 0;
            Write($"    [{(n > 0 ? "OK" : "--")}] {label,-18} {p} ({n} installé(s))");
        }
        Write("");
    }

    // ------------------------------------------------------------------ filets de sécurité

    private static void HookGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Exception("exception non gérée (fatale)", e.ExceptionObject as System.Exception);
            Write("L'application va se fermer.");
        };

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            // Seules les erreurs de CHARGEMENT nous intéressent ici : ce sont elles qui
            // traduisent une dépendance manquante, et elles sont souvent avalées plus haut.
            if (e.Exception is DllNotFoundException or BadImageFormatException
                or FileNotFoundException { FileName: not null } or TypeLoadException)
                Write($"chargement — {e.Exception.GetType().Name}: {e.Exception.Message}");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Exception("tâche en arrière-plan", e.Exception);
            e.SetObserved();
        };
    }
}
