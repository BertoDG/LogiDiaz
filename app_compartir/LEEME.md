# LogiDiaz+ para compartir

Copia de la app con los ficheros que suelen bloquear el correo o los filtros de descarga renombrados:

| Aquí se llama | Nombre real |
|---|---|
| `iniciar.cm.txt` | `iniciar.cmd` |
| `parar.cm.txt` | `parar.cmd` |
| `raton.ps1.txt` | `raton.ps1` |

El resto de ficheros (`raton.cs`, `ocr.cs`, `index.html`, `icono.ico` y la carpeta `img`) ya tienen su nombre real.
El contenido es exactamente el de la app; solo cambia el nombre.

## Ponerla en marcha

1. Copia esta carpeta donde quieras tenerla (por ejemplo, `C:\Users\<usuario>\LogiDiaz+`).
2. Abre PowerShell **dentro de la carpeta** y devuelve a los ficheros su nombre real:
   ```powershell
   Rename-Item iniciar.cm.txt iniciar.cmd
   Rename-Item parar.cm.txt parar.cmd
   Rename-Item raton.ps1.txt raton.ps1
   ```
3. Desbloquea los ficheros (Windows los marca como «descargados de internet» y, si no, PowerShell no ejecuta `raton.ps1`):
   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```
4. Doble clic en **`iniciar.cmd`**. La primera vez tarda unos segundos más (compila su parte en C#); se abre la página
   de configuración en el navegador.

Si al arrancar dice que el script no está firmado o que la ejecución está deshabilitada, mira `Get-ExecutionPolicy -List`:
si `MachinePolicy` o `UserPolicy` están en `Restricted` o `AllSigned`, lo impone la empresa y tiene que permitirlo IT.

Más detalles (qué hace, requisitos, uso y seguridad) en el `README.md` de la carpeta principal del repositorio.
