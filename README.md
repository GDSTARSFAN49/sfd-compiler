# SFD Script Compiler API

Una API web ligera de alto rendimiento creada en **C# (ASP.NET Core Minimal APIs)** que permite compilar y validar scripts del juego **Superfighters Deluxe (SFD)** de forma remota y segura.

No reimplementa las reglas del juego: **carga el motor de scripts original de SFD (`SFD.ScriptEngine.dll`) y le pide a él mismo que valide y compile**, igual que hace el editor de mapas. Eso significa que la lista negra de namespaces, la lista negra/blanca de tipos, las palabras reservadas y la API disponible salen siempre de los DLL del juego. **Cuando SFD se actualiza basta con sustituir dos DLL**: no hay nada codificado a mano que mantener.

Está diseñado para ser el motor backend de extensiones de editores de código (como VSCodium / VS Code) o herramientas web.

## Cómo usar la API

El servidor expone un único *endpoint* principal para validar tu código.

### `POST /validate`

Recibe el código del script en formato JSON y devuelve el resultado de la compilación.

**Cuerpo de la petición (JSON):**
```json
{
  "Code": "public void OnStartup() { Game.ShowPopupMessage(\"Hola Mundo\"); }"
}

```

**Respuesta Exitosa (HTTP 200):**
Si el código es perfectamente válido para el motor de SFD.

```json
{
  "success": true,
  "errors": []
}

```

**Respuesta con Errores (HTTP 200):**
Si hay errores de sintaxis o uso de métodos que no existen en el juego.

```json
{
  "success": false,
  "errors": [
    {
      "line": 1,
      "column": 4,
      "code": "CS0103",
      "message": "error CS0103: El nombre 'Gaaame' no existe en el contexto actual"
    }
  ],
  "warnings": []
}

```

Los errores del *sandbox* de seguridad llegan con `code` vacío y mensajes del propio juego, por ejemplo
`Type 'Timer' unavailable` o `Method name 'Activator.CreateInstance' unavailable`.

### `GET /`

Estado del compilador: si el motor del juego cargó, para qué versión de .NET está compilado, la huella
SHA-256 corta de cada DLL y el tamaño de cada lista del sandbox. Es la forma rápida de comprobar que un
despliegue está usando los DLL de la versión correcta del juego.

### `GET /sandbox`

Vuelca las reglas completas leídas del juego: `namespacesProhibidos`, `tiposProhibidos`, `tiposPermitidos`,
`palabrasReservadas`, `callbacksHeredados` y los ensamblados referenciados al compilar. Útil para editores,
documentación o para alimentar datasets.

---

## Actualizar el compilador cuando SFD se actualice

1. Un solo comando, apuntando a la carpeta de instalación del juego:

```bash
./tools/actualizar-sfd.sh "/ruta/a/steamapps/common/Superfighters Deluxe"

```

2. Reconstruye y comprueba el arranque:

```bash
dotnet build && dotnet run
curl http://localhost:8080/

```

Eso es todo. Las listas negra y blanca, las palabras reservadas, la plantilla que el juego inyecta en
cada script y la API se releen de los DLL en cada arranque.

El script también detecta **sobre qué versión de .NET corre el juego** (la lee del propio
`SFD.ScriptEngine.dll`) y, si SFD ha saltado de versión, se descarga de NuGet los ensamblados de
referencia que hagan falta. Ver la sección siguiente.

### Variables de entorno

| Variable | Por defecto | Para qué sirve |
| --- | --- | --- |
| `SFD_DLL_DIR` | carpeta del ejecutable | Carpeta de donde leer los DLL. Se puede apuntar directamente a la instalación del juego. |
| `SFD_ESCANEO_VECINOS` | `1` | Amplía la lista de tipos vetados escaneando los DLL que haya en esa carpeta. Con `0` se usa solo la lista fija que trae el motor. |
| `SFD_ESCANEO_HOST` | `0` | Con `1` deja que el motor calcule esa lista escaneando el proceso, como hace dentro del juego. Menos determinista. |
| `PORT` | `8080` | Puerto de escucha. |

Sobre `SFD_ESCANEO_VECINOS`: el motor del juego deduce parte de su lista negra recorriendo los ensamblados
cargados en el proceso. Apuntando `SFD_DLL_DIR` a la carpeta completa del juego se obtiene la interpretación
más estricta (todo lo que el juego podría llegar a ver); dejando solo las dos DLL de SFD se obtiene la más
predecible. Lo que **no** se hace nunca es dejar que los tipos internos del runtime de esta API contaminen
la lista, porque son de una versión de .NET distinta a la del juego.

---

## Por qué esta API no tiene que correr sobre .NET 8

SFD 1.6.0 corre sobre **.NET 8**, pero esta API corre sobre .NET 10. Eso no es un problema —
un runtime moderno carga sin dificultad un ensamblado compilado para .NET 8 — siempre que se
cuide una cosa: **contra qué superficie de API se compilan los scripts**.

Si se compilase contra los ensamblados del runtime de esta API, un script que use
`Enumerable.CountBy` (que existe desde .NET 9) se daría por bueno aquí y fallaría dentro del
juego con `CS1061`. Por eso el compilador elige **una sola** fuente de referencias, nunca una
mezcla, en este orden:

1. **Los ensamblados del propio juego**, si `SFD_DLL_DIR` apunta a la instalación de SFD. Fidelidad total.
2. **`referencias/<versión>/`**, con los *ensamblados de referencia* de esa versión de .NET (~1 MB, solo firmas, sin código). Es el modo por defecto.
3. **El runtime de esta API**, como último recurso si falta lo anterior. Compila, pero la superficie puede no coincidir.

El arranque te dice cuál está usando:

```
[OK ] Referencias de compilacion: 10 (ensamblados de referencia de net8.0)

```

### Si SFD cambia de versión de .NET

No hay que tocar código ni el `csproj`. El compilador lee del propio `SFD.ScriptEngine.dll` sobre
qué versión está compilado el juego y **elige solo** la carpeta `referencias/netX.Y/` que toca.
Si esa carpeta no existe, el arranque avisa y te dice el comando exacto:

```
[!! ] Referencias de compilacion: 11 (AVISO: runtime de esta API, falta la carpeta referencias/net10.0)
[!! ] Ejecuta: ./tools/actualizar-sfd.sh "<carpeta del juego>"  para descargar net10.0

```

Y ese comando la descarga. Las carpetas de versiones viejas pueden quedarse: solo se usa la que
coincide con el juego. Detalles en [`referencias/LEEME.md`](referencias/LEEME.md).

---

## Desarrollo Local

Para probar o modificar esta API en tu propia máquina, necesitas tener instalado el SDK de **.NET 10** o **Docker**.

### Opción A: Ejecución normal (con .NET SDK)

1. Clona el repositorio:
```bash
git clone [https://github.com/TU-USUARIO/sfd-compiler-api.git](https://github.com/TU-USUARIO/sfd-compiler-api.git)
cd sfd-compiler-api

```


2. Inicia el servidor:
```bash
dotnet run

```


3. El servidor estará escuchando (usualmente en `http://localhost:8080` o `http://localhost:5000`).

### Opción B: Ejecución con Docker (Recomendado)

1. Construye la imagen:
```bash
docker build -t sfd-api .

```


2. Enciende el contenedor mapeando el puerto 8080:
```bash
docker run -p 8080:8080 sfd-api

```



---

## Estructura del Proyecto

* `Program.cs`: Servidor web, configuración de CORS y los tres *endpoints*.
* `Sfd/MotorSfd.cs`: Carga `SFD.ScriptEngine.dll` en un contexto aislado y delega en él la validación y la compilación. Aquí está la traducción de líneas y la lectura de las listas del sandbox.
* `Sfd/LectorMetadatos.cs`: Lee metadatos de los DLL del juego **sin ejecutarlos** (extrae la plantilla que SFD inyecta en cada script y escanea tipos por namespace).
* `referencias/netX.Y/`: Ensamblados de referencia de cada versión de .NET, la superficie de API que ve el juego. Ver su `LEEME.md`.
* `tools/actualizar-sfd.sh`: Copia las DLL desde una instalación del juego y descarga las referencias de la versión de .NET que toque.
* `Dockerfile`: Receta multi-etapa para construir el proyecto y desplegarlo usando una imagen pura de ASP.NET muy ligera.
* `SFD.GameScriptInterface.dll`: **(Importante)** Es la API oficial del juego: define `IGame`, `IPlayer`, `IObject`… El compilador la referencia al compilar cada script.
* `SFD.ScriptEngine.dll`: **(Importante)** Es el motor de scripts del juego. De aquí salen la lista negra de namespaces y tipos, la lista blanca, las palabras reservadas y el propio compilador que usa SFD.