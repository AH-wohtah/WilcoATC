using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WilcoATC.Input;

/// <summary>
/// Push-to-talk sur un BOUTON DE JOYSTICK/HOTAS. Comme le hook clavier, il fonctionne quel que
/// soit la fenêtre au premier plan (le simulateur garde le focus). On INTERROGE le périphérique
/// via l'API multimédia Windows (winmm <c>joyGetPosEx</c>) sur un thread de fond — aucune
/// dépendance, aucun pilote : c'est la même philosophie « P/Invoke minimal » que le PTT clavier.
///
/// Limite connue de l'API héritée : elle ne voit que les 16 premiers périphériques et les
/// 32 premiers boutons de chacun. Cela couvre l'immense majorité des manches/palonniers pour un
/// simple bouton de radio ; un HOTAS à >32 boutons devra choisir un bouton dans les 32 premiers.
/// </summary>
public sealed class GlobalJoystickButton : IDisposable
{
    // dwFlags : ne demander QUE l'état des boutons (le plus léger).
    private const int JOY_RETURNBUTTONS = 0x00000080;
    private const int JOYERR_NOERROR = 0;

    private readonly Func<(int Device, int Button)> _config;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _down;

    /// <summary>Bouton PTT enfoncé (début de capture micro).</summary>
    public event Action? Pressed;

    /// <summary>Bouton PTT relâché (fin de capture -> transcription).</summary>
    public event Action? Released;

    /// <param name="config">Renvoie (index périphérique, n° bouton 1-based). Bouton &lt; 1 = désactivé.</param>
    public GlobalJoystickButton(Func<(int Device, int Button)> config) => _config = config;

    public void Start()
    {
        if (_thread is not null) return;

        // Raw Input doit tourner AVANT le premier sondage : c'est sa fenêtre message-only qui
        // reçoit les rapports, et elle met un instant à s'établir.
        RawInputJoystick.EnsureStarted();

        _stop = false;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "PTT-Joystick" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread = null;
        _down = false;
    }

    private void PollLoop()
    {
        while (!_stop)
        {
            try
            {
                var (device, button) = _config();
                bool pressed = IsPressed(device, button);
                if (pressed && !_down) { _down = true; SafeRaise(Pressed); }
                else if (!pressed && _down) { _down = false; SafeRaise(Released); }
            }
            catch (Exception ex) { Debug.WriteLine("[WilcoATC/PTT-Joy] " + ex); }

            Thread.Sleep(15);   // ~66 Hz : réactif sans charger le CPU
        }
    }

    /// <summary>
    /// DEUX API, DEUX NUMÉROTATIONS — d'où ce décalage.
    ///
    /// L'index winmm et l'index Raw Input ne désignent PAS le même périphérique : le premier
    /// suit l'ordre du pilote hérité, le second l'ordre des chemins système. Les confondre
    /// ferait pointer un alternat enregistré sur un tout autre appareil, silencieusement.
    ///
    /// Un identifiant au-delà de ce seuil est donc une liaison Raw Input ; en dessous, une
    /// liaison winmm. Les réglages déjà enregistrés restent valides et continuent d'emprunter
    /// l'ancienne voie : corriger un défaut ne doit pas défaire ce qui marchait.
    /// </summary>
    public const int RawInputDeviceBase = 100;

    /// <summary>
    /// RAW INPUT POUR LES NOUVELLES LIAISONS, winmm pour les anciennes.
    ///
    /// L'API héritée ne peut pas voir un bouton au-delà du trente-deuxième : son masque tient
    /// dans un entier de 32 bits. C'est ce qui rendait muets les boutons hauts d'un Honeycomb
    /// Bravo — ses sept inverseurs occupent déjà quatorze positions. Raw Input lit le rapport
    /// HID brut et en gère cent vingt-huit.
    /// </summary>
    private static bool IsPressed(int device, int button)
    {
        if (device < 0 || button < 1) return false;

        if (device >= RawInputDeviceBase)
            return RawInputJoystick.IsPressed(device - RawInputDeviceBase, button);

        if (button > ButtonCount(device)) return false;
        var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
        if (joyGetPosEx(device, ref info) != JOYERR_NOERROR) return false;   // débranché
        return (info.dwButtons & (1u << (button - 1))) != 0;
    }

    /// <summary>
    /// Nombre de boutons RÉELS du périphérique (borné à 32). Sert à ignorer les bits « fantômes »
    /// au-delà — typiquement le pilote hérité « Microsoft PC-joystick driver » qui signale un
    /// « bouton 17 » en permanence alors que le périphérique n'a que quelques boutons.
    /// </summary>
    private static int ButtonCount(int device)
    {
        try
        {
            var caps = new JOYCAPS();
            if (joyGetDevCaps(device, ref caps, Marshal.SizeOf<JOYCAPS>()) == JOYERR_NOERROR && caps.wNumButtons > 0)
                return (int)Math.Min(caps.wNumButtons, 32u);
        }
        catch { /* ignore */ }
        return 0; // caps illisibles -> périphérique fantôme, on n'en lit aucun bouton
    }

    // Applique <paramref name="onButton"/> (n° 1-based) à CHAQUE bouton réel enfoncé du périphérique.
    private static void ForEachPressedButton(int device, Action<int> onButton)
    {
        int max = ButtonCount(device);
        if (max <= 0) return;
        var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
        if (joyGetPosEx(device, ref info) != JOYERR_NOERROR || info.dwButtons == 0) return;
        for (int b = 0; b < max; b++)
            if ((info.dwButtons & (1u << b)) != 0) onButton(b + 1);
    }

    // Boutons DÉJÀ enfoncés au début de la capture : ignorés, pour ne jamais assigner un bouton
    // « collé » (pilote fantôme) et n'accepter qu'un NOUVEL appui de l'utilisateur.
    private static readonly HashSet<(int Device, int Button)> _captureBaseline = new();

    /// <summary>À appeler quand on lance « appuyez sur un bouton » : mémorise l'état de départ.</summary>
    public static void BeginCapture()
    {
        RawInputJoystick.BeginCapture();

        _captureBaseline.Clear();
        int n = joyGetNumDevs();
        for (int id = 0; id < n; id++)
        {
            int dev = id;
            ForEachPressedButton(dev, b => _captureBaseline.Add((dev, b)));
        }
    }

    /// <summary>
    /// Renvoie le premier bouton NOUVELLEMENT enfoncé (pas dans l'état de départ) — c'est le
    /// bouton que l'utilisateur vient réellement de presser. Un bouton fantôme resté enfoncé est
    /// dans le baseline, donc ignoré ; un vrai bouton qu'on tenait par hasard redevient assignable
    /// une fois relâché puis repressé.
    /// </summary>
    public static bool TryReadNewlyPressed(out int device, out int button)
    {
        device = -1; button = 0;

        // RAW INPUT EN PREMIER : il voit tout ce que voit winmm, plus les boutons hauts. Un
        // périphérique reconnu par les deux sera donc enregistré côté Raw Input — la voie qui
        // ne plafonne pas.
        if (RawInputJoystick.TryReadNewlyPressed(out int rawDevice, out button))
        {
            device = rawDevice + RawInputDeviceBase;
            return true;
        }

        // Un bouton du baseline qui n'est plus enfoncé est « libéré » : un futur appui comptera.
        _captureBaseline.RemoveWhere(bl => !IsPressed(bl.Device, bl.Button));

        int n = joyGetNumDevs();
        for (int id = 0; id < n; id++)
        {
            int dev = id, found = 0;
            ForEachPressedButton(dev, b => { if (found == 0 && !_captureBaseline.Contains((dev, b))) found = b; });
            if (found > 0) { device = id; button = found; return true; }
        }
        return false;
    }

    /// <summary>Nom lisible d'un périphérique (produit), ou « Joystick N » à défaut.</summary>
    public static string DeviceName(int device)
    {
        if (device >= RawInputDeviceBase)
            return RawInputJoystick.DeviceName(device - RawInputDeviceBase);

        try
        {
            var caps = new JOYCAPS();
            if (joyGetDevCaps(device, ref caps, Marshal.SizeOf<JOYCAPS>()) == JOYERR_NOERROR
                && !string.IsNullOrWhiteSpace(caps.szPname))
                return caps.szPname.Trim();
        }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/PTT-Joy] devcaps: " + ex); }
        return $"Joystick {device}";
    }

    private static void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/PTT-Joy] " + ex); }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ P/Invoke winmm

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public uint dwButtons;       // masque de bits : bit 0 = bouton 1, bit 1 = bouton 2, …
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct JOYCAPS
    {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;      // nom du produit (seul champ qu'on lit)
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons;
        public uint wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps;
        public uint wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int joyGetDevCaps(int uJoyID, ref JOYCAPS pjc, int cbjc);
}
