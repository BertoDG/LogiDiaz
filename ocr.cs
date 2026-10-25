// ocr.cs - Copiar texto de cualquier parte de la pantalla (reconocimiento de texto de Windows, sin internet).
// Modulo aparte: raton.ps1 solo lo carga si la configuracion tiene alguna accion de OCR, para que
// la captura de pantalla no forme parte del servidor cuando no se usa.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace LogiOcr {
  // ---- Copiar texto de cualquier parte de la pantalla (OCR de Windows, sin internet) ----
  public static class Ocr {
    // Funciones del servidor (las asigna raton.ps1 al cargar el modulo)
    public static Action<string> Avisar = delegate { };
    public static Action<string> Registro = delegate { };
    public static Action<IntPtr> Activar = delegate { };
    public static Action<MethodInvoker> EnUI = delegate (MethodInvoker m) { m(); };

    static FormRecorte form;
    public static void Iniciar() {
      EnUI(delegate { if (form == null) form = new FormRecorte(); form.Abrir(Reconocer); });
    }

    // Recorte de pantalla para otro uso (p. ej. llevarlo al chat): entrega la imagen de la zona elegida
    // en un hilo STA (puede usar el portapapeles). Si se cancela, no se llama.
    public static void Recortar(Action<Bitmap> alTerminar) {
      EnUI(delegate {
        if (form == null) form = new FormRecorte();
        form.Abrir(delegate (Rectangle r) {
          Thread th = new Thread(delegate () {
            try {
              Thread.Sleep(120);                                               // que la capa oscura haya desaparecido
              Bitmap b = new Bitmap(r.Width, r.Height);
              using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
              alTerminar(b);
            } catch (Exception e) { Registro("Recorte: " + e.Message); }
          });
          th.SetApartmentState(ApartmentState.STA);
          th.IsBackground = true;
          th.Start();
        });
      });
    }

    // Captura la zona, reconoce el texto y lo copia al portapapeles
    internal static void Reconocer(Rectangle r) {
      Thread th = new Thread(delegate () {
        try {
          Thread.Sleep(120);                                                   // que la capa oscura haya desaparecido
          string texto;
          using (Bitmap b = new Bitmap(r.Width, r.Height)) {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            texto = TextoDe(b);
          }
          if (string.IsNullOrEmpty(texto.Trim())) { Avisar("No se ha encontrado texto en esa zona"); return; }
          Clipboard.SetDataObject(texto, true, 10, 50);
          string una = Regex.Replace(texto, @"\s+", " ").Trim();
          Avisar("Copiado: " + (una.Length > 60 ? una.Substring(0, 60) + "..." : una));
        } catch (Exception e) {
          Registro("OCR: " + e.Message);
          Avisar("No se pudo reconocer el texto");
        }
      });
      th.SetApartmentState(ApartmentState.STA);
      th.IsBackground = true;
      th.Start();
    }

    // Reconocimiento con Windows.Media.Ocr (WinRT) mediante reflexion, para no depender de compilar contra Windows.winmd
    static Type T(string nombre) {
      Type t = Type.GetType(nombre + ", Windows.Foundation, ContentType=WindowsRuntime", false);
      if (t == null) throw new Exception("Windows no ofrece " + nombre);
      return t;
    }
    const string RT = ", System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    static object Esperar(object operacion, Type resultado) {
      Type ext = Type.GetType("System.WindowsRuntimeSystemExtensions" + RT, true);
      System.Reflection.MethodInfo m = null;
      foreach (System.Reflection.MethodInfo mi in ext.GetMethods()) {
        if (mi.Name != "AsTask" || !mi.IsGenericMethod) continue;
        System.Reflection.ParameterInfo[] ps = mi.GetParameters();
        if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") { m = mi; break; }
      }
      System.Threading.Tasks.Task t = (System.Threading.Tasks.Task)m.MakeGenericMethod(resultado).Invoke(null, new object[] { operacion });
      t.Wait();
      return t.GetType().GetProperty("Result").GetValue(t, null);
    }

    public static string TextoDe(Bitmap original) {
      // Ampliar zonas pequenas mejora mucho el reconocimiento
      int lado = Math.Max(original.Width, original.Height);
      double f = Math.Min(3.0, Math.Max(1.0, 2400.0 / Math.Max(1, lado)));
      if (lado * f > 2500) f = 2500.0 / lado;
      using (Bitmap b = new Bitmap((int)(original.Width * f), (int)(original.Height * f))) {
        using (Graphics g = Graphics.FromImage(b)) {
          g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
          g.DrawImage(original, 0, 0, b.Width, b.Height);
        }
        using (System.IO.MemoryStream ms = new System.IO.MemoryStream()) {
          b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
          ms.Position = 0;
          Type extStream = Type.GetType("System.IO.WindowsRuntimeStreamExtensions" + RT, true);
          object ras = extStream.GetMethod("AsRandomAccessStream", new Type[] { typeof(System.IO.Stream) }).Invoke(null, new object[] { ms });
          Type tDec = T("Windows.Graphics.Imaging.BitmapDecoder");
          Type tRas = T("Windows.Storage.Streams.IRandomAccessStream");
          object dec = Esperar(tDec.GetMethod("CreateAsync", new Type[] { tRas }).Invoke(null, new object[] { ras }), tDec);
          Type tSb = T("Windows.Graphics.Imaging.SoftwareBitmap");
          object sb = Esperar(tDec.GetMethod("GetSoftwareBitmapAsync", Type.EmptyTypes).Invoke(dec, null), tSb);
          Type tOcr = T("Windows.Media.Ocr.OcrEngine");
          object motor = tOcr.GetMethod("TryCreateFromUserProfileLanguages").Invoke(null, null);
          if (motor == null) throw new Exception("No hay ningun idioma de reconocimiento de texto instalado en Windows");
          Type tRes = T("Windows.Media.Ocr.OcrResult");
          object res = Esperar(tOcr.GetMethod("RecognizeAsync").Invoke(motor, new object[] { sb }), tRes);
          Type tLinea = T("Windows.Media.Ocr.OcrLine");
          StringBuilder texto = new StringBuilder();
          foreach (object linea in (System.Collections.IEnumerable)tRes.GetProperty("Lines").GetValue(res, null)) {
            if (texto.Length > 0) texto.Append("\r\n");
            texto.Append((string)tLinea.GetProperty("Text").GetValue(linea, null));
          }
          return texto.ToString();
        }
      }
    }
  }

  // Capa oscura sobre toda la pantalla para dibujar el recuadro del OCR (Esc o clic derecho cancelan)
  public class FormRecorte : Form {
    Point inicio; Rectangle sel; bool arrastrando;
    Action<Rectangle> destino;                                               // que hacer con la zona elegida
    public FormRecorte() {
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
      BackColor = Color.Black; Opacity = 0.35; TransparencyKey = Color.Magenta;
      DoubleBuffered = true; KeyPreview = true; Cursor = Cursors.Cross;
    }
    public void Abrir(Action<Rectangle> alElegir) {
      destino = alElegir;
      Bounds = SystemInformation.VirtualScreen;
      sel = Rectangle.Empty; arrastrando = false;
      Show();
      Ocr.Activar(Handle);                                                    // para recibir Esc
      // Por si Windows no le da el foco: mientras se ve, Esc tambien como atajo de Windows (sin ganchos de teclado)
      if (!escRegistrado) escRegistrado = RegisterHotKey(Handle, ID_ESC, 0x4000, 0x1B);   // MOD_NOREPEAT, VK_ESCAPE
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int ID_ESC = 0x5E5E;
    bool escRegistrado;
    protected override void OnVisibleChanged(EventArgs e) {
      base.OnVisibleChanged(e);
      if (!Visible && escRegistrado) { UnregisterHotKey(Handle, ID_ESC); escRegistrado = false; }
    }
    protected override void WndProc(ref Message m) {
      if (m.Msg == 0x0312 && (int)m.WParam == ID_ESC) { Hide(); return; }   // WM_HOTKEY
      base.WndProc(ref m);
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == System.Windows.Forms.Keys.Escape) Hide(); }
    protected override void OnMouseDown(MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) { Hide(); return; }
      inicio = e.Location; arrastrando = true; sel = Rectangle.Empty;
    }
    protected override void OnMouseMove(MouseEventArgs e) {
      if (!arrastrando) return;
      sel = Rectangle.FromLTRB(Math.Min(inicio.X, e.X), Math.Min(inicio.Y, e.Y), Math.Max(inicio.X, e.X), Math.Max(inicio.Y, e.Y));
      Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) {
      if (!arrastrando || e.Button != MouseButtons.Left) return;
      arrastrando = false;
      Rectangle r = sel;
      Hide();
      if (r.Width > 4 && r.Height > 4 && destino != null) destino(new Rectangle(r.X + Left, r.Y + Top, r.Width, r.Height));
    }
    protected override void OnPaint(PaintEventArgs e) {
      if (sel.Width <= 0 || sel.Height <= 0) return;
      using (SolidBrush b = new SolidBrush(Color.Magenta)) e.Graphics.FillRectangle(b, sel);   // zona elegida: se ve sin oscurecer
      using (Pen p = new Pen(Color.White, 2)) e.Graphics.DrawRectangle(p, sel);
    }
  }

}
