using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WilcoATC.Input;

/// <summary>
/// Lecture des boutons de manche/HOTAS via RAW INPUT, l'API HID de Windows.
///
/// POURQUOI ELLE REMPLACE WINMM. L'ancienne implémentation interrogeait <c>joyGetPosEx</c>,
/// héritée des années 1990 : son masque de boutons est un entier de 32 bits, donc elle est
/// PHYSIQUEMENT incapable de voir un bouton au-delà du trente-deuxième. Les manches simples y
/// échappent, mais un Honeycomb Bravo — sept inverseurs, soit quatorze positions, plus le
/// panneau pilote automatique et les crans de manettes — dépasse le seuil : ses boutons hauts
/// étaient invisibles, sans le moindre message d'erreur. C'est le défaut exact remonté.
///
/// Raw Input lit le rapport HID brut : jusqu'à 128 boutons, tous les périphériques, et sans
/// aucune dépendance externe — on reste sur du P/Invoke, comme le reste de la couche entrée.
///
/// FONCTIONNE SANS LE FOCUS, ce qui est la condition d'un alternat utilisable : le drapeau
/// <c>RIDEV_INPUTSINK</c> demande à Windows de nous livrer les rapports même quand le
/// simulateur est au premier plan. D'où la fenêtre message-only et sa boucle dédiée.
/// </summary>
public sealed class RawInputJoystick : IDisposable
{
    /// <summary>Un périphérique vu par Raw Input.</summary>
    public sealed record Device(int Index, string Name, string Path);

    private static readonly object Gate = new();

    /// <summary>Boutons actuellement enfoncés, par (périphérique, bouton 1-based).</summary>
    private static readonly HashSet<(int Device, int Button)> Down = new();

    /// <summary>Poignée système -> index stable. Rempli au fil des rapports reçus.</summary>
    private static readonly Dictionary<IntPtr, int> HandleToIndex = new();
    private static List<Device> _devices = new();

    private static Thread? _pump;
    private static IntPtr _hwnd;
    private static WndProcDelegate? _wndProc;   // référence gardée : sinon le GC la ramasse
    private static volatile bool _running;

    // ------------------------------------------------------------------ cycle de vie

    /// <summary>
    /// Démarre l'écoute. Idempotent : plusieurs appels ne créent qu'une seule fenêtre et
    /// qu'une seule boucle de messages.
    /// </summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_running) return;
            _running = true;

            RefreshDevices();

            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "RawInput-HID" };
            _pump.SetApartmentState(ApartmentState.STA);
            _pump.Start();
        }
    }

    /// <summary>Périphériques détectés, dans un ordre stable (trié par chemin système).</summary>
    public static IReadOnlyList<Device> Devices
    {
        get { lock (Gate) return _devices.ToList(); }
    }

    /// <summary>Ce bouton est-il enfoncé ? Numéro 1-based, comme dans les réglages.</summary>
    public static bool IsPressed(int device, int button)
    {
        if (device < 0 || button < 1) return false;
        lock (Gate) return Down.Contains((device, button));
    }

    /// <summary>Nom lisible d'un périphérique, ou « Joystick N » s'il a disparu.</summary>
    public static string DeviceName(int device)
    {
        lock (Gate)
            return _devices.FirstOrDefault(d => d.Index == device)?.Name ?? $"Joystick {device}";
    }

    // ------------------------------------------------------------------ capture d'un bouton

    /// <summary>
    /// Boutons DÉJÀ enfoncés au moment où l'on demande « appuyez sur un bouton ». Un inverseur
    /// laissé en position haute est enfoncé en permanence : sans cette photographie de départ,
    /// il serait assigné instantanément à la place du bouton voulu — cas très concret sur un
    /// Bravo, dont les sept inverseurs sont des contacts maintenus, pas des poussoirs.
    /// </summary>
    private static readonly HashSet<(int Device, int Button)> Baseline = new();

    public static void BeginCapture()
    {
        EnsureStarted();
        lock (Gate)
        {
            Baseline.Clear();
            foreach (var b in Down) Baseline.Add(b);
        }
    }

    /// <summary>
    /// Premier bouton NOUVELLEMENT enfoncé depuis <see cref="BeginCapture"/>. Un bouton du
    /// départ qu'on relâche redevient assignable : c'est ce qui permet d'affecter malgré tout
    /// un inverseur, en le basculant.
    /// </summary>
    public static bool TryReadNewlyPressed(out int device, out int button)
    {
        device = -1; button = 0;
        lock (Gate)
        {
            Baseline.RemoveWhere(b => !Down.Contains(b));

            foreach (var (d, b) in Down.OrderBy(x => x.Device).ThenBy(x => x.Button))
                if (!Baseline.Contains((d, b))) { device = d; button = b; return true; }
        }
        return false;
    }

    // ------------------------------------------------------------------ énumération

    private static void RefreshDevices()
    {
        var found = new List<(string Path, string Name)>();

        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return;

        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return;

        foreach (var d in list)
        {
            if (d.dwType != RIM_TYPEHID) continue;

            // On ne retient que les manches et manettes : le clavier, la souris et les
            // périphériques HID exotiques n'ont rien à faire dans une liste de boutons.
            var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
            uint infoSize = info.cbSize;
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICEINFO, ref info, ref infoSize) <= 0) continue;
            if (info.hid.usUsagePage != 1 || (info.hid.usUsage != 4 && info.hid.usUsage != 5)) continue;

            string path = DevicePath(d.hDevice);
            if (path.Length == 0) continue;
            found.Add((path, ProductName(path)));
        }

        lock (Gate)
        {
            // Tri par CHEMIN SYSTÈME : il ne dépend pas de l'ordre de branchement, donc
            // l'index d'un périphérique ne bouge pas d'une session à l'autre — sans quoi un
            // alternat enregistré changerait de bouton au prochain démarrage.
            _devices = found.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                            .Select((f, i) => new Device(i, f.Name, f.Path))
                            .ToList();
            HandleToIndex.Clear();
        }
    }

    private static int IndexOf(IntPtr hDevice)
    {
        lock (Gate)
        {
            if (HandleToIndex.TryGetValue(hDevice, out int known)) return known;
        }

        string path = DevicePath(hDevice);
        if (path.Length == 0) return -1;

        lock (Gate)
        {
            var match = _devices.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            int index = match?.Index ?? -1;
            if (index >= 0) HandleToIndex[hDevice] = index;
            return index;
        }
    }

    private static string DevicePath(IntPtr hDevice)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0 || size == 0)
            return "";

        var buffer = new StringBuilder((int)size + 1);
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref size) <= 0) return "";
        return buffer.ToString();
    }

    /// <summary>
    /// Nom commercial du périphérique, lu sur le pilote HID. Le chemin système est illisible
    /// (« \\?\HID#VID_294B... ») : c'est ce nom que l'utilisateur reconnaîtra dans les réglages.
    /// </summary>
    private static string ProductName(string path)
    {
        IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                              OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE) return ShortName(path);
        try
        {
            var buffer = new StringBuilder(256);
            if (HidD_GetProductString(h, buffer, (uint)(buffer.Capacity * 2)) && buffer.Length > 0)
                return buffer.ToString().Trim();
        }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/RawInput] produit : " + ex); }
        finally { CloseHandle(h); }

        return ShortName(path);
    }

    private static string ShortName(string path)
    {
        int i = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && path.Length >= i + 17 ? "HID " + path.Substring(i, 17) : "HID";
    }

    // ------------------------------------------------------------------ boucle de messages

    private static void PumpLoop()
    {
        try
        {
            _wndProc = WndProc;

            var cls = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                lpszClassName = "WilcoATC.RawInput." + Environment.ProcessId,
                hInstance = GetModuleHandle(null),
            };
            if (RegisterClass(ref cls) == 0) { _running = false; return; }

            // Fenêtre MESSAGE-ONLY : invisible, sans barre des tâches, elle n'existe que pour
            // recevoir WM_INPUT.
            _hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0,
                                   HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { _running = false; return; }

            // RIDEV_INPUTSINK : on reçoit les rapports MÊME SANS LE FOCUS. C'est toute la
            // raison d'être de ce composant — le simulateur garde le focus, pas nous.
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 4, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
                new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                Debug.WriteLine("[WilcoATC/RawInput] RegisterRawInputDevices a échoué");
                _running = false;
                return;
            }

            while (_running && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WilcoATC/RawInput] boucle : " + ex);
            _running = false;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleInput(lParam); }
            catch (Exception ex) { Debug.WriteLine("[WilcoATC/RawInput] rapport : " + ex); }
        }
        else if (msg == WM_INPUT_DEVICE_CHANGE)
        {
            RefreshDevices();   // branchement/débranchement à chaud
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void HandleInput(IntPtr hRawInput)
    {
        uint size = 0;
        int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize) != 0 || size == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)headerSize) != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return;

            int device = IndexOf(header.hDevice);
            if (device < 0) return;

            // Le corps HID suit l'en-tête : nombre de rapports, taille de chacun, puis les
            // octets bruts. On lit CHAQUE rapport du paquet — un périphérique bavard peut en
            // grouper plusieurs, et n'en traiter qu'un ferait manquer des relâchements.
            IntPtr hidPart = buffer + headerSize;
            int dwSizeHid = Marshal.ReadInt32(hidPart);
            int dwCount = Marshal.ReadInt32(hidPart + 4);
            IntPtr raw = hidPart + 8;

            byte[] preparsed = GetPreparsed(header.hDevice);
            if (preparsed.Length == 0) return;

            for (int i = 0; i < dwCount; i++)
                ReadButtons(device, raw + i * dwSizeHid, dwSizeHid, preparsed);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Données « preparsed » du périphérique, mises en cache : leur lecture coûte cher.</summary>
    private static readonly Dictionary<IntPtr, byte[]> PreparsedCache = new();

    private static byte[] GetPreparsed(IntPtr hDevice)
    {
        lock (Gate)
            if (PreparsedCache.TryGetValue(hDevice, out var cached)) return cached;

        uint size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return Array.Empty<byte>();

        var data = new byte[size];
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, data, ref size) <= 0)
            return Array.Empty<byte>();

        lock (Gate) PreparsedCache[hDevice] = data;
        return data;
    }

    private static void ReadButtons(int device, IntPtr report, int reportLength, byte[] preparsed)
    {
        var caps = new HIDP_CAPS();
        if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return;
        if (caps.NumberInputButtonCaps == 0) return;

        var buttonCaps = new HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
        ushort capsLength = caps.NumberInputButtonCaps;
        if (HidP_GetButtonCaps(HidP_Input, buttonCaps, ref capsLength, preparsed) != HIDP_STATUS_SUCCESS)
            return;

        var pressed = new HashSet<int>();

        foreach (var bc in buttonCaps)
        {
            if (bc.UsagePage != 9) continue;   // page « Button »

            int max = HidP_MaxUsageListLength(HidP_Input, bc.UsagePage, preparsed);
            if (max <= 0) continue;

            var usages = new ushort[max];
            uint length = (uint)max;
            if (HidP_GetUsages(HidP_Input, bc.UsagePage, 0, usages, ref length, preparsed,
                               report, (uint)reportLength) != HIDP_STATUS_SUCCESS)
                continue;

            // L'usage HID est déjà 1-based sur la page « Button » : usage 1 = bouton 1.
            for (int i = 0; i < length; i++) pressed.Add(usages[i]);
        }

        lock (Gate)
        {
            Down.RemoveWhere(b => b.Device == device && !pressed.Contains(b.Button));
            foreach (int b in pressed) Down.Add((device, b));
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ P/Invoke

    private const int RIM_TYPEHID = 2;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint WM_QUIT = 0x0012;
    private const int HidP_Input = 0;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId, dwProductId, dwVersionNumber;
        public ushort usUsagePage, usUsage;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength,
                      FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
                      NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps,
                      NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps,
                      NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public uint[] Reserved;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax,
                      DataIndexMin, DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint command, StringBuilder data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint command, [Out] byte[] data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint command, ref RID_DEVICE_INFO data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS cls);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
                                                int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
                                            uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, uint length);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(byte[] preparsed, ref HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] caps,
                                                 ref ushort length, byte[] preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
                                             [In, Out] ushort[] usages, ref uint length,
                                             byte[] preparsed, IntPtr report, uint reportLength);
}
