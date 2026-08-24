// Librerias a usar
using SFD_COMPILER.Sfd;
using Microsoft.AspNetCore.Builder;

// Inicializamos el constructor de la aplicacion web utilizando los argumentos del sistema
var constructorAplicacion = WebApplication.CreateBuilder(args);

// Evitamos que .NET intente usar FileSystemWatcher en el contenedor (soluciona el error de inotify en Render)
constructorAplicacion.Configuration.Sources.Clear();

// Añadimos el servicio de politicas CORS para permitir el trafico de red cruzado
constructorAplicacion.Services.AddCors(opcionesCors => {

    // Configuramos la politica por defecto para aceptar cualquier origen, cabecera y metodo
    opcionesCors.AddDefaultPolicy(politica => {
        politica.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// Construimos la aplicacion basandonos en la configuracion previa
var aplicacionServidor = constructorAplicacion.Build();

// Activamos el uso de CORS en la aplicacion web
aplicacionServidor.UseCors();

// Leemos la variable de entorno que permite apuntar a otra carpeta de DLL, como la instalacion del juego
var directorioDll = Environment.GetEnvironmentVariable("SFD_DLL_DIR");

// Si no se ha configurado nada, usamos la carpeta donde vive el propio ejecutable
if (string.IsNullOrWhiteSpace(directorioDll)) directorioDll = AppContext.BaseDirectory;

// Arrancamos el motor real del juego: el es quien decide que esta permitido y que no
var motorSfd = new MotorSfd(directorioDll);

// Volcamos por consola el estado del arranque para poder diagnosticar despliegues a simple vista
Console.WriteLine("========== COMPILADOR SFD ==========");
// Mostramos en que carpeta se han buscado los ficheros del juego
Console.WriteLine($"[DLL] Carpeta de DLL del juego : {motorSfd.DirectorioDll}");

// Mostramos la huella del motor de scripts, o un aviso claro si no aparece
Console.WriteLine($"[DLL] Motor de scripts         : {(File.Exists(motorSfd.RutaMotor) ? motorSfd.HuellaMotor : "NO ENCONTRADO")}");

// Mostramos lo mismo para la libreria de API del juego
Console.WriteLine($"[DLL] API del juego            : {(File.Exists(motorSfd.RutaApi) ? motorSfd.HuellaApi : "NO ENCONTRADA")}");

if (motorSfd.Disponible)
{
    // Con el motor cargado ya sabemos exactamente que reglas aplica esta version del juego
    Console.WriteLine($"[OK ] Motor cargado (compilado para {motorSfd.FrameworkMotor})");

    // Resumimos el tamano de las tres listas que gobiernan el sandbox
    Console.WriteLine($"[OK ] Sandbox: {motorSfd.NamespacesBaneados.Count} namespaces prohibidos, "
                    + $"{motorSfd.TiposBaneados.Count} tipos prohibidos, {motorSfd.TiposPermitidos.Count} tipos en lista blanca");

    // Indicamos cuantos tipos quedan vetados por namespace y como se ha calculado esa cifra
    Console.WriteLine($"[OK ] Tipos vetados por namespace: {motorSfd.TotalTiposBaneadosPorNamespace} ({motorSfd.OrigenListaPorNamespace})");

    // Decimos si la plantilla sale del DLL del juego o del respaldo interno
    Console.WriteLine($"[OK ] Plantilla del script: {motorSfd.OrigenCabecera}");

    // Y sobre todo de donde salen las referencias, que es lo que fija que APIs de .NET ve el script.
    // Si no hemos encontrado la carpeta de la version correcta lo marcamos como aviso, no como exito:
    // en ese caso el compilador aceptaria APIs mas nuevas de las que el juego tiene.
    var marcaReferencias = motorSfd.OrigenReferencias.StartsWith("AVISO") ? "[!! ]" : "[OK ]";
    Console.WriteLine($"{marcaReferencias} Referencias de compilacion: {motorSfd.Referencias.Count} ({motorSfd.OrigenReferencias})");

    // Si falta esa carpeta, decimos exactamente que comando la deja lista
    if (motorSfd.OrigenReferencias.StartsWith("AVISO"))
    {
        Console.WriteLine($"[!! ] Ejecuta: ./tools/actualizar-sfd.sh \"<carpeta del juego>\"  para descargar {motorSfd.MonikerFramework}");
    }
}
else
{
    // Sin motor la API sigue en pie, pero avisa en cada peticion de que no puede validar nada
    Console.WriteLine($"[ERR] {motorSfd.MotivoNoDisponible}");
}
Console.WriteLine("====================================");

// Mapeamos la ruta raiz para que responda con el estado operativo completo del compilador
aplicacionServidor.MapGet("/", () => Results.Json(new
{
    estado = motorSfd.Disponible ? "operativo" : "sin motor",
    motivo = motorSfd.Disponible ? null : motorSfd.MotivoNoDisponible,
    motor = new
    {
        framework = motorSfd.FrameworkMotor,
        huellaMotor = motorSfd.HuellaMotor,
        huellaApi = motorSfd.HuellaApi,
        carpeta = motorSfd.DirectorioDll,
        plantilla = motorSfd.OrigenCabecera,
        referencias = motorSfd.OrigenReferencias,
        ensambladoJuego = string.IsNullOrEmpty(motorSfd.RutaJuego) ? null : Path.GetFileName(motorSfd.RutaJuego)
    },
    sandbox = new
    {
        namespacesProhibidos = motorSfd.NamespacesBaneados.Count,
        tiposProhibidos = motorSfd.TiposBaneados.Count,
        tiposPermitidos = motorSfd.TiposPermitidos.Count,
        tiposVetadosPorNamespace = motorSfd.TotalTiposBaneadosPorNamespace,
        origen = motorSfd.OrigenListaPorNamespace
    }
}));

// Mapeamos una ruta que expone las reglas completas leidas del juego, util para editores y documentacion
aplicacionServidor.MapGet("/sandbox", () => Results.Json(new
{
    disponible = motorSfd.Disponible,
    namespacesProhibidos = motorSfd.NamespacesBaneados.OrderBy(x => x, StringComparer.Ordinal),
    tiposProhibidos = motorSfd.TiposBaneados.OrderBy(x => x, StringComparer.Ordinal),
    tiposPermitidos = motorSfd.TiposPermitidos.OrderBy(x => x, StringComparer.Ordinal),
    palabrasReservadas = motorSfd.PalabrasReservadas.OrderBy(x => x, StringComparer.Ordinal),
    callbacksHeredados = motorSfd.CallbacksHeredados,
    referencias = motorSfd.Referencias.Select(Path.GetFileName)
}));

// Mapeamos la ruta de validacion que recibira las peticiones POST enviadas con el codigo fuente
aplicacionServidor.MapPost("/validate", (CargaUtilScript cargaUtil) =>
{
    // Verificamos si el contenido de codigo recibido esta vacio o es completamente nulo
    if (string.IsNullOrWhiteSpace(cargaUtil.Code))
    {
        // Devolvemos una respuesta de error en formato JSON indicando la falta absoluta de codigo fuente
        return Results.Json(new { success = false, message = "No se envio ningun codigo." });
    }

    // Delegamos toda la validacion en el motor del juego: sandbox de seguridad primero y compilacion despues
    var resultado = motorSfd.Validar(cargaUtil.Code);

    // Devolvemos el veredicto junto al detalle de errores y advertencias en el formato que ya consumen los clientes
    return Results.Json(new
    {
        // Bandera principal que indica si el script funcionaria dentro del juego
        success = resultado.Exito,

        // Problemas que impiden que el script se ejecute
        errors = resultado.Errores.Select(error => new
        {
            // Linea del codigo original del usuario
            line = error.Linea,

            // Columna donde empieza el fragmento senalado
            column = error.ColumnaInicio,

            // Codigo del compilador, vacio si el problema lo ha detectado el sandbox
            code = error.Codigo,

            // Explicacion del problema
            message = error.Mensaje
        }),

        // Avisos que no invalidan el script pero conviene mostrar en el editor
        warnings = resultado.Advertencias.Select(advertencia => new
        {
            // Linea del codigo original del usuario
            line = advertencia.Linea,

            // Columna donde empieza el fragmento senalado
            column = advertencia.ColumnaInicio,

            // Codigo del compilador
            code = advertencia.Codigo,

            // Explicacion del aviso
            message = advertencia.Mensaje
        })
    });
});

// Recuperamos el puerto de escucha configurado en las variables de entorno o asignamos el predeterminado
var puertoServidor = Environment.GetEnvironmentVariable("PORT") ?? "8080";

// Iniciamos la escucha activa de la aplicacion web en todas las interfaces de red disponibles
aplicacionServidor.Run($"http://0.0.0.0:{puertoServidor}");

// Declaramos la clase auxiliar que modelara y deserializara el cuerpo JSON de la peticion HTTP entrante
public class CargaUtilScript
{
    // Propiedad publica que almacenara el codigo fuente original enviado por el cliente o editor
    public string? Code { get; set; }
}
