// Librerias a usar
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.CodeAnalysis.CSharp;

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

// Definimos la ruta absoluta hacia la libreria original del juego
var rutaLibreriaJuego = Path.Combine(AppContext.BaseDirectory, "SFD.GameScriptInterface.dll");

// Definimos la ruta absoluta hacia el motor de scripts del juego para leer sus restricciones
var rutaMotorScript = Path.Combine(AppContext.BaseDirectory, "SFD.ScriptEngine.dll");

// Imprimimos en consola la ruta generada para facilitar la depuracion interna
Console.WriteLine($"[DEBUG] Ruta absoluta de la DLL (del SFD): {rutaLibreriaJuego}");
Console.WriteLine($"[DEBUG] Ruta absoluta de la DLL (lista de baneados): {rutaMotorScript}");

// Comprobamos fisicamente si el archivo de la libreria existe en el disco duro
Console.WriteLine($"[DEBUG] ¿El archivo existe fisicamente?: {File.Exists(rutaLibreriaJuego)}");
Console.WriteLine($"[DEBUG] ¿El archivo existe fisicamente?: {File.Exists(rutaMotorScript)}");

// Inicializamos las variables donde guardaremos los datos extraidos dinamicamente
string versionNetDetectada = "Desconocido";

// Inicializamos las listas dinámicas de seguridad para el sandbox (con valores por defecto por seguridad)
List<string> namespacesBaneadosDinamicos = new List<string> { "System.Threading", "System.Security.Cryptography", "System.Reflection" };
List<string> tiposBaneadosDinamicos = new List<string>();
List<string> tiposBlancosDinamicos = new List<string>();

// Si el archivo del motor de scripts existe fisicamente en el disco duro
if (File.Exists(rutaMotorScript)) {

    try {
        // Cargamos el ensamblado del motor en memoria para inspeccionar sus metadatos
        var ensambladoMotor = Assembly.LoadFrom(rutaMotorScript);

        // Extraemos la version exacta de .NET utilizando el atributo del framework objetivo
        var atributoFramework = ensambladoMotor.GetCustomAttribute<TargetFrameworkAttribute>();

        // Comprobamos que no sea null
        if (atributoFramework != null) {

            // Guardamos la version del .NET
            versionNetDetectada = atributoFramework.FrameworkName;
            Console.WriteLine($"[AUTO-CONFIG] Version de .NET leida de la DLL: {versionNetDetectada}");
        }

        // Escaneamos todas las clases del motor buscando los campos internos de seguridad
        foreach (var tipo in ensambladoMotor.GetTypes()) {
            
            // Extraemos _bannedNamespaces de forma automatica
            var campoNs = tipo.GetField("_bannedNamespaces", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (campoNs?.GetValue(null) is System.Collections.IEnumerable coleccionNs) {
                foreach (var item in coleccionNs) {
                    if (item != null && !namespacesBaneadosDinamicos.Contains(item.ToString()!)) {
                        namespacesBaneadosDinamicos.Add(item.ToString()!);
                    }
                }
            }

            // Extraemos _bannedTypes de forma automatica
            var campoBt = tipo.GetField("_bannedTypes", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (campoBt?.GetValue(null) is System.Collections.IEnumerable coleccionBt) {
                foreach (var item in coleccionBt) {
                    if (item != null && !tiposBaneadosDinamicos.Contains(item.ToString()!)) {
                        tiposBaneadosDinamicos.Add(item.ToString()!);
                    }
                }
            }

            // Extraemos _whitelistedTypes de forma automatica
            var campoWt = tipo.GetField("_whitelistedTypes", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (campoWt?.GetValue(null) is System.Collections.IEnumerable coleccionWt) {
                foreach (var item in coleccionWt) {
                    if (item != null && !tiposBlancosDinamicos.Contains(item.ToString()!)) {
                        tiposBlancosDinamicos.Add(item.ToString()!);
                    }
                }
            }
        }

        // Mensaje de los resultados
        Console.WriteLine($"[AUTO-CONFIG] Sandbox sincronizado: {namespacesBaneadosDinamicos.Count} namespaces y {tiposBaneadosDinamicos.Count} tipos bloqueados leidos de la DLL.");

    } catch (Exception excepcionLecturaMotor) {

        // Mensaje de error
        Console.WriteLine($"[AUTO-CONFIG] Error al leer restricciones de la DLL: {excepcionLecturaMotor.Message}");
    }
}

// Si el archivo de la libreria existe en la ruta especificada
if (File.Exists(rutaLibreriaJuego)) {
    try {
        
        // Intentamos extraer la informacion del ensamblado de la libreria
        var informacionEnsamblado = System.Reflection.AssemblyName.GetAssemblyName(rutaLibreriaJuego);

        // Informamos por consola que la lectura ha sido exitosa mostrando su version
        Console.WriteLine($"[DEBUG] ¡DLL leida con exito! Nombre: {informacionEnsamblado.Name}, Version: {informacionEnsamblado.Version}");
        
    } catch (Exception excepcionLectura) {
        
        // Capturamos e imprimimos cualquier error ocurrido durante la lectura del ensamblado
        Console.WriteLine($"[DEBUG] Error al leer DLL: {excepcionLectura.Message}");
    }
}

// Obtenemos el directorio raiz donde residen las librerias del nucleo de .NET
string? directorioNucleoNet = Path.GetDirectoryName(typeof(object).Assembly.Location);

// Verificamos que el directorio del nucleo haya sido localizado correctamente
if (directorioNucleoNet == null) {
    
    // Lanzamos una excepcion critica si no podemos encontrar el entorno de ejecucion
    throw new Exception("Error critico: No se pudo localizar el nucleo de .NET");
}

// Preparamos un arreglo con todas las referencias de metadatos necesarias para el compilador
var referenciasCompilador = new MetadataReference[]
{
    // Añadimos las referencias basicas y estructurales del sistema
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.Dictionary<,>).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.Timers.Timer).Assembly.Location),
    MetadataReference.CreateFromFile(typeof(System.ComponentModel.Component).Assembly.Location),
    
    // Añadimos las referencias ubicadas dinamicamente en el directorio del nucleo
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Collections.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Text.RegularExpressions.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "mscorlib.dll")),
    
    // Añadimos la referencia principal a la libreria del juego Superfighters Deluxe
    MetadataReference.CreateFromFile(rutaLibreriaJuego)
};

// Mapeamos la ruta raiz para que responda con el estado incluyendo la version de .NET leida de la DLL
aplicacionServidor.MapGet("/", () => $"[OK] Compilador SFD operativo. Target .NET: {versionNetDetectada}");
// Mapeamos la ruta de validacion que recibira las peticiones POST con el codigo

aplicacionServidor.MapPost("/validate", (CargaUtilScript cargaUtil) =>
{
    // Verificamos si el contenido de codigo recibido esta vacio o es nulo
    if (string.IsNullOrWhiteSpace(cargaUtil.Code))
    {
        // Devolvemos una respuesta de error indicando la falta de codigo fuente
        return Results.Json(new { success = false, message = "No se envio ningun codigo." });
    }

    // Construimos una plantilla de clase valida para envolver el codigo del usuario
    string codigoEnvuelto = @"
using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using SFDGameScriptInterface;

public class GameScript : GameScriptInterface {
    public GameScript() : base(null) {}
" + cargaUtil.Code + @"
}
";

    // El juego real compila los scripts con el CSharpCodeProvider clasico de
    // .NET Framework (csc.exe pre-Roslyn, tope C# 5), asi que capamos aqui el
    // parseo a la misma version para no aceptar sintaxis que el juego rechazaria
    // (interpolacion de strings, nameof, ?., catch...when, etc.)
    var opcionesParseo = new CSharpParseOptions(LanguageVersion.Latest);

    // Convertimos el codigo de texto en un arbol sintactico estructurado
    var arbolSintactico = CSharpSyntaxTree.ParseText(codigoEnvuelto, opcionesParseo);

    // Dividimos el código en un array de líneas (teniendo en cuenta saltos de línea de Windows y Linux)
    var lineasCodigo = cargaUtil.Code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

    // Iteramos sobre cada namespace prohibido que extrajimos dinámicamente del ensamblado del juego
    foreach (var baneado in namespacesBaneadosDinamicos)
    {
        // Recorremos el código fuente del usuario línea por línea desde la primera hasta la última
        for (int i = 0; i < lineasCodigo.Length; i++)
        {
            // Verificamos si la línea actual de texto contiene el namespace prohibido que estamos evaluando
            if (lineasCodigo[i].Contains(baneado))
            {
                // Comprobamos si algún tipo de la lista blanca está presente en esta misma línea para eximir el baneo
                bool estaExcepcionado = tiposBlancosDinamicos.Any(blanco => lineasCodigo[i].Contains(blanco));
                
                // Si la línea no cuenta con una excepción oficial de la whitelist, procedemos a bloquearla
                if (!estaExcepcionado)
                {
                    // Retornamos inmediatamente una respuesta JSON indicando que el código falló el filtro de seguridad
                    return Results.Json(new { 
                        success = false, // Marcador booleano que indica que la validación ha fallado
                        errors = new[] { new { line = i + 1, message = $"El namespace '{baneado}' está prohibido por el sandbox." } } // Objeto con la línea real (ajustada a base 1) y el mensaje de error
                    });
                }
            }
        }
    }

    // Iteramos sobre cada tipo o clase específica prohibida descubierta en el motor de scripts
    foreach (var tipoBaneado in tiposBaneadosDinamicos)
    {
        // Recorremos de nuevo todo el código del usuario línea por línea para localizar el tipo bloqueado
        for (int i = 0; i < lineasCodigo.Length; i++)
        {
            // Comprobamos si la línea de código analizada contiene el nombre del tipo no permitido
            if (lineasCodigo[i].Contains(tipoBaneado))
            {
                // Revisamos si la lista blanca contiene alguna excepción aplicable en esta línea específica
                bool estaExcepcionado = tiposBlancosDinamicos.Any(blanco => lineasCodigo[i].Contains(blanco));
                
                // Si el uso del tipo no está respaldado por la whitelist, bloqueamos la ejecución del validador
                if (!estaExcepcionado)
                {
                    // Respondemos con un objeto JSON estructurado detallando la infracción del sandbox
                    return Results.Json(new { 
                        success = false, // Indicador de que el script no superó la validación
                        errors = new[] { new { line = i + 1, message = $"El tipo '{tipoBaneado}' está prohibido por el sandbox." } } // Línea exacta detectada (i + 1) y el motivo del baneo
                    });
                }
            }
        }
    }

    // Creamos las opciones de compilacion indicando que queremos generar una libreria vinculada
    var opcionesCompilacion = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release);

    // Configuramos la sesion de compilacion con su nombre, el arbol y las referencias
    var sesionCompilacion = CSharpCompilation.Create(
        "SFD_Script_Assembly",
        syntaxTrees: new[] { arbolSintactico },
        references: referenciasCompilador,
        options: opcionesCompilacion
    );

    // Emitimos la compilacion hacia un flujo nulo, ya que solo queremos el analisis
    var resultadoCompilacion = sesionCompilacion.Emit(Stream.Null);
    
    // Definimos la compensacion de lineas por el envoltorio de la clase inyectada
    int compensacionCabecera = 11;

    // Filtramos los diagnosticos de Roslyn quedandonos unicamente con los errores severos
    var listaCompletaErrores = resultadoCompilacion.Diagnostics
        .Where(diagnostico => diagnostico.Severity == DiagnosticSeverity.Error)
        .Select(error => {
            
            // Extraemos la ubicacion fisica del error subyacente
            var ubicacionError = error.Location.GetLineSpan();
            
            // Calculamos la linea real visible para el usuario en su editor
            int lineaReal = Math.Max(1, ubicacionError.StartLinePosition.Line - compensacionCabecera); 

            // Construimos el objeto anonimo con la linea y el mensaje original del compilador
            return new { 
                line = lineaReal, 
                message = error.GetMessage() 
            };
        })
        .ToList();

    // Si la compilacion nativa fue exitosa y no se encontraron errores
    if (resultadoCompilacion.Success)
    {
        // Devolvemos un estado positivo junto a un arreglo vacio de errores
        return Results.Json(new { success = true, errors = Array.Empty<object>() });
    }

    // En caso de fallos, devolvemos el estado negativo y toda la coleccion unificada de errores
    return Results.Json(new { success = false, errors = listaCompletaErrores });
});

// Recuperamos el puerto de las variables de entorno o asignamos el predeterminado
var puertoServidor = Environment.GetEnvironmentVariable("PORT") ?? "8080";

// Iniciamos la escucha de la aplicacion en todas las interfaces de red disponibles
aplicacionServidor.Run($"http://0.0.0.0:{puertoServidor}");

// Declaramos la clase base que modelara el cuerpo JSON de la peticion HTTP
public class CargaUtilScript
{
    // Propiedad que almacenara el codigo fuente original enviado por el editor
    public string? Code { get; set; }
}