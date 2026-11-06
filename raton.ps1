# raton.ps1
# Configura un raton Logitech (MX Master) sin Logi Options+: atajos de teclado, reasignacion de
# botones, rueda (SmartShift, inversion, alta resolucion) y modo prueba para ver los botones pulsados.
#
# Hace de servidor local en http://localhost:8765/ que sirve index.html y atiende sus botones.
# Solo acepta conexiones del propio equipo, y las POST llevan un token que cambia en cada arranque.
# Habla con el raton por HID++ 2.0 (funciones 0x1B04, 0x2110/0x2111, 0x2121, 0x2150); ese codigo esta en raton.cs.
#
# Arranque: iniciar.cmd (o "Arrancar con Windows" desde la pagina).
# Parada: boton "Cerrar servidor" de la pagina, o parar.cmd.

param(
    [int]$Puerto = 8765,
    [switch]$SinNavegador
)

$ErrorActionPreference = 'Stop'
# Titulo de la consola: el Administrador de tareas identifica el proceso por el titulo de su ventana
try { $Host.UI.RawUI.WindowTitle = 'LogiDiaz+' } catch { }
$dir      = $PSScriptRoot
$url      = "http://localhost:$Puerto/"
$cfgFile  = Join-Path $dir 'configuracion.json'
$oldFile  = Join-Path $dir 'atajos.json'
$htmlFile = Join-Path $dir 'index.html'
$logFile  = Join-Path $dir 'raton.log'
$stopFile = Join-Path $dir 'parar.flag'
$lnkPath  = Join-Path ([Environment]::GetFolderPath('Startup')) 'Raton MX Master.lnk'
$bgFile   = Join-Path $dir 'segundoplano.flag'
$trayFile = @{ raton = (Join-Path $dir 'bandeja.raton.flag'); teclado = (Join-Path $dir 'bandeja.teclado.flag') }
$utf8SinBom = New-Object System.Text.UTF8Encoding $false

# Abre la pagina con el navegador por defecto: si es Chrome, Edge, Brave o Vivaldi, en ventana propia (modo app,
# sin pestanas); con cualquier otro, en una pestana normal. (El icono de bandeja usa lo mismo: Bandeja.AbrirPagina)
function Abrir-Pagina {
    $nav = $null
    try {
        $prog = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' -ErrorAction Stop).ProgId
        $cmd = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\$prog\shell\open\command" -ErrorAction Stop).'(default)'
        if ($cmd -match '^\s*"([^"]+)"') { $nav = $Matches[1] } elseif ($cmd -match '^\s*(\S+)') { $nav = $Matches[1] }
    } catch { }
    if ($nav -and (Test-Path $nav) -and (Split-Path $nav -Leaf).ToLower() -in 'chrome.exe', 'msedge.exe', 'brave.exe', 'vivaldi.exe') {
        Start-Process $nav "--app=$url"
    } else { Start-Process $url }
}

# ---- Una sola instancia: si ya hay una en marcha, solo se abre la pagina ----
$mutex = New-Object System.Threading.Mutex($false, 'Local\LogiRatonServidor')
if (-not $mutex.WaitOne(0)) {
    if (-not $SinNavegador) { Abrir-Pagina }
    exit
}

# ---- Codigo nativo (raton.cs y, si se usa, ocr.cs) ----
# Se compila una sola vez por version y se guarda como DLL en %LOCALAPPDATA%\RatonMX; los arranques
# siguientes solo la cargan. Asi el compilador (csc.exe) no se lanza en cada inicio de sesion.
$cacheDir = Join-Path $env:LOCALAPPDATA 'RatonMX'
function Cargar-Modulo([string]$fuente, [string]$nombre) {
    $hash = (Get-FileHash -Path $fuente -Algorithm SHA256).Hash.Substring(0, 16)
    $dll = Join-Path $cacheDir ('{0}-{1}.dll' -f $nombre, $hash)
    if (-not (Test-Path $dll)) {
        if (-not (Test-Path $cacheDir)) { [void](New-Item -ItemType Directory -Path $cacheDir) }
        $tmp = Join-Path $cacheDir ('{0}-{1}.{2}.tmp' -f $nombre, $hash, $PID)
        Add-Type -TypeDefinition ([IO.File]::ReadAllText($fuente)) -ReferencedAssemblies System.Windows.Forms, System.Drawing `
                 -OutputAssembly $tmp -OutputType Library
        Move-Item -Path $tmp -Destination $dll -Force
        # Versiones anteriores del mismo modulo (si alguna esta en uso, se deja)
        Get-ChildItem -Path $cacheDir -Filter "$nombre-*.dll" | Where-Object { $_.FullName -ne $dll } |
            ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -ErrorAction Stop } catch { } }
    }
    Add-Type -Path $dll
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Cargar-Modulo (Join-Path $dir 'raton.cs') 'LogiRaton'

# ---- Registro ----
if ((Test-Path $logFile) -and (Get-Item $logFile).Length -gt 1MB) { Remove-Item $logFile }
$registro = New-Object System.Collections.ArrayList
function Log([string]$m) {
    $l = '[{0:dd-MM-yyyy HH:mm:ss}] {1}' -f (Get-Date), $m
    # Primero el fichero y la web; la consola al final (si se quedara esperando, lo demas ya esta escrito)
    [void]$registro.Add($l)
    while ($registro.Count -gt 60) { $registro.RemoveAt(0) }
    try { Add-Content -Path $logFile -Value $l } catch { }
    Write-Host $l
}
function Drenar {
    $m = $null
    foreach ($k in $mgrs.Keys) {
        while ($mgrs[$k].Messages.TryDequeue([ref]$m)) { Log "[$k] $m" }
    }
    while ([LogiRaton.Acciones]::Avisos.TryDequeue([ref]$m)) { Log $m }
}
function Mensaje-Error($e) {
    if ($e.Exception.InnerException) { $e.Exception.InnerException.Message } else { $e.Exception.Message }
}

# ---- Configuracion (configuracion.json) ----
$alias = @{ gestos = 0xC3; modo = 0xC4; atras = 0x53; adelante = 0x56; central = 0x52 }
function Cid-De([string]$s) {
    $k = $s.Trim().ToLower()
    if ($alias.ContainsKey($k)) { return [int]$alias[$k] }
    if ($k -match '^0x[0-9a-f]{1,4}$') { return [Convert]::ToInt32($k.Substring(2), 16) }
    throw "Boton desconocido: '$s'"
}

# ---- Copias de la configuracion (carpeta copias): antes de sobrescribirla se guarda la version anterior; quedan las 10 ultimas ----
$copiasDir = Join-Path $dir 'copias'
function Guardar-Copia([string]$nuevoTexto) {
    if (-not (Test-Path $cfgFile)) { return }
    $actual = [IO.File]::ReadAllText($cfgFile, [Text.Encoding]::UTF8)
    if ($actual -eq $nuevoTexto) { return }                     # sin cambios: no hace falta copia
    if (-not (Test-Path $copiasDir)) { [void](New-Item -ItemType Directory -Path $copiasDir) }
    [IO.File]::WriteAllText((Join-Path $copiasDir ('configuracion-{0:yyyyMMdd-HHmmss}.json' -f (Get-Date))), $actual, $utf8SinBom)
    Get-ChildItem -Path $copiasDir -Filter 'configuracion-*.json' | Sort-Object Name -Descending | Select-Object -Skip 10 |
        ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -ErrorAction Stop } catch { } }
}
function Listar-Copias {
    if (-not (Test-Path $copiasDir)) { return @() }
    return @(Get-ChildItem -Path $copiasDir -Filter 'configuracion-*.json' | Sort-Object Name -Descending | ForEach-Object {
        [ordered]@{ nombre = $_.Name; fecha = $_.LastWriteTime.ToString('dd/MM/yyyy HH:mm:ss'); bytes = $_.Length }
    })
}

# Devuelve el texto JSON de la configuracion; migra atajos.json (version anterior) si hace falta.
function Leer-ConfigTexto {
    if (Test-Path $cfgFile) { return [IO.File]::ReadAllText($cfgFile, [Text.Encoding]::UTF8) }
    if (Test-Path $oldFile) {
        $old = [IO.File]::ReadAllText($oldFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $botones = [ordered]@{}
        if ($old.atajos) {
            foreach ($p in $old.atajos.PSObject.Properties) {
                if ($p.Value) { $botones[('0x{0:X4}' -f (Cid-De $p.Name))] = [ordered]@{ accion = 'atajo'; atajo = [string]$p.Value } }
            }
        }
        $nuevo = [ordered]@{ dispositivo = $(if ($old.dispositivo) { $old.dispositivo } else { 'MX Master' }); botones = $botones; rueda = $old.rueda }
        $txt = $nuevo | ConvertTo-Json -Depth 6
        [IO.File]::WriteAllText($cfgFile, $txt, $utf8SinBom)
        Log 'atajos.json convertido a configuracion.json'
        return $txt
    }
    return '{ "dispositivo": "MX Master", "botones": {}, "rueda": {} }'
}

# Botones del raton que una opcion de lista puede pulsar (texto mostrado en la ventana)
$botonesClic = @{ 0x50 = 'Clic izquierdo'; 0x51 = 'Clic derecho'; 0x52 = 'Clic central'; 0x53 = "Atr$([char]0xE1)s"; 0x56 = 'Adelante' }

# Opciones de una lista: cada una es un atajo, un clic de un boton, un texto a pegar o un comando
# Formato propio de una lista: { disposicion: lista|rejilla|circular, columnas: 0 (automatico) o 2-6, icono: px en la rejilla,
#   mostrar: { icono, etiqueta, contenido } }. Devuelve @(disposicion, columnas, icono, ver) con -1 en lo que no se indique
#   (disposicion: 0 lista, 1 rejilla, 2 circular)
#   (= como en la pestana Listas); ver = 1 icono + 2 etiqueta + 4 contenido.
function Formato-Lista($v) {
    $r = if ($v.disposicion -eq 'circular') { 2 } elseif ($v.disposicion -eq 'rejilla') { 1 } elseif ($v.disposicion -eq 'lista') { 0 } else { -1 }
    $c = -1; $i = -1; $n = 0
    if ($null -ne $v.columnas -and [int]::TryParse([string]$v.columnas, [ref]$n)) { $c = if ($n -le 0) { 0 } else { [Math]::Max(2, [Math]::Min(6, $n)) } }
    if ($null -ne $v.icono -and [int]::TryParse([string]$v.icono, [ref]$n) -and $n -gt 0) { $i = [Math]::Max(16, [Math]::Min(96, $n)) }
    $ver = -1
    if ($v.mostrar) {
        $ver = 0
        if ($v.mostrar.icono) { $ver += 1 }
        if ($v.mostrar.etiqueta) { $ver += 2 }
        if ($v.mostrar.contenido) { $ver += 4 }
        if ($ver -eq 0) { $ver = 2 }                     # algo hay que ver: la etiqueta
    }
    return ,@($r, $c, $i, $ver)
}
function Poner-FormatoLista($perfil, [int]$cid, $v) {
    $f = Formato-Lista $v
    if ($f[0] -ge 0 -or $f[1] -ge 0 -or $f[2] -ge 0 -or $f[3] -ge 0) { $perfil.FormatoListas[$cid] = [int[]]$f }
}

# Acciones de boton que tambien puede ser una opcion de lista (tipo -> texto secundario de la opcion)
$nombresAccionLista = @{ menu = 'Lista'; ventanas = 'Programas abiertos'; paleta = 'Paleta'; claude = 'Chat con Claude'; recolocar = 'Recolocar ventana'
                         dpi = 'Velocidad del puntero'; equipo = 'Cambiar de equipo'; macro = 'Macro'; volumenApp = 'Volumen de la aplicacion' }
# Texto corto de una accion para la rueda de gestos (el atajo, el programa, "Lista"...)
function Etiqueta-Accion($v) {
    if ($v -is [string]) { return $v }
    $t = [string]$v.accion
    switch ($t) {
        'atajo' { return [string]$v.atajo }
        'texto' { $una = ([string]$v.texto -replace '\s+', ' ').Trim(); return $(if ($una.Length -gt 22) { $una.Substring(0, 22) + '...' } else { $una }) }
        'ejecutar' {
            # El programa sin ruta ni extension: entre comillas si las lleva; si no, hasta el primer espacio
            $c = ([string]$v.comando).Trim()
            $prog = if ($c -match '^"([^"]+)"') { $Matches[1] } else { $c.Split(' ')[0] }
            $nom = [IO.Path]::GetFileNameWithoutExtension($prog)
            return $(if ($nom) { $nom } else { $prog })
        }
        'boton' { $bc = 0; try { $bc = Cid-De ([string]$v.boton) } catch { }; return $(if ($botonesClic.ContainsKey($bc)) { $botonesClic[$bc] } else { 'Boton' }) }
        'transformar' { return 'Transformar' }
        'captura' { return 'Captura' }
        'ocr' { return 'Texto de pantalla' }
    }
    if ($nombresAccionLista.ContainsKey($t)) { return $nombresAccionLista[$t] }
    return $t
}
function Convertir-Opciones($opciones, $errores, [string]$donde) {
    $lista = New-Object 'System.Collections.Generic.List[LogiRaton.Opcion]'
    $n = 0
    foreach ($o in @($opciones)) {
        $n++
        if ($null -eq $o) { continue }
        try {
            $op = New-Object LogiRaton.Opcion
            if ($o.tipo -eq 'boton') {
                $bc = Cid-De ([string]$o.boton)
                if (-not $botonesClic.ContainsKey($bc)) { throw "Boton no valido para un clic: '$($o.boton)'" }
                $op.Boton = $bc; $op.Detalle = $botonesClic[$bc]
            } elseif ($o.tipo -eq 'texto') {
                if (-not [string]$o.texto) { throw 'Texto a pegar vacio' }
                $op.Texto = [string]$o.texto
                # En la ventana se muestra el principio del texto, en una linea
                $una = ($op.Texto -replace '\s+', ' ').Trim()
                $op.Detalle = if ($una.Length -gt 24) { $una.Substring(0, 24) + '...' } else { $una }
            } elseif ($o.tipo -eq 'ejecutar') {
                if (-not ([string]$o.comando).Trim()) { throw 'Comando a ejecutar vacio' }
                $op.Comando = ([string]$o.comando).Trim(); $op.Detalle = $op.Comando
            } elseif ($o.tipo -eq 'transformar') {
                # Transforma el texto seleccionado; aqui no se admite 'elegir' (ya es una lista)
                $clave = ([string]$o.transformacion).Trim()
                $nombreT = if ($clave) { [LogiRaton.Transformar]::Nombre($clave) } else { $null }
                if (-not $nombreT) { throw "Transformacion no valida: '$($o.transformacion)'" }
                $op.Transformacion = $clave; $op.Detalle = $nombreT
            } elseif ($o.tipo -eq 'ocr') {
                $op.Ocr = $true; $op.Detalle = 'Texto de la pantalla'
            } elseif ($o.tipo -eq 'captura') {
                # Captura de pantalla: texto (OCR), imagen (recorte) o pantalla (Imprimir pantalla)
                $modo = ([string]$o.modo).Trim().ToLower()
                if (-not $modo) { $modo = 'texto' }
                if ($modo -notin 'texto', 'imagen', 'pantalla', 'color', 'colorRgb') { throw "Modo de captura no valido: '$($o.modo)'" }
                $op.Captura = $modo
                $op.Detalle = @{ texto = 'Recorte a texto'; imagen = 'Recorte a imagen'; pantalla = 'Imprimir pantalla'; color = 'Color (HEX)'; colorRgb = 'Color (RGB)' }[$modo]
            } elseif ($nombresAccionLista.ContainsKey([string]$o.tipo)) {
                # Cualquier otra accion de boton (salvo gestos): la opcion lleva sus campos y se convierte como un boton
                $o2 = $o | Select-Object *
                $o2 | Add-Member -NotePropertyName accion -NotePropertyValue ([string]$o.tipo) -Force
                $acc = Accion-Desde $o2 $errores $donde
                if (-not $acc) { continue }                          # el error ya esta anotado
                $op.Hacer = $acc; $op.Detalle = $nombresAccionLista[[string]$o.tipo]
            } else {
                $op.Teclas = [LogiRaton.Keys]::ParseCombo([string]$o.atajo); $op.Detalle = [string]$o.atajo
            }
            $op.Nombre = if ([string]$o.nombre) { [string]$o.nombre } else { $op.Detalle }
            $op.Icono = [LogiRaton.Iconos]::Crear([string]$o.icono, $op.Comando)
            $lista.Add($op)
        } catch { [void]$errores.Add(("{0}, opcion {1}: {2}" -f $donde, $n, (Mensaje-Error $_))) }
    }
    return ,$lista
}

# Accion de una direccion de gesto. Admite el formato antiguo (un texto = atajo) y el nuevo
# ({ accion: atajo | boton | texto | ejecutar | menu | ventanas | transformar | ocr | paleta, ... }). Devuelve $null si no hay accion.
function Accion-Desde($v, $errores, [string]$donde) {
    if ($null -eq $v) { return $null }
    if ($v -is [string]) {
        if (-not $v.Trim()) { return $null }
        $v = [pscustomobject]@{ accion = 'atajo'; atajo = $v }
    }
    $a = New-Object LogiRaton.Accion
    try {
        switch ([string]$v.accion) {
            'atajo' { $a.Tipo = 'atajo'; $a.Teclas = [LogiRaton.Keys]::ParseCombo([string]$v.atajo) }
            'boton' {
                $bc = Cid-De ([string]$v.boton)
                if (-not ($botonesClic.ContainsKey($bc) -or $bc -eq 0xC4)) { throw "Boton no valido: '$($v.boton)'" }
                $a.Tipo = 'boton'; $a.Boton = $bc
            }
            'texto' {
                if (-not [string]$v.texto) { throw 'Texto a pegar vacio' }
                $a.Tipo = 'texto'; $a.Texto = [string]$v.texto
            }
            'ejecutar' {
                if (-not ([string]$v.comando).Trim()) { throw 'Comando a ejecutar vacio' }
                $a.Tipo = 'ejecutar'; $a.Comando = ([string]$v.comando).Trim()
            }
            'menu' { $a.Tipo = 'menu'; $a.Opciones = Convertir-Opciones $v.opciones $errores $donde; $f = Formato-Lista $v; $a.ListaRejilla = $f[0]; $a.ListaColumnas = $f[1]; $a.ListaIcono = $f[2]; $a.ListaVer = $f[3] }
            'ventanas' { $a.Tipo = 'ventanas'; $f = Formato-Lista $v; $a.ListaRejilla = $f[0]; $a.ListaColumnas = $f[1]; $a.ListaIcono = $f[2]; $a.ListaVer = $f[3] }
            'transformar' {
                # Transforma el texto seleccionado; 'elegir' muestra la lista de transformaciones
                $clave = ([string]$v.transformacion).Trim()
                if ($clave -ne 'elegir' -and -not ($clave -and [LogiRaton.Transformar]::Nombre($clave))) {
                    throw "Transformacion no valida: '$($v.transformacion)'"
                }
                $a.Tipo = 'transformar'; $a.Transformacion = $clave
            }
            'ocr' { $a.Tipo = 'ocr' }
            'recolocar' {
                # Recolocar la ventana activa en una cuadricula de la pantalla (filas x columnas, hueco en px)
                $a.Tipo = 'recolocar'
                $n = 0
                if ([int]::TryParse([string]$v.filas, [ref]$n) -and $n -gt 0) { $a.RejFilas = [Math]::Min(8, $n) }
                if ([int]::TryParse([string]$v.columnas, [ref]$n) -and $n -gt 0) { $a.RejColumnas = [Math]::Min(8, $n) }
                if ([int]::TryParse([string]$v.separacion, [ref]$n) -and $n -ge 0) { $a.RejSeparacion = [Math]::Min(40, $n) }
                if ($v.gestoRapido -eq $false) { $a.GestoRapido = $false }   # sin gesto rapido: pulsar siempre abre el panel
            }
            'captura' {
                # Captura de pantalla: texto (OCR), imagen (recorte) o pantalla (Imprimir pantalla)
                $modo = ([string]$v.modo).Trim().ToLower()
                if (-not $modo) { $modo = 'texto' }
                if ($modo -notin 'texto', 'imagen', 'pantalla', 'color', 'colorRgb') { throw "Modo de captura no valido: '$($v.modo)'" }
                $a.Tipo = 'captura'; $a.Modo = $modo
            }
            'claude' {
                $u = ([string]$v.url).Trim()
                if (-not $u) { $u = 'https://claude.ai/new' }
                if ($u -notmatch '^https?://') { throw "Direccion no valida: '$u'" }
                $a.Tipo = 'claude'; $a.Url = $u; $a.ConSeleccion = [bool]$v.seleccion; $a.Plantilla = [string]$v.plantilla
                $ll = [string]$v.llevar
                if ($ll -and $ll -notin 'seleccion', 'recorte', 'recorteTexto', 'elegir') { throw "Valor de 'llevar' no valido: '$ll'" }
                $a.Llevar = if ($ll) { $ll } elseif ($v.seleccion) { 'seleccion' } else { '' }
                if ($v.ancho) { $a.Ancho = [Math]::Max(280, [Math]::Min(1600, [int]$v.ancho)) }
                if ($v.alto) { $a.Alto = [Math]::Max(300, [Math]::Min(1400, [int]$v.alto)) }
            }
            'dpi' {
                # Velocidad del puntero: fijar un valor, ir pasando por varios (ciclo) o precision mientras se mantiene
                $modo = ([string]$v.modo).Trim().ToLower()
                if (-not $modo) { $modo = 'fijar' }
                if ($modo -notin 'fijar', 'ciclo', 'precision') { throw "Modo de velocidad no valido: '$($v.modo)'" }
                $a.Tipo = 'dpi'; $a.Modo = $modo
                if ($modo -eq 'ciclo') {
                    $vals = @(@($v.valores) | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_ } | Where-Object { $_ -ge 100 -and $_ -le 25600 })
                    if ($vals.Count -lt 2) { throw 'El ciclo de velocidades necesita al menos dos valores (100-25600)' }
                    $a.Valores = [int[]]$vals
                } else {
                    $n = 0
                    if ([int]::TryParse([string]$v.dpi, [ref]$n) -and $n -ge 100 -and $n -le 25600) { $a.Valor = $n }
                    elseif ($modo -eq 'precision') { $a.Valor = 400 }
                    else { throw "Velocidad no valida: '$($v.dpi)' (100-25600)" }
                }
            }
            'equipo' {
                # Easy-Switch: pasar teclado y/o raton a otro canal (equipo)
                $c = ([string]$v.canal).Trim().ToLower()
                $a.Tipo = 'equipo'
                if (-not $c -or $c -eq 'siguiente') { $a.Canal = 0 }
                elseif ($c -in '1', '2', '3') { $a.Canal = [int]$c }
                else { throw "Equipo no valido: '$($v.canal)'" }
                $q = ([string]$v.dispositivos).Trim().ToLower()
                if (-not $q) { $q = 'ambos' }
                if ($q -notin 'ambos', 'raton', 'teclado') { throw "Dispositivos no validos: '$($v.dispositivos)'" }
                $a.Dispositivos = $q
            }
            'macro' {
                # Pasos en orden: atajo, texto, ejecutar o boton (clic), y pausas { accion: pausa, ms }
                $a.Tipo = 'macro'
                $a.Pasos = New-Object 'System.Collections.Generic.List[LogiRaton.Accion]'
                $i = 0
                foreach ($p in @($v.pasos)) {
                    $i++
                    if ($null -eq $p) { continue }
                    if ([string]$p.accion -eq 'pausa') {
                        $ms = 0
                        if (-not ([int]::TryParse([string]$p.ms, [ref]$ms) -and $ms -ge 10 -and $ms -le 10000)) { throw "Paso ${i}: pausa no valida '$($p.ms)' (10-10000 ms)" }
                        $pa = New-Object LogiRaton.Accion; $pa.Tipo = 'pausa'; $pa.Valor = $ms; $a.Pasos.Add($pa); continue
                    }
                    if ([string]$p.accion -notin 'atajo', 'texto', 'ejecutar', 'boton') { throw "Paso ${i}: accion no admitida en una macro '$($p.accion)'" }
                    $pa = Accion-Desde $p $errores "$donde, paso $i"
                    if ($pa) { $a.Pasos.Add($pa) }
                }
                if ($a.Pasos.Count -eq 0) { throw 'La macro no tiene pasos' }
            }
            'volumenApp' {
                # Volumen solo de la aplicacion activa: subir / bajar (paso en %) o silenciar
                $modo = ([string]$v.modo).Trim().ToLower()
                if (-not $modo) { $modo = 'subir' }
                if ($modo -notin 'subir', 'bajar', 'silenciar') { throw "Modo de volumen no valido: '$($v.modo)'" }
                $a.Tipo = 'volumenApp'; $a.Modo = $modo
                $n = 0
                $a.Valor = if ([int]::TryParse([string]$v.paso, [ref]$n) -and $n -ge 1 -and $n -le 50) { $n } else { 10 }
            }
            'color' {
                # Color del pixel bajo el cursor, al portapapeles: hex (#1E90FF) o rgb (rgb(30, 144, 255))
                $fmt = ([string]$v.formato).Trim().ToLower()
                if (-not $fmt) { $fmt = 'hex' }
                if ($fmt -notin 'hex', 'rgb') { throw "Formato de color no valido: '$($v.formato)'" }
                $a.Tipo = 'color'; $a.Modo = $fmt
            }
            'paleta' {
                # Lista de opciones propias mas, si se piden, las ventanas abiertas, las transformaciones y el OCR
                $a.Tipo = 'paleta'
                $a.Opciones = Convertir-Opciones $v.opciones $errores $donde
                $a.IncluirVentanas = [bool]$v.ventanas
                $a.IncluirTransformaciones = [bool]$v.transformaciones
                $a.IncluirOcr = [bool]$v.ocr
                if ($a.Opciones.Count -eq 0 -and -not ($a.IncluirVentanas -or $a.IncluirTransformaciones -or $a.IncluirOcr)) {
                    throw 'La paleta no tiene ninguna entrada'
                }
            }
            default { return $null }
        }
    } catch { [void]$errores.Add(("{0}: {1}" -f $donde, (Mensaje-Error $_))); return $null }
    return ,$a
}

# Direcciones de giro de la rueda admitidas en una combinacion (clave -> codigo)
$dirRueda = @{ arriba = 0; abajo = 1; izquierda = 2; derecha = 3 }

# Combinaciones de un perfil. Tres tipos de disparador:
#   { teclas: "Ctrl+Alt+K", accion } ............ tecla (atajo global de Windows)
#   { teclas: "Ctrl", raton: "atras", accion } .. modificador(es) + boton del raton
#   { teclas: "Ctrl", rueda: "arriba", accion } . modificador(es) + giro de la rueda
# "defecto" = dejar pasar la combinacion a la aplicacion (solo teclas); sin accion = se ignora.
function Convertir-Combinaciones($lista, $perfil, $errores, [string]$ambito) {
    $n = 0
    foreach ($c in @($lista)) {
        $n++
        if ($null -eq $c) { continue }
        $disp = if ([string]$c.raton) { "boton $($c.raton)" } elseif ([string]$c.rueda) { "rueda $($c.rueda)" } else { [string]$c.teclas }
        $donde = "{0}, combinacion {1} ({2})" -f $ambito, $n, $disp
        try {
            $teclas = [LogiRaton.Keys]::ParseCombo([string]$c.teclas)
            if ([string]$c.raton) {
                # Modificador(es) + boton del raton
                $mask = [LogiRaton.Combinaciones]::SoloModificadores($teclas)
                $cid = Cid-De ([string]$c.raton)
                $clave = ($mask -shl 16) -bor $cid
                if ($perfil.CombosBoton.ContainsKey($clave)) { throw 'Combinacion de boton repetida en este perfil' }
                $a = Accion-Desde $c $errores $donde
                if ($a) { $perfil.CombosBoton[$clave] = $a }
            } elseif ([string]$c.rueda) {
                # Modificador(es) + giro de la rueda
                $mask = [LogiRaton.Combinaciones]::SoloModificadores($teclas)
                $dirNom = ([string]$c.rueda).Trim().ToLower()
                if (-not $dirRueda.ContainsKey($dirNom)) { throw "Direccion de rueda no valida: '$($c.rueda)'" }
                $clave = ($mask -shl 4) -bor [int]$dirRueda[$dirNom]
                if ($perfil.CombosRueda.ContainsKey($clave)) { throw 'Combinacion de rueda repetida en este perfil' }
                $a = Accion-Desde $c $errores $donde
                if ($a) { $perfil.CombosRueda[$clave] = $a }
            } else {
                # Tecla (atajo global)
                # Una tecla sola que escribe solo vale si su accion es otra tecla (se reasigna)
                $clave = [LogiRaton.Combinaciones]::Clave($teclas, ([string]$c.accion -eq 'atajo'))
                if ($perfil.Combos.ContainsKey($clave)) { throw 'Combinacion repetida en este perfil' }
                if ($c.accion -eq 'defecto') { $a = New-Object LogiRaton.Accion; $a.Tipo = 'pasar' }
                else { $a = Accion-Desde $c $errores $donde }
                if ($a) { $perfil.Combos[$clave] = $a }
            }
        } catch { [void]$errores.Add(("{0}: {1}" -f $donde, (Mensaje-Error $_))) }
    }
}

# Traduce un apartado "botones" a un perfil del gestor; los errores se anaden a $errores.
function Convertir-Botones($botones, $errores, [string]$ambito) {
    $perfil = New-Object LogiRaton.Perfil
    if (-not $botones) { return ,$perfil }
    foreach ($p in $botones.PSObject.Properties) {
        try {
            $cid = Cid-De $p.Name
            $b = $p.Value
            if ($b.accion -eq 'atajo') { $perfil.Atajos[$cid] = [LogiRaton.Keys]::ParseCombo([string]$b.atajo) }
            elseif ($b.accion -eq 'boton') { $perfil.Botones[$cid] = Cid-De ([string]$b.boton) }
            elseif ($b.accion -eq 'defecto') { [void]$perfil.Defecto.Add($cid) }
            elseif ($b.accion -eq 'ventanas') { [void]$perfil.Programas.Add($cid); Poner-FormatoLista $perfil $cid $b }
            elseif ($b.accion -eq 'texto') {
                if (-not [string]$b.texto) { throw 'Texto a pegar vacio' }
                $perfil.Textos[$cid] = [string]$b.texto
            }
            elseif ($b.accion -eq 'ejecutar') {
                if (-not ([string]$b.comando).Trim()) { throw 'Comando a ejecutar vacio' }
                $perfil.Comandos[$cid] = ([string]$b.comando).Trim()
            }
            elseif ($b.accion -eq 'menu') {
                $perfil.Menus[$cid] = Convertir-Opciones $b.opciones $errores ("{0}, boton {1}" -f $ambito, $p.Name)
                Poner-FormatoLista $perfil $cid $b
            }
            elseif ($b.accion -in 'transformar', 'ocr', 'captura', 'paleta', 'claude', 'recolocar', 'dpi', 'equipo', 'macro', 'volumenApp', 'color') {
                $acc = Accion-Desde $b $errores ("{0}, boton {1}" -f $ambito, $p.Name)
                if ($acc) { $perfil.Otras[$cid] = $acc }
            }
            elseif ($b.accion -eq 'gestos') {
                # Una accion por direccion: clic (sin mover), arriba, abajo, izquierda, derecha
                $g = New-Object 'System.Collections.Generic.Dictionary[string,LogiRaton.Accion]'
                foreach ($d in 'clic', 'arriba', 'abajo', 'izquierda', 'derecha') {
                    $acc = Accion-Desde $b.gestos.$d $errores ("{0}, boton {1}, gesto {2}" -f $ambito, $p.Name, $d)
                    if ($acc) { $acc.Etiqueta = Etiqueta-Accion $b.gestos.$d; $g[$d] = $acc }
                }
                $perfil.Gestos[$cid] = $g
                # Rueda de gestos junto al cursor al mantener el boton: { rueda: true, ruedaEspera: ms (0-1500, 300 por defecto) }
                if ($b.rueda) {
                    $esp = 300; $n = 0
                    if ([int]::TryParse([string]$b.ruedaEspera, [ref]$n)) { $esp = [Math]::Max(0, [Math]::Min(1500, $n)) }
                    $perfil.RuedaGestos[$cid] = $esp
                }
            }
        } catch { [void]$errores.Add(("{0}, boton {1}: {2}" -f $ambito, $p.Name, (Mensaje-Error $_))) }
        # Pulsaciones extra del boton: [ { tipo: "doble" | "larga", accion: ..., ... } ]
        if ($p.Value.pulsaciones) {
            try {
                $extra = New-Object 'System.Collections.Generic.Dictionary[string,LogiRaton.Accion]'
                foreach ($pl in @($p.Value.pulsaciones)) {
                    if ($null -eq $pl) { continue }
                    $t = [string]$pl.tipo
                    $donde = "{0}, boton {1}, pulsacion {2}" -f $ambito, $p.Name, $t
                    if ($t -ne 'doble' -and $t -ne 'larga') { [void]$errores.Add("${donde}: tipo de pulsacion no valido"); continue }
                    if ($extra.ContainsKey($t)) { [void]$errores.Add("${donde}: repetida"); continue }
                    $acc = Accion-Desde $pl $errores $donde
                    if ($acc) { $extra[$t] = $acc }
                }
                if ($extra.Count -gt 0) { $perfil.Pulsaciones[(Cid-De $p.Name)] = $extra }
            } catch { [void]$errores.Add(("{0}, boton {1}: {2}" -f $ambito, $p.Name, (Mensaje-Error $_))) }
        }
    }
    return ,$perfil
}

# Traduce la configuracion de un dispositivo (raton o teclado) a las estructuras de su gestor.
function Convertir-Dispositivo($cfg, [string]$filtroDefecto, $errores, [string]$etiqueta) {
    $res = @{
        general = $null
        apps = New-Object 'System.Collections.Generic.Dictionary[string,LogiRaton.Perfil]'
        wheel = New-Object 'System.Collections.Generic.Dictionary[string,int]'
        lineas = 0; chars = 0; filtro = $filtroDefecto; avisoBateria = $true
    }
    if ($null -eq $cfg) { $res.general = New-Object LogiRaton.Perfil; return $res }
    if ($cfg.dispositivo) { $res.filtro = [string]$cfg.dispositivo }
    $res.general = Convertir-Botones $cfg.botones $errores "$etiqueta General"
    Convertir-Combinaciones $cfg.combinaciones $res.general $errores "$etiqueta General"
    # Perfiles por aplicacion: clave = nombre del ejecutable (chrome.exe)
    if ($cfg.aplicaciones) {
        foreach ($a in $cfg.aplicaciones.PSObject.Properties) {
            $exe = $a.Name.Trim().ToLower()
            if (-not $exe.EndsWith('.exe')) { $exe += '.exe' }
            $res.apps[$exe] = Convertir-Botones $a.Value.botones $errores "$etiqueta $exe"
            Convertir-Combinaciones $a.Value.combinaciones $res.apps[$exe] $errores "$etiqueta $exe"
        }
    }
    $r = $cfg.rueda
    if ($r) {
        if ($r.modo -eq 'tope') { $res.wheel['modo'] = 2 } elseif ($r.modo -eq 'libre') { $res.wheel['modo'] = 1 }
        if ($null -ne $r.smartshift) {
            if ($r.smartshift) {
                $u = if ($r.umbral) { [int]$r.umbral } else { 10 }
                $res.wheel['umbral'] = [Math]::Max(1, [Math]::Min(50, $u))
            } else { $res.wheel['umbral'] = 255 }
        }
        if ($null -ne $r.invertir)        { $res.wheel['invertir']        = [int][bool]$r.invertir }
        if ($null -ne $r.altaResolucion)  { $res.wheel['hires']           = [int][bool]$r.altaResolucion }
        if ($null -ne $r.invertirLateral) { $res.wheel['invertirLateral'] = [int][bool]$r.invertirLateral }
        if ($r.lineas)            { $res.lineas = [int]$r.lineas }
        if ($r.caracteresLateral) { $res.chars  = [int]$r.caracteresLateral }
    }
    # Gestos: distancia minima (cuentas del sensor) para que el movimiento cuente como gesto
    $res.distanciaGesto = 0
    if ($cfg.gestos -and $null -ne $cfg.gestos.distancia) {
        $v = 0
        if ([int]::TryParse([string]$cfg.gestos.distancia, [ref]$v)) { $res.distanciaGesto = [Math]::Max(10, [Math]::Min(300, $v)) }
        else { [void]$errores.Add("${etiqueta}: distancia de los gestos no valida '$($cfg.gestos.distancia)'") }
    }
    # Velocidad del sensor al conectar (raton): ppp; se ajusta a la mas cercana que admita
    if ($null -ne $cfg.dpi) {
        $v = 0
        if ([int]::TryParse([string]$cfg.dpi, [ref]$v) -and $v -ge 100 -and $v -le 25600) { $res.wheel['dpi'] = $v }
        else { [void]$errores.Add("${etiqueta}: velocidad del puntero no valida '$($cfg.dpi)' (100-25600)") }
    }
    # Aviso de bateria baja (activado salvo "avisoBateria": false)
    $res.avisoBateria = $cfg.avisoBateria -ne $false
    # Retroiluminacion (teclado): { activa, modo: automatico|manual, nivel (1..N), apagarManosFuera/Cerca/ConCorriente (s) }
    $l = $cfg.iluminacion
    if ($l) {
        if ($null -ne $l.activa) { $res.wheel['luz'] = [int][bool]$l.activa }
        if ($l.modo -eq 'automatico') { $res.wheel['luzModo'] = 1 } elseif ($l.modo -eq 'manual') { $res.wheel['luzModo'] = 3 }
        if ($l.nivel) { $res.wheel['luzNivel'] = [Math]::Max(0, [int]$l.nivel - 1) }
        foreach ($t in @(@('apagarManosFuera', 'luzFuera'), @('apagarManosCerca', 'luzCerca'), @('apagarConCorriente', 'luzCorriente'))) {
            $seg = $l.($t[0])
            if ($seg) { $res.wheel[$t[1]] = [Math]::Max(5, [Math]::Min(3600, [int]$seg)) }
        }
    }
    return $res
}

# Configuracion completa: { raton: {...}, teclado: {...} }. El formato anterior (un solo dispositivo
# en la raiz) se interpreta como la configuracion del raton.
function Convertir-Config($cfg) {
    $errores = New-Object System.Collections.ArrayList
    if ($null -eq $cfg) { [void]$errores.Add('Configuracion vacia') }
    $secRaton = $cfg; $secTeclado = $null
    if ($cfg -and ($cfg.PSObject.Properties['raton'] -or $cfg.PSObject.Properties['teclado'])) { $secRaton = $cfg.raton; $secTeclado = $cfg.teclado }
    # Espera antes de devolver el portapapeles tras pegar (las aplicaciones lentas lo leen tarde)
    $espera = 1000
    if ($cfg -and $cfg.portapapeles -and $null -ne $cfg.portapapeles.esperaMs) {
        $v = 0
        if ([int]::TryParse([string]$cfg.portapapeles.esperaMs, [ref]$v)) { $espera = [Math]::Max(300, [Math]::Min(5000, $v)) }
        else { [void]$errores.Add("Portapapeles: espera no valida '$($cfg.portapapeles.esperaMs)'") }
    }
    # Emparejamiento: { easySwitch (accion Cambiar de equipo, si por defecto), seguir (no por defecto),
    #   canal: { raton, teclado } = canal del otro equipo en cada dispositivo (1-3; sin poner = el siguiente) }
    $emp = @{ easySwitch = $true; seguir = $false; canalRaton = 0; canalTeclado = 0 }
    $e = if ($cfg) { $cfg.emparejamiento } else { $null }
    if ($e) {
        if ($e.easySwitch -eq $false) { $emp.easySwitch = $false }
        $emp.seguir = [bool]$e.seguir
        foreach ($k in 'raton', 'teclado') {
            $c = if ($e.canal) { [string]$e.canal.$k } else { '' }
            if (-not $c -or $c -in 'auto', 'siguiente') { continue }    # automatico (el otro canal emparejado)
            if ($c -in '1', '2', '3') { $emp[$(if ($k -eq 'raton') { 'canalRaton' } else { 'canalTeclado' })] = [int]$c }
            else { [void]$errores.Add("Emparejamiento: canal del otro equipo no valido para $k '$c'") }
        }
    }
    return @{
        emparejamiento = $emp
        raton   = Convertir-Dispositivo $secRaton 'MX Master' $errores 'Raton'
        teclado = Convertir-Dispositivo $secTeclado 'MX Keys' $errores 'Teclado'
        esperaPortapapeles = $espera
        estiloListas = Convertir-EstiloListas $(if ($cfg) { $cfg.listas } else { $null })
        errores = $errores
    }
}

# Estetica de las listas: { tema, acento, opacidad, radio, detalle, iconos, letra, espaciado, difuminado, disposicion (lista,
#   rejilla o circular), columnas, r* de las rejillas, c* y tamIconoCircular de las rejillas circulares };
# lo que falte, por defecto
function Convertir-EstiloListas($l) {
    if ($null -eq $l) { return New-Object LogiRaton.Estilo }
    $num = { param($v, $def) $n = 0.0; if ($null -ne $v -and [double]::TryParse([string]$v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n)) { $n } else { $def } }
    return [LogiRaton.EstiloListas]::Crear([string]$l.tema, [string]$l.acento, [int](& $num $l.opacidad 100), [int](& $num $l.radio 0),
        $(if ($null -ne $l.detalle) { [bool]$l.detalle } else { $true }), $(if ($null -ne $l.iconos) { [bool]$l.iconos } else { $true }), $(if ($null -ne $l.etiqueta) { [bool]$l.etiqueta } else { $true }),
        [double](& $num $l.letra 10), [string]$l.espaciado, [string]$l.difuminado, [string]$l.disposicion, [int](& $num $l.columnas 0), [int](& $num $l.tamIcono 32),
        $(if ($null -ne $l.rIconos) { [bool]$l.rIconos } else { $true }), $(if ($null -ne $l.rEtiqueta) { [bool]$l.rEtiqueta } else { $true }),
        $(if ($null -ne $l.rDetalle) { [bool]$l.rDetalle } else { $false }), [int](& $num $l.tamIconoLista 20),
        $(if ($null -ne $l.cIconos) { [bool]$l.cIconos } else { $true }), $(if ($null -ne $l.cEtiqueta) { [bool]$l.cEtiqueta } else { $true }),
        $(if ($null -ne $l.cDetalle) { [bool]$l.cDetalle } else { $false }), [int](& $num $l.tamIconoCircular 28))
}

# Modulo de OCR (ocr.cs): solo se compila y carga si la configuracion recorta la pantalla (OCR o imagen),
# directamente o en una paleta/chat. El modo de captura "pantalla" (Imprimir pantalla) no lo necesita.
$ocrCargado = $false
function Cargar-OcrSiHaceFalta([string]$txt) {
    if ($script:ocrCargado) { return }
    if ($txt -notmatch '"(accion|tipo)"\s*:\s*"ocr"' -and $txt -notmatch '"ocr"\s*:\s*true' -and
        $txt -notmatch '"modo"\s*:\s*"(texto|imagen)"' -and $txt -notmatch '"llevar"\s*:\s*"(recorte|elegir)') { return }
    try {
        Cargar-Modulo (Join-Path $dir 'ocr.cs') 'LogiOcr'
        $p = [LogiRaton.OcrPuente]
        [LogiOcr.Ocr]::Avisar = [Delegate]::CreateDelegate([Action[string]], $p.GetMethod('Avisar'))
        [LogiOcr.Ocr]::Registro = [Delegate]::CreateDelegate([Action[string]], $p.GetMethod('Registro'))
        [LogiOcr.Ocr]::Activar = [Delegate]::CreateDelegate([Action[IntPtr]], $p.GetMethod('Activar'))
        [LogiOcr.Ocr]::EnUI = [Delegate]::CreateDelegate([Action[System.Windows.Forms.MethodInvoker]], $p.GetMethod('EnUI'))
        [LogiRaton.OcrPuente]::Iniciar = [Delegate]::CreateDelegate([Action], [LogiOcr.Ocr].GetMethod('Iniciar'))
        [LogiRaton.OcrPuente]::Recortar = [Delegate]::CreateDelegate([Action[Action[System.Drawing.Bitmap]]], [LogiOcr.Ocr].GetMethod('Recortar'))
        [LogiRaton.OcrPuente]::Texto = [Delegate]::CreateDelegate([Func[System.Drawing.Bitmap,string]], [LogiOcr.Ocr].GetMethod('TextoDe'))
        $script:ocrCargado = $true
        Log 'Modulo de OCR cargado'
    } catch { Log ("OCR no disponible: {0}" -f (Mensaje-Error $_)) }
}

function Aplicar-Config($conv) {
    foreach ($k in 'raton', 'teclado') {
        $d = $conv[$k]
        $mgrs[$k].SetConfig($d.general, $d.apps, $d.wheel, $d.filtro)
        $mgrs[$k].AvisarBateria = [bool]$d.avisoBateria
    }
    # Combinaciones del apartado del teclado; sin combinaciones no se registra nada.
    # Las de tecla son atajos globales de Windows; las de boton/rueda las atienden el raton y un gancho.
    [LogiRaton.Combinaciones]::Configurar($conv.teclado.general, $conv.teclado.apps)
    $btnApps = New-Object 'System.Collections.Generic.Dictionary[string, System.Collections.Generic.Dictionary[int, LogiRaton.Accion]]'
    $rdaApps = New-Object 'System.Collections.Generic.Dictionary[string, System.Collections.Generic.Dictionary[int, LogiRaton.Accion]]'
    foreach ($kv in $conv.teclado.apps.GetEnumerator()) { $btnApps[$kv.Key] = $kv.Value.CombosBoton; $rdaApps[$kv.Key] = $kv.Value.CombosRueda }
    $mgrs.raton.SetCombosBoton($conv.teclado.general.CombosBoton, $btnApps)
    [LogiRaton.RuedaCombos]::Configurar($conv.teclado.general.CombosRueda, $rdaApps)
    $r = $conv.raton
    if ($r.lineas -gt 0 -or $r.chars -gt 0) { [LogiRaton.Keys]::SetScrollSpeed($r.lineas, $r.chars) }
    [LogiRaton.Engine]::UmbralGesto = $(if ($r.distanciaGesto -gt 0) { $r.distanciaGesto } else { 50 })
    [LogiRaton.Portapapeles]::EsperaMs = $conv.esperaPortapapeles
    $e = $conv.emparejamiento
    [LogiRaton.Manager]::EasySwitchActivo = $e.easySwitch
    [LogiRaton.Manager]::SeguirActivo = $e.seguir
    [LogiRaton.Manager]::CanalOtroRaton = $e.canalRaton
    [LogiRaton.Manager]::CanalOtroTeclado = $e.canalTeclado
    [LogiRaton.EstiloListas]::Configurar($conv.estiloListas)
}

function Estado-Dispositivo($m) {
    $s = $m.GetStatus()
    return [ordered]@{
        conectado = $s.Conectado; dispositivo = $s.Dispositivo; situacion = $s.Situacion; modo = $s.Modo
        botones = @($s.Botones | ForEach-Object { [ordered]@{ cid = $_.Cid; desviable = $_.Desviable; reasignable = $_.Reasignable; virtual = $_.Virtual; gestos = $_.Gestos; pos = $_.Pos; fkey = $_.Fkey; flags = $_.Flags; destinos = @($_.Destinos) } })
        rueda = @($s.Rueda)
        appActual = $s.AppActual; perfil = $s.Perfil; recientes = @($s.Recientes)
        bateria = $s.Bateria; cargando = $s.Cargando
        dpi = $(if ($s.Dpi) { [ordered]@{ actual = $s.Dpi[0]; minimo = $s.Dpi[1]; maximo = $s.Dpi[2]; paso = $s.Dpi[3]; fabrica = $s.Dpi[4] } } else { $null })
        equipos = $(if ($s.Equipos) { [ordered]@{ canales = $s.Equipos[0]; actual = $s.Equipos[1]; emparejados = $(if ($s.Emparejados) { @($s.Emparejados) } else { $null }) } } else { $null })
        luz = $(if ($s.Luz) { [ordered]@{ activa = [bool]$s.Luz[0]; modo = $(if ($s.Luz[1] -eq 3) { 'manual' } else { 'automatico' })
                                         nivel = $s.Luz[2] + 1; niveles = $s.Luz[3]
                                         apagarManosFuera = $s.Luz[4]; apagarManosCerca = $s.Luz[5]; apagarConCorriente = $s.Luz[6] } } else { $null })
    }
}

function Eventos-Dispositivo($m, [long]$desde) {
    return [ordered]@{
        ultimo = $m.LastEventId
        pulsados = @($m.GetPressed())
        eventos = @($m.GetEvents($desde) | ForEach-Object { [ordered]@{ id = $_.Id; hora = $_.Hora; cid = $_.Cid; pulsado = $_.Pulsado; gesto = $_.Gesto } })
    }
}

# ---- Arranque con Windows (acceso directo en la carpeta Inicio del usuario) ----
function Set-Arranque([bool]$activar) {
    if ($activar) {
        $sh = New-Object -ComObject WScript.Shell
        $lnk = $sh.CreateShortcut($lnkPath)
        $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $lnk.Arguments = "-NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Minimized -File `"$PSCommandPath`" -SinNavegador"
        $lnk.WorkingDirectory = $dir
        $lnk.WindowStyle = 7
        $ico = Join-Path $dir 'icono.ico'
        if (Test-Path $ico) { $lnk.IconLocation = "$ico,0" }
        $lnk.Save()
        Log 'Arranque con Windows activado'
    } elseif (Test-Path $lnkPath) {
        Remove-Item $lnkPath
        Log 'Arranque con Windows desactivado'
    }
}

# ---- HTTP ----
function Responder($ctx, [int]$code, [string]$body, [string]$type = 'application/json; charset=utf-8') {
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    $res = $ctx.Response
    $res.StatusCode = $code
    $res.ContentType = $type
    $res.Headers['Cache-Control'] = 'no-store'
    $res.ContentLength64 = $bytes.Length
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.OutputStream.Close()
}
# Imagen de la carpeta img (fotos del teclado y del raton) o el icono de la app; no cambia entre arranques, se deja en cache
function Responder-Imagen($ctx, [string]$nombre, [string]$tipo = 'image/png') {
    $f = Join-Path $dir $nombre
    if (-not (Test-Path $f)) { Responder $ctx 404 (Json @{ error = 'No encontrado' }); return }
    $bytes = [IO.File]::ReadAllBytes($f)
    $res = $ctx.Response
    $res.StatusCode = 200
    $res.ContentType = $tipo
    $res.Headers['Cache-Control'] = 'max-age=86400'
    $res.ContentLength64 = $bytes.Length
    $res.OutputStream.Write($bytes, 0, $bytes.Length)
    $res.OutputStream.Close()
}
function Leer-Cuerpo($req) {
    $sr = New-Object IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8)
    try { return $sr.ReadToEnd() } finally { $sr.Close() }
}
function Json($o) { return ($o | ConvertTo-Json -Depth 6 -Compress) }

# ---- Programas para elegir la aplicacion de un perfil ----
# Instalados: accesos directos del menu Inicio (de todos los usuarios y del usuario) que apuntan a un .exe, con el
# nombre del acceso directo. Se guarda unos minutos porque recorrerlos tarda alrededor de un segundo.
# Abiertos: procesos con ventana en este momento (asi salen tambien las aplicaciones de la Tienda, como Teams).
# Se descartan los lanzadores genericos (su acceso directo no es el programa: cmd, powershell, mmc...) y los
# procesos internos de Windows. Sin permisos de administrador.
$script:programasCache = $null
$script:programasFecha = [datetime]::MinValue
$lanzadores = @('cmd.exe', 'powershell.exe', 'pwsh.exe', 'mmc.exe', 'msiexec.exe', 'hh.exe', 'control.exe', 'rundll32.exe',
    'wscript.exe', 'cscript.exe', 'javaw.exe', 'launch.exe', 'setup.exe', 'install.exe', 'uninstall.exe', 'explorer.exe')
$internosWindows = @('applicationframehost.exe', 'textinputhost.exe', 'shellexperiencehost.exe', 'searchhost.exe',
    'startmenuexperiencehost.exe', 'lockapp.exe', 'rtkuwp.exe', 'systemsettings.exe')
function Programa-Valido([string]$exe) { return $exe -match '^[\w.\- ]+\.exe$' }
function Listar-Instalados {
    if ($script:programasCache -and ((Get-Date) - $script:programasFecha).TotalMinutes -lt 10) { return $script:programasCache }
    $porExe = @{}
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($carpeta in @([Environment]::GetFolderPath('CommonStartMenu'), [Environment]::GetFolderPath('StartMenu'))) {
            if (-not $carpeta -or -not (Test-Path -LiteralPath $carpeta)) { continue }
            foreach ($lnk in Get-ChildItem -LiteralPath $carpeta -Recurse -Filter '*.lnk' -File -ErrorAction SilentlyContinue) {
                $destino = $null
                try { $destino = $shell.CreateShortcut($lnk.FullName).TargetPath } catch { continue }
                if (-not $destino -or -not $destino.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $exe = [IO.Path]::GetFileName($destino).ToLowerInvariant()
                if (-not (Programa-Valido $exe) -or $lanzadores -contains $exe -or $exe -match '^unins') { continue }
                if ($lnk.Name -match 'desinstal|uninstall') { continue }
                if (-not $porExe.ContainsKey($exe)) { $porExe[$exe] = [IO.Path]::GetFileNameWithoutExtension($lnk.Name) }
            }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    $script:programasCache = $porExe
    $script:programasFecha = Get-Date
    return $porExe
}
function Listar-Programas {
    $instalados = Listar-Instalados
    $abiertos = @{}
    foreach ($p in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }) {
        $exe = ($p.ProcessName + '.exe').ToLowerInvariant()
        if (-not (Programa-Valido $exe) -or $internosWindows -contains $exe -or $abiertos.ContainsKey($exe)) { continue }
        $desc = $null
        try { $desc = $p.Description } catch { }
        $abiertos[$exe] = if ($desc) { $desc } else { $p.ProcessName }
    }
    $todos = @{}
    foreach ($k in $instalados.Keys) { $todos[$k] = $instalados[$k] }
    foreach ($k in $abiertos.Keys) { if (-not $todos.ContainsKey($k)) { $todos[$k] = $abiertos[$k] } }
    return @($todos.Keys | Sort-Object { $todos[$_] } | ForEach-Object {
        [ordered]@{ exe = $_; nombre = $todos[$_]; abierto = $abiertos.ContainsKey($_) }
    })
}

function Atender($ctx) {
    $req = $ctx.Request
    $ruta = $req.Url.AbsolutePath

    # Solo conexiones del propio equipo: http.sys escucha en todas las interfaces y la cabecera Host
    # la puede escribir cualquiera, asi que se comprueba la direccion de origen real.
    if (-not $req.IsLocal) { Responder $ctx 403 (Json @{ error = 'Solo se admiten conexiones locales' }); return }
    if ($req.Headers['Host'] -ne "localhost:$Puerto") { Responder $ctx 403 (Json @{ error = 'Host no permitido' }); return }
    # La API exige el token de esta ejecucion (va dentro de la pagina servida); en las POST,
    # ademas, el origen debe ser la propia pagina (bloquea peticiones de otras webs)
    if ($ruta.StartsWith('/api/')) {
        $origen = $req.Headers['Origin']
        if ($req.Headers['X-Raton'] -cne $token) { Responder $ctx 403 (Json @{ error = 'Token no valido'; token = $true }); return }
        if ($req.HttpMethod -eq 'POST' -and $origen -and $origen -ne "http://localhost:$Puerto") {
            Responder $ctx 403 (Json @{ error = 'Peticion no permitida' }); return
        }
    }

    switch ("$($req.HttpMethod) $ruta") {
        { $_ -eq 'GET /' -or $_ -eq 'GET /index.html' } {
            $html = [IO.File]::ReadAllText($htmlFile, [Text.Encoding]::UTF8).Replace('__TOKEN_RATON__', $token)
            Responder $ctx 200 $html 'text/html; charset=utf-8'
        }
        { $_ -cmatch '^GET /img/[a-z0-9_-]+\.png$' } { Responder-Imagen $ctx ('img\' + $ruta.Substring(5)) }
        # Icono con todos sus tamanos: el navegador elige el de la barra de tareas sin escalar (nitido)
        'GET /icono.ico' { Responder-Imagen $ctx 'icono.ico' 'image/x-icon' }
        'GET /api/estado' {
            $o = [ordered]@{
                modo = $mgrs.raton.Mode
                raton = Estado-Dispositivo $mgrs.raton
                teclado = Estado-Dispositivo $mgrs.teclado
                arranqueWindows = (Test-Path $lnkPath)
                segundoPlano = (Test-Path $bgFile)
                bandeja = [ordered]@{ raton = (Test-Path $trayFile.raton); teclado = (Test-Path $trayFile.teclado) }
                registro = @($registro)
            }
            Responder $ctx 200 (Json $o)
        }
        'GET /api/eventos' {
            # ?raton=N&teclado=M : ultimo evento ya recibido de cada dispositivo
            $script:ultimoSondeo = Get-Date
            $o = [ordered]@{}
            foreach ($k in 'raton', 'teclado') {
                $desde = 0L
                [void][long]::TryParse([string]$req.QueryString[$k], [ref]$desde)
                $o[$k] = Eventos-Dispositivo $mgrs[$k] $desde
            }
            Responder $ctx 200 (Json $o)
        }
        'GET /api/config' {
            Responder $ctx 200 (Leer-ConfigTexto)
        }
        'POST /api/config' {
            $txt = Leer-Cuerpo $req
            try { $cfg = $txt | ConvertFrom-Json } catch { Responder $ctx 400 (Json @{ errores = @('JSON no valido') }); return }
            $conv = Convertir-Config $cfg
            if ($conv.errores.Count -gt 0) { Responder $ctx 400 (Json @{ errores = @($conv.errores) }); return }
            try { Guardar-Copia $txt } catch { Log ("No se pudo guardar la copia de la configuracion: {0}" -f (Mensaje-Error $_)) }
            [IO.File]::WriteAllText($cfgFile, $txt, $utf8SinBom)
            Aplicar-Config $conv
            Cargar-OcrSiHaceFalta $txt
            Log 'Configuracion guardada y aplicada'
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'GET /api/programas' {
            Responder $ctx 200 (Json @{ programas = @(Listar-Programas) })
        }
        'GET /api/copias' {
            Responder $ctx 200 (Json @{ copias = @(Listar-Copias) })
        }
        'POST /api/copias/restaurar' {
            # Vuelve a una copia: se comprueba como al guardar, se guarda copia de la actual y se aplica
            $nombre = [string]((Leer-Cuerpo $req) | ConvertFrom-Json).nombre
            if ($nombre -notmatch '^configuracion-\d{8}-\d{6}\.json$') { Responder $ctx 400 (Json @{ errores = @('Copia no valida') }); return }
            $ruta = Join-Path $copiasDir $nombre
            if (-not (Test-Path -LiteralPath $ruta)) { Responder $ctx 404 (Json @{ errores = @('No existe esa copia') }); return }
            $txt = [IO.File]::ReadAllText($ruta, [Text.Encoding]::UTF8)
            try { $cfg = $txt | ConvertFrom-Json } catch { Responder $ctx 400 (Json @{ errores = @('La copia no es un JSON valido') }); return }
            $conv = Convertir-Config $cfg
            if ($conv.errores.Count -gt 0) { Responder $ctx 400 (Json @{ errores = @($conv.errores) }); return }
            try { Guardar-Copia $txt } catch { }
            [IO.File]::WriteAllText($cfgFile, $txt, $utf8SinBom)
            Aplicar-Config $conv
            Cargar-OcrSiHaceFalta $txt
            Log "Configuracion restaurada desde la copia $nombre"
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'POST /api/listas/probar' {
            # Abre una lista (o la paleta) de ejemplo con la estetica que se esta disenando, sin guardarla
            $b = (Leer-Cuerpo $req) | ConvertFrom-Json
            [LogiRaton.EstiloListas]::Probar((Convertir-EstiloListas $b.estilo), [bool]$b.paleta)
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'POST /api/modo' {
            $m = [string]((Leer-Cuerpo $req) | ConvertFrom-Json).modo
            if ($m -eq 'prueba' -and $mgrs.raton.Mode -ne 'prueba') { $script:modoPrevio = $mgrs.raton.Mode }
            $script:ultimoSondeo = Get-Date
            try { foreach ($k in $mgrs.Keys) { $mgrs[$k].SetMode($m) }; [LogiRaton.Combinaciones]::SetActivo($m -eq 'activo'); [LogiRaton.RuedaCombos]::SetActivo($m -eq 'activo') }
            catch { Responder $ctx 400 (Json @{ errores = @((Mensaje-Error $_)) }); return }
            Log "Modo: $m"
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'POST /api/arranque' {
            Set-Arranque ([bool]((Leer-Cuerpo $req) | ConvertFrom-Json).activar)
            Responder $ctx 200 (Json @{ ok = $true; arranqueWindows = (Test-Path $lnkPath) })
        }
        'POST /api/fondo' {
            # Envia la ventana de PowerShell a segundo plano (la oculta) o la vuelve a mostrar
            $on = [bool]((Leer-Cuerpo $req) | ConvertFrom-Json).activar
            if ($on) { [void](New-Item -ItemType File $bgFile -Force); [LogiRaton.Consola]::Ocultar(); Log 'Ventana en segundo plano' }
            else { if (Test-Path $bgFile) { Remove-Item $bgFile }; [LogiRaton.Consola]::Mostrar(); Log 'Ventana visible' }
            Responder $ctx 200 (Json @{ ok = $true; segundoPlano = (Test-Path $bgFile) })
        }
        'POST /api/capturar' {
            # La web va a capturar una tecla: se sueltan las combinaciones mientras (se reanudan solas a los 30 s)
            $si = [bool]((Leer-Cuerpo $req) | ConvertFrom-Json).si
            [LogiRaton.Combinaciones]::Suspender($si)
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'POST /api/bandeja' {
            # Muestra u oculta el icono de bandeja de un periferico (raton o teclado)
            $c = (Leer-Cuerpo $req) | ConvertFrom-Json
            $dev = [string]$c.dispositivo
            if ($dev -notin 'raton', 'teclado') { Responder $ctx 400 (Json @{ error = 'Dispositivo no valido' }); return }
            if ([bool]$c.activar) {
                [void](New-Item -ItemType File $trayFile[$dev] -Force); [LogiRaton.Bandeja]::Mostrar($dev); $script:ultimaBandeja = [datetime]::MinValue
                Log "Icono de bandeja ($dev) activado"
            } else {
                if (Test-Path $trayFile[$dev]) { Remove-Item $trayFile[$dev] }; [LogiRaton.Bandeja]::Ocultar($dev)
                Log "Icono de bandeja ($dev) desactivado"
            }
            Responder $ctx 200 (Json @{ ok = $true; bandeja = @{ raton = (Test-Path $trayFile.raton); teclado = (Test-Path $trayFile.teclado) } })
        }
        'POST /api/bandeja/probar' {
            # Pasa el icono de bandeja de un periferico por todos sus estados (si esta apagado, se ve solo durante la prueba)
            $dev = [string]((Leer-Cuerpo $req) | ConvertFrom-Json).dispositivo
            if ($dev -notin 'raton', 'teclado') { Responder $ctx 400 (Json @{ error = 'Dispositivo no valido' }); return }
            [LogiRaton.Bandeja]::Probar($dev)
            Responder $ctx 200 (Json @{ ok = $true })
        }
        'POST /api/salir' {
            Log 'Cierre solicitado desde la pagina'
            Responder $ctx 200 (Json @{ ok = $true })
            $script:salir = $true
        }
        default { Responder $ctx 404 (Json @{ error = 'No encontrado' }) }
    }
}

# ---- Arranque ----
if (Test-Path $stopFile) { Remove-Item $stopFile }
# Token aleatorio de esta ejecucion: la pagina lo recibe al cargarse y lo envia en cada llamada a la API
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$bytesToken = New-Object byte[] 24
$rng.GetBytes($bytesToken); $rng.Dispose()
$token = -join ($bytesToken | ForEach-Object { $_.ToString('x2') })
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($url)
try { $listener.Start() }
catch { Log "No se pudo abrir $url : $($_.Exception.Message)"; $mutex.ReleaseMutex(); exit 1 }

# Un gestor por dispositivo, cada uno con su conexion y su hilo
$mgrs = [ordered]@{ raton = [LogiRaton.Manager]::new(); teclado = [LogiRaton.Manager]::new() }
foreach ($k in $mgrs.Keys) { $mgrs[$k].Clave = $k }
try {
    $txtInicial = Leer-ConfigTexto
    $conv = Convertir-Config ($txtInicial | ConvertFrom-Json)
    foreach ($e in $conv.errores) { Log "Configuracion: $e" }
    Aplicar-Config $conv
    Cargar-OcrSiHaceFalta $txtInicial
} catch { Log ("Configuracion no valida: {0}" -f (Mensaje-Error $_)) }
foreach ($k in $mgrs.Keys) { $mgrs[$k].Start() }
Log "Servidor en marcha en $url"
try { [void][LogiRaton.Consola]::SinEdicionRapida() } catch {}   # un clic en la consola no debe congelar el servidor
if (Test-Path $bgFile) { try { [LogiRaton.Consola]::Ocultar() } catch {} }   # segundo plano recordado
[LogiRaton.Bandeja]::Config($url, $token)
foreach ($k in 'raton', 'teclado') { if (Test-Path $trayFile[$k]) { try { [LogiRaton.Bandeja]::Mostrar($k) } catch {} } }   # iconos recordados
if (-not $SinNavegador) { Abrir-Pagina }

$salir = $false
$modoPrevio = 'activo'
$ultimoSondeo = Get-Date
$ultimaParada = Get-Date
$ultimaBandeja = [datetime]::MinValue
$tarea = $listener.GetContextAsync()

# Actualiza el icono de bandeja de cada periferico que este activo, con su bateria
function Actualizar-Bandeja {
    foreach ($k in 'raton', 'teclado') {
        if (-not (Test-Path $trayFile[$k])) { continue }
        $s = $mgrs[$k].GetStatus()
        $etiq = if ($k -eq 'raton') { 'Raton' } else { 'Teclado' }
        $nom = if ($s.Dispositivo) { $s.Dispositivo } else { $etiq }
        if ($s.Conectado -and $s.Bateria -ge 0) {
            $texto = '{0}: {1}%{2}' -f $nom, [int]$s.Bateria, $(if ($s.Cargando) { ' (cargando)' } else { '' })
            [LogiRaton.Bandeja]::Actualizar($k, $texto, [int]$s.Bateria, [bool]$s.Cargando)
        } elseif ($s.Conectado) {
            [LogiRaton.Bandeja]::Actualizar($k, ('{0}: conectado (bateria desconocida)' -f $nom), -1, $false)
        } else {
            [LogiRaton.Bandeja]::Actualizar($k, ('{0}: desconectado' -f $etiq), -1, $false)
        }
    }
}
try {
    while (-not $salir) {
        if ($tarea.Wait(200)) {
            $ctx = $tarea.Result
            $tarea = $listener.GetContextAsync()
            try { Atender $ctx }
            catch {
                Log ("Error atendiendo {0}: {1}" -f $ctx.Request.Url.AbsolutePath, (Mensaje-Error $_))
                try { Responder $ctx 500 (Json @{ errores = @((Mensaje-Error $_)) }) } catch { }
            }
        }
        Drenar
        # La prueba termina sola si la pagina deja de consultar (cerrada o sin conexion)
        if ($mgrs.raton.Mode -eq 'prueba' -and ((Get-Date) - $ultimoSondeo).TotalSeconds -gt 15) {
            foreach ($k in $mgrs.Keys) { $mgrs[$k].SetMode($modoPrevio) }
            [LogiRaton.Combinaciones]::SetActivo($modoPrevio -eq 'activo'); [LogiRaton.RuedaCombos]::SetActivo($modoPrevio -eq 'activo')
            Log "Fin de la prueba (la pagina no responde); vuelta al modo '$modoPrevio'"
        }
        # parar.flag se mira una vez por segundo (no en cada vuelta del bucle)
        if (((Get-Date) - $ultimaParada).TotalSeconds -ge 1) {
            $ultimaParada = Get-Date
            if (Test-Path $stopFile) { Log 'Parada solicitada (parar.flag)'; $salir = $true }
        }
        # Iconos de bandeja: se refrescan cada 3 s si hay alguno activo
        if (((Get-Date) - $ultimaBandeja).TotalSeconds -ge 3 -and ((Test-Path $trayFile.raton) -or (Test-Path $trayFile.teclado))) {
            $ultimaBandeja = Get-Date
            try { Actualizar-Bandeja } catch { }
        }
    }
} finally {
    foreach ($k in $mgrs.Keys) { $mgrs[$k].Stop() }
    foreach ($k in 'raton', 'teclado') { try { [LogiRaton.Bandeja]::Ocultar($k) } catch { } }
    try { [LogiRaton.Consola]::Mostrar() } catch { }   # al cerrar, la consola vuelve a verse
    Drenar
    try { $listener.Stop() } catch { }
    if (Test-Path $stopFile) { Remove-Item $stopFile }
    Log 'Servidor detenido'
    $mutex.ReleaseMutex()
}
