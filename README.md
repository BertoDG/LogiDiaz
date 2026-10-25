# LogiDiaz+

Configura un ratón y un teclado **Logitech** (MX Master, MX Keys y similares) **sin instalar Logi Options+**.
Pensada para equipos de empresa donde no se puede instalar software: es un pequeño servidor local en PowerShell que
habla con el dispositivo por HID++ 2.0 y sirve una página de configuración en `http://localhost:8765`.

No necesita instalación ni permisos de administrador, y no envía nada a internet: solo escucha en el propio equipo.

## Qué puede hacer

- **Botones y teclas**: atajos de teclado, actuar como otro botón, plantillas de texto (con fecha, hora, portapapeles…),
  ejecutar programas, listas y rejillas de opciones junto al cursor, lista de programas abiertos, paleta de comandos,
  transformar el texto seleccionado, captura de pantalla (también a texto con el OCR de Windows), chat con Claude y
  recolocar la ventana activa en una cuadrícula.
- **Doble clic y pulsación larga** con su propia acción, y **gestos** (mantener el botón y mover) con una acción por dirección.
- **Perfiles por aplicación**: cada botón o tecla puede hacer algo distinto según el programa que tenga el foco.
- **Combinaciones**: atajos globales de teclado, modificador + botón del ratón y modificador + giro de rueda.
- **Ratón**: velocidad del puntero (DPI), modo precisión mientras se mantiene un botón, SmartShift, dirección y
  resolución de las ruedas.
- **Teclado**: retroiluminación (modo, brillo y tiempos de apagado).
- **Batería**: icono en la bandeja del sistema y aviso de batería baja.
- **Easy-Switch**: cambiar teclado y ratón de equipo a la vez con una acción, o que uno siga al otro al cambiarlo con su botón.
- **Silenciar el micrófono** de Windows desde cualquier botón, tecla o gesto.
- Aspecto configurable de las listas (tema, color, transparencia, difuminado, esquinas, iconos…), tema claro/oscuro
  de la página y buscador de botones, teclas y acciones (Ctrl+F).

## Requisitos

- Windows 10 u 11 (PowerShell y .NET Framework vienen de serie).
- Un dispositivo Logitech con HID++ 2.0 emparejado con el equipo, por Bluetooth o con receptor.
- Las fotos de la página son de un **MX Keys S** (distribución española) y un **MX Master 3S**. Con otros modelos la
  configuración funciona igual, pero los puntos sobre las fotos pueden no coincidir.

## Puesta en marcha

1. Descarga el repositorio (Code → Download ZIP, o `git clone`) y descomprímelo en una carpeta tuya.
2. Si lo has descargado como ZIP, **desbloquea los ficheros**: Windows los marca como «procedentes de internet» y
   PowerShell se niega a ejecutar `raton.ps1`. En PowerShell, dentro de la carpeta:
   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```
3. Doble clic en **`iniciar.cmd`**. La primera vez tarda unos segundos más: compila su parte en C# (con el `csc.exe`
   de Windows) y la guarda en `%LOCALAPPDATA%\RatonMX`; los siguientes arranques solo la cargan.
4. Se abre la página con tu navegador por defecto (en una ventana propia si es Chrome, Edge, Brave o Vivaldi).

Si `MachinePolicy` o `UserPolicy` de `Get-ExecutionPolicy -List` están en `Restricted` o `AllSigned`, la política la
impone la empresa (GPO) y el script no arrancará: tiene que permitirlo el departamento de IT.

## Uso

- **Guardar** aplica la configuración al momento y la guarda en `configuracion.json`.
- Menú **⏻** de la barra superior: arrancar con Windows, segundo plano (oculta la consola) y cerrar el servidor.
- Pestañas **Teclado** y **Ratón**: botones y teclas (al pulsar uno en la foto se abre su configuración), perfiles por
  aplicación, combinaciones, iluminación, velocidad del puntero, gestos, ruedas, batería y emparejamiento.
- Pestaña **Listas/Rejillas**: aspecto de las listas, rejillas y paleta.
- Modos: **Activo** (lo configurado), **Probar** (ilumina lo que pulsas sin ejecutar nada) y **Pausado** (todo de fábrica).
- Para pararlo: menú ⏻ → *Cerrar servidor*, o `parar.cmd`. Al cerrarse, botones y teclas vuelven a su función de fábrica.

### Si el dispositivo no aparece

La app busca los dispositivos por nombre (por defecto `MX Master` y `MX Keys`). Con otro modelo, edita
`configuracion.json` y cambia el campo `dispositivo` de la sección `raton` o `teclado` por una parte del nombre real
(el que aparece en Configuración de Windows → Bluetooth y dispositivos), por ejemplo `"K380"`.

## Ficheros

| Fichero | Qué es |
|---|---|
| `iniciar.cmd` / `parar.cmd` | Arrancan y paran el servidor |
| `raton.ps1` | Servidor local: página, API y lectura de la configuración |
| `raton.cs` | Motor en C#: HID++, acciones, listas, bandeja, gestos… |
| `ocr.cs` | Reconocimiento de texto (solo se carga si alguna acción lo usa) |
| `index.html` | Página de configuración |
| `icono.ico` | Icono de la app |
| `img/` | Fotos del teclado y del ratón que muestra la página |

`configuracion.json`, el registro (`raton.log`) y los ficheros `*.flag` son de cada equipo y no se suben al repositorio.

## Seguridad

- El servidor solo acepta conexiones del propio equipo, y la API exige un token que cambia en cada arranque.
- Se ejecuta con la política `RemoteSigned` del propio proceso y la ventana minimizada; no usa `Bypass` ni ventanas ocultas.
- No lee el teclado de forma continua: solo recibe las teclas o combinaciones que se configuran.

## Aviso

Proyecto personal, sin relación con Logitech. Logitech, MX Master, MX Keys, Logi Options+ y Easy-Switch son marcas de
Logitech. Las fotos de `img/` proceden de la web de Logitech y se usan solo como referencia visual.
