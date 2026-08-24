// Librerias a usar
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SFD_COMPILER.Sfd;

// Utilidades que leen los metadatos de un ensamblado SIN cargarlo ni ejecutar una sola instruccion suya.
// Se usa para sacar del propio juego cosas que no estan expuestas como API publica (la cabecera que inyecta
// el motor en cada script, los nombres de los callbacks antiguos, etc.).
public static class LectorMetadatos
{
    // Codigo de operacion IL de "ldstr" (cargar cadena literal en la pila)
    private const byte OpcodeLdstr = 0x72;

    // Byte alto que identifica a un token de la tabla de cadenas de usuario (#US)
    private const int TablaCadenasUsuario = 0x70;

    // Devuelve, en orden de aparicion, todas las cadenas literales que un metodo concreto carga en su IL
    public static List<string> LeerCadenasDeMetodo(string rutaEnsamblado, string espacioNombres, string nombreTipo, string nombreMetodo)
    {
        // Inicializamos la lista donde iremos acumulando cada literal encontrado
        var cadenasEncontradas = new List<string>();

        // Si el ensamblado no existe fisicamente no hay nada que leer
        if (!File.Exists(rutaEnsamblado)) return cadenasEncontradas;

        try
        {
            // Abrimos el fichero en solo lectura y montamos el lector de metadatos del PE
            using var flujo = File.OpenRead(rutaEnsamblado);
            using var lectorPe = new PEReader(flujo);

            // Si el binario no lleva metadatos administrados no es un ensamblado .NET
            if (!lectorPe.HasMetadata) return cadenasEncontradas;

            // Obtenemos el lector de la seccion de metadatos
            var metadatos = lectorPe.GetMetadataReader();

            // Recorremos todas las definiciones de tipo buscando la que nos han pedido
            foreach (var manejadorTipo in metadatos.TypeDefinitions)
            {
                var definicionTipo = metadatos.GetTypeDefinition(manejadorTipo);

                // Descartamos cualquier tipo cuyo namespace o nombre no coincida con el objetivo
                if (metadatos.GetString(definicionTipo.Namespace) != espacioNombres) continue;
                if (metadatos.GetString(definicionTipo.Name) != nombreTipo) continue;

                // Ya dentro del tipo, recorremos sus metodos buscando el que nos interesa
                foreach (var manejadorMetodo in definicionTipo.GetMethods())
                {
                    var definicionMetodo = metadatos.GetMethodDefinition(manejadorMetodo);
                    if (metadatos.GetString(definicionMetodo.Name) != nombreMetodo) continue;

                    // Un metodo abstracto o externo no tiene cuerpo IL que analizar
                    if (definicionMetodo.RelativeVirtualAddress == 0) continue;

                    // Leemos el cuerpo del metodo y volcamos su IL a un array de bytes
                    var cuerpo = lectorPe.GetMethodBody(definicionMetodo.RelativeVirtualAddress);
                    var lectorIl = cuerpo.GetILReader();
                    var instrucciones = lectorIl.ReadBytes(lectorIl.Length);

                    // Barremos el IL buscando la secuencia "ldstr <token de cadena de usuario>"
                    for (int posicion = 0; posicion + 4 < instrucciones.Length; posicion++)
                    {
                        if (instrucciones[posicion] != OpcodeLdstr) continue;

                        // Reconstruimos el token de 4 bytes en little endian tal y como lo escribe el compilador
                        int token = instrucciones[posicion + 1]
                                  | (instrucciones[posicion + 2] << 8)
                                  | (instrucciones[posicion + 3] << 16)
                                  | (instrucciones[posicion + 4] << 24);

                        // Solo aceptamos tokens que realmente apunten a la tabla de cadenas de usuario
                        if ((token >>> 24) != TablaCadenasUsuario) continue;

                        // Resolvemos el token contra el heap #US y guardamos la cadena resultante
                        try { cadenasEncontradas.Add(metadatos.GetUserString(MetadataTokens.UserStringHandle(token))); }
                        catch { /* token invalido: era un falso positivo del barrido, lo ignoramos */ }
                    }
                }
            }
        }
        catch
        {
            // Cualquier problema leyendo el binario se traduce en "no he podido extraer nada"
        }

        // Devolvemos todo lo encontrado al llamante
        return cadenasEncontradas;
    }

    // Recorre varios ensamblados y devuelve el nombre simple de todos los tipos que viven en alguno de los namespaces indicados.
    // Replica lo que el motor del juego hace por reflexion, pero leyendo ficheros en vez de ensamblados ya cargados en memoria.
    public static HashSet<string> EscanearTiposPorNamespace(IEnumerable<string> rutasEnsamblados, IEnumerable<string> prefijosNamespace)
    {
        // Preparamos el conjunto de salida y normalizamos los prefijos quitando los comodines finales
        var tiposEncontrados = new HashSet<string>(StringComparer.Ordinal);
        var prefijos = prefijosNamespace.Select(prefijo => prefijo.TrimEnd('*')).ToArray();

        // Analizamos ensamblado por ensamblado
        foreach (var ruta in rutasEnsamblados)
        {
            try
            {
                using var flujo = File.OpenRead(ruta);
                using var lectorPe = new PEReader(flujo);
                if (!lectorPe.HasMetadata) continue;

                var metadatos = lectorPe.GetMetadataReader();

                // Recorremos cada tipo definido dentro del ensamblado actual
                foreach (var manejadorTipo in metadatos.TypeDefinitions)
                {
                    // Resolvemos el namespace real del tipo (los tipos anidados lo heredan del tipo contenedor)
                    var espacioNombres = ResolverNamespace(metadatos, manejadorTipo);
                    if (espacioNombres.Length == 0) continue;

                    // Si el namespace empieza por alguno de los prefijos prohibidos, el nombre del tipo queda vetado
                    if (prefijos.Any(prefijo => espacioNombres.StartsWith(prefijo, StringComparison.Ordinal)))
                    {
                        tiposEncontrados.Add(metadatos.GetString(metadatos.GetTypeDefinition(manejadorTipo).Name));
                    }
                }
            }
            catch
            {
                // Un DLL nativo o corrupto simplemente se salta
            }
        }

        // Devolvemos el conjunto completo de nombres de tipo vetados
        return tiposEncontrados;
    }

    // Sube por la cadena de tipos contenedores hasta encontrar el namespace efectivo de un tipo
    private static string ResolverNamespace(MetadataReader metadatos, TypeDefinitionHandle manejadorTipo)
    {
        var definicion = metadatos.GetTypeDefinition(manejadorTipo);
        var espacioNombres = metadatos.GetString(definicion.Namespace);

        // Si el tipo declara namespace propio ya hemos terminado
        if (!string.IsNullOrEmpty(espacioNombres)) return espacioNombres;

        // Si es un tipo anidado heredamos el namespace de quien lo contiene
        var contenedor = definicion.GetDeclaringType();
        return contenedor.IsNil ? string.Empty : ResolverNamespace(metadatos, contenedor);
    }
}
