// Librerias a usar
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SFD_COMPILER.Sfd;

// Representa un unico problema detectado en el script, ya venga del sandbox de seguridad o del compilador de C#
public sealed class ErrorScript
{
    // Linea del codigo del usuario donde esta el problema, ya descontada la cabecera que inyecta el juego
    public int Linea { get; set; }

    // Columna donde empieza el fragmento senalado dentro de esa linea
    public int ColumnaInicio { get; set; }

    // Columna donde termina el fragmento senalado dentro de esa linea
    public int ColumnaFin { get; set; }

    // Codigo del compilador de C# (por ejemplo CS0103). Los errores del sandbox llegan sin codigo
    public string Codigo { get; set; } = "";

    // Texto explicativo del problema, tal y como lo redacta el juego o Roslyn
    public string Mensaje { get; set; } = "";

    // Marca si es una simple advertencia, que no invalida el script, o un error de verdad
    public bool EsAdvertencia { get; set; }
}

// Resultado completo de pasar un script por el motor del juego
public sealed class ResultadoValidacion
{
    // Verdadero solo cuando el script pasa el sandbox y compila sin un solo error
    public bool Exito { get; set; }

    // Problemas que impiden que el script funcione dentro del juego
    public List<ErrorScript> Errores { get; set; } = new();

    // Avisos que no impiden nada pero conviene ensenar al usuario
    public List<ErrorScript> Advertencias { get; set; } = new();
}

// Contexto de carga aislado: mete SFD.ScriptEngine.dll (y su Roslyn) en su propia burbuja para que no choque
// con las versiones que use esta API. Todo lo que no sea del juego se delega al runtime del proceso.
internal sealed class ContextoMotorSfd : AssemblyLoadContext
{
    // Carpeta desde la que se cargaran los ensamblados propios del juego
    private readonly string _directorio;

    public ContextoMotorSfd(string directorio) : base(name: "SFD-Engine", isCollectible: false)
    {
        // Memorizamos la carpeta para poder resolver cada ensamblado que nos pidan mas adelante
        _directorio = directorio;
    }

    protected override Assembly? Load(AssemblyName nombreEnsamblado)
    {
        // Sin nombre no podemos resolver nada
        if (nombreEnsamblado.Name is null) return null;

        // Solo secuestramos los ensamblados propios del juego y su compilador Roslyn.
        // Devolver null en el resto hace que se resuelvan contra el runtime del host, que es lo que queremos:
        // cargar aqui System.Runtime o System.Private.CoreLib del juego reventaria el proceso.
        bool esDelJuego = nombreEnsamblado.Name.StartsWith("SFD.", StringComparison.Ordinal)
                       || nombreEnsamblado.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
        if (!esDelJuego) return null;

        // Si el DLL esta en la carpeta del juego lo cargamos desde ahi; si no, que decida el runtime
        var ruta = Path.Combine(_directorio, nombreEnsamblado.Name + ".dll");
        return File.Exists(ruta) ? LoadFromAssemblyPath(ruta) : null;
    }
}

// Envoltorio sobre el motor de scripts real del juego.
//
// La idea de fondo: no reimplementamos NADA de las reglas de SFD. Cargamos SFD.ScriptEngine.dll tal cual
// viene del juego y le pedimos a el mismo que valide y compile, exactamente igual que hace el editor de mapas.
// Cuando el juego se actualiza basta con sustituir los DLL: la lista negra, la blanca y la API se actualizan solas.
public sealed class MotorSfd
{
    // Nombres de fichero que buscamos dentro de la carpeta de DLL del juego
    private const string FicheroMotor = "SFD.ScriptEngine.dll";
    private const string FicheroApi = "SFD.GameScriptInterface.dll";
    private static readonly string[] FicherosJuego = { "Superfighters Deluxe.dll", "Superfighters Deluxe Server.dll" };

    // Carpeta raiz con los ensamblados de referencia, organizados por version de .NET (referencias/net8.0, ...)
    private const string CarpetaReferencias = "referencias";

    // Cabecera que el juego inyecta delante de todo script de mapa (respaldo por si no tenemos el DLL del juego)
    private const string CabeceraPorDefecto = "using System;\r\nusing System.Linq;\r\nusing System.Collections;\r\nusing System.Collections.Generic;\r\nusing System.Text;\r\nusing System.Text.RegularExpressions;\r\nusing SFDGameScriptInterface;\r\n\r\nnamespace SFDScript\r\n{\r\n    public static class SFD\r\n    {\r\n        public static IGame Game { get { return GameScript.Game; } }\r\n    }\r\n    \r\n    public class GameScript : GameScriptInterface\r\n    {\r\n        // Cancellation token used for cooperative cancellation of scripts, set by the sandbox environment.\r\n        public static System.Threading.CancellationToken __sandboxCancellationToken;\r\n\r\n        // Needs to be static for script compatability reasons.\r\n        // Static needs to live in this GameScript class to be isolated to compiled assemblies.\r\n        private static IGame __game = null;\r\n        public static IGame Game { get { return __game; } }\r\n\r\n        protected override void __onDispose() { __game = null; }\r\n\r\n        // SFDScript.GameScript\r\n        public GameScript(IGame game) : base() { __game = game; }\r\n";
    private const string PiePorDefecto = "\r\n    }\r\n}";

    // Callbacks del API antiguo que el juego reescribe antes de compilar (respaldo si no tenemos el DLL del juego)
    private static readonly string[] CallbacksHeredadosPorDefecto =
    {
        "ExplosionHitCallback", "ObjectCreatedCallback", "ObjectDamageCallback", "ObjectTerminatedCallback",
        "PlayerCreatedCallback", "PlayerDamageCallback", "PlayerDeathCallback", "PlayerKeyInputCallback",
        "PlayerMeleeActionCallback", "PlayerWeaponAddedActionCallback", "PlayerWeaponRemovedActionCallback",
        "ProjectileCreatedCallback", "ProjectileHitCallback", "UpdateCallback", "UserJoinCallback",
        "UserLeaveCallback", "UserMessageCallback"
    };

    // Delegado reflexivo hacia ScriptEngine.Ros.RosScriptCompiler.CompileScript
    private readonly MethodInfo? _metodoCompilar;

    // Numero de saltos de linea que ocupa la cabecera, para traducir las lineas del motor a lineas del usuario
    private readonly int _lineasCabecera;

    // ---- Estado publico consultable desde la API ----

    // Indica si el motor del juego llego a cargarse y por tanto si podemos validar scripts
    public bool Disponible { get; }

    // Explicacion en texto plano de por que no se pudo cargar, para mostrarla en el endpoint de estado
    public string MotivoNoDisponible { get; } = "";

    // Carpeta en la que se han buscado los DLL del juego
    public string DirectorioDll { get; }

    // Ruta completa al motor de scripts del juego
    public string RutaMotor { get; } = "";

    // Ruta completa a la libreria de API del juego
    public string RutaApi { get; } = "";

    // Ruta completa al ensamblado del juego, si es que esta disponible
    public string RutaJuego { get; } = "";

    // Huella corta del motor, para identificar la version desplegada de un vistazo
    public string HuellaMotor { get; } = "";

    // Huella corta de la libreria de API, con el mismo proposito
    public string HuellaApi { get; } = "";

    // Version de .NET contra la que esta compilado el motor del juego, tal cual la declara el DLL
    public string FrameworkMotor { get; } = "";

    // Esa misma version en formato corto (net8.0), que es como se llaman las carpetas de referencias
    public string MonikerFramework { get; } = "";

    // De donde ha salido la plantilla: del DLL del juego o del respaldo interno
    public string OrigenCabecera { get; } = "respaldo interno";

    // Rutas de los ensamblados que se pasan al compilador al validar cada script
    public List<string> Referencias { get; } = new();

    // De donde salen esas referencias, que determina que APIs de .NET ve el script
    public string OrigenReferencias { get; private set; } = "sin resolver";

    // Trozo de codigo que el juego pone delante de todo script de mapa
    public string Cabecera { get; } = CabeceraPorDefecto;

    // Trozo de codigo que el juego pone detras para cerrar la clase y el namespace
    public string Pie { get; } = PiePorDefecto;

    // Callbacks del API antiguo que hay que reescribir antes de compilar
    public string[] CallbacksHeredados { get; } = CallbacksHeredadosPorDefecto;

    // ---- Reglas del sandbox leidas del propio SFD.ScriptEngine.dll ----

    // Espacios de nombres que el juego prohibe por completo, como System.IO o System.Net
    public List<string> NamespacesBaneados { get; } = new();

    // Tipos concretos prohibidos por nombre, como File, Process o Timer
    public List<string> TiposBaneados { get; } = new();

    // Lista blanca: nombres que se salvan aunque coincidan con algo prohibido
    public List<string> TiposPermitidos { get; } = new();

    // Identificadores que el motor se reserva para si mismo y el script no puede usar
    public List<string> PalabrasReservadas { get; } = new();

    // Cuantos tipos quedan vetados por vivir dentro de un namespace prohibido
    public int TotalTiposBaneadosPorNamespace { get; private set; }

    // Explicacion de como se ha calculado esa ultima lista
    public string OrigenListaPorNamespace { get; private set; } = "no calculada";

    public MotorSfd(string directorioDll)
    {
        // Guardamos la carpeta donde el usuario ha dejado los DLL del juego
        DirectorioDll = directorioDll;

        // Componemos la ruta completa hasta el motor de scripts
        RutaMotor = Path.Combine(directorioDll, FicheroMotor);

        // Componemos la ruta completa hasta la libreria de API del juego
        RutaApi = Path.Combine(directorioDll, FicheroApi);

        // Sin el motor de scripts no hay nada que hacer: es la fuente de la lista negra y blanca
        if (!File.Exists(RutaMotor))
        {
            MotivoNoDisponible = $"No se encuentra {FicheroMotor} en '{directorioDll}'. Copialo desde la carpeta del juego.";
            return;
        }

        // Sin la API del juego los scripts no compilarian: no existiria ni IGame ni GameScriptInterface
        if (!File.Exists(RutaApi))
        {
            MotivoNoDisponible = $"No se encuentra {FicheroApi} en '{directorioDll}'. Copialo desde la carpeta del juego.";
            return;
        }

        // Calculamos la huella del motor para poder ver de un vistazo que version esta desplegada
        HuellaMotor = CalcularHuella(RutaMotor);

        // Hacemos lo mismo con la libreria de API del juego
        HuellaApi = CalcularHuella(RutaApi);

        try
        {
            // Creamos la burbuja aislada donde va a vivir el motor del juego
            var contexto = new ContextoMotorSfd(directorioDll);

            // Cargamos dentro de esa burbuja el ensamblado del motor de scripts
            var ensambladoMotor = contexto.LoadFromAssemblyPath(RutaMotor);

            // Anotamos contra que version de .NET esta compilado el motor
            FrameworkMotor = ensambladoMotor.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "desconocido";

            // La traducimos al formato corto, que es el que da nombre a la carpeta de referencias a usar
            MonikerFramework = MonikerDesdeFramework(FrameworkMotor);

            // Localizamos dentro del ensamblado la clase que compila los scripts de verdad
            var tipoCompilador = ensambladoMotor.GetType("ScriptEngine.Ros.RosScriptCompiler")
                ?? throw new Exception("El DLL no expone ScriptEngine.Ros.RosScriptCompiler");

            // Y dentro de ella el metodo concreto al que llamaremos en cada validacion
            _metodoCompilar = tipoCompilador.GetMethod("CompileScript", BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception("RosScriptCompiler no expone CompileScript");

            // Localizamos el analizador de seguridad, que es quien guarda las listas negra y blanca
            var tipoAnalizador = ensambladoMotor.GetType("ScriptEngine.Ros.ScriptSecurityAnalyzer")
                ?? throw new Exception("El DLL no expone ScriptEngine.Ros.ScriptSecurityAnalyzer");

            // Volcamos la lista de espacios de nombres prohibidos tal y como esta dentro del ensamblado
            NamespacesBaneados = LeerConjunto(tipoAnalizador, "_bannedNamespaces");

            // Volcamos la lista negra de tipos concretos
            TiposBaneados = LeerConjunto(tipoAnalizador, "_bannedTypes");

            // Volcamos la lista blanca, que es la que salva nombres que si estan permitidos
            TiposPermitidos = LeerConjunto(tipoAnalizador, "_whitelistedTypes");

            // Volcamos los identificadores que el motor se reserva para su propio uso
            PalabrasReservadas = LeerConjunto(tipoAnalizador, "_reservedKeywords");

            // Fijamos de antemano la lista de tipos derivada de los namespaces prohibidos
            PrepararTiposPorNamespace(tipoAnalizador, directorioDll);

            // Buscamos si el usuario ha dejado tambien el ensamblado del juego junto a los DLL
            var rutaJuego = FicherosJuego.Select(f => Path.Combine(directorioDll, f)).FirstOrDefault(File.Exists);

            // Si esta, podemos sacar de el la plantilla exacta en vez de tirar del respaldo interno
            if (rutaJuego != null)
            {
                // Anotamos la ruta para poder informar de ella en el endpoint de estado
                RutaJuego = rutaJuego;

                // Intentamos extraer del binario la cabecera y el cierre que el juego inyecta
                if (ExtraerPlantillaDelJuego(rutaJuego, out var cabecera, out var pie))
                {
                    // Sustituimos la cabecera de respaldo por la autentica del juego
                    Cabecera = cabecera;

                    // Hacemos lo mismo con el trozo de cierre
                    Pie = pie;

                    // Dejamos constancia de que la plantilla ya no es la de respaldo
                    OrigenCabecera = Path.GetFileName(rutaJuego);
                }

                // Extraemos tambien los nombres de callback antiguos que el juego reescribe
                var callbacks = ExtraerCallbacksDelJuego(rutaJuego);

                // Solo los adoptamos si de verdad hemos encontrado alguno
                if (callbacks.Length > 0) CallbacksHeredados = callbacks;
            }

            // Cuantas lineas ocupa la cabecera determina el desfase que hay que restar a los errores
            _lineasCabecera = Cabecera.Count(caracter => caracter == '\n');

            // Montamos el mismo juego de referencias que usa GameWorld.GetScriptTypes() en el juego
            Referencias = ResolverReferencias(directorioDll, MonikerFramework, out var origenReferencias);

            // Guardamos de donde han salido para poder mostrarlo en el arranque y en el estado
            OrigenReferencias = origenReferencias;

            // A partir de aqui el motor esta operativo
            Disponible = true;
        }
        catch (Exception excepcion)
        {
            // Cualquier fallo de carga se reporta tal cual para que se vea en el endpoint de estado
            MotivoNoDisponible = $"Error cargando {FicheroMotor}: {excepcion.GetBaseException().Message}";
        }
    }

    // Pasa el codigo del usuario por el motor del juego y traduce el resultado a nuestro modelo
    public ResultadoValidacion Validar(string codigoUsuario)
    {
        // Preparamos el objeto donde iremos acumulando el veredicto y los problemas encontrados
        var resultado = new ResultadoValidacion();

        // Si el motor no llego a cargar no podemos afirmar nada sobre el script
        if (!Disponible || _metodoCompilar is null)
        {
            resultado.Exito = false;
            resultado.Errores.Add(new ErrorScript { Linea = 1, Codigo = "SFD000", Mensaje = MotivoNoDisponible });
            return resultado;
        }

        // Reproducimos el mismo montaje que hace el juego: cabecera + script del usuario + cierre de clase
        var scriptCompleto = ConvertirLlamadasHeredadas(Cabecera + codigoUsuario + Pie);

        object? resultadoMotor;
        try
        {
            // Llamamos al compilador del juego. La ruta de salida vacia hace que compile solo en memoria.
            resultadoMotor = _metodoCompilar.Invoke(null, new object?[]
            {
                scriptCompleto,      // codigo fuente completo
                Cabecera.Length,     // longitud de la cabecera segura (en caracteres)
                "",                  // sin fichero de salida
                Referencias,         // ensamblados referenciados
                false                // sin informacion de depuracion
            });
        }
        catch (Exception excepcion)
        {
            // Si el propio motor revienta lo reportamos como un error mas en vez de tumbar la API
            resultado.Exito = false;
            resultado.Errores.Add(new ErrorScript
            {
                Linea = 1,
                Codigo = "SFD001",
                Mensaje = "El motor del juego fallo al compilar: " + excepcion.GetBaseException().Message
            });
            return resultado;
        }

        // Sin resultado no hay nada que interpretar
        if (resultadoMotor is null)
        {
            resultado.Exito = false;
            resultado.Errores.Add(new ErrorScript { Linea = 1, Codigo = "SFD002", Mensaje = "El motor del juego no devolvio resultado." });
            return resultado;
        }

        // Averiguamos de que tipo es el objeto que nos ha devuelto el motor para poder inspeccionarlo
        var tipoResultado = resultadoMotor.GetType();

        // Sacamos de el la coleccion de problemas; si viniera vacia trabajamos con una coleccion sin elementos
        var errores = tipoResultado.GetField("Errors")?.GetValue(resultadoMotor) as Array ?? Array.Empty<object>();

        // Recorremos cada problema para traducirlo a nuestro propio modelo
        foreach (var elemento in errores)
        {
            // Un hueco vacio en la coleccion no aporta nada, lo saltamos
            if (elemento is null) continue;

            // Averiguamos el tipo del elemento para poder leerle las propiedades por reflexion
            var tipoError = elemento.GetType();

            // Leemos la linea que reporta el motor, que va referida al script completo con cabecera incluida
            int lineaMotor = (int)(tipoError.GetProperty("Line")?.GetValue(elemento) ?? 0);

            // Leemos si el motor lo considera una simple advertencia o un error de verdad
            bool esAdvertencia = (bool)(tipoError.GetProperty("IsWarning")?.GetValue(elemento) ?? false);

            // Construimos nuestro objeto de error ya con la linea traducida a la del codigo del usuario
            var problema = new ErrorScript
            {
                // Restamos las lineas de la cabecera y nos aseguramos de no bajar nunca de la linea 1
                Linea = Math.Max(1, lineaMotor - _lineasCabecera + 1),

                // Columna donde empieza el fragmento problematico
                ColumnaInicio = (int)(tipoError.GetProperty("TextStart")?.GetValue(elemento) ?? 0),

                // Columna donde termina el fragmento problematico
                ColumnaFin = (int)(tipoError.GetProperty("TextEnd")?.GetValue(elemento) ?? 0),

                // Codigo del compilador de C#, que viene vacio cuando el problema lo detecta el sandbox
                Codigo = tipoError.GetProperty("ErrorNumber")?.GetValue(elemento) as string ?? "",

                // Texto del problema, limpiando el prefijo de posicion que anade Roslyn
                Mensaje = LimpiarMensaje(tipoError.GetProperty("ErrorText")?.GetValue(elemento) as string ?? ""),

                // Arrastramos la marca de advertencia tal cual la reporta el motor
                EsAdvertencia = esAdvertencia
            };

            // Separamos advertencias de errores reales: solo los segundos invalidan el script
            if (esAdvertencia) resultado.Advertencias.Add(problema);
            else resultado.Errores.Add(problema);
        }

        // El script es valido cuando el motor no ha devuelto ningun error no-advertencia
        resultado.Exito = resultado.Errores.Count == 0;

        // Devolvemos el veredicto completo al llamante
        return resultado;
    }

    // Reescribe las llamadas del API antiguo igual que hace GameWorld.ConvertLegacyCalls antes de compilar
    private string ConvertirLlamadasHeredadas(string scriptCompleto)
    {
        // Sobre un script vacio no hay nada que reescribir
        if (string.IsNullOrEmpty(scriptCompleto)) return scriptCompleto;

        // Recorremos cada uno de los nombres de callback del API antiguo
        foreach (var callback in CallbacksHeredados)
        {
            // Traducimos la forma antigua de arrancar el callback a la equivalente moderna
            scriptCompleto = scriptCompleto.Replace("Events." + callback + ".Start(", "Game.Events.Start" + callback + "(");

            // Traducimos igualmente la forma antigua de detenerlo
            scriptCompleto = scriptCompleto.Replace("Events." + callback + ".Stop(", "Game.Events.Stop(");
        }

        // Devolvemos el script ya modernizado, listo para pasar por el compilador
        return scriptCompleto;
    }

    // Roslyn antepone la posicion "(linea,columna)" al texto del diagnostico; la quitamos porque ya viajan aparte
    private static string LimpiarMensaje(string mensaje)
    {
        // Buscamos donde termina ese prefijo de posicion
        var separador = mensaje.IndexOf("): ", StringComparison.Ordinal);

        // Si el mensaje empieza por parentesis y hemos encontrado el cierre, nos quedamos con lo que va detras
        if (separador > 0 && mensaje.StartsWith("(", StringComparison.Ordinal)) return mensaje[(separador + 3)..];

        // Si no encaja con ese formato lo devolvemos tal cual
        return mensaje;
    }

    // Lee por reflexion uno de los HashSet<string> estaticos privados del analizador de seguridad
    private static List<string> LeerConjunto(Type tipoAnalizador, string nombreCampo)
    {
        // Localizamos el campo por su nombre, aunque sea privado y estatico
        var campo = tipoAnalizador.GetField(nombreCampo, BindingFlags.NonPublic | BindingFlags.Static);

        // Si existe y contiene una coleccion de textos, la copiamos a una lista propia
        if (campo?.GetValue(null) is IEnumerable<string> conjunto) return conjunto.ToList();

        // Si el campo no existe devolvemos una lista vacia en lugar de fallar
        return new List<string>();
    }

    // Deja fijada de antemano la lista de tipos vetados que el motor deduce de los namespaces prohibidos.
    //
    // Por que: el motor la calcula recorriendo los ensamblados YA CARGADOS en el proceso. Dentro del juego eso son
    // los de .NET 8 del propio juego; dentro de esta API serian los del runtime del servidor, que trae tipos internos
    // distintos (por ejemplo .NET 10 define un "Node" interno que .NET 8 no tiene, y eso tumbaria scripts que en el
    // juego funcionan). Fijandola nosotros la lista sale integra de los ficheros del juego y el resultado es estable.
    private void PrepararTiposPorNamespace(Type tipoAnalizador, string directorioDll)
    {
        // Permitimos volver al comportamiento original del motor por si alguna vez interesa comparar
        if (Environment.GetEnvironmentVariable("SFD_ESCANEO_HOST") == "1")
        {
            OrigenListaPorNamespace = "diferida al motor (escaneo del proceso host)";
            return;
        }

        // Localizamos el campo interno donde el motor guarda esa lista una vez calculada
        var campoLista = tipoAnalizador.GetField("_bannedTypesFromNamespaces", BindingFlags.NonPublic | BindingFlags.Static);

        // Y el metodo interno que devuelve la parte fija de la lista, la que viene embebida en el DLL
        var metodoListaFija = tipoAnalizador.GetMethod("GetBannedTypesFromnamespacesFixed", BindingFlags.NonPublic | BindingFlags.Static);

        // Si una futura version del juego renombra estos miembros, dejamos que el motor haga lo suyo
        if (campoLista is null || metodoListaFija is null)
        {
            OrigenListaPorNamespace = "diferida al motor (miembros internos no encontrados)";
            return;
        }

        try
        {
            // Partimos de la lista fija que el propio motor lleva embebida
            if (metodoListaFija.Invoke(null, null) is not HashSet<string> tiposVetados)
            {
                OrigenListaPorNamespace = "diferida al motor (formato inesperado)";
                return;
            }

            // La ampliamos escaneando los DLL que el usuario haya dejado junto al motor.
            // Si solo estan los dos DLL de SFD esto casi no anade nada; si esta la carpeta entera del juego,
            // se acerca a lo que el juego llega a ver en tiempo de ejecucion (modo mas estricto).
            // Con SFD_ESCANEO_VECINOS=0 nos quedamos unicamente con la lista fija que trae el motor.
            var escanearVecinos = Environment.GetEnvironmentVariable("SFD_ESCANEO_VECINOS") != "0";
            var ensambladosVecinos = escanearVecinos ? Directory.GetFiles(directorioDll, "*.dll") : Array.Empty<string>();
            var tiposVecinos = LectorMetadatos.EscanearTiposPorNamespace(ensambladosVecinos, NamespacesBaneados);
            tiposVetados.UnionWith(tiposVecinos);

            // La lista blanca siempre gana, igual que hace el motor al final de su propio calculo
            tiposVetados.ExceptWith(TiposPermitidos);

            // Dejamos el resultado ya puesto para que el motor no vuelva a calcularlo
            campoLista.SetValue(null, tiposVetados);

            // Anotamos el total para poder ensenarlo en el arranque y en el endpoint de estado
            TotalTiposBaneadosPorNamespace = tiposVetados.Count;

            // Dejamos escrito como se ha calculado, que ayuda mucho a diagnosticar diferencias
            OrigenListaPorNamespace = escanearVecinos
                ? $"lista fija del motor + {ensambladosVecinos.Length} DLL de la carpeta ({tiposVecinos.Count} tipos)"
                : "solo la lista fija del motor";
        }
        catch (Exception excepcion)
        {
            OrigenListaPorNamespace = "diferida al motor (" + excepcion.GetBaseException().Message + ")";
        }
    }

    // Saca del DLL del juego la plantilla exacta que envuelve a todo script de mapa
    private static bool ExtraerPlantillaDelJuego(string rutaJuego, out string cabecera, out string pie)
    {
        // Partimos de valores vacios por si no logramos extraer nada del binario
        cabecera = "";
        pie = "";

        // Todas las cadenas literales de SFD.GameWorld.GetFullScript, en orden de aparicion
        var cadenas = LectorMetadatos.LeerCadenasDeMetodo(rutaJuego, "SFD", "GameWorld", "GetFullScript");
        if (cadenas.Count == 0) return false;

        // La cabecera es la unica que declara la clase base de los scripts
        int indiceCabecera = cadenas.FindIndex(c => c.Contains("class GameScript : GameScriptInterface", StringComparison.Ordinal));

        // Si no aparece por ningun lado es que el juego ha cambiado la plantilla y mejor usar el respaldo
        if (indiceCabecera < 0) return false;

        // Nos quedamos con esa cadena como cabecera autentica del juego
        cabecera = cadenas[indiceCabecera];

        // El cierre es la ultima cadena posterior a la cabecera formada solo por llaves y espacios
        pie = cadenas.Skip(indiceCabecera + 1)
                     .LastOrDefault(c => c.Length > 0 && c.All(caracter => caracter is '}' or ' ' or '\r' or '\n' or '\t'))
              ?? PiePorDefecto;

        // Confirmamos al llamante que la extraccion ha ido bien
        return true;
    }

    // Saca del DLL del juego los nombres de callback que se reescriben por compatibilidad con scripts antiguos
    private static string[] ExtraerCallbacksDelJuego(string rutaJuego)
    {
        // Leemos todas las cadenas literales del metodo que hace esa reescritura
        return LectorMetadatos.LeerCadenasDeMetodo(rutaJuego, "SFD", "GameWorld", "ConvertLegacyCalls")

            // Nos quedamos solo con las que son nombres de callback
            .Where(cadena => cadena.EndsWith("Callback", StringComparison.Ordinal))

            // Descartamos repeticiones por si alguna aparece varias veces en el IL
            .Distinct(StringComparer.Ordinal)

            // Y devolvemos el conjunto ya cerrado
            .ToArray();
    }

    // Construye la lista de ensamblados referenciados equivalente a la que el juego pasa al compilar un script.
    // Los nombres salen de replicar GameWorld.GetScriptTypes().
    //
    // De donde se cogen esos ensamblados importa mucho: el juego corre sobre .NET 8, y si compilasemos contra
    // el runtime de esta API (hoy .NET 10) daríamos por buenos scripts que usan APIs que en el juego no existen.
    // Por eso se elige UNA sola fuente, nunca una mezcla, en este orden de preferencia:
    //
    //   1. La carpeta de instalacion del juego, si estan ahi sus propios ensamblados de .NET 8. Fidelidad total.
    //   2. La carpeta referencias-net8, con los ensamblados de referencia de .NET 8. Misma superficie de API.
    //   3. El runtime de esta API. Ultimo recurso: compila, pero la superficie puede no coincidir con el juego.
    private static List<string> ResolverReferencias(string directorioDll, string monikerFramework, out string origen)
    {
        // Estos son exactamente los tipos que el juego usa para decidir que ensamblados referenciar
        var tiposReferencia = new[]
        {
            typeof(object), typeof(Enumerable), typeof(EnumerableQuery), typeof(IQueryable),
            typeof(ArrayList), typeof(List<>), typeof(Dictionary<,>), typeof(SortedList),
            typeof(OrderedDictionary), typeof(SortedList<,>), typeof(ConcurrentDictionary<,>),
            typeof(StringBuilder), typeof(Regex)
        };

        // Traducimos cada tipo al ensamblado que lo define, sin repetir y añadiendo System.Runtime como hace el juego
        var nombresEnsamblado = tiposReferencia
            .Select(tipo => tipo.Assembly.GetName().Name!)
            .Append("System.Runtime")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Buscamos la carpeta de referencias correspondiente a la version de .NET que usa el juego.
        // Al ir indexadas por version (referencias/net8.0, referencias/net10.0, ...), el dia que SFD salte
        // de version basta con anadir la carpeta nueva: aqui se elige sola.
        var carpetaReferencias = monikerFramework.Length == 0 ? null : new[]
            {
                Path.Combine(directorioDll, CarpetaReferencias, monikerFramework),
                Path.Combine(AppContext.BaseDirectory, CarpetaReferencias, monikerFramework)
            }
            .FirstOrDefault(carpeta => File.Exists(Path.Combine(carpeta, "System.Runtime.dll")));

        // Decidimos la fuente unica de la que van a salir todas las referencias del framework
        string carpetaFuente;
        if (File.Exists(Path.Combine(directorioDll, "System.Private.CoreLib.dll")))
        {
            carpetaFuente = directorioDll;
            origen = "ensamblados del propio juego";
        }
        else if (carpetaReferencias != null)
        {
            carpetaFuente = carpetaReferencias;
            origen = $"ensamblados de referencia de {monikerFramework}";
        }
        else
        {
            carpetaFuente = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            origen = $"AVISO: runtime de esta API, falta la carpeta {CarpetaReferencias}/{monikerFramework}";
        }

        // Resolvemos cada nombre contra esa unica carpeta.
        // Los que no existan se omiten: en un paquete de referencia no hay System.Private.CoreLib porque
        // todos sus tipos publicos viven dentro de System.Runtime.dll, y anadir el del host mezclaria
        // dos definiciones del mismo tipo y reventaria la compilacion con errores de ambiguedad.
        var referencias = nombresEnsamblado
            .Select(nombre => Path.Combine(carpetaFuente, nombre + ".dll"))
            .Where(File.Exists)
            .ToList();

        // La API del juego es obligatoria: es la que define IGame, IPlayer y demas
        referencias.Add(Path.Combine(directorioDll, FicheroApi));

        // Si ademas tenemos el ensamblado del juego lo referenciamos, igual que hace el juego con typeof(GameWorld)
        var ensambladoJuego = FicherosJuego.Select(f => Path.Combine(directorioDll, f)).FirstOrDefault(File.Exists);
        if (ensambladoJuego != null) referencias.Add(ensambladoJuego);

        return referencias;
    }

    // Traduce el nombre largo del framework (".NETCoreApp,Version=v8.0") al corto ("net8.0"),
    // que es el que da nombre a las carpetas dentro de referencias/ y a las carpetas de los paquetes de NuGet.
    private static string MonikerDesdeFramework(string framework)
    {
        // Localizamos la marca que precede al numero de version
        var marca = framework.IndexOf("Version=v", StringComparison.OrdinalIgnoreCase);

        // Si el DLL no declara version en el formato esperado no podemos deducir nada
        if (marca < 0) return "";

        // Nos quedamos con lo que va detras de la marca, que es el numero de version
        var version = framework[(marca + "Version=v".Length)..].Trim();

        // Descartamos cualquier cosa que venga detras del numero, como el nombre del perfil
        var separador = version.IndexOf(',');
        if (separador > 0) version = version[..separador];

        // Componemos el nombre corto solo si de verdad tenemos un numero de version
        return version.Length == 0 ? "" : "net" + version;
    }

    // Huella corta del fichero para identificar de un vistazo la version desplegada
    private static string CalcularHuella(string ruta)
    {
        try
        {
            // Abrimos el fichero en solo lectura para no bloquearlo
            using var flujo = File.OpenRead(ruta);

            // Calculamos su SHA-256 y nos quedamos con los primeros doce caracteres, suficiente para distinguir versiones
            return Convert.ToHexString(SHA256.HashData(flujo))[..12].ToLowerInvariant();
        }
        catch
        {
            // Si el fichero no se puede leer devolvemos huella vacia en vez de tumbar el arranque
            return "";
        }
    }
}
