// Librerias a usar
using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.CodeAnalysis.CSharp;

// Inicializamos el constructor de la aplicacion web utilizando los argumentos del sistema
var constructorAplicacion = WebApplication.CreateBuilder(args);

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

// Imprimimos en consola la ruta generada para facilitar la depuracion interna
Console.WriteLine($"[DEBUG] Ruta absoluta de la DLL: {rutaLibreriaJuego}");

// Comprobamos fisicamente si el archivo de la libreria existe en el disco duro
Console.WriteLine($"[DEBUG] ¿El archivo existe fisicamente?: {File.Exists(rutaLibreriaJuego)}");

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
    
    // Añadimos las referencias ubicadas dinamicamente en el directorio del nucleo
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Collections.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "System.Text.RegularExpressions.dll")),
    MetadataReference.CreateFromFile(Path.Combine(directorioNucleoNet, "mscorlib.dll")),
    
    // Añadimos la referencia principal a la libreria del juego Superfighters Deluxe
    MetadataReference.CreateFromFile(rutaLibreriaJuego)
};

// Mapeamos la ruta raiz para que responda con un mensaje de estado de salud basico
aplicacionServidor.MapGet("/", () => "[OK] La API del compilador de SFD esta online y operativa.");

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
using SFDGameScriptInterface;

public class GameScript : GameScriptInterface {
    public GameScript() : base(null) {}
" + cargaUtil.Code + @"
}
";

    // Convertimos el codigo de texto en un arbol sintactico estructurado
    var arbolSintactico = CSharpSyntaxTree.ParseText(codigoEnvuelto);

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

    // Definimos un conjunto de cadenas con los nombres exactos de los eventos del juego
    var eventosValidosJuego = new HashSet<string> {
        "AfterStartup", "OnStartup", "OnPlayerDamage", "OnPlayerKill", 
        "OnPlayerSpawn", "OnUserMessage", "OnPlayerKeyInput", "OnUpdate", "OnPlayerDead"
    };

    // Instanciamos una lista dinamica para guardar los errores tipograficos detectados
    var erroresPersonalizados = new List<object>();
    
    // Calculamos la compensacion de lineas debido a nuestra cabecera inyectada
    int compensacionCabecera = 11;

    // Buscamos todas las declaraciones de metodos dentro de la raiz del arbol sintactico
    var declaracionesMetodos = arbolSintactico.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>();
    
    // Iteramos a traves de cada metodo encontrado en el codigo del usuario
    foreach (var metodo in declaracionesMetodos)
    {
        // Extraemos el nombre en formato de texto puro del metodo actual
        string nombreMetodo = metodo.Identifier.Text;
        
        // Evaluamos si el nombre del metodo cumple con los prefijos habituales de eventos
        bool pareceEventoOficial = nombreMetodo.StartsWith("On") || nombreMetodo.StartsWith("After");

        // Si tiene prefijo de evento pero no coincide exactamente con la lista oficial
        if (pareceEventoOficial && !eventosValidosJuego.Contains(nombreMetodo))
        {
            // Obtenemos la posicion fisica de este metodo dentro del documento analizado
            var ubicacionMetodo = metodo.Identifier.GetLocation().GetLineSpan();
            
            // Ajustamos el numero de linea restando el tamaño de nuestro envoltorio superior
            int lineaReal = Math.Max(1, ubicacionMetodo.StartLinePosition.Line - compensacionCabecera);

            // Añadimos el error a nuestra lista indicando un posible fallo tipografico
            erroresPersonalizados.Add(new {
                line = lineaReal,
                message = $"El metodo '{nombreMetodo}' no es un evento valido de SFD. Comprueba si es un error tipografico."
            });
        }
    }

    // Filtramos los diagnosticos de Roslyn quedandonos unicamente con los errores severos
    var erroresRoslyn = resultadoCompilacion.Diagnostics
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
        });

    // Concatenamos ambas listas de errores y materializamos el resultado en memoria
    var listaCompletaErrores = erroresPersonalizados.Concat(erroresRoslyn).ToList();

    // Si la compilacion nativa fue exitosa y no se encontraron errores manuales
    if (resultadoCompilacion.Success && !erroresPersonalizados.Any())
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