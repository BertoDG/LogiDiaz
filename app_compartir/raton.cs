// raton.cs - Codigo nativo del servidor del raton (HID++, teclado, ventanas flotantes, acciones).
// Lo compila y carga raton.ps1; el resultado se guarda como DLL en %LOCALAPPDATA%\RatonMX
// y solo se vuelve a compilar si este fichero cambia. Fichero ASCII: los acentos van escapados.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace LogiRaton {

  public static class Native {
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS {
      public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
      public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                    NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                    NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr devInfo);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int n, out int written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
  }

  // ---- Teclado: traduccion de "Ctrl+Shift+T" y envio de pulsaciones ----
  public static class Keys {
    const int EXT = 0x100;                                   // marca de tecla extendida
    public const int MICRO = 0x1000;                         // tecla propia: silenciar / activar el microfono (no es una tecla de Windows)

    static readonly Dictionary<string, int> MAP = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
      {"CTRL",0x11},{"CONTROL",0x11},{"SHIFT",0x10},{"MAYUS",0x10},{"ALT",0x12},{"WIN",0x5B|EXT},
      {"ENTER",0x0D},{"INTRO",0x0D},{"ESC",0x1B},{"TAB",0x09},{"SPACE",0x20},{"ESPACIO",0x20},
      {"BACKSPACE",0x08},{"RETROCESO",0x08},{"DELETE",0x2E|EXT},{"SUPR",0x2E|EXT},{"INSERT",0x2D|EXT},
      {"HOME",0x24|EXT},{"INICIO",0x24|EXT},{"END",0x23|EXT},{"FIN",0x23|EXT},
      {"PAGEUP",0x21|EXT},{"REPAG",0x21|EXT},{"PAGEDOWN",0x22|EXT},{"AVPAG",0x22|EXT},
      {"LEFT",0x25|EXT},{"UP",0x26|EXT},{"RIGHT",0x27|EXT},{"DOWN",0x28|EXT},
      {"PRINTSCREEN",0x2C|EXT},{"IMPRPANT",0x2C|EXT},
      {"VOLUMEUP",0xAF},{"VOLUMEDOWN",0xAE},{"MUTE",0xAD},{"PLAYPAUSE",0xB3},{"NEXT",0xB0},{"PREV",0xB1},{"STOP",0xB2},
      {"MICMUTE",MICRO},{"MICROFONO",MICRO},
      {"PAUSE",0x13},{"PAUSA",0x13},{"CAPSLOCK",0x14},{"BLOQMAYUS",0x14},{"NUMLOCK",0x90|EXT},{"SCROLLLOCK",0x91},
      {"MENU",0x5D|EXT},{"SLEEP",0x5F},
      {"BROWSERBACK",0xA6},{"BROWSERFORWARD",0xA7},{"BROWSERREFRESH",0xA8},{"BROWSERSTOP",0xA9},
      {"BROWSERSEARCH",0xAA},{"BROWSERFAVORITES",0xAB},{"BROWSERHOME",0xAC},
      {"MAIL",0xB4},{"MEDIASELECT",0xB5},{"MYCOMPUTER",0xB6},{"CALCULATOR",0xB7},{"CALCULADORA",0xB7},
      {"COMA",0xBC},{"PUNTO",0xBE},{"MENOS",0xBD},{"MAS",0xBB},
      {"NUMMULTIPLY",0x6A},{"NUMADD",0x6B},{"NUMSUBTRACT",0x6D},{"NUMDECIMAL",0x6E},{"NUMDIVIDE",0x6F|EXT}
    };

    public static int[] ParseCombo(string s) {
      if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException("Atajo vacio");
      List<int> res = new List<int>();
      foreach (string raw in s.Split('+')) {
        string t = raw.Trim().ToUpperInvariant();
        int vk;
        if (MAP.TryGetValue(t, out vk)) res.Add(vk);
        else if (t.Length == 1 && ((t[0] >= 'A' && t[0] <= 'Z') || (t[0] >= '0' && t[0] <= '9'))) res.Add(t[0]);
        else if (t.Length >= 2 && t[0] == 'F' && int.TryParse(t.Substring(1), out vk) && vk >= 1 && vk <= 24) res.Add(0x70 + vk - 1);
        else if (t.Length == 4 && t.StartsWith("NUM") && t[3] >= '0' && t[3] <= '9') res.Add(0x60 + (t[3] - '0'));
        else if (t.StartsWith("0X") && int.TryParse(t.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out vk) && vk > 0 && vk < 256) res.Add(vk);
        else throw new ArgumentException("Tecla desconocida: '" + raw.Trim() + "' en '" + s + "'");
      }
      return res.ToArray();
    }

    public static void SendCombo(int[] keys) {
      // Silenciar el microfono no es una pulsacion: lo hace el propio servidor (el resto de teclas se ignoran)
      foreach (int k in keys) if (k == MICRO) { Microfono.Alternar(); return; }
      foreach (int k in keys) SendKey(k, false);
      for (int i = keys.Length - 1; i >= 0; i--) SendKey(keys[i], true);
    }

    // Envia la tecla con su codigo fisico: Imprimir pantalla y las multimedia lo necesitan.
    static void SendKey(int k, bool up) {
      byte vk = (byte)(k & 0xFF);
      bool ext = (k & EXT) != 0;
      uint sc;
      if (vk == 0x2C) { sc = 0x37; ext = true; }            // Imprimir pantalla: E0 37
      else {
        uint s = Native.MapVirtualKey(vk, 4);               // MAPVK_VK_TO_VSC_EX
        sc = s & 0xFF;
        if ((s & 0xFF00) == 0xE000 || (s & 0xFF00) == 0xE100) ext = true;
      }
      Native.keybd_event(vk, (byte)sc, (uint)((ext ? 1 : 0) | (up ? 2 : 0)), UIntPtr.Zero);
    }

    // Velocidad de desplazamiento de Windows (ajuste del usuario, no requiere administrador)
    public static void SetScrollSpeed(int lines, int chars) {
      if (lines > 0) Native.SystemParametersInfo(0x0069, (uint)lines, IntPtr.Zero, 0x03);   // SPI_SETWHEELSCROLLLINES
      if (chars > 0) Native.SystemParametersInfo(0x006D, (uint)chars, IntPtr.Zero, 0x03);   // SPI_SETWHEELSCROLLCHARS
    }
  }

  public class Control {
    public int Cid, Flags, Group, Gmask, AddFlags, Pos;
    public bool ExpectRaw;
    public bool RawXY { get { return (AddFlags & 0x01) != 0; } }     // admite desviar el movimiento (gestos)
    public bool ExpectDivert, Applied;
    public int ExpectRemap;
    public bool Divertible { get { return (Flags & 0x20) != 0; } }
    public bool Remappable { get { return (Flags & 0x10) != 0 && Gmask != 0; } }
  }

  public class BotonInfo {
    public int Cid { get; set; }
    public bool Desviable { get; set; }
    public bool Reasignable { get; set; }
    public bool Virtual { get; set; }
    public bool Gestos { get; set; }
    public int Pos { get; set; }
    public bool Fkey { get; set; }
    public int Flags { get; set; }
    public int[] Destinos { get; set; }
  }

  public class Evento {
    public long Id { get; set; }
    public string Hora { get; set; }
    public int Cid { get; set; }
    public bool Pulsado { get; set; }
    public string Gesto { get; set; }
  }

  public class Estado {
    public bool Conectado { get; set; }
    public string Dispositivo { get; set; }
    public string Situacion { get; set; }
    public string Modo { get; set; }
    public List<BotonInfo> Botones { get; set; }
    public List<string> Rueda { get; set; }
    public string AppActual { get; set; }
    public string Perfil { get; set; }
    public string[] Recientes { get; set; }
    public int Bateria { get; set; }          // % (-1 = desconocida)
    public bool Cargando { get; set; }
    public int[] Luz { get; set; }            // retroiluminacion actual (ver Engine.LeerLuz); null si no tiene
    public int[] Dpi { get; set; }            // velocidad del sensor { actual, minimo, maximo, paso, fabrica }; null si no se puede ajustar
    public int[] Equipos { get; set; }        // Easy-Switch { canales, canal de este equipo (1..n) }; null si no tiene
  }

  // ---- Comunicacion HID++ 2.0 con un dispositivo ----
  public class Engine {
    const byte SWID = 0x0B;

    public volatile bool Dead;
    public volatile bool NeedsReapply;
    public string DeviceName = "";
    public byte DevIdx = 0xFF;
    public List<Control> Controls = new List<Control>();
    // Decide la accion de un boton gestionado en el momento de pulsarlo (segun la aplicacion con el foco).
    // null = no ejecutar nada (modo prueba)
    public volatile Func<int, Accion> Resolver;
    public Action<int, string> OnGesture;
    Dictionary<int, Accion> enCurso = new Dictionary<int, Accion>();
    volatile HashSet<int> trackGestures = new HashSet<int>();   // botones con el movimiento desviado
    readonly HashSet<int> moverCursor = new HashSet<int>();      // botones mantenidos con Recolocar y movimiento desviado (solo WorkLoop)
    // Botones con un gesto en curso -> movimiento acumulado desde que se pulsaron (dx, dy). Solo en el hilo de WorkLoop.
    readonly Dictionary<int, int[]> enGesto = new Dictionary<int, int[]>();
    // Movimiento minimo para que cuente como gesto (cuentas del sensor); se configura en raton.gestos.distancia
    public static volatile int UmbralGesto = 50;
    public Action<int, bool> OnButton;

    ConcurrentQueue<string> messages;
    byte reprogIdx, statusIdx, ssIdx, hiresIdx, thumbIdx, batIdx, luzIdx, dpiIdx, hostIdx;
    int luzNiveles;
    public string Clave = "";                   // "raton" o "teclado" (lo pone su gestor)
    // Dispositivo conectado con velocidad del sensor ajustable (el raton): lo usan las acciones de velocidad
    public static volatile Engine ConDpi;
    public volatile int DpiActual = -1;
    public int DpiMin, DpiMax, DpiPaso, DpiDefecto;
    List<int> dpiLista;
    public int Equipos, EquipoActual = -1;      // Easy-Switch: canales y el de este equipo (0..n-1)
    bool batUnificada;
    public volatile int Bateria = -1;
    public volatile bool Cargando;
    bool ssEnhanced;
    SafeFileHandle rh, wh;
    int inLen = 20;
    BlockingCollection<byte[]> notes = new BlockingCollection<byte[]>();
    readonly object reqLock = new object();
    readonly object respLock = new object();
    byte[] resp; bool respErr; int respCode; bool waiting; byte expFeat, expFs;
    HashSet<int> pressed = new HashSet<int>();

    public Engine(ConcurrentQueue<string> messages) { this.messages = messages; }

    // Colecciones HID++ de Logitech: pagina 0xFF43/0xFF00 con informe largo de 20 bytes
    public static List<string> FindCandidates() {
      List<string> list = new List<string>();
      Guid g; Native.HidD_GetHidGuid(out g);
      IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
      if (set == new IntPtr(-1)) return list;
      try {
        for (int i = 0; ; i++) {
          Native.SP_DEVICE_INTERFACE_DATA d = new Native.SP_DEVICE_INTERFACE_DATA();
          d.cbSize = Marshal.SizeOf(d);
          if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
          int req;
          Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
          if (req <= 0) continue;
          IntPtr buf = Marshal.AllocHGlobal(req);
          try {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
            string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            if (IsCandidate(path)) list.Add(path);
          } finally { Marshal.FreeHGlobal(buf); }
        }
      } finally { Native.SetupDiDestroyDeviceInfoList(set); }
      return list;
    }

    static bool IsCandidate(string path) {
      using (SafeFileHandle h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
        if (h.IsInvalid) return false;
        Native.HIDD_ATTRIBUTES a = new Native.HIDD_ATTRIBUTES();
        a.Size = Marshal.SizeOf(a);
        if (!Native.HidD_GetAttributes(h, ref a) || a.VendorID != 0x046D) return false;
        IntPtr pp;
        if (!Native.HidD_GetPreparsedData(h, out pp)) return false;
        try {
          Native.HIDP_CAPS c = new Native.HIDP_CAPS();
          c.Reserved = new ushort[17];
          Native.HidP_GetCaps(pp, ref c);
          return (c.UsagePage == 0xFF43 || c.UsagePage == 0xFF00) && c.OutputReportByteLength == 20;
        } finally { Native.HidD_FreePreparsedData(pp); }
      }
    }

    // Un handle para leer y otro para escribir: la E/S sincrona se serializa por handle
    public void Open(string path) {
      rh = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
      if (rh.IsInvalid) throw new Exception("No se pudo abrir el dispositivo (error " + Marshal.GetLastWin32Error() + ")");
      wh = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
      if (wh.IsInvalid) throw new Exception("No se pudo abrir el dispositivo para escritura (error " + Marshal.GetLastWin32Error() + ")");
      IntPtr pp;
      if (Native.HidD_GetPreparsedData(rh, out pp)) {
        Native.HIDP_CAPS c = new Native.HIDP_CAPS();
        c.Reserved = new ushort[17];
        Native.HidP_GetCaps(pp, ref c);
        if (c.InputReportByteLength > 0) inLen = c.InputReportByteLength;
        Native.HidD_FreePreparsedData(pp);
      }
      Thread t1 = new Thread(ReadLoop); t1.IsBackground = true; t1.Start();
      Thread t2 = new Thread(WorkLoop); t2.IsBackground = true; t2.Start();
    }

    public void Close() {
      Dead = true;
      if (ConDpi == this) ConDpi = null;
      try { if (rh != null) { Native.CancelIoEx(rh, IntPtr.Zero); rh.Dispose(); } } catch { }
      try { if (wh != null) { Native.CancelIoEx(wh, IntPtr.Zero); wh.Dispose(); } } catch { }
      try { notes.CompleteAdding(); } catch { }
      lock (respLock) { Monitor.PulseAll(respLock); }
    }

    public byte[] Request(byte dev, byte feat, int fn, byte[] p, int timeout) {
      lock (reqLock) {
        if (Dead) throw new Exception("Dispositivo desconectado");
        byte[] r = new byte[20];
        r[0] = 0x11; r[1] = dev; r[2] = feat; r[3] = (byte)((fn << 4) | SWID);
        Array.Copy(p, 0, r, 4, Math.Min(p.Length, 16));
        lock (respLock) { resp = null; respErr = false; expFeat = feat; expFs = r[3]; waiting = true; }
        int w;
        if (!Native.WriteFile(wh, r, r.Length, out w, IntPtr.Zero)) {
          lock (respLock) { waiting = false; }
          throw new Exception("Error de escritura (" + Marshal.GetLastWin32Error() + ")");
        }
        lock (respLock) {
          DateTime end = DateTime.UtcNow.AddMilliseconds(timeout);
          while (resp == null && !Dead) {
            int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) break;
            Monitor.Wait(respLock, left);
          }
          waiting = false;
          if (resp == null) throw new TimeoutException("Sin respuesta del raton");
          if (respErr) throw new Exception("Error HID++ " + respCode);
          return resp;
        }
      }
    }

    // Envia una peticion sin esperar respuesta (cambiar de equipo: el dispositivo se desconecta enseguida)
    void Enviar(byte feat, int fn, byte[] p) {
      lock (reqLock) {
        if (Dead) throw new Exception("Dispositivo desconectado");
        byte[] r = new byte[20];
        r[0] = 0x11; r[1] = DevIdx; r[2] = feat; r[3] = (byte)((fn << 4) | SWID);
        Array.Copy(p, 0, r, 4, Math.Min(p.Length, 16));
        int w;
        if (!Native.WriteFile(wh, r, r.Length, out w, IntPtr.Zero)) throw new Exception("Error de escritura (" + Marshal.GetLastWin32Error() + ")");
      }
    }

    byte GetFeature(byte idx, int featId) {
      return Request(idx, 0, 0, new byte[] { (byte)(featId >> 8), (byte)featId }, 2000)[0];
    }
    byte TryFeature(int featId) { try { return GetFeature(DevIdx, featId); } catch { return 0; } }

    void ReadLoop() {
      byte[] buf = new byte[inLen];
      while (!Dead) {
        int n;
        if (!Native.ReadFile(rh, buf, buf.Length, out n, IntPtr.Zero) || n == 0) {
          if (!Dead) messages.Enqueue("Lectura interrumpida (error " + Marshal.GetLastWin32Error() + ")");
          Dead = true;
          lock (respLock) { Monitor.PulseAll(respLock); }
          break;
        }
        byte[] b = new byte[n];
        Array.Copy(buf, b, n);
        Handle(b);
      }
    }

    // b[0]=id informe, b[1]=indice dispositivo, b[2]=indice funcion, b[3]=funcion|swid, b[4..]=parametros
    void Handle(byte[] b) {
      if (b.Length < 5) return;
      lock (respLock) {
        if (waiting && resp == null) {
          if (b[2] == expFeat && b[3] == expFs) {
            resp = new byte[b.Length - 4]; Array.Copy(b, 4, resp, 0, resp.Length);
            Monitor.PulseAll(respLock); return;
          }
          if ((b[2] == 0xFF || b[2] == 0x8F) && b.Length > 5 && b[3] == expFeat && b[4] == expFs) {
            resp = new byte[0]; respErr = true; respCode = b[5];
            Monitor.PulseAll(respLock); return;
          }
        }
      }
      try { notes.Add(b); } catch { }
    }

    // Localiza el raton: 0xFF por Bluetooth/USB directo, 1-6 tras un receptor
    public bool Init(string filter) {
      int[] idxs = { 0xFF, 1, 2, 3, 4, 5, 6 };
      HashSet<string> ignorados = new HashSet<string>();
      foreach (int i in idxs) {
        byte idx = (byte)i;
        byte[] r;
        try { r = Request(idx, 0, 1, new byte[] { 0, 0, 0x5A }, 700); }
        catch { if (Dead) return false; continue; }
        if (r.Length < 3 || r[2] != 0x5A || r[0] < 2) continue;
        byte rp = GetFeature(idx, 0x1B04);
        if (rp == 0) { if (idx == 0xFF) return false; continue; }
        string name = ReadName(idx);
        if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) {
          // Conexion directa, o el mismo dispositivo respondiendo en todos los indices
          bool nuevo = ignorados.Add(name);
          if (idx == 0xFF || !nuevo) return false;
          continue;
        }
        DevIdx = idx; reprogIdx = rp; DeviceName = name;
        statusIdx = TryFeature(0x1D4B);
        ssIdx = TryFeature(0x2111); ssEnhanced = ssIdx != 0;
        if (!ssEnhanced) ssIdx = TryFeature(0x2110);
        hiresIdx = TryFeature(0x2121);
        batIdx = TryFeature(0x1004); batUnificada = batIdx != 0;           // Unified Battery; si no, Battery Status
        if (!batUnificada) batIdx = TryFeature(0x1000);
        thumbIdx = TryFeature(0x2150);
        luzIdx = TryFeature(0x1982);                                        // retroiluminacion (Backlight2)
        dpiIdx = TryFeature(0x2201);                                        // velocidad del sensor (Adjustable DPI)
        hostIdx = TryFeature(0x1814);                                       // Easy-Switch (Change Host)
        if (dpiIdx != 0) ConDpi = this;
        return true;
      }
      return false;
    }

    string ReadName(byte idx) {
      try {
        byte fi = GetFeature(idx, 0x0005);
        if (fi == 0) return "";
        int len = Request(idx, fi, 0, new byte[0], 2000)[0];
        StringBuilder sb = new StringBuilder();
        while (sb.Length < len) {
          byte[] r = Request(idx, fi, 1, new byte[] { (byte)sb.Length }, 2000);
          int added = 0;
          for (int k = 0; k < r.Length && sb.Length < len; k++) {
            if (r[k] == 0) { len = sb.Length; break; }
            sb.Append((char)r[k]); added++;
          }
          if (added == 0) break;
        }
        return sb.ToString();
      } catch { return ""; }
    }

    // ---- Botones (0x1B04) ----
    public void LoadControls() {
      List<Control> list = new List<Control>();
      int count = Request(DevIdx, reprogIdx, 0, new byte[0], 2000)[0];
      for (int i = 0; i < count; i++) {
        byte[] r = Request(DevIdx, reprogIdx, 1, new byte[] { (byte)i }, 2000);
        Control c = new Control();
        c.Cid = (r[0] << 8) | r[1]; c.Flags = r[4]; c.Group = r[6]; c.Gmask = r[7]; c.ExpectRemap = c.Cid;
        c.AddFlags = r.Length > 8 ? r[8] : 0;
        c.Pos = r[5];                                  // en teclados: numero de tecla F (1-12); 0 = sin posicion
        list.Add(c);
      }
      Controls = list;
    }

    // Destinos validos de reasignacion: el propio boton y los de los grupos de su mascara
    public List<int> Targets(Control c) {
      List<int> l = new List<int>();
      if (!c.Remappable) return l;
      l.Add(c.Cid);
      foreach (Control t in Controls)
        if (t.Cid != c.Cid && t.Group != 0 && (c.Gmask & (1 << (t.Group - 1))) != 0) l.Add(t.Cid);
      return l;
    }

    // modo: "activo" (configuracion), "prueba" (desvia todo para verlo), "pausado" (valores de fabrica)
    // gestionados: botones que decide el servidor al pulsarlos (desviados); conGestos: ademas con movimiento desviado;
    // rm: reasignaciones fijas que hace el propio raton para el resto de botones.
    public void ApplyButtons(string mode, HashSet<int> gestionados, HashSet<int> conGestos, Dictionary<int, int> rm) {
      HashSet<int> track = new HashSet<int>();
      foreach (Control c in Controls) {
        if (!c.Divertible && !c.Remappable) continue;
        bool divert = false, raw = false; int remap = c.Cid; int t;
        if (mode == "prueba") { divert = c.Divertible; raw = c.Divertible && c.RawXY; }
        else if (mode == "activo") {
          if (gestionados.Contains(c.Cid) && c.Divertible) { divert = true; raw = conGestos.Contains(c.Cid) && c.RawXY; }
          else if (rm.TryGetValue(c.Cid, out t) && Targets(c).Contains(t)) remap = t;
        }
        int flags = 0;
        if (c.Divertible) flags |= divert ? 0x03 : 0x02;     // divert + "divert valido"
        if (c.RawXY) flags |= raw ? 0x30 : 0x20;             // movimiento desviado + "rawXY valido"
        int r = c.Remappable ? remap : 0;                    // 0 = sin cambio
        Request(DevIdx, reprogIdx, 3, new byte[] { (byte)(c.Cid >> 8), (byte)c.Cid, (byte)flags, (byte)(r >> 8), (byte)r }, 2000);
        c.ExpectDivert = divert; c.ExpectRaw = raw; c.ExpectRemap = remap; c.Applied = true;
        if (raw) track.Add(c.Cid);
      }
      trackGestures = track;
    }

    // true si el raton ya no tiene lo aplicado (se ha reiniciado u otro programa lo ha cambiado)
    public bool CheckButtons() {
      foreach (Control c in Controls) {
        if (!c.Applied) continue;
        byte[] r = Request(DevIdx, reprogIdx, 2, new byte[] { (byte)(c.Cid >> 8), (byte)c.Cid }, 2000);
        bool div = (r[2] & 0x01) != 0;
        bool raw = (r[2] & 0x10) != 0;
        int rem = (r[3] << 8) | r[4];
        if (rem == 0) rem = c.Cid;
        if ((c.Divertible && div != c.ExpectDivert) || (c.RawXY && raw != c.ExpectRaw) ||
            (c.Remappable && rem != c.ExpectRemap)) return true;
      }
      return false;
    }

    // ---- Rueda: SmartShift (0x2111/0x2110), rueda principal (0x2121), rueda lateral (0x2150) ----
    // En 0x2111 las funciones get/set de modo estan desplazadas una posicion respecto a 0x2110
    byte[] GetSmartShift() { return Request(DevIdx, ssIdx, ssEnhanced ? 1 : 0, new byte[0], 2000); }

    public List<string> WheelInfo() {
      List<string> l = new List<string>();
      if (ssIdx != 0) {
        byte[] r = GetSmartShift();
        l.Add(r[0] == 2 ? "Con tope" : r[0] == 1 ? "Giro libre" : "Modo " + r[0]);
        l.Add(r[1] == 255 ? "SmartShift desactivado" : "SmartShift " + r[1] + " (f\u00e1brica " + r[2] + ")");
      }
      if (hiresIdx != 0) {
        int m = Request(DevIdx, hiresIdx, 1, new byte[0], 2000)[0];
        l.Add((m & 4) != 0 ? "Direcci\u00f3n natural" : "Direcci\u00f3n normal");
        l.Add("Alta resoluci\u00f3n: " + ((m & 2) != 0 ? "s\u00ed" : "no"));
      }
      if (thumbIdx != 0) {
        byte[] r = Request(DevIdx, thumbIdx, 1, new byte[0], 2000);
        l.Add((r[1] & 1) != 0 ? "Pulgar invertido" : "Pulgar normal");
      }
      return l;
    }

    // Claves: modo (1 libre, 2 tope), umbral (1-50, 255 = SmartShift desactivado),
    // invertir, hires, invertirLateral (0/1). Las ausentes no se tocan.
    // setMode=false: no cambia el modo tope/libre actual (el usuario lo alterna con el boton).
    public void ApplyWheel(Dictionary<string, int> w, bool setMode) {
      int v;
      if (ssIdx != 0) {
        byte mode = (byte)(setMode && w.TryGetValue("modo", out v) ? v : 0);   // 0 = sin cambio
        byte th = 0;
        if (w.TryGetValue("umbral", out v)) {
          th = (byte)v;
          if (!setMode && GetSmartShift()[1] == th) th = 0;
        }
        if (mode != 0 || th != 0)
          Request(DevIdx, ssIdx, ssEnhanced ? 2 : 1, new byte[] { mode, th }, 2000);
      }
      if (hiresIdx != 0 && (w.ContainsKey("invertir") || w.ContainsKey("hires"))) {
        int cur = Request(DevIdx, hiresIdx, 1, new byte[0], 2000)[0];
        int nw = cur & ~0x01;                                   // la rueda siempre informa a Windows
        if (w.TryGetValue("invertir", out v)) nw = v != 0 ? (nw | 0x04) : (nw & ~0x04);
        if (w.TryGetValue("hires", out v)) nw = v != 0 ? (nw | 0x02) : (nw & ~0x02);
        if (nw != cur) Request(DevIdx, hiresIdx, 2, new byte[] { (byte)nw }, 2000);
      }
      if (thumbIdx != 0 && w.TryGetValue("invertirLateral", out v)) {
        byte[] r = Request(DevIdx, thumbIdx, 1, new byte[0], 2000);
        if (r[0] != 0 || ((r[1] & 1) != 0) != (v != 0))
          Request(DevIdx, thumbIdx, 2, new byte[] { 0, (byte)(v != 0 ? 1 : 0) }, 2000);
      }
      // La iluminacion solo al conectar o al guardar: si el usuario la cambia con F3/F4 no se le deshace
      if (setMode) ApplyLuz(w);
      // La velocidad del sensor, igual: lo que cambie una accion (ciclo, precision) no se deshace en las comprobaciones
      if (setMode && dpiIdx != 0 && w.TryGetValue("dpi", out v)) SetDpi(v);
    }

    // ---- Retroiluminacion (0x1982 Backlight2) ----
    // getConfig (fn 0): activa, opciones (modo en los bits 3-4: 1 automatico, 3 manual), soportado, efectos (2 bytes),
    // nivel y tres tiempos en unidades de 5 s (2 bytes, primero el bajo): manos fuera, manos cerca, con corriente.
    // fn 2: numero de niveles. setConfig (fn 1): activa, opciones, 0xFF, nivel y los tres tiempos.
    // Devuelve { activa, modo, nivel, niveles, segFuera, segCerca, segCorriente } o null si no hay iluminacion.
    public int[] LeerLuz() {
      if (luzIdx == 0) return null;
      byte[] r = Request(DevIdx, luzIdx, 0, new byte[0], 2000);
      if (luzNiveles == 0) luzNiveles = Request(DevIdx, luzIdx, 2, new byte[0], 2000)[0];
      return new int[] { r[0], (r[1] >> 3) & 3, r[5], luzNiveles,
                         (r[6] | r[7] << 8) * 5, (r[8] | r[9] << 8) * 5, (r[10] | r[11] << 8) * 5 };
    }

    // Claves: luz (0/1), luzModo (1 automatico, 3 manual), luzNivel (0..niveles-1),
    // luzFuera, luzCerca, luzCorriente (segundos). Las ausentes no se tocan; solo se escribe si algo cambia.
    void ApplyLuz(Dictionary<string, int> w) {
      if (luzIdx == 0) return;
      if (!w.ContainsKey("luz") && !w.ContainsKey("luzModo") && !w.ContainsKey("luzNivel") &&
          !w.ContainsKey("luzFuera") && !w.ContainsKey("luzCerca") && !w.ContainsKey("luzCorriente")) return;
      byte[] r = Request(DevIdx, luzIdx, 0, new byte[0], 2000);
      byte[] n = { r[0], r[1], 0xFF, r[5], r[6], r[7], r[8], r[9], r[10], r[11] };
      int v;
      if (w.TryGetValue("luz", out v)) n[0] = (byte)(v != 0 ? 1 : 0);
      if (w.TryGetValue("luzModo", out v)) n[1] = (byte)((r[1] & 0x07) | ((v & 3) << 3));
      if (w.TryGetValue("luzNivel", out v)) n[3] = (byte)Math.Max(0, v);
      PonTiempoLuz(w, "luzFuera", n, 4);
      PonTiempoLuz(w, "luzCerca", n, 6);
      PonTiempoLuz(w, "luzCorriente", n, 8);
      bool igual = n[0] == r[0] && n[1] == r[1] && n[3] == r[5];
      for (int i = 4; i < 10; i++) if (n[i] != r[i + 2]) igual = false;
      if (!igual) Request(DevIdx, luzIdx, 1, n, 2000);
    }
    static void PonTiempoLuz(Dictionary<string, int> w, string clave, byte[] n, int i) {
      int v;
      if (!w.TryGetValue(clave, out v)) return;
      int u = Math.Max(1, Math.Min(0x7FFF, (v + 2) / 5));                 // unidades de 5 s
      n[i] = (byte)u; n[i + 1] = (byte)(u >> 8);
    }

    // Al soltar el boton: direccion dominante del movimiento, o "clic" si apenas se ha movido
    // ---- Bateria (0x1004 Unified Battery o 0x1000 Battery Status) ----
    public void LeerBateria() {
      if (batIdx == 0) return;
      LeerBateria(Request(DevIdx, batIdx, batUnificada ? 1 : 0, new byte[0], 2000));
    }
    void LeerBateria(byte[] r) {
      if (r.Length < 3) return;
      if (batUnificada) {
        int pct = r[0];
        if (pct == 0 && r[1] != 0) pct = (r[1] & 8) != 0 ? 100 : (r[1] & 4) != 0 ? 60 : (r[1] & 2) != 0 ? 20 : 5;   // solo niveles
        Bateria = pct; Cargando = r[2] == 1 || r[2] == 2;
      } else {
        Bateria = r[0]; Cargando = r[2] == 1 || r[2] == 2 || r[2] == 4;
      }
    }

    // ---- Velocidad del sensor (0x2201 Adjustable DPI, sensor 0) ----
    // fn 1: lista de valores (2 bytes cada uno tras el indice del sensor; 0xE000|paso = rango desde el anterior
    // hasta el siguiente); fn 2: actual y de fabrica; fn 3: fijar.
    public bool TieneDpi { get { return dpiIdx != 0; } }
    public void LeerDpi() {
      if (dpiIdx == 0) return;
      if (dpiLista == null) {
        byte[] r = Request(DevIdx, dpiIdx, 1, new byte[] { 0 }, 2000);
        List<int> l = new List<int>();
        for (int i = 1; i + 1 < r.Length; i += 2) {
          int v = (r[i] << 8) | r[i + 1];
          if (v == 0) break;
          if ((v >> 13) == 7 && l.Count > 0 && i + 3 < r.Length) {
            int paso = v & 0x1FFF, max = (r[i + 2] << 8) | r[i + 3];
            for (int x = l[l.Count - 1] + paso; paso > 0 && x <= max; x += paso) l.Add(x);
            i += 2;
          } else l.Add(v);
        }
        if (l.Count == 0) return;
        l.Sort();
        int menor = 0;
        for (int i = 1; i < l.Count; i++) { int d = l[i] - l[i - 1]; if (d > 0 && (menor == 0 || d < menor)) menor = d; }
        DpiMin = l[0]; DpiMax = l[l.Count - 1]; DpiPaso = menor > 0 ? menor : 50;
        dpiLista = l;
      }
      byte[] c = Request(DevIdx, dpiIdx, 2, new byte[] { 0 }, 2000);
      DpiActual = (c[1] << 8) | c[2];
      int def = (c[3] << 8) | c[4];
      if (def > 0) DpiDefecto = def;
    }
    // Fija la velocidad (la mas cercana que admita el sensor) y devuelve la aplicada
    public int SetDpi(int v) {
      if (dpiIdx == 0) throw new Exception("Este dispositivo no permite cambiar la velocidad del puntero");
      if (dpiLista == null) LeerDpi();
      if (dpiLista == null) throw new Exception("No se pudo leer las velocidades del sensor");
      int mejor = dpiLista[0];
      foreach (int x in dpiLista) if (Math.Abs(x - v) < Math.Abs(mejor - v)) mejor = x;
      if (mejor != DpiActual) Request(DevIdx, dpiIdx, 3, new byte[] { 0, (byte)(mejor >> 8), (byte)mejor }, 2000);
      DpiActual = mejor;
      return mejor;
    }

    // ---- Easy-Switch (0x1814 Change Host): fn 0 = canales y el actual; fn 1 = pasar a otro canal ----
    public bool TieneEquipos { get { return hostIdx != 0; } }
    public void LeerEquipos() {
      if (hostIdx == 0) return;
      byte[] r = Request(DevIdx, hostIdx, 0, new byte[0], 2000);
      Equipos = r[0]; EquipoActual = r[1];
    }
    // canal: 1..n, o 0 = el siguiente. Devuelve false si ya esta en ese canal (o no existe).
    // Mientras esta conectado aqui, su canal actual es el de este equipo.
    public bool CambiarEquipo(int canal) {
      if (hostIdx == 0 || Equipos <= 0) return false;
      int destino = canal <= 0 ? (EquipoActual + 1) % Equipos : canal - 1;
      if (destino == EquipoActual || destino >= Equipos) return false;
      Enviar(hostIdx, 1, new byte[] { (byte)destino });
      return true;
    }

    // ---- Doble clic y pulsacion larga ----
    // Con pulsaciones extra la accion principal pasa a ser el clic: se ejecuta al soltar y, si hay doble clic,
    // tras esperar DOBLE_MS por si llega la segunda pulsacion. La larga salta al mantener LARGA_MS.
    class EstadoPulsacion { public Accion A; public bool Esperando, Larga, IgnorarSoltar; public System.Threading.Timer T; }
    readonly Dictionary<int, EstadoPulsacion> pulsaciones = new Dictionary<int, EstadoPulsacion>();
    const int LARGA_MS = 500, DOBLE_MS = 300;

    static void Parar(EstadoPulsacion st) { if (st.T != null) { st.T.Dispose(); st.T = null; } }

    void PulsarVariantes(int cid, Accion a) {
      Accion ejecutar = null;
      lock (pulsaciones) {
        EstadoPulsacion st;
        if (pulsaciones.TryGetValue(cid, out st) && st.Esperando) {
          st.Esperando = false; Parar(st); st.IgnorarSoltar = true;      // segunda pulsacion a tiempo: doble clic
          st.A.Variantes.TryGetValue("doble", out ejecutar);
        } else {
          st = new EstadoPulsacion(); st.A = a; pulsaciones[cid] = st;
          Accion larga;
          if (a.Variantes.TryGetValue("larga", out larga)) {
            EstadoPulsacion este = st;
            st.T = new System.Threading.Timer(delegate {
              lock (pulsaciones) { if (este.T == null) return; Parar(este); este.Larga = true; }
              Suelta(cid, larga);
            }, null, LARGA_MS, System.Threading.Timeout.Infinite);
          }
        }
      }
      if (ejecutar != null) Suelta(cid, ejecutar);
    }

    void SoltarVariantes(int cid) {
      Accion ejecutar = null;
      lock (pulsaciones) {
        EstadoPulsacion st;
        if (!pulsaciones.TryGetValue(cid, out st)) return;
        Parar(st);
        if (st.IgnorarSoltar || st.Larga) { pulsaciones.Remove(cid); return; }
        if (st.A.Variantes.ContainsKey("doble")) {
          st.Esperando = true;
          EstadoPulsacion este = st;
          st.T = new System.Threading.Timer(delegate {
            lock (pulsaciones) { if (!este.Esperando) return; este.Esperando = false; Parar(este); pulsaciones.Remove(cid); }
            Suelta(cid, este.A);
          }, null, DOBLE_MS, System.Threading.Timeout.Infinite);
        } else { pulsaciones.Remove(cid); ejecutar = st.A; }
      }
      if (ejecutar != null) Suelta(cid, ejecutar);
    }

    // Ejecuta una accion completa de una vez (clic, doble clic, pulsacion larga)
    void Suelta(int cid, Accion acc) {
      try {
        if (acc == null) return;
        if (acc.Tipo == "defecto") { if (cid == 0x00C4) ToggleRatchet(); else Emulacion.Clic(cid); }
        else if (acc.Tipo == "boton" && acc.Boton == 0x00C4) ToggleRatchet();
        else Ejecutor.Ejecutar(acc);
      } catch (Exception e) { messages.Enqueue("Aviso: " + e.Message); }
    }

    // Accion de una direccion de gesto: cualquiera salvo gestos. Las listas se abren fijas (el boton ya esta suelto).
    void EjecutarGesto(int cid, Accion a, string dir) {
      Accion g;
      if (a == null || a.Gestos == null || !a.Gestos.TryGetValue(dir, out g) || g == null) return;
      switch (g.Tipo) {
        case "atajo": Keys.SendCombo(g.Teclas); break;
        case "boton": if (g.Boton == 0x00C4) ToggleRatchet(); else Emulacion.Clic(g.Boton); break;
        case "texto": Acciones.PegarTexto(g.Texto); break;
        case "ejecutar": Acciones.Ejecutar(g.Comando); break;
        case "menu": MenuFlotante.AbrirFijo(cid, g.Opciones, g); break;
        case "ventanas": MenuFlotante.AbrirFijo(cid, Ventanas.Listar(), g); break;
        default: Ejecutor.Ejecutar(g); break;
      }
    }

    // Direccion de un movimiento acumulado: "clic" si no llega a la distancia minima; si llega, el eje con mas movimiento
    public static string DireccionDe(int[] mov) {
      int gdx = mov[0], gdy = mov[1], umbral = UmbralGesto;
      if (Math.Abs(gdx) < umbral && Math.Abs(gdy) < umbral) return "clic";
      if (Math.Abs(gdx) >= Math.Abs(gdy)) return gdx > 0 ? "derecha" : "izquierda";
      return gdy > 0 ? "abajo" : "arriba";
    }

    void FinishGesture(int cid, Accion a, int[] mov) {
      string dir = DireccionDe(mov);
      if (OnGesture != null) OnGesture(cid, dir);
      if (a != null && a.Tipo == "gestos") EjecutarGesto(cid, a, dir);
    }

    // Imita un boton del raton cuando la accion de la aplicacion es "actuar como" o "de fabrica"
    void EmularBoton(int cid, bool down) {
      if (cid == 0x00C4) { if (down) ToggleRatchet(); return; }      // modo de rueda
      Emulacion.Pulsar(cid, down);
    }

    // Funcion normal del boton de modo: alterna la rueda entre tope y giro libre
    void ToggleRatchet() {
      if (ssIdx == 0) return;
      byte cur = GetSmartShift()[0];
      Request(DevIdx, ssIdx, ssEnhanced ? 2 : 1, new byte[] { (byte)(cur == 2 ? 1 : 2), 0 }, 2000);
    }

    // ---- Notificaciones del raton ----
    void WorkLoop() {
      try {
        foreach (byte[] b in notes.GetConsumingEnumerable()) {
          try {
            if (reprogIdx == 0 || b[1] != DevIdx) continue;
            if (b[2] == reprogIdx && b[3] == 0x00) {
              // Botones desviados: lista de hasta 4 CID pulsados
              HashSet<int> now = new HashSet<int>();
              for (int k = 4; k + 1 < b.Length && k < 12; k += 2) {
                int cid = (b[k] << 8) | b[k + 1];
                if (cid != 0) now.Add(cid);
              }
              Func<int, Accion> resolver = Resolver;
              HashSet<int> track = trackGestures;
              foreach (int cid in now) {
                if (pressed.Contains(cid)) continue;
                if (OnButton != null) OnButton(cid, true);
                Accion a = resolver != null ? resolver(cid) : null;
                enCurso[cid] = a;
                if (a != null && a.Variantes != null) { PulsarVariantes(cid, a); continue; }   // doble clic / pulsacion larga
                if (track.Contains(cid) && (a == null || a.Tipo == "gestos")) {
                  enGesto[cid] = new int[2];
                  if (a != null && a.MostrarRueda) RuedaGestos.Empezar(cid, a);           // aparece si se mantiene un momento
                  continue;
                }
                if (a == null) continue;
                if (a.Tipo == "atajo") Keys.SendCombo(a.Teclas);
                else if (a.Tipo == "gestos") EjecutarGesto(cid, a, "clic");            // boton sin movimiento desviado
                else if (a.Tipo == "menu") MenuFlotante.Pulsado(cid, a.Opciones, a);
                else if (a.Tipo == "ventanas") MenuFlotante.Pulsado(cid, Ventanas.Listar(), a);
                else if (a.Tipo == "texto") Acciones.PegarTexto(a.Texto);
                else if (a.Tipo == "ejecutar") Acciones.Ejecutar(a.Comando);
                else if (a.Tipo == "recolocar") {                                       // mantener o pulsar: se decide al soltar
                  if (track.Contains(cid)) moverCursor.Add(cid);
                  Recolocar.Pulsado(cid, a);
                }
                else if (a.Tipo == "dpi") Dpi.Pulsado(a);                               // precision: mientras se mantiene
                else if (a.Tipo == "transformar" || a.Tipo == "ocr" || a.Tipo == "captura" || a.Tipo == "paleta" || a.Tipo == "claude" || a.Tipo == "equipo"
                         || a.Tipo == "macro" || a.Tipo == "volumenApp" || a.Tipo == "color") Ejecutor.Ejecutar(a);
                else EmularBoton(a.Tipo == "boton" ? a.Boton : cid, true);
              }
              foreach (int cid in pressed) {
                if (now.Contains(cid)) continue;
                Accion a;
                enCurso.TryGetValue(cid, out a);
                enCurso.Remove(cid);
                if (a != null && a.Variantes != null) SoltarVariantes(cid);
                else if (enGesto.ContainsKey(cid)) { int[] mov = enGesto[cid]; enGesto.Remove(cid); RuedaGestos.Terminar(cid); FinishGesture(cid, a, mov); }
                else if (a != null && (a.Tipo == "boton" || a.Tipo == "defecto")) EmularBoton(a.Tipo == "boton" ? a.Boton : cid, false);
                else if (a != null && (a.Tipo == "menu" || a.Tipo == "ventanas")) MenuFlotante.Soltado(cid);
                else if (a != null && a.Tipo == "recolocar") { moverCursor.Remove(cid); Recolocar.Soltado(cid); }
                else if (a != null && a.Tipo == "dpi") Dpi.Soltado(a);
                if (OnButton != null) OnButton(cid, false);
              }
              pressed = now;
              if (now.Count == 0) moverCursor.Clear();                         // nada pulsado: no queda ningun arrastre a medias
            } else if (b[2] == reprogIdx && b[3] == 0x10) {
              // Movimiento desviado mientras se mantiene el boton: dx, dy con signo (big endian).
              // El aviso no dice de que boton es: cuenta para todos los gestos en curso.
              if (enGesto.Count > 0 && b.Length >= 8) {
                int dx = (short)((b[4] << 8) | b[5]), dy = (short)((b[6] << 8) | b[7]);
                foreach (KeyValuePair<int, int[]> g in enGesto) {
                  g.Value[0] += dx; g.Value[1] += dy;
                  RuedaGestos.Mover(g.Key, DireccionDe(g.Value));                     // solo hace algo si ese boton tiene la rueda
                }
              }
              // Recolocar con un boton de movimiento desviado (el de gestos): el cursor no se mueve solo, asi que lo
              // mueve el servidor; Recolocar sigue al cursor real (gesto rapido o celda del panel)
              if (moverCursor.Count > 0 && b.Length >= 8) {
                int dx = (short)((b[4] << 8) | b[5]), dy = (short)((b[6] << 8) | b[7]);
                Recolocar.Mover(dx, dy);                                         // decide el gesto rapido (como los gestos)
                // El cursor solo se mueve con el panel a la vista (para elegir celda); en el gesto rapido se queda quieto
                if (Recolocar.PanelVisible) try { Point p = Cursor.Position; Cursor.Position = new Point(p.X + dx, p.Y + dy); } catch { }
              }
            } else if (batIdx != 0 && b[2] == batIdx && b[3] == 0x00) {
              byte[] p = new byte[b.Length - 4]; Array.Copy(b, 4, p, 0, p.Length);
              LeerBateria(p);                                                  // aviso de cambio de bateria
            } else if (statusIdx != 0 && b[2] == statusIdx && (b[3] & 0x0F) == 0) {
              messages.Enqueue("Raton reactivado: se vuelve a aplicar la configuracion");
              NeedsReapply = true;
            }
          } catch (Exception e) { messages.Enqueue("Aviso: " + e.Message); }
        }
      } catch { }
    }
  }

  // Accion de un boton en un momento dado: atajo, gestos, menu (lista de opciones),
  // boton (actuar como otro) o defecto (fabrica)
  public class Accion {
    public string Tipo;
    public int[] Teclas;
    public Dictionary<string, Accion> Gestos;   // accion por direccion de gesto
    public List<Opcion> Opciones;
    public int ListaRejilla = -1, ListaColumnas = -1;   // formato propio de la lista (rejilla 0/1, columnas 0 = auto); -1 = como en Listas
    public int ListaIcono = -1;                          // tamano de los iconos de la lista en rejilla (px); -1 = como en Listas
    public int ListaVer = -1;                            // que se ve: 1 icono, 2 etiqueta, 4 contenido (suma); -1 = como en Listas
    public int RejFilas = 2, RejColumnas = 3, RejSeparacion = 8;   // recolocar ventana: cuadricula de la pantalla y hueco (px)
    public Dictionary<string, Accion> Variantes;   // pulsaciones extra: "doble", "larga"
    public string Transformacion;                  // transformar seleccion ("elegir" = lista)
    public bool IncluirVentanas, IncluirTransformaciones, IncluirOcr;   // paleta de comandos
    public string Url, Plantilla; public bool ConSeleccion;            // chat con Claude
    public int Ancho, Alto;                                            // tamano de la ventana del chat
    public string Llevar;                                              // "", seleccion, recorte, recorteTexto
    public string Texto;        // pegar texto
    public string Comando;      // ejecutar programa o comando
    public string Modo;         // captura de pantalla: "texto" (OCR), "imagen", "pantalla" (Imprimir pantalla);
                                // velocidad del puntero: "fijar", "ciclo", "precision"
    public int Valor;           // velocidad del puntero (ppp) al fijar o en precision
    public int[] Valores;       // velocidades del ciclo
    public int Canal;           // cambiar de equipo: canal 1..3 (0 = el siguiente)
    public string Dispositivos; // cambiar de equipo: "ambos", "raton" o "teclado"
    public List<Accion> Pasos;  // macro: acciones en orden ("pausa" con Valor = ms)
    public bool MostrarRueda;   // gestos: ver la rueda junto al cursor al mantener el boton
    public int EsperaRueda = 300;   // gestos: ms que hay que mantener antes de que aparezca la rueda
    public string Etiqueta;     // texto corto de la accion (en la rueda de gestos)
    public int Boton;
  }

  // Opcion de una lista: un atajo de teclado o un clic de un boton del raton
  public class Opcion {
    public string Nombre;
    public string Detalle;      // texto secundario (el atajo o el boton)
    public int[] Teclas;        // si es atajo
    public int Boton;           // si es "actuar como" (CID del boton del raton)
    public string Texto;        // si es "pegar texto"
    public string Comando;      // si es "ejecutar"
    public IntPtr Ventana;      // si es una ventana abierta (lista de programas)
    public Image Icono;         // icono del programa
    public string Transformacion;   // si es "transformar seleccion"
    public bool Ocr;            // si es "copiar texto de la pantalla" (formato antiguo)
    public string Captura;      // captura de pantalla: "texto", "imagen" o "pantalla"
    public Action Accion;       // accion propia (listas que construye el servidor)
    public LogiRaton.Accion Hacer;   // cualquier otra accion de boton (otra lista, paleta, macro, volumen...)
  }

  // ---- Ejecutar una accion suelta (direccion de gesto, combinacion de teclas) ----
  public static class Ejecutor {
    public static void Ejecutar(Accion g) {
      if (g == null) return;
      switch (g.Tipo) {
        case "atajo": Keys.SendCombo(g.Teclas); break;
        case "boton": Emulacion.Clic(g.Boton); break;
        case "texto": Acciones.PegarTexto(g.Texto); break;
        case "ejecutar": Acciones.Ejecutar(g.Comando); break;
        case "menu": MenuFlotante.AbrirFijo(-1, g.Opciones, g); break;
        case "ventanas": MenuFlotante.AbrirFijo(-1, Ventanas.Listar(), g); break;
        case "recolocar": Recolocar.AbrirFijo(g); break;                  // desde gesto, combinacion o pulsacion extra
        case "transformar":
          if (g.Transformacion == "elegir") MenuFlotante.AbrirFijo(-1, Transformar.ComoOpciones(false));
          else Transformar.Aplicar(g.Transformacion);
          break;
        case "ocr": OcrPuente.Lanzar(); break;
        case "captura": Captura.Hacer(g.Modo); break;
        case "paleta": Paleta.Abrir(g); break;
        case "claude": Chat.Abrir(g.Url, g.Llevar ?? (g.ConSeleccion ? "seleccion" : ""), g.Plantilla, g.Ancho, g.Alto); break;
        case "dpi": Dpi.Ejecutar(g); break;
        case "equipo": Manager.CambiarEquipo(g.Canal, g.Dispositivos); break;
        case "macro": Macro.Ejecutar(g); break;
        case "volumenApp": VolumenApp.Cambiar(g.Modo, g.Valor); break;
        case "color": ColorPantalla.Copiar(g.Modo); break;
      }
    }

    // Opcion de una lista o de la paleta
    public static void EjecutarOpcion(Opcion op) {
      if (op.Teclas != null) Keys.SendCombo(op.Teclas);
      else if (op.Boton != 0) Emulacion.Clic(op.Boton);
      else if (op.Texto != null) Acciones.PegarTexto(op.Texto);
      else if (op.Comando != null) Acciones.Ejecutar(op.Comando);
      else if (op.Ventana != IntPtr.Zero) Ventanas.Activar(op.Ventana);
      else if (op.Transformacion != null) Transformar.Aplicar(op.Transformacion);
      else if (op.Captura != null) Captura.Hacer(op.Captura);
      else if (op.Ocr) OcrPuente.Lanzar();
      else if (op.Hacer != null) {
        // Un momento despues, con la lista ya cerrada: asi otra lista, la paleta o recolocar se abren limpias
        Accion h = op.Hacer;
        MenuFlotante.EnUI(delegate {
          System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
          t.Interval = 120;
          t.Tick += delegate {
            t.Stop(); t.Dispose();
            try { Ejecutar(h); } catch (Exception e) { Acciones.Avisos.Enqueue("Opcion de lista: " + e.Message); }
          };
          t.Start();
        });
      }
      else if (op.Accion != null) op.Accion();
    }
  }

  // ---- Velocidad del puntero (DPI del sensor del raton) desde una accion ----
  // fijar: un valor; ciclo: el siguiente de la lista; precision: baja la velocidad mientras se mantiene el boton o la
  // tecla y al soltar vuelve la de antes. Sin mantener (gesto, combinacion, doble clic) la precision se alterna.
  public static class Dpi {
    static int previo = -1;                     // velocidad antes de entrar en precision (-1 = no se esta en precision)
    static readonly object bloqueo = new object();

    static Engine Raton() {
      Engine e = Engine.ConDpi;
      if (e == null || e.Dead) { Aviso.Mostrar("No hay ning\u00fan rat\u00f3n con velocidad ajustable conectado"); return null; }
      return e;
    }

    public static void Pulsado(Accion a) { if (a.Modo == "precision") Precision(a, true); else Ejecutar(a); }
    public static void Soltado(Accion a) { if (a.Modo == "precision") Precision(a, false); }

    public static void Ejecutar(Accion a) {
      if (a.Modo == "precision") { Precision(a, previo < 0); return; }
      Engine e = Raton();
      if (e == null) return;
      int v = a.Valor;
      if (a.Modo == "ciclo" && a.Valores != null && a.Valores.Length > 0) {
        if (e.DpiActual < 0) try { e.LeerDpi(); } catch { }
        // El primero mayor que el actual; tras el ultimo, vuelta al primero
        v = a.Valores[0];
        for (int i = 0; i < a.Valores.Length; i++)
          if (Math.Abs(a.Valores[i] - e.DpiActual) <= e.DpiPaso / 2) { v = a.Valores[(i + 1) % a.Valores.Length]; break; }
      }
      lock (bloqueo) previo = -1;
      Fijar(e, v, "Velocidad del puntero: ");
    }

    static void Precision(Accion a, bool entrar) {
      Engine e = Raton();
      if (e == null) return;
      int volver;
      lock (bloqueo) {
        if (entrar) {
          if (previo >= 0) return;
          if (e.DpiActual < 0) try { e.LeerDpi(); } catch { }
          previo = e.DpiActual > 0 ? e.DpiActual : (e.DpiDefecto > 0 ? e.DpiDefecto : 1000);
          volver = -1;
        } else {
          if (previo < 0) return;
          volver = previo; previo = -1;
        }
      }
      if (volver < 0) Fijar(e, a.Valor > 0 ? a.Valor : 400, "Precisi\u00f3n: ");
      else Fijar(e, volver, "Velocidad normal: ");
    }

    static void Fijar(Engine e, int v, string texto) {
      try { int r = e.SetDpi(v); Aviso.Mostrar(texto + r + " ppp"); }
      catch (Exception ex) { Acciones.Avisos.Enqueue("Velocidad del puntero: " + ex.Message); }
    }
  }

  // ---- Microfono: silenciar o activar todos los microfonos de Windows (Core Audio, como el boton de la barra) ----
  // Si alguno esta activo se silencian todos; si todos estaban silenciados, se activan.
  public static class Microfono {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumeradorCom { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
      [PreserveSig] int EnumAudioEndpoints(int flujo, int estados, out IMMDeviceCollection lista);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection {
      [PreserveSig] int GetCount(out int n);
      [PreserveSig] int Item(int i, out IMMDevice d);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
      [PreserveSig] int Activate(ref Guid iid, int contexto, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    // Solo se usan SetMute y GetMute; las anteriores ocupan su sitio en la tabla de la interfaz
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume {
      [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
      [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
      [PreserveSig] int GetChannelCount(out int n);
      [PreserveSig] int SetMasterVolumeLevel(float db, IntPtr ctx);
      [PreserveSig] int SetMasterVolumeLevelScalar(float v, IntPtr ctx);
      [PreserveSig] int GetMasterVolumeLevel(out float db);
      [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
      [PreserveSig] int SetChannelVolumeLevel(int canal, float db, IntPtr ctx);
      [PreserveSig] int SetChannelVolumeLevelScalar(int canal, float v, IntPtr ctx);
      [PreserveSig] int GetChannelVolumeLevel(int canal, out float db);
      [PreserveSig] int GetChannelVolumeLevelScalar(int canal, out float v);
      [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool silencio, IntPtr ctx);
      [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool silencio);
    }

    public static void Alternar() {
      // En un hilo propio: COM no depende del hilo que lo pida (raton, teclado o ventana)
      Thread th = new Thread(delegate () {
        List<IAudioEndpointVolume> vols = new List<IAudioEndpointVolume>();
        try {
          IMMDeviceEnumerator en = (IMMDeviceEnumerator)new EnumeradorCom();
          IMMDeviceCollection lista;
          int n = 0;
          if (en.EnumAudioEndpoints(1, 1, out lista) == 0) lista.GetCount(out n);   // eCapture, DEVICE_STATE_ACTIVE
          Guid iid = typeof(IAudioEndpointVolume).GUID;
          for (int i = 0; i < n; i++) {
            IMMDevice d; object o;
            if (lista.Item(i, out d) != 0) continue;
            if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) vols.Add((IAudioEndpointVolume)o);   // CLSCTX_ALL
          }
          if (vols.Count == 0) { Aviso.Mostrar("No hay ning\u00fan micr\u00f3fono activo"); return; }
          bool alguno = false;
          foreach (IAudioEndpointVolume v in vols) { bool m; if (v.GetMute(out m) == 0 && !m) alguno = true; }
          foreach (IAudioEndpointVolume v in vols) v.SetMute(alguno, IntPtr.Zero);
          Aviso.Mostrar(alguno ? "Micr\u00f3fono silenciado" : "Micr\u00f3fono activado");
        } catch (Exception e) { Acciones.Avisos.Enqueue("Microfono: " + e.Message); }
        finally { foreach (IAudioEndpointVolume v in vols) try { Marshal.ReleaseComObject(v); } catch { } }
      });
      th.SetApartmentState(ApartmentState.MTA);
      th.IsBackground = true;
      th.Start();
    }
  }

  // ---- Macro: varias acciones seguidas (atajo, texto, ejecutar, clic) con pausas opcionales ----
  // Va en su propio hilo para no bloquear el raton ni el teclado; solo una a la vez (pulsar de nuevo mientras corre no hace nada).
  public static class Macro {
    static int enCurso;
    public static void Ejecutar(Accion a) {
      if (a.Pasos == null || a.Pasos.Count == 0) return;
      if (Interlocked.Exchange(ref enCurso, 1) == 1) return;
      Thread th = new Thread(delegate () {
        try {
          foreach (Accion p in a.Pasos) {
            if (p.Tipo == "pausa") { Thread.Sleep(Math.Max(0, Math.Min(10000, p.Valor))); continue; }
            Ejecutor.Ejecutar(p);
            Thread.Sleep(p.Tipo == "texto" ? 250 : 60);           // margen para que la aplicacion procese el paso
          }
        } catch (Exception e) { Acciones.Avisos.Enqueue("Macro: " + e.Message); }
        finally { Interlocked.Exchange(ref enCurso, 0); }
      });
      th.IsBackground = true;
      th.SetApartmentState(ApartmentState.STA);
      th.Start();
    }
  }

  // ---- Volumen solo de la aplicacion activa (sesiones de audio de Windows, como el mezclador de volumen) ----
  // modo: "subir", "bajar" (paso en %) o "silenciar" (alterna). Cuenta todas las sesiones del mismo programa (p. ej. Chrome).
  public static class VolumenApp {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumeradorCom { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
      [PreserveSig] int EnumAudioEndpoints(int flujo, int estados, out IntPtr lista);
      [PreserveSig] int GetDefaultAudioEndpoint(int flujo, int rol, out IMMDevice d);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
      [PreserveSig] int Activate(ref Guid iid, int contexto, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 {
      [PreserveSig] int GetAudioSessionControl(IntPtr grupo, int flags, out IntPtr control);
      [PreserveSig] int GetSimpleAudioVolume(IntPtr grupo, int flags, out IntPtr volumen);
      [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator {
      [PreserveSig] int GetCount(out int n);
      [PreserveSig] int GetSession(int i, out IntPtr sesion);
    }
    // Solo se usa GetProcessId; los metodos anteriores ocupan su sitio en la tabla de la interfaz
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2 {
      [PreserveSig] int GetState(out int s);
      [PreserveSig] int GetDisplayName(out IntPtr n);
      [PreserveSig] int SetDisplayName(IntPtr n, IntPtr ctx);
      [PreserveSig] int GetIconPath(out IntPtr p);
      [PreserveSig] int SetIconPath(IntPtr p, IntPtr ctx);
      [PreserveSig] int GetGroupingParam(out Guid g);
      [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr ctx);
      [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
      [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
      [PreserveSig] int GetSessionIdentifier(out IntPtr s);
      [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
      [PreserveSig] int GetProcessId(out uint pid);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume {
      [PreserveSig] int SetMasterVolume(float v, IntPtr ctx);
      [PreserveSig] int GetMasterVolume(out float v);
      [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool silencio, IntPtr ctx);
      [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool silencio);
    }

    public static void Cambiar(string modo, int paso) {
      string exe = Primerplano.Exe();
      if (exe == "") return;
      string nombre = exe.EndsWith(".exe") ? exe.Substring(0, exe.Length - 4) : exe;
      if (paso <= 0) paso = 10;
      Thread th = new Thread(delegate () {
        List<object> rcw = new List<object>();
        try {
          IMMDeviceEnumerator en = (IMMDeviceEnumerator)new EnumeradorCom(); rcw.Add(en);
          IMMDevice dev;
          if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0) { Aviso.Mostrar("No hay salida de sonido"); return; }   // eRender, eMultimedia
          rcw.Add(dev);
          Guid iid = typeof(IAudioSessionManager2).GUID; object o;
          if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return;                                                // CLSCTX_ALL
          rcw.Add(o);
          IAudioSessionEnumerator se;
          if (((IAudioSessionManager2)o).GetSessionEnumerator(out se) != 0) return;
          rcw.Add(se);
          int n; se.GetCount(out n);
          List<ISimpleAudioVolume> vols = new List<ISimpleAudioVolume>();
          for (int i = 0; i < n; i++) {
            IntPtr p;
            if (se.GetSession(i, out p) != 0 || p == IntPtr.Zero) continue;
            object s = Marshal.GetObjectForIUnknown(p); Marshal.Release(p); rcw.Add(s);
            uint pid;
            if (((IAudioSessionControl2)s).GetProcessId(out pid) != 0 || pid == 0) continue;
            string pn;
            try { using (Process pr = Process.GetProcessById((int)pid)) pn = pr.ProcessName; } catch { continue; }
            if (string.Equals(pn, nombre, StringComparison.OrdinalIgnoreCase)) vols.Add((ISimpleAudioVolume)s);
          }
          if (vols.Count == 0) { Aviso.Mostrar(nombre + " no tiene sonido abierto"); return; }
          float actual; bool mudo;
          vols[0].GetMasterVolume(out actual); vols[0].GetMute(out mudo);
          if (modo == "silenciar") {
            foreach (ISimpleAudioVolume v in vols) v.SetMute(!mudo, IntPtr.Zero);
            Aviso.Mostrar(nombre + (mudo ? ": con sonido" : ": silenciado"));
          } else {
            float nuevo = Math.Max(0f, Math.Min(1f, actual + (modo == "bajar" ? -paso : paso) / 100f));
            foreach (ISimpleAudioVolume v in vols) { v.SetMasterVolume(nuevo, IntPtr.Zero); if (mudo && modo == "subir") v.SetMute(false, IntPtr.Zero); }
            Aviso.Mostrar(nombre + ": " + (int)Math.Round(nuevo * 100) + " %");
          }
        } catch (Exception e) { Acciones.Avisos.Enqueue("Volumen de la aplicacion: " + e.Message); }
        finally { foreach (object r in rcw) try { if (r != null && Marshal.IsComObject(r)) Marshal.ReleaseComObject(r); } catch { } }
      });
      th.SetApartmentState(ApartmentState.MTA);
      th.IsBackground = true;
      th.Start();
    }
  }

  // ---- Color de la pantalla: copia el color del pixel bajo el cursor ("hex": #1E90FF; "rgb": rgb(30, 144, 255)) ----
  public static class ColorPantalla {
    public static void Copiar(string formato) {
      MenuFlotante.EnUI(delegate {
        try {
          Point p = Cursor.Position;
          Color c;
          using (Bitmap b = new Bitmap(1, 1)) {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(1, 1));
            c = b.GetPixel(0, 0);
          }
          string t = formato == "rgb" ? "rgb(" + c.R + ", " + c.G + ", " + c.B + ")" : "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
          Clipboard.SetDataObject(t, true, 10, 50);
          Aviso.Mostrar("Color " + t + " copiado");
        } catch (Exception e) { Acciones.Avisos.Enqueue("Color de la pantalla: " + e.Message); }
      });
    }
  }

  // Ejecutable de la ventana en primer plano (solo el nombre)
  public static class Primerplano {
    public static string Exe() {
      IntPtr h = Native.GetForegroundWindow();
      if (h == IntPtr.Zero) return "";
      uint pid;
      Native.GetWindowThreadProcessId(h, out pid);
      try { using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant() + ".exe"; }
      catch { return ""; }
    }
  }

  // ---- Estado de los modificadores del teclado (Ctrl, Mayus, Alt, Win) ----
  // Se consulta solo en el momento de una accion del raton (boton o rueda), no de forma continua:
  // no es un registrador de teclas, solo mira si hay modificadores pulsados justo entonces.
  public static class Modificadores {
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    static bool Baja(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    // Misma convencion que Combinaciones.Clave: Alt 1, Ctrl 2, Mayus 4, Win 8
    public static int Mascara() {
      int m = 0;
      if (Baja(0x11)) m |= 2;                       // Ctrl
      if (Baja(0x10)) m |= 4;                       // Mayus
      if (Baja(0x12)) m |= 1;                       // Alt
      if (Baja(0x5B) || Baja(0x5C)) m |= 8;         // Win
      return m;
    }

    // Suelta los modificadores que el usuario mantiene antes de ejecutar la accion de la combinacion,
    // para que no se mezclen con ella (si la accion es un atajo con esos mismos modificadores).
    // La tecla 0xE8 (sin uso) evita que soltar Alt o Win abra el menu de la ventana o el menu Inicio.
    public static void Soltar(int mask) {
      Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero); Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
      if ((mask & 2) != 0) Native.keybd_event(0x11, 0, 2, UIntPtr.Zero);
      if ((mask & 4) != 0) Native.keybd_event(0x10, 0, 2, UIntPtr.Zero);
      if ((mask & 1) != 0) Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
      if ((mask & 8) != 0) { Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero); Native.keybd_event(0x5C, 0, 2, UIntPtr.Zero); }
    }
  }

  // ---- Combinaciones con el giro de la rueda (gancho de raton de bajo nivel) ----
  // La rueda NO se desvia en el dispositivo (el scroll normal sigue yendo a Windows intacto). Un gancho
  // WH_MOUSE_LL ve los giros de rueda: si hay un modificador pulsado que coincide con una combinacion,
  // ejecuta su accion y se traga el giro; si no, lo deja pasar (scroll normal). Direcciones: arriba/abajo
  // (rueda vertical), izquierda/derecha (rueda lateral/horizontal).
  public static class RuedaCombos {
    const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    // Claves: (mascara << 4) | direccion (arriba 0, abajo 1, izquierda 2, derecha 3)
    static volatile Dictionary<int, Accion> general = new Dictionary<int, Accion>();
    static volatile Dictionary<string, Dictionary<int, Accion>> apps = new Dictionary<string, Dictionary<int, Accion>>(StringComparer.OrdinalIgnoreCase);
    static volatile bool activo = true;
    static IntPtr hook = IntPtr.Zero;
    static HookProc proc;                             // referencia viva: el gancho no debe recolectarse

    public static int Direccion(string s) {
      switch (s) { case "arriba": return 0; case "abajo": return 1; case "izquierda": return 2; case "derecha": return 3; default: return -1; }
    }

    public static void Configurar(Dictionary<int, Accion> g, Dictionary<string, Dictionary<int, Accion>> a) {
      general = g ?? new Dictionary<int, Accion>();
      apps = a ?? new Dictionary<string, Dictionary<int, Accion>>(StringComparer.OrdinalIgnoreCase);
      EnHilo(delegate { Ajustar(); });
    }
    public static void SetActivo(bool on) { if (activo == on) return; activo = on; EnHilo(delegate { Ajustar(); }); }

    // El gancho vive en un hilo propio que solo atiende sus mensajes: Windows hace pasar por el cada movimiento del
    // raton, asi que si compartiera hilo con las listas o el portapapeles, cualquier espera congelaria el raton.
    static Thread hilo;
    static System.Windows.Forms.Control receptor;
    static readonly ManualResetEvent hiloListo = new ManualResetEvent(false);
    static void EnHilo(MethodInvoker accion) {
      if (hilo == null) {
        hilo = new Thread(delegate () {
          receptor = new System.Windows.Forms.Control();
          IntPtr h = receptor.Handle;                 // crea la ventana de mensajes en este hilo
          hiloListo.Set();
          Application.Run();
        });
        hilo.IsBackground = true;
        hilo.Priority = ThreadPriority.AboveNormal;
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        hiloListo.WaitOne(5000);
      }
      if (receptor != null) receptor.BeginInvoke(accion);
    }

    static bool HayCombos() {
      if (general.Count > 0) return true;
      foreach (Dictionary<int, Accion> d in apps.Values) if (d.Count > 0) return true;
      return false;
    }

    // Instala o quita el gancho segun haga falta (en su hilo propio, con bucle de mensajes)
    static void Ajustar() {
      bool quiere = activo && HayCombos();
      if (quiere) MenuFlotante.EnUI(delegate { });     // arranca ya el hilo de las ventanas: el gancho no debe esperarlo
      if (quiere && hook == IntPtr.Zero) {
        proc = Gancho;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Acciones.Avisos.Enqueue("No se pudo activar las combinaciones de rueda (error " + Marshal.GetLastWin32Error() + ")");
      } else if (!quiere && hook != IntPtr.Zero) {
        UnhookWindowsHookEx(hook); hook = IntPtr.Zero; proc = null;
      }
    }

    static Accion Buscar(int clave) {
      string exe = Primerplano.Exe();
      Dictionary<int, Accion> app; Accion a;
      if (exe != "" && apps.TryGetValue(exe, out app) && app.TryGetValue(clave, out a)) return a;
      return general.TryGetValue(clave, out a) ? a : null;
    }

    static IntPtr Gancho(int code, IntPtr w, IntPtr l) {
      if (code >= 0 && (w == (IntPtr)WM_MOUSEWHEEL || w == (IntPtr)WM_MOUSEHWHEEL)) {
        int mask = Modificadores.Mascara();
        if (mask != 0) {
          MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
          short delta = (short)(m.mouseData >> 16);
          int dir = w == (IntPtr)WM_MOUSEWHEEL ? (delta > 0 ? 0 : 1) : (delta > 0 ? 3 : 2);
          Accion a = Buscar((mask << 4) | dir);
          if (a != null) {
            Modificadores.Soltar(mask);               // que el atajo no se mezcle con el modificador que se mantiene
            // La accion se ejecuta fuera del gancho (en el hilo de las ventanas): el raton no espera a que termine
            MenuFlotante.EnUI(delegate {
              try { Ejecutor.Ejecutar(a); } catch (Exception e) { Acciones.Avisos.Enqueue("Combinacion de rueda: " + e.Message); }
            });
            return (IntPtr)1;                         // se traga el giro: no llega scroll a la aplicacion
          }
        }
      }
      return CallNextHookEx(hook, code, w, l);
    }
  }

  // ---- Ventana de consola de PowerShell: ocultarla (segundo plano) o mostrarla ----
  public static class Consola {
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    public static void Ocultar() { IntPtr h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); }   // SW_HIDE
    public static void Mostrar() { IntPtr h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 5); }   // SW_SHOW

    // Quita la "edicion rapida" de la consola: con ella, un clic dentro de la ventana empieza a seleccionar texto y
    // Windows deja bloqueado a quien escriba en la consola (el servidor entero) hasta pulsar Esc o Intro
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint modo);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint modo);
    public static bool SinEdicionRapida() {
      IntPtr h = GetStdHandle(-10);                                  // STD_INPUT_HANDLE
      uint modo;
      if (h == IntPtr.Zero || !GetConsoleMode(h, out modo)) return false;
      return SetConsoleMode(h, (modo & ~0x0040u & ~0x0020u) | 0x0080u);   // sin QUICK_EDIT ni INSERT, con EXTENDED_FLAGS
    }
  }

  // ---- Iconos en la bandeja del sistema: uno por periferico, activable por separado ----
  public static class Bandeja {
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    class Item { public NotifyIcon Ni; public Icon Icono; public string Firma; }
    static readonly Dictionary<string, Item> items = new Dictionary<string, Item>();
    static string url, token;

    public static void Config(string u, string tok) { url = u; token = tok; }

    public static void Mostrar(string dev) {
      MenuFlotante.EnUI(delegate {
        Item it;
        if (!items.TryGetValue(dev, out it)) { it = new Item(); items[dev] = it; }
        if (it.Ni == null) {
          it.Ni = new NotifyIcon();
          it.Ni.Text = dev == "raton" ? "Raton" : "Teclado";
          it.Ni.Icon = Render(dev, -1, false);
          it.Ni.DoubleClick += delegate { Abrir(); };
          it.Ni.ContextMenuStrip = Menu();
        }
        it.Ni.Visible = true;
      });
    }

    public static void Ocultar(string dev) {
      MenuFlotante.EnUI(delegate {
        Item it;
        if (!items.TryGetValue(dev, out it)) return;
        if (it.Ni != null) { it.Ni.Visible = false; it.Ni.Dispose(); it.Ni = null; }
        if (it.Icono != null) { DestroyIcon(it.Icono.Handle); it.Icono.Dispose(); it.Icono = null; }
        it.Firma = null;
      });
    }

    // Bateria de un dispositivo (-1 = desconectado). Solo actualiza si su icono esta visible.
    public static void Actualizar(string dev, string texto, int nivel, bool cargando) {
      string f = texto + "|" + nivel + cargando;
      MenuFlotante.EnUI(delegate {
        Item it;
        if (!items.TryGetValue(dev, out it) || it.Ni == null) return;
        if (f == it.Firma) return;
        it.Firma = f;
        it.Ni.Text = texto.Length > 127 ? texto.Substring(0, 124) + "..." : texto;
        Icon nuevo = Render(dev, nivel, cargando);
        Icon viejo = it.Icono; it.Icono = nuevo; it.Ni.Icon = nuevo;
        if (viejo != null) { DestroyIcon(viejo.Handle); viejo.Dispose(); }
      });
    }

    // Notificacion de Windows (bateria baja). Usa el icono de bandeja del dispositivo si esta visible; si no, pone uno
    // un momento solo para la notificacion y lo quita despues.
    public static void Notificar(string dev, string titulo, string texto, int nivel) {
      MenuFlotante.EnUI(delegate {
        Item it;
        if (items.TryGetValue(dev, out it) && it.Ni != null) { it.Ni.ShowBalloonTip(10000, titulo, texto, ToolTipIcon.Warning); return; }
        NotifyIcon ni = new NotifyIcon();
        Icon ic = Render(dev, nivel, false);
        ni.Icon = ic; ni.Text = titulo; ni.Visible = true;
        ni.ShowBalloonTip(10000, titulo, texto, ToolTipIcon.Warning);
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 15000;
        t.Tick += delegate {
          t.Stop(); t.Dispose();
          ni.Visible = false; ni.Dispose();
          DestroyIcon(ic.Handle); ic.Dispose();
        };
        t.Start();
      });
    }

    static ContextMenuStrip Menu() {
      ContextMenuStrip m = new ContextMenuStrip();
      m.Items.Add("Abrir configuracion", null, delegate { Abrir(); });
      m.Items.Add(new ToolStripSeparator());
      m.Items.Add("Modo activo", null, delegate { Post("api/modo", "{\"modo\":\"activo\"}"); });
      m.Items.Add("Modo probar", null, delegate { Post("api/modo", "{\"modo\":\"prueba\"}"); });
      m.Items.Add("Modo pausado", null, delegate { Post("api/modo", "{\"modo\":\"pausado\"}"); });
      m.Items.Add(new ToolStripSeparator());
      m.Items.Add("Mostrar la consola", null, delegate { Post("api/fondo", "{\"activar\":false}"); });
      m.Items.Add(new ToolStripSeparator());
      m.Items.Add("Cerrar servidor", null, delegate { Post("api/salir", "{}"); });
      return m;
    }

    static void Abrir() { AbrirPagina(url); }

    // Abre la pagina con el navegador por defecto. Si es Chrome, Edge, Brave o Vivaldi, en una ventana propia
    // (modo app, sin pestanas); con cualquier otro, en una pestana normal.
    public static void AbrirPagina(string u) {
      string nav = Chat.NavegadorPorDefecto();
      string exe = nav != null ? System.IO.Path.GetFileName(nav).ToLowerInvariant() : "";
      if (exe == "chrome.exe" || exe == "msedge.exe" || exe == "brave.exe" || exe == "vivaldi.exe") {
        try { ProcessStartInfo psi = new ProcessStartInfo(nav, "--app=" + u); psi.UseShellExecute = true; Process.Start(psi); return; } catch { }
      }
      try { ProcessStartInfo psi = new ProcessStartInfo(u); psi.UseShellExecute = true; Process.Start(psi); } catch { }
    }

    // Llama a la propia API local (con el token) en segundo plano, sin bloquear la interfaz
    static void Post(string ruta, string cuerpo) {
      Thread th = new Thread(delegate () {
        try {
          System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url + ruta);
          req.Method = "POST"; req.ContentType = "application/json";
          req.Headers["X-Raton"] = token; req.Headers["Origin"] = url.TrimEnd('/');
          byte[] body = Encoding.UTF8.GetBytes(cuerpo);
          req.ContentLength = body.Length;
          using (System.IO.Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
          using (req.GetResponse()) { }
        } catch { }
      });
      th.IsBackground = true; th.Start();
    }

    // Blanco en barra de tareas oscura; casi negro si la barra es clara (para que se vea en ambas)
    static Color Tinta() {
      try {
        object v = Microsoft.Win32.Registry.GetValue(
          @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
        if (v is int && (int)v == 1) return Color.FromArgb(30, 30, 30);
      } catch { }
      return Color.White;
    }

    // Icono-medidor (como el de sonido): la silueta grande del dispositivo se rellena de abajo
    // arriba segun la bateria. El resto queda tenue. Por debajo del 15 % el relleno es rojo.
    static Icon Render(string dev, int nivel, bool carg) {
      Color col = Tinta();                                   // blanco (o casi negro en barra clara)
      Color lleno = (nivel >= 0 && nivel < 15) ? Color.FromArgb(235, 70, 60) : col;   // alerta en rojo
      Bitmap b = new Bitmap(32, 32);
      using (Graphics g = Graphics.FromImage(b)) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (System.Drawing.Drawing2D.GraphicsPath path = (dev == "raton" ? PathRaton() : PathTeclado())) {
          RectangleF bb = path.GetBounds();
          int pct = nivel < 0 ? 0 : Math.Max(0, Math.Min(100, nivel));
          // Silueta completa en tenue
          g.SetClip(path);
          using (SolidBrush dim = new SolidBrush(Color.FromArgb(nivel < 0 ? 55 : 85, col)))
            g.FillRectangle(dim, bb.Left - 1, bb.Top - 1, bb.Width + 2, bb.Height + 2);
          // Parte cargada: el teclado (ancho) se rellena de lado; el raton (alto) de abajo arriba
          if (pct > 0) {
            using (SolidBrush fb = new SolidBrush(lleno)) {
              if (dev == "raton") {
                float h = bb.Height * pct / 100f;
                g.FillRectangle(fb, bb.Left - 1, bb.Bottom - h, bb.Width + 2, h + 1);
              } else {
                float w = bb.Width * pct / 100f;
                g.FillRectangle(fb, bb.Left - 1, bb.Top - 1, w + 1, bb.Height + 2);
              }
            }
          }
          g.ResetClip();
          // Contorno siempre nitido
          using (Pen p = new Pen(col, 1.9f)) g.DrawPath(p, path);
          // Cablecito del teclado
          if (dev != "raton")
            using (Pen p = new Pen(col, 1.9f)) { p.StartCap = p.EndCap = System.Drawing.Drawing2D.LineCap.Round; g.DrawLine(p, 16, 3, 16, 9); }
          // Detalle: huecos (teclas o rueda) que atraviesan el icono para que se reconozca
          g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
          using (SolidBrush hueco = new SolidBrush(Color.Transparent)) {
            if (dev == "raton") {
              g.FillRectangle(hueco, 15f, 5f, 2f, 6f);                       // ranura de la rueda
            } else {
              // 2 filas de 6 teclas y una fila inferior con teclas laterales y barra espaciadora
              for (int fila = 0; fila < 2; fila++)
                for (int c = 0; c < 6; c++)
                  g.FillRectangle(hueco, 3.6f + c * 4.15f, 12f + fila * 4.3f, 2.9f, 2.9f);
              g.FillRectangle(hueco, 3.6f, 20.6f, 2.9f, 2.9f);               // tecla inferior izquierda
              g.FillRectangle(hueco, 8f, 20.6f, 16.2f, 2.9f);               // barra espaciadora
              g.FillRectangle(hueco, 25.7f, 20.6f, 2.9f, 2.9f);             // tecla inferior derecha
            }
          }
          // Cargando: rayo arriba a la derecha. Primero se recorta un borde transparente alrededor (para que se
          // distinga de la silueta) y luego se pinta en amarillo (ambar oscuro si la barra es clara)
          if (carg && nivel >= 0) {
            using (System.Drawing.Drawing2D.GraphicsPath rayo = new System.Drawing.Drawing2D.GraphicsPath()) {
              rayo.AddPolygon(new PointF[] { new PointF(25f, 0f), new PointF(17.5f, 10f), new PointF(23f, 10f), new PointF(20.5f, 19f),
                                             new PointF(30.5f, 7.5f), new PointF(25f, 7.5f), new PointF(29f, 0f) });
              using (Pen borde = new Pen(Color.Transparent, 4f)) { borde.LineJoin = System.Drawing.Drawing2D.LineJoin.Round; g.DrawPath(borde, rayo); }
              g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
              Color amarillo = col == Color.White ? Color.FromArgb(255, 205, 40) : Color.FromArgb(190, 120, 0);
              using (SolidBrush b2 = new SolidBrush(amarillo)) g.FillPath(b2, rayo);
            }
          }
          g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        }
      }
      Icon ic = Icon.FromHandle(b.GetHicon());
      b.Dispose();
      return ic;
    }

    static System.Drawing.Drawing2D.GraphicsPath Redondeado(float x, float y, float w, float h, float r) {
      System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();
      float d = r * 2;
      gp.AddArc(x, y, d, d, 180, 90);
      gp.AddArc(x + w - d, y, d, d, 270, 90);
      gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
      gp.AddArc(x, y + h - d, d, d, 90, 90);
      gp.CloseFigure();
      return gp;
    }

    // Teclado: rectangulo ancho y redondeado (cuerpo); el cablecito se dibuja aparte, arriba
    static System.Drawing.Drawing2D.GraphicsPath PathTeclado() { return Redondeado(1, 9, 30, 20, 4f); }
    // Raton: capsula alta (cuerpo del raton) que ocupa casi todo el alto
    static System.Drawing.Drawing2D.GraphicsPath PathRaton() { return Redondeado(8, 1, 16, 30, 8); }
  }

  // ---- Combinaciones de teclas con accion propia (RegisterHotKey) ----
  // Windows solo avisa cuando se pulsa exactamente una combinacion registrada; no se lee el resto del teclado.
  // Sin combinaciones configuradas no se registra nada.
  public static class Combinaciones {
    const int WM_HOTKEY = 0x0312;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    static volatile Perfil general = new Perfil();
    static volatile Dictionary<string, Perfil> apps = new Dictionary<string, Perfil>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, int> claveDeId = new Dictionary<int, int>();     // solo en el hilo de la ventana
    static readonly Dictionary<int, int> idDeClave = new Dictionary<int, int>();
    static volatile bool activo = true;
    static Receptor receptor;

    // Clave unica de una combinacion: modificadores (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8) << 16 | tecla
    public static int Clave(int[] teclas) {
      uint mods = 0; int principal = -1;
      foreach (int k in teclas) {
        if (k == Keys.MICRO) throw new ArgumentException("Silenciar microfono no puede ser la tecla de una combinacion (si su accion)");
        int vk = k & 0xFF;
        if (vk == 0x11) mods |= 2; else if (vk == 0x10) mods |= 4; else if (vk == 0x12) mods |= 1; else if (vk == 0x5B) mods |= 8;
        else {
          if (principal >= 0) throw new ArgumentException("Solo puede haber una tecla ademas de Ctrl, Mayus, Alt y Win");
          principal = vk;
        }
      }
      if (principal < 0) throw new ArgumentException("Falta la tecla principal (solo hay modificadores)");
      bool escribe = (principal >= 0x30 && principal <= 0x5A) || principal == 0x20 || principal == 0x0D || principal == 0x09 ||
                     principal == 0x08 || (principal >= 0x60 && principal <= 0x6F) || (principal >= 0xBA && principal <= 0xE2);
      if (mods == 0 && escribe) throw new ArgumentException("Sin Ctrl, Mayus, Alt o Win esa tecla dejaria de funcionar: anade un modificador");
      return (int)(mods << 16) | principal;
    }

    // Mascara de modificadores (Alt 1, Ctrl 2, Mayus 4, Win 8) de una combinacion de raton/rueda.
    // Solo admite modificadores y exige al menos uno (si no, pisaria el uso normal del boton o la rueda).
    public static int SoloModificadores(int[] teclas) {
      int m = 0;
      foreach (int k in teclas) {
        int vk = k & 0xFF;
        if (vk == 0x11) m |= 2; else if (vk == 0x10) m |= 4; else if (vk == 0x12) m |= 1; else if (vk == 0x5B) m |= 8;
        else throw new ArgumentException("Solo valen Ctrl, Mayus, Alt y Win como modificadores del raton");
      }
      if (m == 0) throw new ArgumentException("Falta un modificador (Ctrl, Mayus, Alt o Win)");
      return m;
    }

    public static void Configurar(Perfil g, Dictionary<string, Perfil> a) {
      general = g;
      apps = new Dictionary<string, Perfil>(a, StringComparer.OrdinalIgnoreCase);
      bool hay = g.Combos.Count > 0;
      foreach (Perfil p in a.Values) if (p.Combos.Count > 0) hay = true;
      if (hay || receptor != null) MenuFlotante.EnUI(delegate { Registrar(); });
    }

    public static void SetActivo(bool on) {
      if (activo == on) return;
      activo = on;
      if (receptor != null || on) Configurar(general, apps);
    }

    // En el hilo de la ventana flotante (las combinaciones pertenecen al hilo que las registra)
    static void Registrar() {
      if (receptor == null) { receptor = new Receptor(); receptor.CreateHandle(new CreateParams()); }
      foreach (int id in claveDeId.Keys) UnregisterHotKey(receptor.Handle, id);
      claveDeId.Clear(); idDeClave.Clear();
      if (!activo) return;
      HashSet<int> claves = new HashSet<int>(general.Combos.Keys);
      foreach (Perfil p in apps.Values) claves.UnionWith(p.Combos.Keys);
      int sig = 1;
      foreach (int c in claves) {
        if (RegisterHotKey(receptor.Handle, sig, (uint)(c >> 16) | 0x4000, (uint)(c & 0xFF))) {   // 0x4000 = MOD_NOREPEAT
          claveDeId[sig] = c; idDeClave[c] = sig; sig++;
        } else Acciones.Avisos.Enqueue("No se pudo reservar la combinacion " + Nombre(c) + ": Windows u otro programa ya la usa");
      }
    }

    class Receptor : NativeWindow {
      protected override void WndProc(ref Message m) {
        int c;
        if (m.Msg == WM_HOTKEY && claveDeId.TryGetValue(m.WParam.ToInt32(), out c)) { Disparar(c); return; }
        base.WndProc(ref m);
      }
    }

    static void Disparar(int clave) {
      string exe = Primerplano.Exe();
      Perfil app; Accion a = null;
      if (exe != "" && apps.TryGetValue(exe, out app)) app.Combos.TryGetValue(clave, out a);
      if (a == null) general.Combos.TryGetValue(clave, out a);
      if (a == null || a.Tipo == "pasar") { Pasar(clave); return; }
      SoltarModificadores(clave);
      Ejecutor.Ejecutar(a);
    }

    // Sin accion en esta aplicacion: se le entrega la combinacion tal cual (los modificadores siguen pulsados)
    static void Pasar(int clave) {
      int id;
      if (!idDeClave.TryGetValue(clave, out id)) return;
      UnregisterHotKey(receptor.Handle, id);
      Keys.SendCombo(new int[] { clave & 0xFF });
      System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
      t.Interval = 80;
      t.Tick += delegate {
        t.Stop(); t.Dispose();
        if (activo && claveDeId.ContainsKey(id)) RegisterHotKey(receptor.Handle, id, (uint)(clave >> 16) | 0x4000, (uint)(clave & 0xFF));
      };
      t.Start();
    }

    // Antes de ejecutar la accion se sueltan los modificadores de la combinacion, para que no se mezclen con ella.
    // La tecla 0xE8 (sin uso) evita que soltar Alt o Win abra el menu de la ventana o el menu Inicio.
    static void SoltarModificadores(int clave) {
      uint mods = (uint)(clave >> 16);
      Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero); Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
      if ((mods & 2) != 0) Native.keybd_event(0x11, 0, 2, UIntPtr.Zero);
      if ((mods & 4) != 0) Native.keybd_event(0x10, 0, 2, UIntPtr.Zero);
      if ((mods & 1) != 0) Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
      if ((mods & 8) != 0) Native.keybd_event(0x5B, 0, 1 | 2, UIntPtr.Zero);
    }

    static string Nombre(int c) {
      uint mods = (uint)(c >> 16); int vk = c & 0xFF;
      string s = ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 4) != 0 ? "Mayus+" : "") + ((mods & 1) != 0 ? "Alt+" : "") + ((mods & 8) != 0 ? "Win+" : "");
      if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return s + (char)vk;
      if (vk >= 0x70 && vk <= 0x87) return s + "F" + (vk - 0x6F);
      return s + "0x" + vk.ToString("X2");
    }
  }

  // ---- Transformar el texto seleccionado ----
  // Copia la seleccion (Ctrl+C), la transforma, la pega encima (Ctrl+V) y devuelve el portapapeles.
  public static class Transformar {
    // clave, nombre visible (los acentos van escapados: el script es ASCII)
    public static readonly string[,] Tipos = {
      { "mayusculas", "MAY\u00daSCULAS" }, { "minusculas", "min\u00fasculas" }, { "titulo", "Tipo T\u00edtulo" }, { "frase", "Tipo frase" },
      { "camel", "camelCase" }, { "pascal", "PascalCase" }, { "snake", "snake_case" }, { "constante", "CONSTANTE_CASE" },
      { "kebab", "kebab-case" }, { "sinAcentos", "Quitar acentos" }, { "unaLinea", "Unir en una l\u00ednea" },
      { "limpiar", "Limpiar espacios" }, { "listaIn", "Lista \u2192 IN (...)" }, { "listaComillas", "Lista \u2192 'a', 'b'" },
      { "ordenar", "Ordenar l\u00edneas" }, { "sinDuplicados", "Quitar l\u00edneas repetidas" }, { "escaparSql", "Escapar comillas (SQL)" } };
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    public static string Nombre(string clave) {
      for (int i = 0; i < Tipos.GetLength(0); i++) if (Tipos[i, 0] == clave) return Tipos[i, 1];
      return null;
    }

    public static List<Opcion> ComoOpciones(bool prefijo) {
      List<Opcion> l = new List<Opcion>();
      Image ic = Iconos.Crear("sim:E70F", null);
      for (int i = 0; i < Tipos.GetLength(0); i++) {
        Opcion o = new Opcion();
        o.Nombre = (prefijo ? "Transformar: " : "") + Tipos[i, 1]; o.Transformacion = Tipos[i, 0]; o.Icono = ic;
        l.Add(o);
      }
      return l;
    }

    public static void Aplicar(string clave) {
      Thread th = new Thread(delegate () {
        try {
          DataObject previo = Portapapeles.Guardar();
          uint seq = GetClipboardSequenceNumber();
          Keys.SendCombo(new int[] { 0x11, 0x43 });                           // Ctrl+C
          DateTime fin = DateTime.UtcNow.AddMilliseconds(800);
          while (GetClipboardSequenceNumber() == seq && DateTime.UtcNow < fin) Thread.Sleep(20);
          if (GetClipboardSequenceNumber() == seq) { Aviso.Mostrar("No hay texto seleccionado"); return; }
          Thread.Sleep(30);
          string sel = null;
          try { if (Clipboard.ContainsText()) sel = Clipboard.GetText(); } catch { }
          if (string.IsNullOrEmpty(sel)) {
            Portapapeles.Restaurar(previo, GetClipboardSequenceNumber());
            Aviso.Mostrar("La selecci\u00f3n no es texto"); return;
          }
          Clipboard.SetDataObject(Convertir(clave, sel), true, 10, 50);
          uint propia = GetClipboardSequenceNumber();
          Keys.SendCombo(new int[] { 0x11, 0x56 });                           // Ctrl+V
          Thread.Sleep(Portapapeles.EsperaMs);
          Portapapeles.Restaurar(previo, propia);
        } catch (Exception e) { Acciones.Avisos.Enqueue("No se pudo transformar la seleccion: " + e.Message); }
      });
      th.SetApartmentState(ApartmentState.STA);
      th.IsBackground = true;
      th.Start();
    }

    delegate string FLinea(string l);

    public static string Convertir(string clave, string t) {
      string nl = t.Contains("\r\n") ? "\r\n" : "\n";
      System.Globalization.CultureInfo es = new System.Globalization.CultureInfo("es-ES");
      switch (clave) {
        case "mayusculas": return t.ToUpper(es);
        case "minusculas": return t.ToLower(es);
        case "titulo": return PorLinea(t, nl, delegate (string l) { return es.TextInfo.ToTitleCase(l.ToLower(es)); });
        case "frase": return PorLinea(t, nl, delegate (string l) {
            string b = l.ToLower(es); int i = 0;
            while (i < b.Length && !char.IsLetter(b[i])) i++;
            return i < b.Length ? b.Substring(0, i) + char.ToUpper(b[i], es) + b.Substring(i + 1) : b; });
        case "camel": return PorLinea(t, nl, delegate (string l) { return Unir(Palabras(l), "", 1); });
        case "pascal": return PorLinea(t, nl, delegate (string l) { return Unir(Palabras(l), "", 2); });
        case "snake": return PorLinea(t, nl, delegate (string l) { return Unir(Palabras(l), "_", 0); });
        case "constante": return PorLinea(t, nl, delegate (string l) { return Unir(Palabras(l), "_", 3); });
        case "kebab": return PorLinea(t, nl, delegate (string l) { return Unir(Palabras(l), "-", 0); });
        case "sinAcentos": return SinAcentos(t);
        case "unaLinea": return Regex.Replace(Regex.Replace(t, @"\s*\r?\n\s*", " "), @" {2,}", " ").Trim();
        case "limpiar": {
          List<string> r = new List<string>();
          foreach (string l in t.Replace("\r\n", "\n").Split('\n')) { string x = Regex.Replace(l.Trim(), @"[ \t]{2,}", " "); if (x.Length > 0) r.Add(x); }
          return string.Join(nl, r.ToArray());
        }
        case "listaIn": return "IN (" + ListaSql(t) + ")";
        case "listaComillas": return ListaSql(t);
        case "ordenar": {
          List<string> r = Lineas(t);
          r.Sort(StringComparer.Create(es, true));
          return string.Join(nl, r.ToArray());
        }
        case "sinDuplicados": {
          List<string> r = new List<string>(); HashSet<string> vistas = new HashSet<string>();
          foreach (string l in t.Replace("\r\n", "\n").Split('\n')) if (vistas.Add(l)) r.Add(l);
          return string.Join(nl, r.ToArray());
        }
        case "escaparSql": return t.Replace("'", "''");
      }
      return t;
    }

    static string PorLinea(string t, string nl, FLinea f) {
      string[] ls = t.Replace("\r\n", "\n").Split('\n');
      for (int i = 0; i < ls.Length; i++) ls[i] = f(ls[i]);
      return string.Join(nl, ls);
    }

    static List<string> Lineas(string t) {
      List<string> r = new List<string>();
      foreach (string l in t.Replace("\r\n", "\n").Split('\n')) if (l.Trim().Length > 0) r.Add(l);
      return r;
    }

    // Palabras de un texto: separa por espacios y simbolos y tambien por camelCase (nombreColumna -> nombre, Columna)
    static List<string> Palabras(string l) {
      List<string> r = new List<string>();
      foreach (Match m in Regex.Matches(SinAcentos(l), @"\p{Lu}?\p{Ll}+|\p{Lu}+(?!\p{Ll})|\p{N}+")) r.Add(m.Value);
      return r;
    }

    // modo: 0 todo minusculas, 1 camelCase, 2 PascalCase, 3 todo mayusculas
    static string Unir(List<string> p, string sep, int modo) {
      StringBuilder sb = new StringBuilder();
      for (int i = 0; i < p.Count; i++) {
        string w = p[i].ToLowerInvariant();
        if (modo == 3) w = w.ToUpperInvariant();
        else if (modo == 2 || (modo == 1 && i > 0)) w = char.ToUpperInvariant(w[0]) + w.Substring(1);
        if (i > 0) sb.Append(sep);
        sb.Append(w);
      }
      return sb.ToString();
    }

    public static string SinAcentos(string s) {
      string d = s.Normalize(NormalizationForm.FormD);
      StringBuilder sb = new StringBuilder();
      foreach (char c in d)
        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
      return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Valores separados por lineas (o por comas si es una sola linea) -> 'a', 'b' (sin comillas si todos son numeros)
    static string ListaSql(string t) {
      string sep = t.Contains("\n") ? @"[\r\n\t;]+" : @"[,;\t]+";
      List<string> vals = new List<string>(); HashSet<string> vistos = new HashSet<string>();
      foreach (string x in Regex.Split(t, sep)) {
        string v = x.Trim().Trim('\'').Trim();
        if (v.Length == 0 || !vistos.Add(v)) continue;
        vals.Add(v);
      }
      bool numeros = vals.Count > 0 && vals.TrueForAll(delegate (string v) {
        decimal d; return decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out d); });
      List<string> r = new List<string>();
      foreach (string v in vals) r.Add(numeros ? v : "'" + v.Replace("'", "''") + "'");
      return string.Join(", ", r.ToArray());
    }
  }

  // ---- Estetica de las listas (menu flotante, paleta de comandos y avisos). La configura raton.ps1 ----
  public class Estilo {
    public string Tema = "oscuro";                             // oscuro | claro | sistema
    public Color Acento = Color.FromArgb(47, 111, 223);        // fondo de la fila seleccionada
    public int Opacidad = 100;                                 // % del fondo (20-100)
    public int Radio = 0;                                      // esquinas (px a 96 ppp)
    public bool Detalle = true, Iconos = true, Nombre = true;  // que se ve de cada opcion en las listas: contenido, icono y etiqueta
    public bool RDetalle = false, RIconos = true, RNombre = true;   // y en las rejillas
    public float Letra = 10f;                                  // puntos
    public int Espaciado = 14;                                 // relleno vertical de cada fila (px a 96 ppp)
    public int Difuminado = 0;                                 // fondo de la lista: 0 nitido, 1 difuminado suave, 2 intenso (acrilico)
    public bool Rejilla = false;                               // opciones en rejilla (icono grande y nombre debajo) en vez de lista
    public int Columnas = 0;                                   // columnas de la rejilla (0 = segun cuantas opciones haya)
    public int TamIcono = 32;                                  // tamano de los iconos en la rejilla (px a 96 ppp)
    public int TamIconoLista = 20;                             // y en las listas
  }

  public static class EstiloListas {
    static Estilo guardado = new Estilo();
    static volatile Estilo prueba;                             // estilo de "Probar" mientras se ve la ventana de prueba
    public static Estilo Actual { get { return prueba ?? guardado; } }

    public static Estilo Crear(string tema, string acento, int opacidad, int radio, bool detalle, bool iconos, bool etiqueta, double letra, string espaciado, string difuminado,
                               string disposicion, int columnas, int tamIcono, bool rIconos, bool rEtiqueta, bool rDetalle, int tamIconoLista) {
      Estilo e = new Estilo();
      e.Tema = tema == "claro" || tema == "sistema" ? tema : "oscuro";
      try { if (!string.IsNullOrEmpty(acento)) e.Acento = ColorTranslator.FromHtml(acento); } catch { }
      e.Opacidad = Math.Max(20, Math.Min(100, opacidad));
      e.Radio = Math.Max(0, Math.Min(20, radio));
      e.Detalle = detalle; e.Iconos = iconos; e.Nombre = etiqueta;
      if (!e.Detalle && !e.Iconos && !e.Nombre) e.Nombre = true;      // algo hay que ver
      e.RIconos = rIconos; e.RNombre = rEtiqueta; e.RDetalle = rDetalle;
      if (!e.RDetalle && !e.RIconos && !e.RNombre) e.RNombre = true;
      e.Letra = (float)Math.Max(8, Math.Min(16, letra));
      e.Espaciado = espaciado == "compacto" ? 8 : espaciado == "amplio" ? 22 : 14;
      e.Difuminado = difuminado == "suave" ? 1 : difuminado == "intenso" ? 2 : 0;
      e.Rejilla = disposicion == "rejilla";
      e.Columnas = columnas <= 0 ? 0 : Math.Max(2, Math.Min(6, columnas));
      e.TamIcono = tamIcono <= 0 ? 32 : Math.Max(16, Math.Min(96, tamIcono));
      e.TamIconoLista = tamIconoLista <= 0 ? 20 : Math.Max(16, Math.Min(96, tamIconoLista));
      return e;
    }
    public static void Configurar(Estilo e) { guardado = e ?? new Estilo(); }

    // Abre una lista (o la paleta) de ejemplo con un estilo sin guardar; al cerrarla se vuelve al guardado
    public static void Probar(Estilo e, bool paleta) {
      prueba = e;
      List<Opcion> ops = Ejemplo();
      if (paleta) {
        Accion a = new Accion(); a.Opciones = ops;
        Paleta.Abrir(a);
      } else MenuFlotante.AbrirFijo(0, ops);
    }
    public static void FinPrueba() { prueba = null; }

    static List<Opcion> Ejemplo() {
      string[,] d = {
        { "Copiar", "Ctrl+C", "sim:E8C8", "" }, { "Pegar", "Ctrl+V", "sim:E77F", "" },
        { "Bloc de notas", "notepad.exe", "", "notepad.exe" }, { "Buscar en Google", "https://www.google.com", "sim:E721", "" },
        { "Pasar a may\u00fasculas", "Transformar", "sim:E8D2", "" }, { "Firma del correo", "Pegar texto", "sim:E8BD", "" },
      };
      List<Opcion> l = new List<Opcion>();
      for (int i = 0; i < d.GetLength(0); i++) {
        Opcion o = new Opcion(); o.Nombre = d[i, 0]; o.Detalle = d[i, 1];
        o.Icono = Iconos.Crear(d[i, 2], d[i, 3]);
        o.Accion = delegate { Aviso.Mostrar("Lista de ejemplo: no hace nada"); };
        l.Add(o);
      }
      return l;
    }

    // ---- Colores segun el tema ----
    public static bool EsClaro(Estilo e) {
      if (e.Tema == "claro") return true;
      if (e.Tema != "sistema") return false;
      object v = Microsoft.Win32.Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
      return v is int && (int)v == 1;
    }
    public static Color Fondo(Estilo e) { return EsClaro(e) ? Color.FromArgb(250, 251, 252) : Color.FromArgb(31, 35, 41); }
    public static Color Borde(Estilo e) { return EsClaro(e) ? Color.FromArgb(205, 211, 219) : Color.FromArgb(70, 80, 95); }
    public static Color Texto(Estilo e) { return EsClaro(e) ? Color.FromArgb(29, 35, 48) : Color.White; }
    public static Color TextoSuave(Estilo e) { return EsClaro(e) ? Color.FromArgb(95, 107, 122) : Color.FromArgb(154, 164, 178); }
    public static Color Caja(Estilo e) { return EsClaro(e) ? Color.White : Color.FromArgb(22, 25, 30); }
    // Texto sobre la fila seleccionada: blanco, o casi negro si el acento es muy claro
    public static Color TextoSel(Estilo e) {
      Color a = e.Acento;
      return (a.R * 299 + a.G * 587 + a.B * 114) / 1000 > 170 ? Color.FromArgb(20, 22, 26) : Color.White;
    }

    // Los iconos de simbolo o emoji se dibujan en blanco (monocromos): se tinen con el color del texto
    public static void DibujarIcono(Graphics g, Image img, Rectangle r, Color color) {
      if (img == null) return;
      if (!"mono".Equals(img.Tag) || color.ToArgb() == Color.White.ToArgb()) { g.DrawImage(img, r); return; }
      System.Drawing.Imaging.ColorMatrix m = new System.Drawing.Imaging.ColorMatrix();
      m.Matrix00 = color.R / 255f; m.Matrix11 = color.G / 255f; m.Matrix22 = color.B / 255f;
      using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes()) {
        ia.SetColorMatrix(m);
        g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
      }
    }

    // Windows 11 (compilacion 22000 o posterior): redondea las esquinas de las ventanas normales
    static int esWin11 = -1;
    public static bool EsWindows11 {
      get {
        if (esWin11 < 0) {
          int n = 0;
          try { int.TryParse(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "0") as string, out n); } catch { }
          esWin11 = n >= 22000 ? 1 : 0;
        }
        return esWin11 == 1;
      }
    }
    // Radio real de la lista (px a 96 ppp). Con difuminado las esquinas las pone Windows 11 (solo 4 u 8 px) y en
    // Windows 10 son rectas: el dibujo tiene que coincidir con ellas.
    public static int RadioEfectivo(Estilo e) {
      if (e.Difuminado == 0) return e.Radio;
      if (!EsWindows11 || e.Radio == 0) return 0;
      return e.Radio <= 6 ? 4 : 8;
    }

    // Esquinas de Windows 11 (DWM) para las ventanas con controles (paleta, aviso); en Windows 10 no hace nada
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int valor, int tam);
    public static void Esquinas(IntPtr h, Estilo e) {
      int pref = e.Radio == 0 ? 1 : e.Radio <= 6 ? 3 : 2;      // 1 sin redondear, 3 redondeo pequeno, 2 redondeo normal
      try { DwmSetWindowAttribute(h, 33, ref pref, 4); } catch { }
    }

    // Rectangulo con esquinas redondeadas (radio 0 = recto)
    public static System.Drawing.Drawing2D.GraphicsPath Redondeado(RectangleF r, float radio) {
      System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
      if (radio < 0.5f) { p.AddRectangle(r); return p; }
      float d = Math.Min(radio * 2, Math.Min(r.Width, r.Height));
      p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure();
      return p;
    }
  }

  // ---- Aviso breve junto al cursor (no roba el foco) ----
  public static class Aviso {
    static FormAviso form;
    public static void Mostrar(string texto) {
      MenuFlotante.EnUI(delegate { if (form == null) form = new FormAviso(); form.Mostrar(texto); });
    }
  }

  public class FormAviso : Form {
    string texto = "";
    float esc = 1f;
    Font fuente;
    System.Windows.Forms.Timer reloj = new System.Windows.Forms.Timer();
    public FormAviso() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      BackColor = Color.FromArgb(31, 35, 41); DoubleBuffered = true;
      using (Graphics g = CreateGraphics()) esc = g.DpiX / 96f;
      fuente = new Font("Segoe UI", 10f);
      reloj.Interval = 1800;
      reloj.Tick += delegate { reloj.Stop(); Hide(); };
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020; return cp; }   // + WS_EX_TRANSPARENT
    }
    public void Mostrar(string t) {
      texto = t.Length > 120 ? t.Substring(0, 120) + "..." : t;
      Size s = TextRenderer.MeasureText(texto, fuente);
      int pad = (int)(12 * esc);
      Point c = Cursor.Position;
      Rectangle area = Screen.FromPoint(c).WorkingArea;
      int w = Math.Min(s.Width + 2 * pad, area.Width / 2), h = s.Height + pad;
      int x = Math.Min(c.X + (int)(16 * esc), area.Right - w), y = Math.Min(c.Y + (int)(16 * esc), area.Bottom - h);
      Bounds = new Rectangle(x, y, w, h);
      Estilo es = EstiloListas.Actual;
      BackColor = EstiloListas.Fondo(es);
      EstiloListas.Esquinas(Handle, es);
      if (!Visible) Show();
      Invalidate();
      reloj.Stop(); reloj.Start();
    }
    protected override void OnPaint(PaintEventArgs e) {
      Estilo es = EstiloListas.Actual;
      if (es.Radio == 0) using (Pen p = new Pen(EstiloListas.Borde(es))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
      TextRenderer.DrawText(e.Graphics, texto, fuente, ClientRectangle, EstiloListas.Texto(es),
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }
  }

  // ---- Rueda de gestos: al mantener un boton de gestos (si lo tiene activado), una tarta junto al cursor con la accion
  // de cada direccion; se ilumina la que se ejecutaria al soltar. Aparece tras la espera para no estorbar en los gestos
  // rapidos. Solo se repinta cuando cambia la direccion. Las llamadas llegan del hilo del raton; la ventana va en el de la UI.
  public static class RuedaGestos {
    static readonly object bloqueo = new object();
    static volatile int cidActual = -1;
    static int turno;                                   // cada gesto tiene el suyo: lo que llegue tarde de otro se ignora
    static Accion accion;
    static volatile string dir = "clic";
    static volatile bool visible;
    static System.Threading.Timer espera;
    static FormRueda form;

    public static void Empezar(int cid, Accion a) {
      int t;
      lock (bloqueo) {
        if (espera != null) espera.Dispose();
        t = ++turno; cidActual = cid; accion = a; dir = "clic"; visible = false;
        espera = new System.Threading.Timer(delegate { Mostrar(t); }, null, Math.Max(0, a.EsperaRueda), System.Threading.Timeout.Infinite);
      }
    }

    static void Mostrar(int t) {
      Accion a; string d;
      lock (bloqueo) { if (t != turno || cidActual < 0) return; visible = true; a = accion; d = dir; }
      MenuFlotante.EnUI(delegate {
        if (t != turno || cidActual < 0) return;              // ya se ha soltado
        if (form == null) form = new FormRueda();
        form.Mostrar(a, d);
      });
    }

    public static void Mover(int cid, string d) {
      if (cid != cidActual || d == dir) return;
      dir = d;
      if (visible) MenuFlotante.EnUI(delegate { if (form != null && visible) form.Marcar(d); });
    }

    public static void Terminar(int cid) {
      bool estaba;
      lock (bloqueo) {
        if (cid != cidActual) return;
        cidActual = -1; turno++; estaba = visible; visible = false;
        if (espera != null) { espera.Dispose(); espera = null; }
      }
      if (estaba) MenuFlotante.EnUI(delegate { if (form != null) form.Ocultar(); });
    }
  }

  // Ventana de la rueda: solo el circulo (ventana con transparencia por pixel, sin marco ni fondo cuadrado), con el
  // naranja de los botones del raton en la pagina y los neutros del tema de las listas. Los huecos entre gajos son
  // transparentes. Se dibuja en un mapa de bits y se entrega a Windows con UpdateLayeredWindow (no usa OnPaint).
  public class FormRueda : Form {
    [StructLayout(LayoutKind.Sequential)] struct PUNTO { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct TAMANO { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct MEZCLA { public byte Op, Flags, Alfa, Formato; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDestino, ref PUNTO pos, ref TAMANO tam, IntPtr dcOrigen,
                                                                     ref PUNTO origen, int clave, ref MEZCLA mezcla, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    static readonly string[] ORDEN = { "arriba", "derecha", "abajo", "izquierda" };
    // Angulos de GDI+ (grados desde la derecha, en el sentido de las agujas del reloj) de cada gajo
    static readonly Dictionary<string, int> INICIO = new Dictionary<string, int> { { "derecha", -45 }, { "abajo", 45 }, { "izquierda", 135 }, { "arriba", 225 } };
    // Iconos de la fuente de Windows (Segoe MDL2 Assets, tambien en Windows 10): flechas y una mano para pulsar
    static readonly Dictionary<string, string> FLECHA = new Dictionary<string, string> {
      { "arriba", "\ue74a" }, { "abajo", "\ue74b" }, { "izquierda", "\ue72b" }, { "derecha", "\ue72a" }, { "clic", "\ue7c9" } };
    static readonly Color NARANJA = Color.FromArgb(246, 122, 40);   // el de los botones del raton en la pagina (hsl 24 92% 56%)
    Accion acc;
    string sel = "clic";
    float esc = 1f;
    int lado, px, py;
    Font fuente, fuenteDir;

    public FormRueda() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      using (Graphics g = CreateGraphics()) esc = g.DpiX / 96f;
      fuente = new Font("Segoe UI Semibold", 8.5f); fuenteDir = new Font("Segoe MDL2 Assets", 10f);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      // Con transparencia por pixel (LAYERED), sin foco, fuera de la barra de tareas, siempre encima y sin capturar el raton
      get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020; return cp; }
    }

    public void Mostrar(Accion a, string d) {
      acc = a; sel = d;
      lado = (int)(230 * esc);
      Point c = Cursor.Position;
      Rectangle area = Screen.FromPoint(c).WorkingArea;
      px = Math.Max(area.Left, Math.Min(c.X - lado / 2, area.Right - lado));
      py = Math.Max(area.Top, Math.Min(c.Y - lado / 2, area.Bottom - lado));
      Bounds = new Rectangle(px, py, lado, lado);
      Pintar();
      if (!Visible) Show();
    }
    public void Marcar(string d) { if (d == sel) return; sel = d; if (Visible) Pintar(); }
    public void Ocultar() { if (Visible) Hide(); }

    string Texto(string d) {
      Accion g;
      if (acc == null || acc.Gestos == null || !acc.Gestos.TryGetValue(d, out g) || g == null) return "Nada";
      return string.IsNullOrEmpty(g.Etiqueta) ? g.Tipo : g.Etiqueta;
    }
    bool Tiene(string d) { Accion g; return acc != null && acc.Gestos != null && acc.Gestos.TryGetValue(d, out g) && g != null; }

    void Pintar() {
      using (Bitmap bmp = new Bitmap(lado, lado, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
        using (Graphics g = Graphics.FromImage(bmp)) Dibujar(g);
        Entregar(bmp);
      }
    }

    void Dibujar(Graphics g) {
      g.Clear(Color.Transparent);
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      Estilo es = EstiloListas.Actual;
      bool claro = EstiloListas.EsClaro(es);
      Color caja = claro ? Color.FromArgb(250, 251, 252) : Color.FromArgb(31, 35, 41);
      Color texto = EstiloListas.Texto(es), suave = EstiloListas.TextoSuave(es);
      Color conAccion = Color.FromArgb(claro ? 60 : 80, NARANJA);
      float m = 4 * esc, d = lado - 2 * m, cx = lado / 2f, cy = lado / 2f, r = d / 2f, rCentro = d * 0.19f;
      // Sombra suave alrededor del circulo (para que se distinga sobre cualquier fondo)
      for (int i = 3; i >= 1; i--)
        using (SolidBrush b = new SolidBrush(Color.FromArgb(18, 0, 0, 0))) g.FillEllipse(b, m - i * esc, m - i * esc + esc, d + 2 * i * esc, d + 2 * i * esc);
      // Gajos: base del tema, tinte naranja si tiene accion y naranja lleno el elegido
      foreach (string k in ORDEN) {
        using (SolidBrush b = new SolidBrush(caja)) g.FillPie(b, m, m, d, d, INICIO[k], 90);
        if (k == sel || Tiene(k)) using (SolidBrush b = new SolidBrush(k == sel ? NARANJA : conAccion)) g.FillPie(b, m, m, d, d, INICIO[k], 90);
      }
      // Centro (\u00abPulsar\u00bb)
      using (SolidBrush b = new SolidBrush(caja)) g.FillEllipse(b, cx - rCentro, cy - rCentro, 2 * rCentro, 2 * rCentro);
      if (sel == "clic" || Tiene("clic"))
        using (SolidBrush b = new SolidBrush(sel == "clic" ? NARANJA : conAccion)) g.FillEllipse(b, cx - rCentro, cy - rCentro, 2 * rCentro, 2 * rCentro);
      // Huecos transparentes: las dos diagonales y el anillo alrededor del centro
      g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
      using (Pen p = new Pen(Color.Transparent, 3 * esc)) {
        g.DrawLine(p, cx - r, cy - r, cx - rCentro * 0.72f, cy - rCentro * 0.72f); g.DrawLine(p, cx + rCentro * 0.72f, cy + rCentro * 0.72f, cx + r, cy + r);
        g.DrawLine(p, cx - r, cy + r, cx - rCentro * 0.72f, cy + rCentro * 0.72f); g.DrawLine(p, cx + rCentro * 0.72f, cy - rCentro * 0.72f, cx + r, cy - r);
        g.DrawEllipse(p, cx - rCentro - 1.5f * esc, cy - rCentro - 1.5f * esc, 2 * rCentro + 3 * esc, 2 * rCentro + 3 * esc);
      }
      g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
      // Textos: icono de la direccion y la accion en el centro de cada gajo
      float rTexto = d * 0.34f, ancho = d * 0.34f;
      foreach (string k in ORDEN) {
        double ang = (INICIO[k] + 45) * Math.PI / 180;
        float tx = cx + (float)Math.Cos(ang) * rTexto, ty = cy + (float)Math.Sin(ang) * rTexto;
        Escribir(g, k, Texto(k), tx, ty, ancho, k == sel ? Color.White : (Tiene(k) ? NARANJA : suave), k == sel ? Color.White : (Tiene(k) ? texto : suave));
      }
      Escribir(g, "clic", Tiene("clic") ? Texto("clic") : "Pulsar", cx, cy, rCentro * 1.9f,
               sel == "clic" ? Color.White : (Tiene("clic") ? NARANJA : suave), sel == "clic" ? Color.White : (Tiene("clic") ? texto : suave));
    }

    void Escribir(Graphics g, string dir, string accion, float x, float y, float ancho, Color cDir, Color cTexto) {
      using (StringFormat sf = new StringFormat()) {
        sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Far; sf.Trimming = StringTrimming.EllipsisWord;
        using (SolidBrush b = new SolidBrush(cDir)) g.DrawString(FLECHA[dir], fuenteDir, b, new RectangleF(x - ancho / 2, y - 20 * esc, ancho, 18 * esc), sf);
        sf.LineAlignment = StringAlignment.Near;
        using (SolidBrush b = new SolidBrush(cTexto)) g.DrawString(accion, fuente, b, new RectangleF(x - ancho / 2, y - 1 * esc, ancho, 30 * esc), sf);
      }
    }

    // Entrega el dibujo (con su transparencia) a la ventana
    void Entregar(Bitmap bmp) {
      IntPtr pantalla = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(pantalla), hb = IntPtr.Zero, viejo = IntPtr.Zero;
      try {
        hb = bmp.GetHbitmap(Color.FromArgb(0));
        viejo = SelectObject(dc, hb);
        PUNTO pos = new PUNTO { X = px, Y = py }, origen = new PUNTO();
        TAMANO tam = new TAMANO { W = lado, H = lado };
        MEZCLA mz = new MEZCLA { Op = 0, Flags = 0, Alfa = 255, Formato = 1 };          // AC_SRC_OVER, AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, pantalla, ref pos, ref tam, dc, ref origen, 0, ref mz, 2);   // ULW_ALPHA
      } finally {
        if (viejo != IntPtr.Zero) SelectObject(dc, viejo);
        if (hb != IntPtr.Zero) DeleteObject(hb);
        DeleteDC(dc); ReleaseDC(IntPtr.Zero, pantalla);
      }
    }
  }

  // ---- Chat con Claude en una ventana pequena del navegador por defecto ----
  // Chrome, Edge, Brave o Vivaldi: modo aplicacion (sin pestanas ni barra de direcciones) junto al cursor.
  // Otros navegadores: se abre la direccion normalmente. Opcionalmente prepara en el portapapeles una
  // pregunta con el texto seleccionado ({seleccion} en la plantilla) para pegarla con Ctrl+V.
  public static class Chat {
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr despues, int x, int y, int cx, int cy, uint flags);

    // llevar: "" nada, "seleccion" texto seleccionado, "recorte" imagen de una zona de la pantalla,
    // "recorteTexto" texto leido (OCR) de una zona de la pantalla. El resultado queda en el portapapeles.
    public static void Abrir(string url, string llevar, string plantilla, int ancho, int alto) {
      Native.SetProcessDPIAware();                                          // coordenadas reales de pantalla
      string dir = string.IsNullOrEmpty(url) ? "https://claude.ai/new" : url;
      int w = ancho > 0 ? ancho : 400, h = alto > 0 ? alto : 600;
      if (llevar == "elegir") {
        // Se pregunta que llevar con la ventana flotante (sin foco: la seleccion de la aplicacion se conserva)
        List<Opcion> ops = new List<Opcion>();
        ops.Add(OpcionLlevar("Solo abrir el chat", "E8BD", url, "", plantilla, ancho, alto));
        ops.Add(OpcionLlevar("Texto seleccionado", "E8C8", url, "seleccion", plantilla, ancho, alto));
        if (OcrPuente.Recortar != null) {
          ops.Add(OpcionLlevar("Recorte de pantalla (imagen)", "E91B", url, "recorte", plantilla, ancho, alto));
          ops.Add(OpcionLlevar("Recorte de pantalla (texto)", "E8A5", url, "recorteTexto", plantilla, ancho, alto));
        }
        MenuFlotante.AbrirFijo(-1, ops);
        return;
      }
      if (llevar == "recorte" || llevar == "recorteTexto") {
        if (OcrPuente.Recortar == null) { Aviso.Mostrar("Recorte no disponible: guarda la configuracion para cargar el modulo de OCR"); return; }
        // Se elige la zona y, despues, se abre el chat junto a donde termino el recuadro
        OcrPuente.Recortar(delegate (Bitmap b) {
          try {
            using (b) {
              if (llevar == "recorte") {
                Clipboard.SetImage(b);
                Aviso.Mostrar("Captura preparada: p\u00e9gala en el chat con Ctrl+V");
              } else {
                string leido = OcrPuente.Texto != null ? OcrPuente.Texto(b) : "";
                if (string.IsNullOrEmpty(leido.Trim())) Aviso.Mostrar("No se ha encontrado texto en esa zona");
                else PrepararPregunta(plantilla, leido);
              }
            }
            AbrirVentana(dir, Cursor.Position, w, h);
          } catch (Exception e) { Acciones.Avisos.Enqueue("Chat con Claude: " + e.Message); }
        });
        return;
      }
      Point cursor = Cursor.Position;
      Thread th = new Thread(delegate () {
        try {
          if (llevar == "seleccion") {
            string sel = CopiarSeleccion();
            if (sel != null) PrepararPregunta(plantilla, sel);
            else Aviso.Mostrar("No hab\u00eda texto seleccionado");
          }
          AbrirVentana(dir, cursor, w, h);
        } catch (Exception e) { Acciones.Avisos.Enqueue("Chat con Claude: " + e.Message); }
      });
      th.SetApartmentState(ApartmentState.STA);
      th.IsBackground = true;
      th.Start();
    }

    static Opcion OpcionLlevar(string nombre, string simbolo, string url, string llevar, string plantilla, int ancho, int alto) {
      Opcion o = new Opcion();
      o.Nombre = nombre; o.Detalle = "Claude"; o.Icono = Iconos.Crear("sim:" + simbolo, null);
      o.Accion = delegate { Abrir(url, llevar, plantilla, ancho, alto); };
      return o;
    }

    // Mete el texto en la plantilla ({seleccion}) y lo deja en el portapapeles. Hilo STA.
    static void PrepararPregunta(string plantilla, string texto) {
      string q = (string.IsNullOrEmpty(plantilla) ? "{seleccion}" : plantilla).Replace("{seleccion}", texto);
      Clipboard.SetDataObject(q.Replace("\r\n", "\n").Replace("\n", "\r\n"), true, 10, 50);
      Aviso.Mostrar("Pregunta preparada: p\u00e9gala en el chat con Ctrl+V");
    }

    // Copia la seleccion con Ctrl+C y la devuelve (null si no habia texto seleccionado). Hilo STA.
    static string CopiarSeleccion() {
      uint seq = GetClipboardSequenceNumber();
      Keys.SendCombo(new int[] { 0x11, 0x43 });
      DateTime fin = DateTime.UtcNow.AddMilliseconds(800);
      while (GetClipboardSequenceNumber() == seq && DateTime.UtcNow < fin) Thread.Sleep(20);
      if (GetClipboardSequenceNumber() == seq) return null;
      Thread.Sleep(30);
      try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; }
    }

    // ancho y alto en pixeles logicos (a 100 %); se escalan con el escalado de la pantalla
    static void AbrirVentana(string url, Point cursor, int ancho, int alto) {
      string nav = NavegadorPorDefecto();
      string exe = nav != null ? System.IO.Path.GetFileName(nav).ToLowerInvariant() : "";
      bool chromium = exe == "chrome.exe" || exe == "msedge.exe" || exe == "brave.exe" || exe == "vivaldi.exe";
      if (chromium) {
        float esc;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) esc = g.DpiX / 96f;
        Rectangle area = Screen.FromPoint(cursor).WorkingArea;
        int w = Math.Min((int)(ancho * esc), area.Width), h = Math.Min((int)(alto * esc), area.Height);
        int x = Math.Max(area.Left, Math.Min(cursor.X + (int)(16 * esc), area.Right - w));
        int y = Math.Max(area.Top, Math.Min(cursor.Y - (int)(40 * esc), area.Bottom - h));
        string proceso = System.IO.Path.GetFileNameWithoutExtension(exe);
        HashSet<IntPtr> antes = VentanasDe(proceso);
        ProcessStartInfo psi = new ProcessStartInfo(nav, "--app=\"" + url.Replace("\"", "") + "\" --window-size=" + ancho + "," + alto);
        psi.UseShellExecute = true;
        Process.Start(psi);
        // Si el navegador ya estaba abierto ignora tamano y posicion: se busca la ventana nueva y se coloca
        IntPtr nueva = IntPtr.Zero;
        DateTime fin = DateTime.UtcNow.AddSeconds(8);
        while (nueva == IntPtr.Zero && DateTime.UtcNow < fin) {
          Thread.Sleep(120);
          foreach (IntPtr v in VentanasDe(proceso)) if (!antes.Contains(v)) { nueva = v; break; }
        }
        if (nueva == IntPtr.Zero) return;
        for (int i = 0; i < 4; i++) {                                         // el navegador a veces restaura su tamano guardado al terminar de crearla
          SetWindowPos(nueva, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0040);      // SWP_NOZORDER | SWP_SHOWWINDOW
          Thread.Sleep(250);
        }
      } else {
        ProcessStartInfo psi = new ProcessStartInfo(url);
        psi.UseShellExecute = true;
        Process.Start(psi);
      }
    }

    // Ventanas principales visibles de un proceso (por nombre, sin extension)
    static HashSet<IntPtr> VentanasDe(string proceso) {
      HashSet<IntPtr> r = new HashSet<IntPtr>();
      HashSet<uint> pids = new HashSet<uint>();
      foreach (Process p in Process.GetProcessesByName(proceso)) { pids.Add((uint)p.Id); p.Dispose(); }
      EnumWindows(delegate (IntPtr h, IntPtr l) {
        if (!IsWindowVisible(h)) return true;
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        if (pids.Contains(pid)) r.Add(h);
        return true;
      }, IntPtr.Zero);
      return r;
    }

    // Ejecutable del navegador por defecto para https (asociacion del usuario en el registro)
    internal static string NavegadorPorDefecto() {
      try {
        string progId = null;
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
          if (k != null) progId = k.GetValue("ProgId") as string;
        if (string.IsNullOrEmpty(progId)) return null;
        string cmd = null;
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
          if (k != null) cmd = k.GetValue("") as string;
        if (string.IsNullOrEmpty(cmd)) return null;
        cmd = cmd.Trim();
        string ruta = cmd.StartsWith("\"") ? cmd.Substring(1, cmd.IndexOf('"', 1) - 1) : cmd.Split(' ')[0];
        return System.IO.File.Exists(ruta) ? ruta : null;
      } catch { return null; }
    }
  }

  // ---- Puente con el modulo de OCR (ocr.cs) ----
  // El OCR va en un fichero aparte que raton.ps1 solo carga si hay alguna accion de OCR configurada.
  public static class OcrPuente {
    public static Action Iniciar;
    public static Action<Action<Bitmap>> Recortar;                         // recorte de pantalla (imagen)
    public static Func<Bitmap, string> Texto;                              // OCR de una imagen
    public static void Lanzar() {
      if (Iniciar != null) Iniciar();
      else Aviso.Mostrar("OCR no disponible: guarda la configuracion con alguna accion de OCR");
    }
    public static void Avisar(string t) { Aviso.Mostrar(t); }
    public static void Registro(string t) { Acciones.Avisos.Enqueue(t); }
    public static void Activar(IntPtr h) { Ventanas.Activar(h); }
    public static void EnUI(MethodInvoker a) { MenuFlotante.EnUI(a); }
  }

  // ---- Captura de pantalla: una sola accion con tres variantes (modo) ----
  // "texto": recorte con OCR al portapapeles; "imagen": recorte al portapapeles como imagen;
  // "pantalla": tecla Imprimir pantalla de Windows (pantalla completa, sin modulo de OCR).
  public static class Captura {
    public static void Hacer(string modo) {
      if (modo == "pantalla") { Keys.SendCombo(new int[] { 0x2C }); return; }   // Imprimir pantalla (VK 0x2C)
      // Color bajo el cursor ("color" = HEX, "colorRgb" = RGB; la configuracion puede llegar en minusculas)
      if (string.Equals(modo, "color", StringComparison.OrdinalIgnoreCase) || string.Equals(modo, "colorRgb", StringComparison.OrdinalIgnoreCase)) {
        ColorPantalla.Copiar(string.Equals(modo, "colorRgb", StringComparison.OrdinalIgnoreCase) ? "rgb" : "hex");
        return;
      }
      if (modo == "imagen") {
        if (OcrPuente.Recortar == null) { Aviso.Mostrar("Recorte no disponible: guarda la configuracion para cargar el modulo de captura"); return; }
        OcrPuente.Recortar(delegate (Bitmap b) {
          try { using (b) { Clipboard.SetImage(b); Aviso.Mostrar("Imagen copiada: pulsa Ctrl+V para pegar"); } }
          catch (Exception e) { Acciones.Avisos.Enqueue("Recorte de imagen: " + e.Message); }
        });
        return;
      }
      OcrPuente.Lanzar();                                                        // "texto" (y valor por defecto)
    }
  }

  // ---- Paleta de comandos: caja de busqueda con tus entradas, ventanas, transformaciones y OCR ----
  public static class Paleta {
    static FormPaleta form;
    public static void Abrir(Accion a) {
      IntPtr previa = Native.GetForegroundWindow();
      List<Opcion> items = new List<Opcion>();
      if (a.Opciones != null) items.AddRange(a.Opciones);
      if (a.IncluirOcr) {
        Opcion o = new Opcion(); o.Nombre = "Copiar texto de la pantalla"; o.Detalle = "OCR"; o.Ocr = true; o.Icono = Iconos.Crear("sim:E722", null);
        items.Add(o);
      }
      if (a.IncluirTransformaciones) items.AddRange(Transformar.ComoOpciones(true));
      if (a.IncluirVentanas) items.AddRange(Ventanas.Listar());
      MenuFlotante.EnUI(delegate { if (form == null) form = new FormPaleta(); form.Mostrar(items, previa); });
    }
  }

  public class FormPaleta : Form {
    TextBox caja = new TextBox();
    ListBox lista = new ListBox();
    List<Opcion> todos = new List<Opcion>(), vista = new List<Opcion>();
    IntPtr previa;
    float esc = 1f;
    Font fuente, fuenteDet;
    bool ejecutando;
    Panel hueco = new Panel();
    Estilo es = new Estilo();

    public FormPaleta() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      KeyPreview = true;
      using (Graphics g = CreateGraphics()) esc = g.DpiX / 96f;
      Padding = new Padding((int)(10 * esc));
      caja.Font = new Font("Segoe UI", 13f); caja.BorderStyle = BorderStyle.FixedSingle; caja.Dock = DockStyle.Top;
      hueco.Dock = DockStyle.Top; hueco.Height = (int)(8 * esc);
      lista.Dock = DockStyle.Fill; lista.BorderStyle = BorderStyle.None;
      lista.DrawMode = DrawMode.OwnerDrawFixed; lista.IntegralHeight = false;
      Controls.Add(lista); Controls.Add(hueco); Controls.Add(caja);
      caja.TextChanged += delegate { Filtrar(); };
      caja.KeyDown += Teclas; lista.KeyDown += Teclas;
      lista.DrawItem += Dibujar;
      lista.MouseClick += delegate { EjecutarSel(); };
      Deactivate += delegate { if (!ejecutando) Hide(); };
      VisibleChanged += delegate { if (!Visible) EstiloListas.FinPrueba(); };
      AplicarEstilo();
    }

    // Colores, letra, translucidez y esquinas segun la estetica de las listas. La paleta tiene controles de
    // Windows dentro, asi que la translucidez es de toda la ventana (con un minimo para que se pueda leer).
    void AplicarEstilo() {
      es = EstiloListas.Actual;
      Color fondo = EstiloListas.Fondo(es);
      BackColor = fondo; hueco.BackColor = fondo; lista.BackColor = fondo; lista.ForeColor = EstiloListas.Texto(es);
      caja.BackColor = EstiloListas.Caja(es); caja.ForeColor = EstiloListas.Texto(es);
      if (fuente == null || fuente.Size != es.Letra + 0.5f) {
        fuente = new Font("Segoe UI", es.Letra + 0.5f); fuenteDet = new Font("Segoe UI", Math.Max(7f, es.Letra - 1.5f));
      }
      lista.ItemHeight = (int)(fuente.Height + es.Espaciado * esc);
      Opacity = Math.Max(60, es.Opacidad) / 100.0;
      EstiloListas.Esquinas(Handle, es);
    }

    public void Mostrar(List<Opcion> items, IntPtr prev) {
      todos = items; previa = prev; ejecutando = false;
      AplicarEstilo();
      Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
      int w = Math.Min((int)(600 * esc), area.Width - 40);
      int filas = Math.Min(12, Math.Max(1, items.Count));
      int h = caja.PreferredHeight + (int)(8 * esc) + filas * lista.ItemHeight + Padding.Vertical;
      Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + area.Height / 5, w, h);
      caja.Text = "";
      Filtrar();
      Show();
      Ventanas.Activar(Handle);
      Activate();
      caja.Focus();
    }

    static string Norm(string s) { return Transformar.SinAcentos(s ?? "").ToLowerInvariant(); }

    // Filtra por nombre y detalle sin distinguir mayusculas ni acentos; primero las que empiezan por lo escrito
    void Filtrar() {
      string q = Norm(caja.Text.Trim());
      List<Opcion> empiezan = new List<Opcion>(), contienen = new List<Opcion>();
      foreach (Opcion o in todos) {
        string n = Norm(o.Nombre), d = Norm(o.Detalle);
        if (q.Length == 0 || n.StartsWith(q)) empiezan.Add(o);
        else if (n.Contains(q) || d.Contains(q)) contienen.Add(o);
      }
      empiezan.AddRange(contienen);
      vista = empiezan;
      lista.BeginUpdate();
      lista.Items.Clear();
      foreach (Opcion o in vista) lista.Items.Add(o.Nombre ?? "");
      if (vista.Count > 0) lista.SelectedIndex = 0;
      lista.EndUpdate();
    }

    void Teclas(object s, KeyEventArgs e) {
      if (e.KeyCode == System.Windows.Forms.Keys.Down) { if (lista.SelectedIndex < vista.Count - 1) lista.SelectedIndex++; }
      else if (e.KeyCode == System.Windows.Forms.Keys.Up) { if (lista.SelectedIndex > 0) lista.SelectedIndex--; }
      else if (e.KeyCode == System.Windows.Forms.Keys.Enter) EjecutarSel();
      else if (e.KeyCode == System.Windows.Forms.Keys.Escape) { ejecutando = true; Hide(); Ventanas.Activar(previa); }
      else return;
      e.Handled = true; e.SuppressKeyPress = true;
    }

    // Se devuelve el foco a la aplicacion anterior y despues se ejecuta (las ventanas se activan directamente)
    void EjecutarSel() {
      int i = lista.SelectedIndex;
      if (i < 0 || i >= vista.Count) return;
      Opcion op = vista[i];
      ejecutando = true;
      Hide();
      if (op.Ventana != IntPtr.Zero) { Ventanas.Activar(op.Ventana); return; }
      Ventanas.Activar(previa);
      System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
      t.Interval = 150;
      t.Tick += delegate { t.Stop(); t.Dispose(); Ejecutor.EjecutarOpcion(op); };
      t.Start();
    }

    void Dibujar(object s, DrawItemEventArgs e) {
      if (e.Index < 0 || e.Index >= vista.Count) return;
      Opcion op = vista[e.Index];
      bool sel = (e.State & DrawItemState.Selected) != 0;
      Graphics g = e.Graphics;
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      using (SolidBrush b = new SolidBrush(EstiloListas.Fondo(es))) g.FillRectangle(b, e.Bounds);
      if (sel) {
        RectangleF rs = new RectangleF(e.Bounds.X + 0.5f, e.Bounds.Y + 1, e.Bounds.Width - 1, e.Bounds.Height - 2);
        using (SolidBrush b = new SolidBrush(es.Acento))
        using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(rs, Math.Min(es.Radio, 8) * esc)) g.FillPath(b, p);
      }
      Color texto = sel ? EstiloListas.TextoSel(es) : EstiloListas.Texto(es);
      int mx = (int)(10 * esc), lado = (int)(20 * esc), x0 = e.Bounds.X + mx;
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      if (es.Iconos) {
        EstiloListas.DibujarIcono(g, op.Icono, new Rectangle(x0, e.Bounds.Y + (e.Bounds.Height - lado) / 2, lado, lado), texto);
        x0 += lado + mx;
      }
      string det = es.Detalle && op.Detalle != op.Nombre ? (op.Detalle ?? "") : "";
      int anchoDet = det.Length > 0 ? Math.Min(TextRenderer.MeasureText(det, fuenteDet).Width, (e.Bounds.Right - x0) / 2) : 0;
      TextRenderer.DrawText(g, op.Nombre ?? "", fuente, new Rectangle(x0, e.Bounds.Y, e.Bounds.Right - x0 - anchoDet - 2 * mx, e.Bounds.Height),
                            texto, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
      if (det.Length > 0)
        TextRenderer.DrawText(g, det, fuenteDet, new Rectangle(e.Bounds.Right - mx - anchoDet, e.Bounds.Y, anchoDet, e.Bounds.Height),
                              sel ? texto : EstiloListas.TextoSuave(es), TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
    }
  }

  // ---- Iconos de las opciones de una lista ----
  // Formato: "sim:E8C8" (simbolo de la fuente de iconos de Windows), "emo:<emoji>", "arc:<ruta .ico/.png/.exe>",
  // "ninguno" o vacio = automatico (las opciones "Ejecutar" usan el icono del programa).
  public static class Iconos {
    static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    static string fuenteSimbolos;
    const int LADO = 64;                                          // se generan a 64 px: nitidos tambien en la rejilla con iconos grandes

    public static Image Crear(string spec, string comando) {
      try {
        if (!string.IsNullOrEmpty(spec)) {
          if (spec == "ninguno") return null;
          if (spec.StartsWith("sim:")) return Texto(char.ConvertFromUtf32(Convert.ToInt32(spec.Substring(4), 16)), FuenteSimbolos());
          if (spec.StartsWith("emo:")) return Texto(spec.Substring(4), "Segoe UI Emoji");
          if (spec.StartsWith("arc:")) return DeArchivo(Environment.ExpandEnvironmentVariables(spec.Substring(4).Trim().Trim('"')));
        }
        if (!string.IsNullOrEmpty(comando)) {
          string exe = ResolverPrograma(comando);
          if (exe != null) return DeArchivo(exe);
        }
      } catch { }
      return null;
    }

    // Windows 11 trae "Segoe Fluent Icons"; Windows 10, "Segoe MDL2 Assets" (mismos codigos)
    static string FuenteSimbolos() {
      if (fuenteSimbolos == null) {
        fuenteSimbolos = "Segoe MDL2 Assets";
        using (System.Drawing.Text.InstalledFontCollection fc = new System.Drawing.Text.InstalledFontCollection())
          foreach (FontFamily ff in fc.Families) if (ff.Name == "Segoe Fluent Icons") fuenteSimbolos = ff.Name;
      }
      return fuenteSimbolos;
    }

    static Image Texto(string s, string fuente) {
      Bitmap b = new Bitmap(LADO, LADO);
      using (Graphics g = Graphics.FromImage(b))
      using (Font f = new Font(fuente, 44f, GraphicsUnit.Pixel))
      using (StringFormat sf = new StringFormat()) {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
        g.DrawString(s, f, Brushes.White, new RectangleF(0, 0, LADO, LADO), sf);
      }
      b.Tag = "mono";                                              // blanco: se tine segun el tema (EstiloListas)
      return b;
    }

    // Icono de un programa al tamano pedido (Windows lo saca de la mejor resolucion que traiga el .exe/.dll)
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(string fichero, int indice, uint flags, out IntPtr grande, out IntPtr pequeno, uint tamanos);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    static Image IconoPrograma(string ruta) {
      IntPtr g = IntPtr.Zero, p = IntPtr.Zero;
      try {
        if (SHDefExtractIcon(ruta, 0, 0, out g, out p, (uint)LADO | (16u << 16)) == 0 && g != IntPtr.Zero)
          using (Icon ic = Icon.FromHandle(g)) return ic.ToBitmap();
      } catch { } finally { if (g != IntPtr.Zero) DestroyIcon(g); if (p != IntPtr.Zero) DestroyIcon(p); }
      using (Icon ic = Icon.ExtractAssociatedIcon(ruta)) return ic != null ? ic.ToBitmap() : null;   // sin icono propio: el asociado
    }

    public static Image DeArchivo(string ruta) {
      lock (cache) { Image img; if (cache.TryGetValue(ruta, out img)) return img; }
      Image r = null;
      string ext = System.IO.Path.GetExtension(ruta).ToLowerInvariant();
      if (System.IO.File.Exists(ruta)) {
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif") {
          using (Image i = Image.FromFile(ruta)) r = new Bitmap(i, LADO, LADO);          // copia: no deja el fichero bloqueado
        } else if (ext == ".ico") {
          using (Icon ic = new Icon(ruta, LADO, LADO)) r = ic.ToBitmap();
        } else {
          r = IconoPrograma(ruta);
        }
      }
      lock (cache) { cache[ruta] = r; }
      return r;
    }

    // Programa de un comando de "Ejecutar": ruta directa, carpetas del PATH o registro App Paths
    static string ResolverPrograma(string comando) {
      string c = comando.Trim(), f;
      if (c.StartsWith("\"")) { int fin = c.IndexOf('"', 1); f = fin > 0 ? c.Substring(1, fin - 1) : c.Trim('"'); }
      else if (System.IO.File.Exists(c)) f = c;
      else { int sp = c.IndexOf(' '); f = sp > 0 ? c.Substring(0, sp) : c; }
      f = Environment.ExpandEnvironmentVariables(f);
      if (f.Contains("://")) return null;
      if (System.IO.File.Exists(f)) return f;
      string nombre = System.IO.Path.HasExtension(f) ? f : f + ".exe";
      foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
        try { string p = System.IO.Path.Combine(dir.Trim(), nombre); if (System.IO.File.Exists(p)) return p; } catch { }
      }
      foreach (Microsoft.Win32.RegistryKey raiz in new Microsoft.Win32.RegistryKey[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine }) {
        using (Microsoft.Win32.RegistryKey k = raiz.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + nombre)) {
          if (k == null) continue;
          string v = k.GetValue("") as string;
          if (!string.IsNullOrEmpty(v)) { v = Environment.ExpandEnvironmentVariables(v.Trim('"')); if (System.IO.File.Exists(v)) return v; }
        }
      }
      return null;
    }
  }

  // ---- Lista de programas abiertos (como Alt+Tab) y cambio de foco a uno de ellos ----
  public static class Ventanas {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Dictionary<string, Image> iconos = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    const int MAXIMO = 25;

    // Ventanas principales visibles, en el orden de Alt+Tab (la mas reciente primero)
    public static List<Opcion> Listar() {
      List<Opcion> l = new List<Opcion>();
      uint propio = (uint)Process.GetCurrentProcess().Id;
      EnumWindows(delegate (IntPtr h, IntPtr p) {
        if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;        // GW_OWNER: ventanas secundarias fuera
        if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                            // WS_EX_TOOLWINDOW
        int oculta;
        if (DwmGetWindowAttribute(h, 14, out oculta, 4) == 0 && oculta != 0) return true; // DWMWA_CLOAKED (otro escritorio, apps suspendidas)
        int len = GetWindowTextLength(h);
        if (len == 0) return true;
        StringBuilder sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        if (pid == propio) return true;
        string ruta = RutaProceso(pid);
        string exe = ruta != null ? System.IO.Path.GetFileName(ruta) : "";
        if (sb.ToString() == "Program Manager") return true;                             // el escritorio
        Opcion o = new Opcion();
        o.Nombre = sb.ToString(); o.Detalle = exe; o.Ventana = h; o.Icono = Icono(ruta);
        l.Add(o);
        return l.Count < MAXIMO;
      }, IntPtr.Zero);
      return l;
    }

    static string RutaProceso(uint pid) {
      IntPtr hp = OpenProcess(0x1000, false, pid);                                       // PROCESS_QUERY_LIMITED_INFORMATION
      if (hp == IntPtr.Zero) return null;
      try {
        StringBuilder sb = new StringBuilder(1024); int n = sb.Capacity;
        return QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null;
      } finally { CloseHandle(hp); }
    }

    static Image Icono(string ruta) {
      if (string.IsNullOrEmpty(ruta)) return null;
      lock (iconos) {
        Image img;
        if (iconos.TryGetValue(ruta, out img)) return img;
        try { img = Iconos.DeArchivo(ruta); } catch { img = null; }
        iconos[ruta] = img;
        return img;
      }
    }

    // Pone la ventana en primer plano. Windows limita que un proceso en segundo plano cambie el foco:
    // se intenta directamente, despues uniendo la entrada con la ventana activa y por ultimo con una pulsacion de Alt.
    public static void Activar(IntPtr h) {
      if (IsIconic(h)) ShowWindow(h, 9);                                                 // SW_RESTORE
      if (SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;
      IntPtr fg = Native.GetForegroundWindow();
      uint nada;
      uint hiloFg = Native.GetWindowThreadProcessId(fg, out nada), yo = GetCurrentThreadId();
      bool unido = hiloFg != 0 && hiloFg != yo && AttachThreadInput(yo, hiloFg, true);
      BringWindowToTop(h); SetForegroundWindow(h);
      if (unido) AttachThreadInput(yo, hiloFg, false);
      if (Native.GetForegroundWindow() != h) {
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero); Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(h);
      }
    }
  }

  // ---- Copia y restauracion del portapapeles completo (texto, imagen, ficheros, formato...) ----
  // Al pegar o transformar texto, lo que habia se devuelve despues tal cual, en todos sus formatos.
  public static class Portapapeles {
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    public static volatile int EsperaMs = 1000;    // espera antes de restaurar: las aplicaciones lentas leen el portapapeles tarde

    // Copia de todos los formatos que se pueden leer; null si estaba vacio. Hilo STA.
    public static DataObject Guardar() {
      try {
        IDataObject o = Clipboard.GetDataObject();
        if (o == null) return null;
        DataObject copia = new DataObject();
        int n = 0;
        foreach (string f in o.GetFormats(false)) {
          try { object d = o.GetData(f, false); if (d != null) { copia.SetData(f, false, d); n++; } } catch { }
        }
        return n > 0 ? copia : null;
      } catch { return null; }
    }

    // Devuelve la copia solo si el portapapeles sigue teniendo lo que puso el servidor
    // (si el usuario ha copiado otra cosa entretanto, no se pisa). Hilo STA.
    public static void Restaurar(DataObject previo, uint secuenciaPropia) {
      if (previo == null || GetClipboardSequenceNumber() != secuenciaPropia) return;
      try { Clipboard.SetDataObject(previo, true, 10, 50); } catch { }
    }
  }

  // ---- Pegar un texto y ejecutar un programa o comando ----
  public static class Acciones {
    public static ConcurrentQueue<string> Avisos = new ConcurrentQueue<string>();

    // Copia el texto al portapapeles, pega con Ctrl+V y despues devuelve lo que habia (en todos sus formatos).
    // El portapapeles necesita un hilo STA: se usa el de la ventana flotante.
    // El texto es una plantilla: las variables ({fecha}, {portapapeles}, {cursor}...) se rellenan al pegar.
    public static void PegarTexto(string texto) {
      if (string.IsNullOrEmpty(texto)) return;
      MenuFlotante.EnUI(delegate {
        string previoTexto = null;
        try { if (Clipboard.ContainsText()) previoTexto = Clipboard.GetText(); } catch { }
        DataObject previo = Portapapeles.Guardar();
        int atras;
        string final = ExpandirPlantilla(texto, previoTexto, out atras);
        if (final.Length == 0) return;
        try { Clipboard.SetDataObject(final, true, 10, 50); }
        catch (Exception e) { Avisos.Enqueue("No se pudo usar el portapapeles: " + e.Message); return; }
        uint propia = Portapapeles.GetClipboardSequenceNumber();
        Keys.SendCombo(new int[] { 0x11, 0x56 });                 // Ctrl+V
        if (atras > 0) {
          // {cursor}: tras pegar, el cursor retrocede hasta donde estaba la marca
          System.Windows.Forms.Timer tc = new System.Windows.Forms.Timer();
          tc.Interval = 120;
          tc.Tick += delegate { tc.Stop(); tc.Dispose(); for (int i = 0; i < Math.Min(atras, 2000); i++) Keys.SendCombo(new int[] { 0x125 }); };
          tc.Start();
        }
        if (previo == null) return;
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = Portapapeles.EsperaMs;
        t.Tick += delegate {
          t.Stop(); t.Dispose();
          Portapapeles.Restaurar(previo, propia);
        };
        t.Start();
      });
    }

    // Variables: {fecha} {hora} {fechahora} (admiten formato: {fecha:dd-MM-yyyy}), {dia}, {mes}, {anio},
    // {portapapeles}, {usuario}, {equipo} y {cursor} (posicion final del cursor). Las desconocidas se dejan tal cual.
    public static string ExpandirPlantilla(string t, string portapapeles, out int atras) {
      System.Globalization.CultureInfo es = new System.Globalization.CultureInfo("es-ES");
      DateTime ahora = DateTime.Now;
      string r = Regex.Replace(t, @"\{([a-z\u00f1]+)(?::([^}]*))?\}", delegate (Match m) {
        string n = m.Groups[1].Value.ToLowerInvariant();
        string fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
        try {
          switch (n) {
            case "fecha": return ahora.ToString(fmt ?? "dd/MM/yyyy", es);
            case "hora": return ahora.ToString(fmt ?? "HH:mm", es);
            case "fechahora": return ahora.ToString(fmt ?? "dd/MM/yyyy HH:mm", es);
            case "dia": return ahora.ToString("dddd", es);
            case "mes": return ahora.ToString("MMMM", es);
            case "anio": case "a\u00f1o": return ahora.ToString("yyyy", es);
            case "portapapeles": return portapapeles ?? "";
            case "usuario": return Environment.UserName;
            case "equipo": return Environment.MachineName;
            case "cursor": return "\u0001";                                // marca temporal
          }
        } catch (FormatException) { }
        return m.Value;
      }, RegexOptions.IgnoreCase);
      r = r.Replace("\r\n", "\n");
      atras = 0;
      int i = r.IndexOf('\u0001');
      if (i >= 0) {
        string tras = r.Substring(i + 1).Replace("\u0001", "");
        atras = tras.Length;                                              // cada salto de linea cuenta como una tecla
        r = r.Substring(0, i) + tras;
      }
      return r.Replace("\n", "\r\n");
    }

    // Abre un programa, documento, carpeta o direccion web con el mecanismo normal de Windows
    // (como Win+R), sin pasar por cmd ni PowerShell. Formato: programa [argumentos]; con espacios, entre comillas.
    public static void Ejecutar(string comando) {
      if (string.IsNullOrWhiteSpace(comando)) return;
      string c = comando.Trim(), fichero, args = "";
      if (c.StartsWith("\"")) {
        int fin = c.IndexOf('"', 1);
        fichero = fin > 0 ? c.Substring(1, fin - 1) : c.Trim('"');
        if (fin > 0) args = c.Substring(fin + 1).Trim();
      } else if (System.IO.File.Exists(c) || System.IO.Directory.Exists(c)) {
        fichero = c;                                               // ruta con espacios sin comillas
      } else {
        int sp = c.IndexOf(' ');
        fichero = sp > 0 ? c.Substring(0, sp) : c;
        if (sp > 0) args = c.Substring(sp + 1).Trim();
      }
      Thread th = new Thread(delegate () {
        try {
          ProcessStartInfo psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(fichero), args);
          psi.UseShellExecute = true;
          psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
          Process.Start(psi);
        } catch (Exception e) { Avisos.Enqueue("No se pudo ejecutar '" + comando + "': " + e.Message); }
      });
      th.SetApartmentState(ApartmentState.STA);
      th.IsBackground = true;
      th.Start();
    }
  }

  // Imitacion de los botones del raton (clic izquierdo, derecho, central, atras, adelante)
  public static class Emulacion {
    public static bool Pulsar(int cid, bool down) {
      switch (cid) {
        case 0x0050: Native.mouse_event(down ? 0x0002u : 0x0004u, 0, 0, 0, UIntPtr.Zero); return true;   // izquierdo
        case 0x0051: Native.mouse_event(down ? 0x0008u : 0x0010u, 0, 0, 0, UIntPtr.Zero); return true;   // derecho
        case 0x0052: Native.mouse_event(down ? 0x0020u : 0x0040u, 0, 0, 0, UIntPtr.Zero); return true;   // central
        case 0x0053: Native.mouse_event(down ? 0x0080u : 0x0100u, 0, 0, 1, UIntPtr.Zero); return true;   // atras (XBUTTON1)
        case 0x0056: Native.mouse_event(down ? 0x0080u : 0x0100u, 0, 0, 2, UIntPtr.Zero); return true;   // adelante (XBUTTON2)
      }
      return false;
    }
    public static void Clic(int cid) { if (Pulsar(cid, true)) Pulsar(cid, false); }
  }

  // ---- Ventana flotante con la lista de opciones junto al cursor ----
  // No roba el foco (el atajo llega a la aplicacion activa). Mantener el boton + soltar encima de una
  // opcion la ejecuta; con una pulsacion corta la ventana se queda abierta y se elige con un clic.
  public static class MenuFlotante {
    static Thread ui;
    static FormMenu form;
    static readonly ManualResetEvent listo = new ManualResetEvent(false);

    static void Iniciar() {
      if (ui != null) { listo.WaitOne(5000); return; }
      ui = new Thread(delegate () {
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        form = new FormMenu();
        IntPtr h = form.Handle;                  // crea la ventana en este hilo sin mostrarla
        listo.Set();
        Application.Run();
      });
      ui.SetApartmentState(ApartmentState.STA);
      ui.IsBackground = true;
      ui.Start();
      listo.WaitOne(5000);
    }

    // Ejecuta algo en el hilo de la ventana (STA), por ejemplo el uso del portapapeles
    public static void EnUI(MethodInvoker accion) {
      Iniciar();
      if (form != null) form.BeginInvoke(accion);
    }

    // Abre la ventana ya fija (se elige con un clic), por ejemplo desde un gesto.
    // formato: accion con el formato propio de la lista (ListaRejilla / ListaColumnas); null = como en Listas
    public static void AbrirFijo(int cid, List<Opcion> ops) { AbrirFijo(cid, ops, null); }
    public static void AbrirFijo(int cid, List<Opcion> ops, Accion formato) {
      Iniciar();
      int rej = formato != null ? formato.ListaRejilla : -1, col = formato != null ? formato.ListaColumnas : -1, ico = formato != null ? formato.ListaIcono : -1, ver = formato != null ? formato.ListaVer : -1;
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.AbrirFijo(cid, ops, rej, col, ico, ver); });
    }

    public static void Pulsado(int cid, List<Opcion> ops, Accion formato) {
      Iniciar();
      int rej = formato != null ? formato.ListaRejilla : -1, col = formato != null ? formato.ListaColumnas : -1, ico = formato != null ? formato.ListaIcono : -1, ver = formato != null ? formato.ListaVer : -1;
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.Abrir(cid, ops, rej, col, ico, ver); });
    }
    public static void Soltado(int cid) {
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.Soltar(cid); });
    }
  }

  // Ventana "en capas" (WS_EX_LAYERED) con transparencia por pixel: se dibuja entera en un bitmap con alfa y se
  // entrega con UpdateLayeredWindow. Asi el fondo puede ser translucido con el texto opaco y las esquinas suaves.
  public class FormMenu : Form {
    const int MANTENER_MS = 300;                   // menos que esto = pulsacion corta (la ventana se queda)
    List<Opcion> ops = new List<Opcion>();
    int cidActual, hover = -1, filaAlto, ancho;
    string modo = "";                              // "" cerrado, "manteniendo", "fijo"
    DateTime abierto, ultimoUso;
    float esc = 1f;
    Font fuente, fuenteDet;
    Estilo es = new Estilo();
    FormRespaldo respaldo;                         // difuminado detras de la lista (si esta activado)
    System.Windows.Forms.Timer reloj = new System.Windows.Forms.Timer();

    [StructLayout(LayoutKind.Sequential)] struct PUNTO { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct TAMANO { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct MEZCLA { public byte Op, Flags, Alfa, Formato; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PUNTO dst, ref TAMANO tam,
                                                                      IntPtr dcSrc, ref PUNTO src, int clave, ref MEZCLA mezcla, int flags);
    public FormMenu() {
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      TopMost = true;
      StartPosition = FormStartPosition.Manual;
      using (Graphics g = CreateGraphics()) esc = g.DpiX / 96f;
      AplicarEstilo();
      reloj.Interval = 150;
      reloj.Tick += delegate { Vigilar(); };
      VisibleChanged += delegate { if (!Visible) { CapturarEsc(false); EstiloListas.FinPrueba(); if (respaldo != null) respaldo.Quitar(); } };
    }

    // Letra y alto de fila segun la estetica de las listas (se mira cada vez que se abre)
    void AplicarEstilo() {
      es = EstiloListas.Actual;
      if (fuente == null || fuente.Size != es.Letra) {
        fuente = new Font("Segoe UI", es.Letra);
        fuenteDet = new Font("Segoe UI", Math.Max(7f, es.Letra - 1.5f));
      }
      filaAlto = (int)(fuente.Height + es.Espaciado * esc);
    }
    int cols = 1, celdaAncho, celdaAlto;
    bool rejilla; int columnasFijas, tamIcono = 32;
    bool verIcono = true, verNombre = true, verDetalle = true;               // disposicion de la lista abierta (la suya o la de la pestana Listas)

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00080000;   // NOACTIVATE | TOOLWINDOW | TOPMOST | LAYERED
        return cp;
      }
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }    // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      if (m.Msg == 0x0312 && (int)m.WParam == ID_ESC) {        // WM_HOTKEY: Esc cierra sin hacer nada
        bool manteniendo = modo == "manteniendo";
        Ocultar();
        if (manteniendo) modo = "ignorar";                     // al soltar el boton no se ejecuta nada
        return;
      }
      base.WndProc(ref m);
    }

    // La lista no toma el foco, asi que no recibe teclas: mientras se ve, Esc se registra como atajo de Windows
    // (sin ganchos de teclado) y se libera al cerrarla
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int ID_ESC = 0x5E5D;
    bool escRegistrado;
    void CapturarEsc(bool si) {
      if (si && !escRegistrado) escRegistrado = RegisterHotKey(Handle, ID_ESC, 0x4000, 0x1B);   // MOD_NOREPEAT, VK_ESCAPE
      else if (!si && escRegistrado) { UnregisterHotKey(Handle, ID_ESC); escRegistrado = false; }
    }

    public void AbrirFijo(int cid, List<Opcion> o, int rej, int col, int ico) { AbrirFijo(cid, o, rej, col, ico, -1); }
    public void AbrirFijo(int cid, List<Opcion> o, int rej, int col, int ico, int ver) {
      modo = "";
      Abrir(cid, o, rej, col, ico, ver);
      modo = "fijo"; ultimoUso = DateTime.UtcNow;
    }

    int Margen { get { return (int)(Math.Max(6, EstiloListas.RadioEfectivo(es) * 0.6f) * esc); } }   // hueco arriba y abajo (mas con esquinas grandes)

    // rej / col: formato propio de la lista (-1 = como en la pestana Listas)
    public void Abrir(int cid, List<Opcion> o, int rej, int col, int ico) { Abrir(cid, o, rej, col, ico, -1); }
    public void Abrir(int cid, List<Opcion> o, int rej, int col, int ico, int ver) {
      // Volver a pulsar el boton con la ventana fija abierta la cierra
      if (Visible && cid == cidActual && modo == "fijo") { Ocultar(); modo = "ignorar"; return; }
      AplicarEstilo();
      rejilla = rej < 0 ? es.Rejilla : rej == 1;
      columnasFijas = col < 0 ? es.Columnas : col;
      tamIcono = ico > 0 ? ico : (rejilla ? es.TamIcono : es.TamIconoLista);   // propio de la lista o el de Listas / Rejillas
      // Que se ve de cada opcion: lo propio de la lista o lo de la pestana Listas (siempre algo)
      verIcono = ver >= 0 ? (ver & 1) != 0 : (rejilla ? es.RIconos : es.Iconos);
      verNombre = ver >= 0 ? (ver & 2) != 0 : (rejilla ? es.RNombre : es.Nombre);
      verDetalle = ver >= 0 ? (ver & 4) != 0 : (rejilla ? es.RDetalle : es.Detalle);
      if (!verIcono && !verNombre && !verDetalle) verNombre = true;
      ops = o ?? new List<Opcion>();
      cidActual = cid; hover = -1;
      Point c = Cursor.Position;
      Rectangle area = Screen.FromPoint(c).WorkingArea;
      int w, h;
      if (rejilla) {
        conIconos = verIcono;
        // Celdas: icono arriba, etiqueta debajo (hasta dos lineas) y contenido en una linea pequena; mas anchas si el icono es grande
        // Celdas del tamano justo de lo que se ve (sin minimo fijo): el icono y, si hay, la etiqueta mas ancha (hasta un tope;
        // a partir de ahi pasa a dos lineas) y el contenido. Todas las celdas igual de anchas.
        float icoR = tamIcono * esc, tope = Math.Max(icoR, 120 * esc), cont = conIconos ? icoR : 0, maxNombre = 0;
        bool hayDetalle = false;
        using (Graphics g = CreateGraphics()) {
          foreach (Opcion op in ops) {
            if (verNombre) maxNombre = Math.Max(maxNombre, g.MeasureString(op.Nombre ?? "", fuente).Width);
            if (ConDetalle(op)) { hayDetalle = true; cont = Math.Max(cont, Math.Min(tope, g.MeasureString(op.Detalle, fuenteDet).Width)); }
          }
        }
        cont = Math.Max(cont, Math.Min(tope, maxNombre));
        if (cont <= 0) cont = 16 * esc;
        int lateral = (int)((es.Espaciado / 2 + 5) * esc), huecoC = (int)(3 * esc);
        celdaAncho = (int)Math.Ceiling(cont) + 2 * (lateral + 2 * huecoC);
        int lineas = !verNombre ? 0 : (maxNombre > tope ? 2 : 1);
        celdaAlto = (int)((conIconos ? icoR + (lineas > 0 || hayDetalle ? 5 * esc : 0) : 0) + lineas * fuente.Height + (hayDetalle ? fuenteDet.Height + 2 * esc : 0)
                          + (es.Espaciado + 10) * esc);
        // Columnas: las fijadas o, en automatico, casi cuadrada (3 opciones = 3 columnas, 6 = 3x2, 10 = 4x3...)
        int n = Math.Max(1, ops.Count);
        cols = columnasFijas > 0 ? Math.Min(columnasFijas, n) : (n <= 3 ? n : Math.Min(6, (int)Math.Ceiling(Math.Sqrt(n))));
        cols = Math.Max(1, Math.Min(cols, (area.Width - 2 * Margen) / celdaAncho));
        int maxFilasR = Math.Max(1, (area.Height - 2 * Margen) / celdaAlto);
        if (ops.Count > cols * maxFilasR) ops = ops.GetRange(0, cols * maxFilasR);
        int filas = (Math.Max(1, ops.Count) + cols - 1) / cols;
        w = cols * celdaAncho + 2 * Margen;
        h = filas * celdaAlto + 2 * Margen;
      } else {
        // Nunca mas alta que la pantalla ni mas ancha que 560 px (los titulos largos se recortan con "...")
        cols = 1;
        if (verIcono) filaAlto = Math.Max(filaAlto, (int)((tamIcono + 8) * esc));   // iconos grandes: filas mas altas
        int pad = (int)(12 * esc);
        int maxFilas = Math.Max(1, (area.Height - 2 * Margen) / filaAlto);
        if (ops.Count > maxFilas) ops = ops.GetRange(0, maxFilas);
        conIconos = verIcono && ops.Exists(delegate (Opcion op) { return op.Icono != null; });
        // Ancho justo para lo que se ve (sin minimo fijo): margenes + icono + etiqueta + contenido de la opcion mas ancha
        int mx = (int)(12 * esc), lado = (int)(tamIcono * esc);
        w = 2 * mx + (conIconos ? lado : 0);
        int maxW = Math.Min((int)(560 * esc), area.Width / 2);
        using (Graphics g = CreateGraphics()) {
          foreach (Opcion op in ops) {
            float tw = 0;
            if (verNombre) {
              tw = g.MeasureString(op.Nombre ?? "", fuente).Width;
              if (ConDetalle(op)) tw += g.MeasureString(op.Detalle, fuenteDet).Width + mx;
            } else if (ConDetalle(op)) tw = g.MeasureString(op.Detalle, fuente).Width;     // sin etiqueta el contenido va como texto principal
            if (tw > 0) tw += 2 * mx + AnchoIcono() + pad / 2;
            if (tw > w) w = (int)Math.Ceiling(tw);
          }
        }
        w = Math.Min(w, maxW);
        h = Math.Max(1, ops.Count) * filaAlto + 2 * Margen;
      }
      ancho = w;
      int x = c.X + (int)(14 * esc), y = c.Y + (int)(10 * esc);
      if (x + w > area.Right) x = c.X - w - (int)(14 * esc);
      if (y + h > area.Bottom) y = area.Bottom - h;
      Bounds = new Rectangle(x, y, w, h);
      abierto = ultimoUso = DateTime.UtcNow;
      modo = "manteniendo";
      Pintar();
      if (!Visible) { Show(); Pintar(); }        // al mostrarse por primera vez Windows puede descartar lo dibujado
      CapturarEsc(true);
      if (es.Difuminado != 0) {
        if (respaldo == null) respaldo = new FormRespaldo();
        respaldo.Colocar(Handle, Bounds, es);
      } else if (respaldo != null) respaldo.Quitar();
      reloj.Start();
    }

    bool ConDetalle(Opcion op) { return verDetalle && !string.IsNullOrEmpty(op.Detalle) && (op.Detalle != op.Nombre || !verNombre); }

    public void Soltar(int cid) {
      if (cid != cidActual) return;
      if (modo == "ignorar") { modo = ""; return; }
      if (modo != "manteniendo") return;
      if ((DateTime.UtcNow - abierto).TotalMilliseconds < MANTENER_MS) { modo = "fijo"; ultimoUso = DateTime.UtcNow; return; }
      int i = FilaEn(PointToClient(Cursor.Position));
      Ocultar();
      if (i >= 0) Ejecutar(i);
    }

    void Ocultar() { reloj.Stop(); Hide(); modo = ""; hover = -1; }

    // Ejecuta la opcion despues de ocultar la ventana (un clic va a lo que hay debajo del cursor)
    void Ejecutar(int i) {
      Opcion op = ops[i];
      Update();
      Ejecutor.EjecutarOpcion(op);
    }

    // Opcion bajo el punto (fila de la lista o celda de la rejilla); -1 si no hay ninguna
    int FilaEn(Point p) {
      if (p.X < 0 || p.X > Width || p.Y < Margen) return -1;
      if (rejilla) {
        if (p.X < Margen) return -1;
        int col = (p.X - Margen) / celdaAncho, fil = (p.Y - Margen) / celdaAlto;
        if (col >= cols) return -1;
        int k = fil * cols + col;
        return k >= 0 && k < ops.Count ? k : -1;
      }
      int i = (p.Y - Margen) / filaAlto;
      return i >= 0 && i < ops.Count ? i : -1;
    }

    // Con la ventana fija: se cierra si el cursor se aleja o tras unos segundos sin usarla
    void Vigilar() {
      if (!Visible) { reloj.Stop(); return; }
      if (modo != "fijo") return;
      Rectangle r = Bounds; r.Inflate((int)(160 * esc), (int)(160 * esc));
      if (!r.Contains(Cursor.Position) || (DateTime.UtcNow - ultimoUso).TotalSeconds > 8) Ocultar();
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      int i = FilaEn(e.Location);
      ultimoUso = DateTime.UtcNow;
      if (i != hover) { hover = i; Pintar(); }
    }
    protected override void OnMouseLeave(EventArgs e) { if (hover != -1) { hover = -1; Pintar(); } }
    protected override void OnMouseUp(MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      int i = FilaEn(e.Location);
      if (i < 0) return;
      Ocultar();
      Ejecutar(i);
    }

    // Dibuja en un bitmap con alfa y lo entrega a la ventana en capas
    void Pintar() {
      if (Width <= 0 || Height <= 0) return;
      using (Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
        using (Graphics g = Graphics.FromImage(bmp)) Dibujar(g);
        IntPtr pantalla = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(pantalla);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), viejo = SelectObject(dc, hbmp);
        try {
          PUNTO dst = new PUNTO(); dst.X = Left; dst.Y = Top;
          PUNTO src = new PUNTO();
          TAMANO tam = new TAMANO(); tam.W = Width; tam.H = Height;
          MEZCLA m = new MEZCLA(); m.Op = 0; m.Alfa = 255; m.Formato = 1;   // AC_SRC_OVER, AC_SRC_ALPHA
          UpdateLayeredWindow(Handle, pantalla, ref dst, ref tam, dc, ref src, 0, ref m, 2);   // ULW_ALPHA
        } finally {
          SelectObject(dc, viejo); DeleteObject(hbmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, pantalla);
        }
      }
    }

    void Dibujar(Graphics g) {
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;   // ClearType no admite fondo con alfa
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      float radio = EstiloListas.RadioEfectivo(es) * esc;
      int alfa = 255 * es.Opacidad / 100;
      RectangleF marco = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
      using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(marco, radio)) {
        using (SolidBrush b = new SolidBrush(Color.FromArgb(alfa, EstiloListas.Fondo(es)))) g.FillPath(b, p);
        using (Pen borde = new Pen(Color.FromArgb(Math.Max(alfa, 140), EstiloListas.Borde(es)))) g.DrawPath(borde, p);
      }
      int mx = (int)(12 * esc), sep = (int)(4 * esc);
      using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
      using (SolidBrush bTexto = new SolidBrush(EstiloListas.Texto(es)))
      using (SolidBrush bSuave = new SolidBrush(EstiloListas.TextoSuave(es)))
      using (SolidBrush bSel = new SolidBrush(EstiloListas.TextoSel(es))) {
        sf.LineAlignment = StringAlignment.Center; sf.Trimming = StringTrimming.EllipsisCharacter;
        if (ops == null || ops.Count == 0) {
          g.DrawString("Nada que mostrar", fuente, bSuave, new RectangleF(mx, Margen, Width - 2 * mx, filaAlto), sf);
          return;
        }
        if (rejilla) { DibujarRejilla(g, radio, bTexto, bSel); return; }
        int lado = (int)(tamIcono * esc);
        for (int i = 0; i < ops.Count; i++) {
          Opcion op = ops[i];
          RectangleF fila = new RectangleF(sep, Margen + i * filaAlto, Width - 2 * sep, filaAlto);
          bool sel = i == hover;
          if (sel) {
            RectangleF rs = new RectangleF(fila.X, fila.Y + 1, fila.Width, fila.Height - 2);
            using (SolidBrush b = new SolidBrush(es.Acento))
            using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(rs, Math.Max(0, radio - sep))) g.FillPath(b, p);
          }
          Color cTexto = sel ? EstiloListas.TextoSel(es) : EstiloListas.Texto(es);
          float x0 = fila.X + mx - sep;
          if (conIconos) {
            EstiloListas.DibujarIcono(g, op.Icono, new Rectangle((int)x0, (int)(fila.Y + (fila.Height - lado) / 2), lado, lado), cTexto);
            x0 += AnchoIcono();
          }
          // Sin etiqueta: el contenido ocupa su sitio (a la izquierda, como texto principal)
          float fin = fila.Right - mx + sep;
          if (!verNombre) {
            if (ConDetalle(op)) g.DrawString(op.Detalle, fuente, sel ? bSel : bTexto, new RectangleF(x0, fila.Y, Math.Max(1, fin - x0), fila.Height), sf);
            continue;
          }
          // El texto de la derecha (atajo, programa...) se reserva primero; el nombre ocupa el resto
          float anchoDet = 0;
          if (ConDetalle(op)) {
            anchoDet = Math.Min(g.MeasureString(op.Detalle, fuenteDet).Width, (fin - x0) / 2);
            using (StringFormat sd = (StringFormat)sf.Clone()) {
              sd.Alignment = StringAlignment.Far;
              g.DrawString(op.Detalle, fuenteDet, sel ? bSel : bSuave, new RectangleF(fin - anchoDet, fila.Y, anchoDet, fila.Height), sd);
            }
          }
          g.DrawString(op.Nombre ?? "", fuente, sel ? bSel : bTexto,
                       new RectangleF(x0, fila.Y, Math.Max(1, fin - x0 - (anchoDet > 0 ? anchoDet + mx : 0)), fila.Height), sf);
        }
      }
    }

    // Rejilla: una baldosa por opcion con el icono grande arriba y el nombre centrado debajo (hasta dos lineas).
    // Las opciones sin icono llevan su inicial en un circulo, para que la rejilla quede uniforme.
    void DibujarRejilla(Graphics g, float radio, SolidBrush bTexto, SolidBrush bSel) {
      int icono = (int)(tamIcono * esc), arriba = (int)((es.Espaciado / 2 + 5) * esc), hueco = (int)(3 * esc);
      using (StringFormat sf = new StringFormat())
      using (Font fInicial = new Font("Segoe UI", Math.Max(9f, icono * 0.47f), FontStyle.Bold, GraphicsUnit.Pixel)) {
        sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Near; sf.Trimming = StringTrimming.EllipsisWord;
        for (int i = 0; i < ops.Count; i++) {
          Opcion op = ops[i];
          RectangleF celda = new RectangleF(Margen + (i % cols) * celdaAncho + hueco, Margen + (i / cols) * celdaAlto + hueco,
                                            celdaAncho - 2 * hueco, celdaAlto - 2 * hueco);
          bool sel = i == hover;
          Color cTexto = sel ? EstiloListas.TextoSel(es) : EstiloListas.Texto(es);
          if (sel) {
            using (SolidBrush b = new SolidBrush(es.Acento))
            using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(celda, Math.Min(Math.Max(radio - hueco, 4 * esc), 10 * esc))) g.FillPath(b, p);
          }
          float y = celda.Y + arriba;
          if (conIconos) {
            Rectangle ri = new Rectangle((int)(celda.X + (celda.Width - icono) / 2), (int)y, icono, icono);
            if (op.Icono != null) EstiloListas.DibujarIcono(g, op.Icono, ri, cTexto);
            else {
              // Sin icono: inicial del nombre en un circulo
              using (SolidBrush b = new SolidBrush(Color.FromArgb(sel ? 70 : 255, sel ? cTexto : EstiloListas.Borde(es)))) g.FillEllipse(b, ri);
              string ini = string.IsNullOrEmpty(op.Nombre) ? "?" : op.Nombre.Substring(0, 1).ToUpperInvariant();
              using (StringFormat sc = new StringFormat()) using (SolidBrush b = new SolidBrush(cTexto)) {
                sc.Alignment = StringAlignment.Center; sc.LineAlignment = StringAlignment.Center;
                g.DrawString(ini, fInicial, b, ri, sc);
              }
            }
            y += icono + (int)(5 * esc);
          }
          if (verNombre) {
            float alto = Math.Max(1, Math.Min(fuente.Height * 2, celda.Bottom - y - hueco));
            g.DrawString(op.Nombre ?? "", fuente, sel ? bSel : bTexto, new RectangleF(celda.X + hueco, y, celda.Width - 2 * hueco, alto), sf);
            y += alto;
          }
          if (ConDetalle(op)) {
            using (StringFormat sd = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush bDet = new SolidBrush(sel ? EstiloListas.TextoSel(es) : EstiloListas.TextoSuave(es))) {
              sd.Alignment = StringAlignment.Center; sd.Trimming = StringTrimming.EllipsisCharacter;
              g.DrawString(op.Detalle, fuenteDet, bDet, new RectangleF(celda.X + hueco, y + 2 * esc, celda.Width - 2 * hueco, fuenteDet.Height + 2), sd);
            }
          }
        }
      }
    }

    bool conIconos;
    int AnchoIcono() { return conIconos ? (int)((tamIcono + 10) * esc) : 0; }
  }

  // Ventana vacia que va justo detras de la lista y solo lleva el difuminado. Windows no recorta el difuminado de una
  // ventana en capas, pero esta es normal: Windows 11 redondea sus esquinas (y con ellas el difuminado) y le pone sombra.
  public class FormRespaldo : Form {
    [StructLayout(LayoutKind.Sequential)] struct ACENTO { public int Estado, Flags, Color, Anim; }
    [StructLayout(LayoutKind.Sequential)] struct DATOS_COMPOSICION { public int Atributo; public IntPtr Datos; public int Tam; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref DATOS_COMPOSICION d);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int valor, int tam);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr despues, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    int estadoAplicado = -1, esquinasAplicadas = -1;

    public FormRespaldo() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      BackColor = Color.Black;                      // con el difuminado activo, el negro se ve transparente
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020;   // NOACTIVATE | TOOLWINDOW | TOPMOST | TRANSPARENT
        return cp;
      }
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }    // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      base.WndProc(ref m);
    }

    // Se pone justo debajo de la lista, con su mismo tamano
    public void Colocar(IntPtr lista, Rectangle r, Estilo es) {
      int estado = es.Difuminado == 1 ? 3 : 4;                 // 3 desenfoque, 4 acrilico
      if (estado != estadoAplicado) {
        ACENTO a = new ACENTO(); a.Estado = estado; a.Color = estado == 4 ? 0x01000000 : 0;   // sin tinte: lo pone la lista
        int tam = Marshal.SizeOf(a);
        IntPtr p = Marshal.AllocHGlobal(tam);
        try {
          Marshal.StructureToPtr(a, p, false);
          DATOS_COMPOSICION d = new DATOS_COMPOSICION(); d.Atributo = 19; d.Datos = p; d.Tam = tam;   // WCA_ACCENT_POLICY
          SetWindowCompositionAttribute(Handle, ref d);
        } catch { } finally { Marshal.FreeHGlobal(p); }
        estadoAplicado = estado;
      }
      int r0 = EstiloListas.RadioEfectivo(es);
      int pref = r0 == 0 ? 1 : r0 <= 4 ? 3 : 2;                 // 1 recta, 3 redondeo pequeno, 2 normal
      if (pref != esquinasAplicadas) {
        try { DwmSetWindowAttribute(Handle, 33, ref pref, 4); } catch { }
        esquinasAplicadas = pref;
      }
      SetWindowPos(Handle, lista, r.X, r.Y, r.Width, r.Height, 0x0010 | 0x0040);   // SWP_NOACTIVATE | SWP_SHOWWINDOW
    }
    public void Quitar() { if (IsHandleCreated) ShowWindow(Handle, 0); }
  }

  // ---- Recolocar la ventana activa en una rejilla de la pantalla (parecido a FancyZones) ----
  // Mantener pulsado: si se mueve y se suelta enseguida es un gesto rapido (<- mitad izquierda, -> mitad derecha,
  // arriba maximizar, abajo minimizar o restaurar); si se queda quieto un momento aparece la rejilla y la ventana va
  // a la celda sobre la que se suelte. Pulsacion corta (o desde un gesto, combinacion o pulsacion extra): la rejilla
  // se queda abierta y se arrastra de una celda a otra para ocupar varias; Esc o clic derecho cancelan sin mover nada.
  // Va en su propio hilo con conciencia de DPI por monitor: coordenadas reales en cada pantalla.
  public static class Recolocar {
    static Thread hilo;
    static FormRejilla form;
    static readonly ManualResetEvent listo = new ManualResetEvent(false);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr contexto);

    static void Iniciar() {
      if (hilo != null) { listo.WaitOne(5000); return; }
      hilo = new Thread(delegate () {
        try { SetThreadDpiAwarenessContext((IntPtr)(-4)); } catch { }   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        form = new FormRejilla();
        IntPtr h = form.Handle;                  // crea la ventana en este hilo sin mostrarla
        listo.Set();
        Application.Run();
      });
      hilo.IsBackground = true;
      hilo.SetApartmentState(ApartmentState.STA);
      hilo.Start();
      listo.WaitOne(5000);
    }
    // Movimiento desviado del raton (cuentas del sensor) mientras se mantiene un boton de gestos con Recolocar.
    // Lo acumula el hilo del motor y lo lee el del panel.
    static int movX, movY, avisosMov;
    public static void Mover(int dx, int dy) { Interlocked.Add(ref movX, dx); Interlocked.Add(ref movY, dy); Interlocked.Increment(ref avisosMov); }
    public static void Movido(out int dx, out int dy, out int avisos) {
      dx = Interlocked.CompareExchange(ref movX, 0, 0); dy = Interlocked.CompareExchange(ref movY, 0, 0);
      avisos = Interlocked.CompareExchange(ref avisosMov, 0, 0);
    }
    public static volatile bool PanelVisible;          // lo pone FormRejilla al mostrar / cerrar el panel
    static void ReiniciarMovido() { Interlocked.Exchange(ref movX, 0); Interlocked.Exchange(ref movY, 0); Interlocked.Exchange(ref avisosMov, 0); }

    public static void Pulsado(int cid, Accion a) {
      ReiniciarMovido();
      Iniciar();
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.Pulsado(cid, a); });
    }
    public static void Soltado(int cid) {
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.Soltado(cid); });
    }
    public static void AbrirFijo(Accion a) {
      Iniciar();
      if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.AbrirFijo(a); });
    }
  }

  // Panel pequeno centrado en el cursor con la cuadricula de la pantalla (misma proporcion que el monitor): llegar a
  // cualquier celda pide muy poco movimiento. Una silueta translucida (FormDestino) marca en la pantalla real el sitio.
  public class FormRejilla : Form {
    const int MANTENER_MS = 450, FIJO_SEG = 20;
    // Umbrales del gesto rapido: los mismos que los gestos (cuentas del sensor) y, con el cursor, algo menos en pixeles
    static int UMBRAL_DESVIADO { get { return Engine.UmbralGesto; } }
    static int UMBRAL_PX { get { return Math.Max(10, Engine.UmbralGesto * 4 / 5); } }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct PUNTO { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct TAMANO { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct MEZCLA { public byte Op, Flags, Alfa, Formato; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr despues, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int tam);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PUNTO dst, ref TAMANO tam,
                                                                      IntPtr dcSrc, ref PUNTO src, int clave, ref MEZCLA mezcla, int flags);
    const int ID_ESC = 0x5E5C;
    bool escRegistrado;

    IntPtr ventana;                    // la ventana que se recoloca (la activa al pulsar)
    int cidActual = -2;
    string modo = "";                  // "" cerrado, "esperando" (pulsado, aun sin rejilla), "manteniendo", "fijo"
    Point inicio; DateTime t0, tFijo; bool movido, arrastrando;
    // Todas las pantallas: su zona de trabajo real y su miniatura (con la cuadricula) dentro del panel
    Rectangle[] zonasPantalla = new Rectangle[0], zonasLocal = new Rectangle[0];
    int monSel = -1;                   // pantalla de la seleccion (las celdas marcadas son siempre de una sola)
    int filas = 2, columnas = 3, sep = 8;
    Point celdaA = new Point(-1, -1), celdaB = new Point(-1, -1);   // rectangulo marcado (columna, fila)
    float esc = 1f;
    FormDestino destino;
    System.Windows.Forms.Timer reloj = new System.Windows.Forms.Timer();

    public FormRejilla() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      reloj.Interval = 30;
      reloj.Tick += delegate { Vigilar(); };
      ajuste.Interval = 150;
      ajuste.Tick += delegate { Corregir(); };
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00080000;   // NOACTIVATE | TOOLWINDOW | TOPMOST | LAYERED
        return cp;
      }
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }    // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      if (m.Msg == 0x0312 && (int)m.WParam == ID_ESC) { Cerrar(); return; }   // WM_HOTKEY: Esc cancela sin mover nada
      base.WndProc(ref m);
    }

    // El panel no toma el foco (la ventana a mover sigue activa), asi que no recibe teclas: mientras se ve, Esc se
    // registra como atajo de Windows (sin ganchos de teclado) y se libera al cerrarlo
    void CapturarEsc(bool si) {
      if (si && !escRegistrado) escRegistrado = RegisterHotKey(Handle, ID_ESC, 0x4000, 0x1B);   // MOD_NOREPEAT, VK_ESCAPE
      else if (!si && escRegistrado) { UnregisterHotKey(Handle, ID_ESC); escRegistrado = false; }
    }

    // ---- Entradas ----
    public void Pulsado(int cid, Accion a) {
      if (modo == "fijo") { Cerrar(); return; }                 // volver a pulsar con el panel abierto lo cierra
      if (!Preparar(a)) return;
      cidActual = cid; modo = "esperando"; movido = false;
      inicio = Cursor.Position; t0 = DateTime.UtcNow;
      reloj.Start();
    }
    public void Soltado(int cid) {
      if (cid != cidActual) return;
      if (modo == "esperando") {
        reloj.Stop();
        // Movimiento: el del cursor o, con el boton de gestos (movimiento desviado), el que llega del raton
        int cx = Cursor.Position.X - inicio.X, cy = Cursor.Position.Y - inicio.Y, rx, ry, n;
        Recolocar.Movido(out rx, out ry, out n);
        bool porDesviado = Math.Abs(rx) + Math.Abs(ry) > UMBRAL_DESVIADO;
        if (!movido && (porDesviado || Math.Abs(cx) + Math.Abs(cy) > UMBRAL_PX)) movido = true;
        string decision = movido ? "gesto " + (Math.Abs(porDesviado ? rx : cx) >= Math.Abs(porDesviado ? ry : cy)
                                               ? ((porDesviado ? rx : cx) < 0 ? "izquierda" : "derecha")
                                               : ((porDesviado ? ry : cy) < 0 ? "arriba" : "abajo")) : "panel abierto";
        Acciones.Avisos.Enqueue(string.Format("Recolocar (boton 0x{0:X4}): cursor {1},{2}; desviado {3},{4} en {5} avisos -> {6}",
                                               cid, cx, cy, rx, ry, n, decision));
        if (movido) { if (porDesviado) Direccion(rx, ry); else Direccion(cx, cy); modo = ""; }
        else Mostrar("fijo");                                    // pulsacion corta: el panel se queda abierto
      } else if (modo == "manteniendo") {
        if (celdaA.X >= 0) Aplicar();
        Cerrar();
      }
    }
    public void AbrirFijo(Accion a) {
      if (modo == "fijo") { Cerrar(); return; }
      if (!Preparar(a)) return;
      cidActual = -2;
      Mostrar("fijo");
    }

    // La ventana activa y la cuadricula de la accion; false si no hay nada que recolocar
    bool Preparar(Accion a) {
      ventana = Objetivo();
      if (ventana == IntPtr.Zero) { Aviso.Mostrar("No hay ninguna ventana que recolocar"); return false; }
      filas = Math.Max(1, Math.Min(8, a != null ? a.RejFilas : 2));
      columnas = Math.Max(1, Math.Min(8, a != null ? a.RejColumnas : 3));
      sep = Math.Max(0, Math.Min(40, a != null ? a.RejSeparacion : 8));
      return true;
    }

    static IntPtr Objetivo() {
      IntPtr h = GetForegroundWindow();
      if (h == IntPtr.Zero) return IntPtr.Zero;
      h = GetAncestor(h, 2);                                    // GA_ROOT
      if (h == IntPtr.Zero || !IsWindowVisible(h)) return IntPtr.Zero;
      StringBuilder c = new StringBuilder(64); GetClassName(h, c, 64);
      string clase = c.ToString();
      if (clase == "Progman" || clase == "WorkerW" || clase == "Shell_TrayWnd" || clase == "Shell_SecondaryTrayWnd") return IntPtr.Zero;
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == (uint)Process.GetCurrentProcess().Id) return IntPtr.Zero;   // ventanas propias (listas, paleta)
      return h;
    }

    // Mientras se mantiene: decide entre gesto rapido (se mueve) y panel (se queda quieto)
    void Vigilar() {
      Point p = Cursor.Position;
      if (modo == "esperando") {
        int rx, ry, n; Recolocar.Movido(out rx, out ry, out n);
        if (!movido && (Math.Abs(p.X - inicio.X) + Math.Abs(p.Y - inicio.Y) > UMBRAL_PX || Math.Abs(rx) + Math.Abs(ry) > UMBRAL_DESVIADO)) movido = true;
        if (!movido && (DateTime.UtcNow - t0).TotalMilliseconds >= MANTENER_MS) Mostrar("manteniendo");
      } else if (modo == "manteniendo") {
        int mon; Point c = CeldaEn(p, out mon);
        if (c != celdaA || c != celdaB || mon != monSel) { celdaA = celdaB = c; monSel = mon; Actualizar(); }
      } else if (modo == "fijo") {
        // Abierto: se cierra si el cursor se aleja del panel o tras un rato sin usarlo
        Rectangle r = Bounds; r.Inflate((int)(160 * esc), (int)(160 * esc));
        if (!arrastrando && (!r.Contains(p) || (DateTime.UtcNow - tFijo).TotalSeconds > FIJO_SEG)) Cerrar();
      } else reloj.Stop();
    }

    void Mostrar(string m) {
      Point cur = Cursor.Position;
      Screen s = Screen.FromPoint(cur);
      Screen[] todas = Screen.AllScreens;
      zonasPantalla = new Rectangle[todas.Length];
      int actual = 0;
      for (int i = 0; i < todas.Length; i++) { zonasPantalla[i] = todas[i].WorkingArea; if (todas[i].Equals(s)) actual = i; }
      Rectangle area = zonasPantalla[actual];
      modo = m; arrastrando = false; tFijo = DateTime.UtcNow;
      // Primero se coloca en el monitor para conocer su escala
      Bounds = new Rectangle(cur.X, cur.Y, 10, 10);
      try { esc = Math.Max(1f, GetDpiForWindow(Handle) / 96f); } catch { esc = 1f; }
      // Cuadricula de la pantalla actual con su proporcion, de 180 a 340 px de ancho (y como mucho 240 de alto);
      // las demas pantallas, a la misma escala y en su posicion (todo junto, como mucho 900 x 500 px)
      double prop = area.Height / (double)Math.Max(1, area.Width);
      int anchoRej = (int)(Math.Min(340, Math.Max(180, columnas * 64)) * esc);
      int altoRej = (int)(anchoRej * prop);
      if (altoRej > 240 * esc) { altoRej = (int)(240 * esc); anchoRej = (int)(altoRej / prop); }
      double k = anchoRej / (double)Math.Max(1, area.Width);
      Rectangle total = area;
      foreach (Rectangle z in zonasPantalla) total = Rectangle.Union(total, z);
      k = Math.Min(k, Math.Min(900 * esc / total.Width, 500 * esc / total.Height));
      int pad = (int)(6 * esc), entre = zonasPantalla.Length > 1 ? (int)(3 * esc) : 0;   // hueco entre pantallas
      zonasLocal = new Rectangle[zonasPantalla.Length];
      for (int i = 0; i < zonasPantalla.Length; i++) {
        Rectangle z = zonasPantalla[i];
        Rectangle l = Rectangle.FromLTRB(pad + (int)Math.Round((z.Left - total.Left) * k), pad + (int)Math.Round((z.Top - total.Top) * k),
                                         pad + (int)Math.Round((z.Right - total.Left) * k), pad + (int)Math.Round((z.Bottom - total.Top) * k));
        l.Inflate(-entre, -entre);
        zonasLocal[i] = l;
      }
      int w = (int)Math.Round(total.Width * k) + 2 * pad, h = (int)Math.Round(total.Height * k) + 2 * pad;
      // Centrado en el cursor (la cuadricula de su pantalla, no el panel): cualquier celda queda a media cuadricula
      Rectangle propia = zonasLocal[actual];
      int x = cur.X - propia.X - propia.Width / 2, y = cur.Y - propia.Y - propia.Height / 2;
      x = Math.Max(s.WorkingArea.Left, Math.Min(x, s.WorkingArea.Right - w));
      y = Math.Max(s.WorkingArea.Top, Math.Min(y, s.WorkingArea.Bottom - h));
      Bounds = new Rectangle(x, y, w, h);
      if (m == "manteniendo") celdaA = celdaB = CeldaEn(cur, out monSel);
      else { celdaA = celdaB = new Point(-1, -1); monSel = -1; }
      Pintar();
      if (!Visible) { Show(); Pintar(); }
      MostrarDestino();
      CapturarEsc(true);
      Recolocar.PanelVisible = true;
      reloj.Start();
    }
    void Cerrar() {
      Recolocar.PanelVisible = false;
      CapturarEsc(false); reloj.Stop(); Hide();
      if (destino != null) destino.Quitar();
      modo = ""; arrastrando = false; cidActual = -2;
    }
    void Actualizar() { Pintar(); MostrarDestino(); }
    // Silueta del sitio en la pantalla real (detras del panel)
    void MostrarDestino() {
      if (celdaA.X < 0) { if (destino != null) destino.Quitar(); return; }
      if (destino == null) destino = new FormDestino();
      destino.Colocar(Handle, Marcado(), EstiloListas.Actual.Acento, esc);
    }

    // ---- Raton con el panel abierto (pulsacion corta): arrastrar de una celda a otra ----
    protected override void OnMouseDown(MouseEventArgs e) {
      if (modo != "fijo") return;
      if (e.Button == MouseButtons.Right) { Cerrar(); return; }
      if (e.Button != MouseButtons.Left) return;
      int mon; Point c = CeldaEn(PointToScreen(e.Location), out mon);
      if (c.X < 0) return;
      arrastrando = true; celdaA = celdaB = c; monSel = mon; Actualizar();
    }
    protected override void OnMouseMove(MouseEventArgs e) {
      if (modo != "fijo") return;
      tFijo = DateTime.UtcNow;
      int mon; Point c = CeldaEn(PointToScreen(e.Location), out mon);
      // Arrastrando: solo celdas de la misma pantalla en la que se empezo
      if (arrastrando) { if (c.X >= 0 && mon == monSel && c != celdaB) { celdaB = c; Actualizar(); } }
      else if (c != celdaA || c != celdaB || mon != monSel) { celdaA = celdaB = c; monSel = mon; Actualizar(); }   // sin arrastrar: la celda bajo el cursor
    }
    protected override void OnMouseLeave(EventArgs e) {
      if (modo == "fijo" && !arrastrando && celdaA.X >= 0) { celdaA = celdaB = new Point(-1, -1); monSel = -1; Actualizar(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) {
      if (modo != "fijo" || e.Button != MouseButtons.Left || !arrastrando) return;
      arrastrando = false;
      if (celdaA.X >= 0) Aplicar();
      Cerrar();
    }

    // ---- Geometria ----
    // Celda del panel bajo un punto de la pantalla y en que pantalla (miniatura) cae; (-1,-1) y -1 si en ninguna
    Point CeldaEn(Point p, out int mon) {
      Point l = new Point(p.X - Left, p.Y - Top);
      for (mon = 0; mon < zonasLocal.Length; mon++) {
        Rectangle z = zonasLocal[mon];
        if (!z.Contains(l)) continue;
        int c = Math.Min(columnas - 1, (l.X - z.X) * columnas / Math.Max(1, z.Width));
        int f = Math.Min(filas - 1, (l.Y - z.Y) * filas / Math.Max(1, z.Height));
        return new Point(c, f);
      }
      mon = -1;
      return new Point(-1, -1);
    }
    // Rectangulo que ocupan las celdas de c0,f0 a c1,f1 dentro de una zona (con el hueco entre celdas y en los bordes)
    static Rectangle Celdas(Rectangle zona, int cols, int fils, int hueco, int c0, int f0, int c1, int f1) {
      double an = (zona.Width - hueco * (cols + 1)) / (double)cols, al = (zona.Height - hueco * (fils + 1)) / (double)fils;
      int x = zona.X + hueco + (int)Math.Round(c0 * (an + hueco)), y = zona.Y + hueco + (int)Math.Round(f0 * (al + hueco));
      int x2 = zona.X + hueco + (int)Math.Round((c1 + 1) * (an + hueco) - hueco);
      int y2 = zona.Y + hueco + (int)Math.Round((f1 + 1) * (al + hueco) - hueco);
      return Rectangle.FromLTRB(x, y, x2, y2);
    }
    Rectangle Seleccion(Rectangle zona, int hueco) {
      return Celdas(zona, columnas, filas, hueco, Math.Min(celdaA.X, celdaB.X), Math.Min(celdaA.Y, celdaB.Y),
                    Math.Max(celdaA.X, celdaB.X), Math.Max(celdaA.Y, celdaB.Y));
    }
    // Destino real en la pantalla. La separacion es solo entre ventanas: contra el borde de la pantalla, pegada
    // (se amplia la zona lo mismo que el hueco, y el hueco exterior cae justo en el borde)
    static Rectangle SinBorde(Rectangle zona, int hueco) { zona.Inflate(hueco, hueco); return zona; }
    Rectangle Marcado() { return Seleccion(SinBorde(zonasPantalla[monSel], sep), sep); }

    // ---- Recolocar ----
    void Aplicar() { Colocar(ventana, Marcado()); }

    void Direccion(int dx, int dy) {
      Rectangle zona = SinBorde(Screen.FromPoint(inicio).WorkingArea, sep);
      if (Math.Abs(dx) >= Math.Abs(dy)) Colocar(ventana, Celdas(zona, 2, 1, sep, dx < 0 ? 0 : 1, 0, dx < 0 ? 0 : 1, 0));
      else if (dy < 0) ShowWindow(ventana, 3);                  // SW_MAXIMIZE
      else ShowWindow(ventana, IsZoomed(ventana) ? 9 : 6);      // maximizada: SW_RESTORE; si no, SW_MINIMIZE
    }

    // Coloca la ventana para que su borde VISIBLE coincida con el destino (en Windows 10/11 hay un borde invisible
    // de redimensionado alrededor que GetWindowRect incluye y DWMWA_EXTENDED_FRAME_BOUNDS no)
    // Al pasar a una pantalla con otro escalado la ventana se reescala sola al llegar (y cambian sus bordes invisibles),
    // asi que se coloca dos veces y, por si la aplicacion lo hace con retraso, se revisa poco despues (Corregir).
    void Colocar(IntPtr h, Rectangle destino) {
      if (h == IntPtr.Zero) return;
      if (IsZoomed(h) || IsIconic(h)) ShowWindow(h, 9);         // SW_RESTORE
      for (int i = 0; i < 2; i++) if (!Ponerla(h, destino)) break;
      pendiente = h; destinoPendiente = destino; revisiones = 0;
      ajuste.Start();
    }
    // Pone la ventana en el sitio si su borde visible no coincide ya; true si la ha movido
    static bool Ponerla(IntPtr h, Rectangle destino) {
      RECT r, v;
      int l = 0, t = 0, d = 0, b = 0;
      if (GetWindowRect(h, out r) && DwmGetWindowAttribute(h, 9, out v, Marshal.SizeOf(typeof(RECT))) == 0) {
        if (Math.Abs(v.L - destino.Left) <= 1 && Math.Abs(v.T - destino.Top) <= 1 &&
            Math.Abs(v.R - destino.Right) <= 1 && Math.Abs(v.B - destino.Bottom) <= 1) return false;
        l = v.L - r.L; t = v.T - r.T; d = r.R - v.R; b = r.B - v.B;
      }
      SetWindowPos(h, IntPtr.Zero, destino.X - l, destino.Y - t, destino.Width + l + d, destino.Height + t + b, 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE
      return true;
    }
    IntPtr pendiente; Rectangle destinoPendiente; int revisiones;
    System.Windows.Forms.Timer ajuste = new System.Windows.Forms.Timer();
    // Revisa a los 150, 300 y 450 ms que la ventana sigue donde se puso (algunas se reescalan tarde al cambiar de pantalla)
    void Corregir() {
      if (pendiente == IntPtr.Zero || ++revisiones > 3) { ajuste.Stop(); pendiente = IntPtr.Zero; return; }
      if (IsZoomed(pendiente) || IsIconic(pendiente)) { ajuste.Stop(); pendiente = IntPtr.Zero; return; }   // el usuario ya la ha cambiado
      Ponerla(pendiente, destinoPendiente);
    }

    // ---- Dibujo del panel (ventana en capas con alfa y el estilo de las listas) ----
    void Pintar() {
      if (Width <= 0 || Height <= 0) return;
      using (Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
        using (Graphics g = Graphics.FromImage(bmp)) Dibujar(g);
        IntPtr pantalla = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(pantalla);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), viejo = SelectObject(dc, hbmp);
        try {
          PUNTO dst = new PUNTO(); dst.X = Left; dst.Y = Top;
          PUNTO src = new PUNTO();
          TAMANO tam = new TAMANO(); tam.W = Width; tam.H = Height;
          MEZCLA m = new MEZCLA(); m.Op = 0; m.Alfa = 255; m.Formato = 1;
          UpdateLayeredWindow(Handle, pantalla, ref dst, ref tam, dc, ref src, 0, ref m, 2);
        } finally {
          SelectObject(dc, viejo); DeleteObject(hbmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, pantalla);
        }
      }
    }

    void Dibujar(Graphics g) {
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      Estilo es = EstiloListas.Actual;
      Color texto = EstiloListas.Texto(es), acento = es.Acento;
      int alfa = 255 * Math.Max(80, es.Opacidad) / 100;        // algo de transparencia, pero que se lea bien
      float radio = Math.Max(8, EstiloListas.RadioEfectivo(es)) * esc;
      RectangleF marco = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
      using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(marco, radio)) {
        using (SolidBrush b = new SolidBrush(Color.FromArgb(alfa, EstiloListas.Fondo(es)))) g.FillPath(b, p);
        using (Pen borde = new Pen(Color.FromArgb(Math.Max(alfa, 160), EstiloListas.Borde(es)))) g.DrawPath(borde, p);
      }
      // Solo la cuadricula de cada pantalla (sin titulo ni ayuda)
      int hueco = (int)Math.Max(3, 4 * esc);
      float rc = 4 * esc;
      using (SolidBrush celda = new SolidBrush(Color.FromArgb(EstiloListas.EsClaro(es) ? 22 : 34, texto)))
      using (Pen borde = new Pen(Color.FromArgb(90, texto), Math.Max(1f, esc)))
        foreach (Rectangle zona in zonasLocal)
          for (int f = 0; f < filas; f++)
            for (int c = 0; c < columnas; c++) {
              Rectangle r = Celdas(zona, columnas, filas, hueco, c, f, c, f);
              using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(r, rc)) { g.FillPath(celda, p); g.DrawPath(borde, p); }
            }
      if (celdaA.X >= 0 && monSel >= 0 && monSel < zonasLocal.Length) {
        Rectangle m = Seleccion(zonasLocal[monSel], hueco);
        using (SolidBrush b = new SolidBrush(acento))                 // opaca: tapa las lineas de las celdas que abarca
        using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(m, rc)) g.FillPath(b, p);
      }
    }
  }

  // Silueta translucida del sitio donde quedara la ventana. No recibe el raton (WS_EX_TRANSPARENT).
  public class FormDestino : Form {
    [StructLayout(LayoutKind.Sequential)] struct PUNTO { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct TAMANO { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct MEZCLA { public byte Op, Flags, Alfa, Formato; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr despues, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PUNTO dst, ref TAMANO tam,
                                                                      IntPtr dcSrc, ref PUNTO src, int clave, ref MEZCLA mezcla, int flags);
    public FormDestino() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00080000 | 0x00000020;   // + LAYERED + TRANSPARENT
        return cp;
      }
    }
    // Se pone justo detras del panel (encima), sin activarse
    public void Colocar(IntPtr encima, Rectangle r, Color acento, float esc) {
      if (r.Width <= 0 || r.Height <= 0) return;
      Bounds = r;
      using (Bitmap bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
        using (Graphics g = Graphics.FromImage(bmp)) {
          g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
          float grosor = 3 * esc, radio = 10 * esc;
          RectangleF m = new RectangleF(grosor / 2, grosor / 2, r.Width - grosor, r.Height - grosor);
          using (System.Drawing.Drawing2D.GraphicsPath p = EstiloListas.Redondeado(m, radio))
          using (SolidBrush b = new SolidBrush(Color.FromArgb(55, acento)))
          using (Pen borde = new Pen(Color.FromArgb(230, acento), grosor)) { g.FillPath(b, p); g.DrawPath(borde, p); }
        }
        IntPtr pantalla = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(pantalla);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), viejo = SelectObject(dc, hbmp);
        try {
          PUNTO dst = new PUNTO(); dst.X = r.X; dst.Y = r.Y;
          PUNTO src = new PUNTO();
          TAMANO tam = new TAMANO(); tam.W = r.Width; tam.H = r.Height;
          MEZCLA mz = new MEZCLA(); mz.Op = 0; mz.Alfa = 255; mz.Formato = 1;
          UpdateLayeredWindow(Handle, pantalla, ref dst, ref tam, dc, ref src, 0, ref mz, 2);
        } finally {
          SelectObject(dc, viejo); DeleteObject(hbmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, pantalla);
        }
      }
      SetWindowPos(Handle, encima, r.X, r.Y, r.Width, r.Height, 0x0010 | 0x0040);   // SWP_NOACTIVATE | SWP_SHOWWINDOW
    }
    public void Quitar() { if (IsHandleCreated) ShowWindow(Handle, 0); }
  }

  // ---- Perfil de botones: atajos, reasignaciones, gestos, listas y botones forzados a fabrica ----
  public class Perfil {
    public Dictionary<int, int[]> Atajos = new Dictionary<int, int[]>();
    public Dictionary<int, int> Botones = new Dictionary<int, int>();
    public Dictionary<int, Dictionary<string, Accion>> Gestos = new Dictionary<int, Dictionary<string, Accion>>();
    public Dictionary<int, List<Opcion>> Menus = new Dictionary<int, List<Opcion>>();
    public Dictionary<int, string> Textos = new Dictionary<int, string>();
    public Dictionary<int, string> Comandos = new Dictionary<int, string>();
    public HashSet<int> Programas = new HashSet<int>();         // botones con la lista de programas abiertos
    public Dictionary<int, int[]> FormatoListas = new Dictionary<int, int[]>();   // formato propio de esas listas: {rejilla, columnas}
    public Dictionary<int, Accion> Combos = new Dictionary<int, Accion>();   // combinaciones de teclas (clave -> accion)
    public Dictionary<int, Accion> CombosBoton = new Dictionary<int, Accion>();   // modificador + boton del raton ((mascara<<16)|cid)
    public Dictionary<int, Accion> CombosRueda = new Dictionary<int, Accion>();   // modificador + giro de rueda ((mascara<<4)|direccion)
    public Dictionary<int, Dictionary<string, Accion>> Pulsaciones = new Dictionary<int, Dictionary<string, Accion>>();   // doble / larga
    public Dictionary<int, Accion> Otras = new Dictionary<int, Accion>();   // transformar, ocr, paleta
    public HashSet<int> Defecto = new HashSet<int>();
    public Dictionary<int, int> RuedaGestos = new Dictionary<int, int>();   // botones de gestos que muestran la rueda (cid -> espera en ms)

    public Perfil Copia() {
      Perfil r = new Perfil();
      r.Atajos = new Dictionary<int, int[]>(Atajos);
      r.Botones = new Dictionary<int, int>(Botones);
      r.Gestos = new Dictionary<int, Dictionary<string, Accion>>(Gestos);
      r.Menus = new Dictionary<int, List<Opcion>>(Menus);
      r.Textos = new Dictionary<int, string>(Textos);
      r.Comandos = new Dictionary<int, string>(Comandos);
      r.Programas = new HashSet<int>(Programas);
      r.FormatoListas = new Dictionary<int, int[]>(FormatoListas);
      r.Combos = new Dictionary<int, Accion>(Combos);
      r.CombosBoton = new Dictionary<int, Accion>(CombosBoton);
      r.CombosRueda = new Dictionary<int, Accion>(CombosRueda);
      r.Pulsaciones = new Dictionary<int, Dictionary<string, Accion>>(Pulsaciones);
      r.Otras = new Dictionary<int, Accion>(Otras);
      r.Defecto = new HashSet<int>(Defecto);
      r.RuedaGestos = new Dictionary<int, int>(RuedaGestos);
      return r;
    }

    // Anade una opcion a la lista de un boton (usado al leer la configuracion)
    public void AddOpcion(int cid, Opcion o) {
      List<Opcion> l;
      if (!Menus.TryGetValue(cid, out l)) { l = new List<Opcion>(); Menus[cid] = l; }
      l.Add(o);
    }
  }

  // ---- Gestor: mantiene la conexion y aplica la configuracion desde su propio hilo ----
  public class Manager {
    public ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
    public string Clave = "";                   // "raton" o "teclado"
    public volatile bool AvisarBateria = true;  // notificacion al bajar del 15 % y del 5 %
    int bateriaAvisada = 101;                   // ultimo umbral avisado (101 = ninguno); solo en el hilo del gestor

    Engine eng;
    Thread th;
    volatile bool stop;
    readonly object cfgLock = new object();
    readonly object evLock = new object();
    Perfil general = new Perfil();
    Dictionary<string, Perfil> apps = new Dictionary<string, Perfil>(StringComparer.OrdinalIgnoreCase);
    volatile string perfilActual = "";          // "" = General; si no, ejecutable con perfil propio
    volatile string appActual = "";             // ejecutable en primer plano
    uint lastPid;
    string lastExe = "";
    List<string> recientes = new List<string>();
    Dictionary<string, int> wheel = new Dictionary<string, int>();
    // Combinaciones de modificador(es) + boton del raton. Clave = (mascara << 16) | cid.
    Dictionary<int, Accion> combosBtnGeneral = new Dictionary<int, Accion>();
    Dictionary<string, Dictionary<int, Accion>> combosBtnApps = new Dictionary<string, Dictionary<int, Accion>>(StringComparer.OrdinalIgnoreCase);
    HashSet<int> cidsCombo = new HashSet<int>();     // botones que intervienen en alguna combinacion (se desvian)
    string filter = "MX Master";
    volatile string mode = "activo";
    volatile bool dirty, wheelDirty, conectado, avisado;
    volatile string situacion = "Buscando el raton...";
    volatile string dispositivo = "";
    volatile List<BotonInfo> botones = new List<BotonInfo>();
    volatile List<string> wheelInfo = new List<string>();
    volatile int[] luz;
    DateTime applyAfter = DateTime.MinValue;
    List<Evento> events = new List<Evento>();
    HashSet<int> pressedNow = new HashSet<int>();
    long seq;

    public string Mode { get { return mode; } }

    public void SetConfig(Perfil g, Dictionary<string, Perfil> a, Dictionary<string, int> w, string f) {
      lock (cfgLock) {
        general = g.Copia();
        apps = new Dictionary<string, Perfil>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Perfil> kv in a) apps[kv.Key] = kv.Value.Copia();
        wheel = new Dictionary<string, int>(w);
        if (!string.IsNullOrEmpty(f)) filter = f;
      }
      wheelDirty = true; applyAfter = DateTime.MinValue; dirty = true;
    }

    // Combinaciones de boton (modificador + boton del raton), general y por aplicacion
    public void SetCombosBoton(Dictionary<int, Accion> g, Dictionary<string, Dictionary<int, Accion>> a) {
      lock (cfgLock) {
        combosBtnGeneral = g ?? new Dictionary<int, Accion>();
        combosBtnApps = a ?? new Dictionary<string, Dictionary<int, Accion>>(StringComparer.OrdinalIgnoreCase);
        cidsCombo = new HashSet<int>();
        foreach (int k in combosBtnGeneral.Keys) cidsCombo.Add(k & 0xFFFF);
        foreach (Dictionary<int, Accion> d in combosBtnApps.Values) foreach (int k in d.Keys) cidsCombo.Add(k & 0xFFFF);
      }
      applyAfter = DateTime.MinValue; dirty = true;
    }

    public void SetMode(string m) {
      if (m != "activo" && m != "prueba" && m != "pausado") throw new ArgumentException("Modo no valido: " + m);
      if (m != mode) { mode = m; applyAfter = DateTime.MinValue; dirty = true; }
    }

    public void Start() { th = new Thread(Loop); th.IsBackground = true; th.Start(); }
    public void Stop() { stop = true; if (th != null) th.Join(8000); }

    // Comprobaciones periodicas del dispositivo, espaciadas para no despertar la radio (bateria).
    // Lo normal (reinicio, reconexion, cambio de bateria) ya lo avisa el propio dispositivo.
    const int SEG_COMPROBAR = 120, SEG_BATERIA = 600;
    volatile bool configurado;                  // hay algo aplicado que vigilar (si no, no se pregunta al dispositivo)

    void Loop() {
      DateTime lastTry = DateTime.MinValue, lastCheck = DateTime.UtcNow, lastBat = DateTime.UtcNow;
      while (!stop) {
        try {
          if (eng == null || eng.Dead) {
            if (eng != null) {
              int bat = eng.Bateria; bool carg = eng.Cargando;
              eng.Close(); Liberar(eng); eng = null; conectado = false;
              if (!stop) AlPerder(Clave, bat, carg);
              situacion = "Desconectado, buscando...";
              Messages.Enqueue("Conexion perdida");
              lock (evLock) { pressedNow.Clear(); }
            }
            if ((DateTime.UtcNow - lastTry).TotalSeconds >= 5) { lastTry = DateTime.UtcNow; TryConnect(); lastCheck = DateTime.UtcNow; }
          } else if ((dirty || eng.NeedsReapply) && DateTime.UtcNow >= applyAfter) {
            bool full = eng.NeedsReapply || wheelDirty;
            eng.NeedsReapply = false;
            Apply(full);
            lastCheck = DateTime.UtcNow;
          } else {
            if ((DateTime.UtcNow - lastBat).TotalSeconds >= SEG_BATERIA) { lastBat = DateTime.UtcNow; try { eng.LeerBateria(); } catch { } }
            VigilarBateria(eng);
            if (configurado && (DateTime.UtcNow - lastCheck).TotalSeconds >= SEG_COMPROBAR) {
              lastCheck = DateTime.UtcNow;
              if (eng.CheckButtons()) {
                Messages.Enqueue("El dispositivo perdio la configuracion: se vuelve a aplicar");
                Apply(true);
              } else if (mode == "activo") {
                Dictionary<string, int> w;
                lock (cfgLock) { w = new Dictionary<string, int>(wheel); }
                eng.ApplyWheel(w, false);
              }
            }
          }
        } catch (Exception ex) { Messages.Enqueue("Aviso: " + ex.Message); }
        Thread.Sleep(150);
      }
      // Al salir: botones con su funcion de fabrica
      if (eng != null && !eng.Dead) {
        try {
          eng.Resolver = null;
          eng.ApplyButtons("pausado", new HashSet<int>(), new HashSet<int>(), new Dictionary<int, int>());
          Messages.Enqueue("Botones devueltos a su funcion normal");
        } catch (Exception ex) { Messages.Enqueue("No se pudieron devolver los botones: " + ex.Message); }
      }
      if (eng != null) { eng.Close(); Liberar(eng); }
    }

    // Avisa una vez al bajar del 15 % y otra al bajar del 5 % (sin cargar); se rearma al cargar o al pasar del 20 %
    void VigilarBateria(Engine en) {
      int b = en.Bateria;
      if (b < 0) return;
      if (en.Cargando || b > 20) { bateriaAvisada = 101; return; }
      int umbral = b <= 5 ? 5 : b <= 15 ? 15 : 101;
      if (umbral >= bateriaAvisada) return;
      bateriaAvisada = umbral;
      if (!AvisarBateria) return;
      string nom = string.IsNullOrEmpty(en.DeviceName) ? (Clave == "teclado" ? "Teclado" : "Rat\u00f3n") : en.DeviceName;
      Bandeja.Notificar(Clave == "teclado" ? "teclado" : "raton", "Bater\u00eda baja: " + nom,
        "Queda un " + b + " %" + (umbral == 5 ? ". C\u00e1rgalo cuanto antes." : ". Convendr\u00eda cargarlo pronto."), b);
      Messages.Enqueue("Bateria baja (" + b + " %): aviso mostrado");
    }

    // ---- Emparejamiento: accion Easy-Switch y "seguir al otro" (los pone raton.ps1 desde la configuracion) ----
    public static volatile bool EasySwitchActivo = true, SeguirActivo = false;
    // Canal del otro equipo en cada dispositivo (1..3; 0 = el siguiente): adonde va el que sigue al otro
    public static volatile int CanalOtroRaton = 0, CanalOtroTeclado = 0;
    static DateTime cambioPropio = DateTime.MinValue;   // ultima vez que una accion cambio de equipo (no hay que seguir)

    // Un dispositivo se ha ido de este equipo. Si el otro sigue aqui, se le manda al otro equipo: asi, al pulsar el boton
    // Easy-Switch de uno (o al cambiarlo desde otro equipo), el otro le sigue. Se espera un momento: si en ese tiempo el
    // otro tambien se ha ido (el equipo se suspende, se apaga el Bluetooth...) no se hace nada.
    static void AlPerder(string clave, int bateria, bool cargando) {
      if (!SeguirActivo || clave == "") return;
      if ((DateTime.UtcNow - cambioPropio).TotalSeconds < 8) return;       // lo ha cambiado una accion: ya van los dos
      if (bateria >= 0 && bateria <= 3 && !cargando) return;               // probablemente se ha quedado sin bateria
      System.Threading.Timer t = null;
      t = new System.Threading.Timer(delegate {
        t.Dispose();
        List<Engine> l;
        lock (enUso) { l = new List<Engine>(enUso.Values); }
        foreach (Engine e in l) if (e.Clave == clave && !e.Dead) return;    // ha vuelto enseguida: era un corte
        foreach (Engine e in l) {
          if (e.Dead || e.Clave == clave || !e.TieneEquipos) continue;
          int canal = e.Clave == "raton" ? CanalOtroRaton : CanalOtroTeclado;
          try {
            if (e.CambiarEquipo(canal))
              Acciones.Avisos.Enqueue("Seguir: el " + (clave == "raton" ? "raton" : "teclado") + " se ha ido de este equipo; '" +
                                      e.DeviceName + "' pasa al " + (canal > 0 ? "canal " + canal : "canal siguiente"));
          } catch (Exception ex) { Acciones.Avisos.Enqueue("Seguir al otro dispositivo: " + ex.Message); }
        }
      }, null, 1500, System.Threading.Timeout.Infinite);
    }

    // Cambiar de equipo (Easy-Switch) con una accion: los dispositivos conectados pasan al canal indicado (0 = el
    // siguiente). quien: "ambos" (por defecto), "raton" o "teclado".
    public static void CambiarEquipo(int canal, string quien) {
      if (!EasySwitchActivo) { Aviso.Mostrar("Cambiar de equipo est\u00e1 desactivado (Emparejamiento)"); return; }
      cambioPropio = DateTime.UtcNow;
      List<Engine> l;
      lock (enUso) { l = new List<Engine>(enUso.Values); }
      int cambiados = 0, conEquipos = 0;
      foreach (Engine e in l) {
        if (e.Dead || !e.TieneEquipos) continue;
        if ((quien == "raton" || quien == "teclado") && e.Clave != quien) continue;
        conEquipos++;
        try { if (e.CambiarEquipo(canal)) cambiados++; }
        catch (Exception ex) { Acciones.Avisos.Enqueue("Cambiar de equipo (" + e.DeviceName + "): " + ex.Message); }
      }
      if (conEquipos == 0) Aviso.Mostrar("No hay ning\u00fan dispositivo con Easy-Switch conectado");
      else if (cambiados == 0) Aviso.Mostrar(canal > 0 ? "Ya est\u00e1 en el equipo " + canal : "No hay otro equipo al que cambiar");
      else Acciones.Avisos.Enqueue("Cambio de equipo: " + cambiados + " dispositivo(s) al " + (canal > 0 ? "equipo " + canal : "siguiente equipo"));
    }

    // Colecciones HID ya abiertas por otro gestor (raton/teclado): no se sondean para no mezclar respuestas
    static readonly Dictionary<string, Engine> enUso = new Dictionary<string, Engine>(StringComparer.OrdinalIgnoreCase);
    static void Liberar(Engine e) {
      lock (enUso) {
        foreach (string k in new List<string>(enUso.Keys)) if (enUso[k] == e) enUso.Remove(k);
      }
    }

    void TryConnect() {
      string f;
      lock (cfgLock) { f = filter; }
      foreach (string path in Engine.FindCandidates()) {
        lock (enUso) { if (enUso.ContainsKey(path)) continue; }
        Engine e = new Engine(Messages);
        e.Clave = Clave;
        e.OnButton = OnButton;
        e.OnGesture = OnGesture;
        try {
          e.Open(path);
          if (e.Init(f)) {
            e.LoadControls();
            try { e.LeerBateria(); } catch { }
            try { e.LeerDpi(); } catch (Exception ex) { Messages.Enqueue("No se pudo leer la velocidad del puntero: " + ex.Message); }
            try { e.LeerEquipos(); } catch { }
            List<BotonInfo> list = new List<BotonInfo>();
            foreach (Control c in e.Controls) {
              BotonInfo bi = new BotonInfo();
              bi.Cid = c.Cid; bi.Desviable = c.Divertible; bi.Reasignable = c.Remappable; bi.Destinos = e.Targets(c).ToArray();
              bi.Virtual = (c.Flags & 0x80) != 0; bi.Flags = c.Flags;     // 0x80 = control virtual, sin boton fisico
              bi.Gestos = c.Divertible && c.RawXY;
              bi.Pos = c.Pos; bi.Fkey = (c.Flags & 0x02) != 0;
              list.Add(bi);
            }
            lock (enUso) { enUso[path] = e; }
            botones = list; dispositivo = e.DeviceName;
            eng = e; conectado = true; avisado = false;
            situacion = "Conectado";
            Messages.Enqueue("Conectado a '" + e.DeviceName + "'");
            wheelDirty = true; applyAfter = DateTime.MinValue; dirty = true;
            return;
          }
        } catch (Exception ex) { Messages.Enqueue("Aviso: " + ex.Message); }
        e.Close();
      }
      situacion = "'" + f + "' no encontrado (encendido y conectado?)";
      if (!avisado) { Messages.Enqueue(situacion + "; se reintenta cada 5 s"); avisado = true; }
    }

    // Ejecutable de la ventana con el foco (solo el nombre). Se consulta al pulsar un boton, no de forma periodica.
    string ForegroundExe() {
      IntPtr h = Native.GetForegroundWindow();
      if (h == IntPtr.Zero) return lastExe;
      uint pid;
      Native.GetWindowThreadProcessId(h, out pid);
      if (pid == lastPid) return lastExe;
      lastPid = pid;
      try { using (Process p = Process.GetProcessById((int)pid)) lastExe = p.ProcessName.ToLowerInvariant() + ".exe"; }
      catch { lastExe = ""; }
      appActual = lastExe;
      if (lastExe != "")
        lock (cfgLock) { recientes.Remove(lastExe); recientes.Insert(0, lastExe); if (recientes.Count > 15) recientes.RemoveAt(15); }
      return lastExe;
    }

    // Accion de un boton gestionado al pulsarlo: perfil de la aplicacion con el foco, si no el General, si no fabrica
    Accion ResolverAccion(int cid) {
      string exe = ForegroundExe();
      Perfil g, app = null;
      Dictionary<int, Accion> cbG, cbA = null; bool esCombo;
      lock (cfgLock) {
        g = general; if (exe != "") apps.TryGetValue(exe, out app);
        cbG = combosBtnGeneral; if (exe != "") combosBtnApps.TryGetValue(exe, out cbA);
        esCombo = cidsCombo.Contains(cid);
      }
      perfilActual = app != null ? exe : "";
      // Combinacion con modificadores: solo si hay alguno pulsado y coincide (app primero, luego General)
      if (esCombo) {
        int mask = Modificadores.Mascara();
        if (mask != 0) {
          int clave = (mask << 16) | cid;
          Accion c;
          if (cbA != null && cbA.TryGetValue(clave, out c)) { Modificadores.Soltar(mask); return c; }
          if (cbG.TryGetValue(clave, out c)) { Modificadores.Soltar(mask); return c; }
        }
        // Sin modificador (o sin coincidencia): el boton hace su accion normal de este perfil (o la de fabrica)
      }
      Accion a = app != null ? AccionDe(app, cid) : null;
      if (a == null) a = AccionDe(g, cid);
      if (a == null) { a = new Accion(); a.Tipo = "defecto"; }
      return a;
    }

    // Accion principal del boton y, si tiene, sus pulsaciones extra (no con gestos: mantener ya es el gesto)
    static Accion AccionDe(Perfil p, int cid) {
      Accion a = Principal(p, cid);
      Dictionary<string, Accion> v;
      if (p.Pulsaciones.TryGetValue(cid, out v) && v.Count > 0) {
        if (a == null) a = new Accion { Tipo = "defecto" };
        if (a.Tipo != "gestos") a.Variantes = v;
      }
      return a;
    }

    // Lista de un boton con su formato propio (disposicion y columnas), si lo tiene
    static Accion ConFormato(Perfil p, int cid, Accion a) {
      int[] f;
      if (p.FormatoListas.TryGetValue(cid, out f)) { a.ListaRejilla = f[0]; a.ListaColumnas = f[1]; if (f.Length > 2) a.ListaIcono = f[2]; if (f.Length > 3) a.ListaVer = f[3]; }
      return a;
    }

    static Accion Principal(Perfil p, int cid) {
      int[] k; Dictionary<string, Accion> gg; int b;
      if (p.Atajos.TryGetValue(cid, out k)) return new Accion { Tipo = "atajo", Teclas = k };
      if (p.Gestos.TryGetValue(cid, out gg)) {
        Accion g = new Accion { Tipo = "gestos", Gestos = gg };
        int espera;
        if (p.RuedaGestos.TryGetValue(cid, out espera)) { g.MostrarRueda = true; g.EsperaRueda = espera; }
        return g;
      }
      if (p.Botones.TryGetValue(cid, out b)) return new Accion { Tipo = "boton", Boton = b };
      List<Opcion> ops;
      if (p.Menus.TryGetValue(cid, out ops)) return ConFormato(p, cid, new Accion { Tipo = "menu", Opciones = ops });
      string s;
      if (p.Textos.TryGetValue(cid, out s)) return new Accion { Tipo = "texto", Texto = s };
      if (p.Comandos.TryGetValue(cid, out s)) return new Accion { Tipo = "ejecutar", Comando = s };
      if (p.Programas.Contains(cid)) return ConFormato(p, cid, new Accion { Tipo = "ventanas" });
      Accion otra;
      if (p.Otras.TryGetValue(cid, out otra)) return otra;
      if (p.Defecto.Contains(cid)) return new Accion { Tipo = "defecto" };
      return null;
    }

    void Apply(bool setWheelMode) {
      dirty = false; wheelDirty = false;
      // Gestionados por el servidor: los que tienen atajo o gestos en algun perfil, o cambian segun la aplicacion
      HashSet<int> gest = new HashSet<int>(), conGestos = new HashSet<int>();
      Dictionary<int, int> rm; Dictionary<string, int> w;
      lock (cfgLock) {
        rm = new Dictionary<int, int>(general.Botones);
        gest.UnionWith(general.Atajos.Keys); gest.UnionWith(general.Gestos.Keys); gest.UnionWith(general.Menus.Keys);
        gest.UnionWith(general.Textos.Keys); gest.UnionWith(general.Comandos.Keys); gest.UnionWith(general.Programas);
        gest.UnionWith(general.Pulsaciones.Keys); gest.UnionWith(general.Otras.Keys);
        conGestos.UnionWith(general.Gestos.Keys);
        foreach (Perfil p in apps.Values) {
          gest.UnionWith(p.Atajos.Keys); gest.UnionWith(p.Gestos.Keys); gest.UnionWith(p.Botones.Keys); gest.UnionWith(p.Defecto);
          gest.UnionWith(p.Menus.Keys); gest.UnionWith(p.Textos.Keys); gest.UnionWith(p.Comandos.Keys); gest.UnionWith(p.Programas);
          gest.UnionWith(p.Pulsaciones.Keys); gest.UnionWith(p.Otras.Keys);
          conGestos.UnionWith(p.Gestos.Keys);
        }
        gest.UnionWith(cidsCombo);                 // botones de combinaciones: tambien se desvian
        w = new Dictionary<string, int>(wheel);
      }
      string m = mode;
      // En pausa todo esta de fabrica; en activo, solo si hay botones o rueda configurados
      configurado = m == "prueba" || (m == "activo" && (gest.Count > 0 || rm.Count > 0 || w.Count > 0));
      try {
        eng.Resolver = m == "activo" ? (Func<int, Accion>)ResolverAccion : null;
        eng.ApplyButtons(m, gest, conGestos, rm);
        if (m == "activo" && setWheelMode) eng.ApplyWheel(w, true);
        if (setWheelMode) {
          wheelInfo = eng.WheelInfo();
          try { luz = eng.LeerLuz(); } catch { }
        }
        Messages.Enqueue("Modo '" + m + "' aplicado");
      } catch (Exception ex) {
        dirty = true; if (setWheelMode) wheelDirty = true;
        applyAfter = DateTime.UtcNow.AddSeconds(5);
        Messages.Enqueue("No se pudo aplicar la configuracion (se reintenta): " + ex.Message);
      }
    }

    void OnButton(int cid, bool down) {
      lock (evLock) {
        seq++;
        Evento e = new Evento();
        e.Id = seq; e.Hora = DateTime.Now.ToString("HH:mm:ss.fff"); e.Cid = cid; e.Pulsado = down;
        events.Add(e);
        if (events.Count > 200) events.RemoveAt(0);
        if (down) pressedNow.Add(cid); else pressedNow.Remove(cid);
      }
    }

    void OnGesture(int cid, string dir) {
      lock (evLock) {
        seq++;
        Evento e = new Evento();
        e.Id = seq; e.Hora = DateTime.Now.ToString("HH:mm:ss.fff"); e.Cid = cid; e.Pulsado = false; e.Gesto = dir;
        events.Add(e);
        if (events.Count > 200) events.RemoveAt(0);
      }
    }

    public Evento[] GetEvents(long since) {
      lock (evLock) { return events.FindAll(delegate (Evento e) { return e.Id > since; }).ToArray(); }
    }
    public int[] GetPressed() {
      lock (evLock) { int[] a = new int[pressedNow.Count]; pressedNow.CopyTo(a); return a; }
    }
    public long LastEventId { get { lock (evLock) { return seq; } } }

    public Estado GetStatus() {
      Estado s = new Estado();
      s.Conectado = conectado; s.Dispositivo = dispositivo; s.Situacion = situacion; s.Modo = mode;
      s.Botones = botones; s.Rueda = wheelInfo; s.Luz = conectado ? luz : null;
      s.AppActual = appActual; s.Perfil = perfilActual;
      Engine en = eng;
      s.Bateria = conectado && en != null ? en.Bateria : -1; s.Cargando = conectado && en != null && en.Cargando;
      if (conectado && en != null && en.TieneDpi && en.DpiActual > 0) s.Dpi = new int[] { en.DpiActual, en.DpiMin, en.DpiMax, en.DpiPaso, en.DpiDefecto };
      if (conectado && en != null && en.TieneEquipos && en.Equipos > 0) s.Equipos = new int[] { en.Equipos, en.EquipoActual + 1 };
      lock (cfgLock) { s.Recientes = recientes.ToArray(); }
      return s;
    }
  }
}
