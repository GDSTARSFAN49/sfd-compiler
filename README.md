# SFD Script Compiler API

Una API web ligera de alto rendimiento creada en **C# (ASP.NET Core Minimal APIs)** que permite compilar y validar scripts del juego **Superfighters Deluxe (SFD)** de forma remota y segura.

Este proyecto utiliza **Roslyn (Microsoft.CodeAnalysis)** para compilar el código en memoria y devolver los errores de sintaxis o ejecución en tiempo real, con las líneas exactas del fallo. Está diseñado para ser el motor backend de extensiones de editores de código (como VSCodium / VS Code) o herramientas web.

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
      "message": "El nombre 'Gaaame' no existe en el contexto actual"
    }
  ]
}

```

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

* `Program.cs`: Contiene toda la lógica del servidor, configuración de CORS y el motor de compilación Roslyn.
* `Dockerfile`: Receta multi-etapa para construir el proyecto y desplegarlo usando una imagen pura de ASP.NET muy ligera.
* `SFD.GameScriptInterface.dll`: **(Importante)** Es la API oficial del juego. El compilador necesita esta librería físicamente presente para entender los comandos propios de SFD (como `Game.GetPlayers()`).