# Ensamblados de referencia de .NET

Superfighters Deluxe corre sobre una version concreta de .NET (hoy **.NET 8**). Esta API puede
correr sobre otra (hoy .NET 10), y ahi esta el problema: si al compilar un script se usan como
referencia los ensamblados del runtime de la API, el script ve APIs que en el juego **no existen**.

Ejemplo real: `Enumerable.CountBy` existe desde .NET 9. Compilando contra .NET 10 el compilador
daba el script por bueno, y dentro del juego fallaba con `CS1061`.

Por eso aqui viven los *ensamblados de referencia*: no contienen codigo ejecutable, solo la
superficie publica de la API. Son exactamente lo que usaria el compilador de C# si el proyecto
tuviese `<TargetFramework>net8.0</TargetFramework>`, pero sin obligar a la API a correr sobre .NET 8.

## Como esta organizado

Una carpeta por version de .NET, con el nombre corto del framework:

```
referencias/
  net8.0/        <- la que usa SFD hoy
  net10.0/       <- se anadiria sola si SFD saltase a .NET 10

```

El compilador lee del propio `SFD.ScriptEngine.dll` sobre que version esta compilado el juego y
**elige la carpeta correspondiente solo**. No hay nada que configurar.

Si la carpeta de esa version no existe, el arranque avisa bien claro y cae al runtime de la API:

```
[!! ] Referencias de compilacion: 11 (AVISO: runtime de esta API, falta la carpeta referencias/net8.0)
[!! ] Ejecuta: ./tools/actualizar-sfd.sh "<carpeta del juego>"  para descargar net8.0

```

## Cuando SFD cambie de version de .NET

Un solo comando, el mismo de siempre:

```bash
./tools/actualizar-sfd.sh "/ruta/a/steamapps/common/Superfighters Deluxe"

```

El script copia las DLL, lee del binario sobre que .NET corre el juego y, si no tenemos esa
version, se descarga de NuGet los ensamblados de referencia que hacen falta. La carpeta vieja
puede quedarse o borrarse, da igual: solo se usa la que coincide con el juego.

## Contenido

* Origen: paquete NuGet `Microsoft.NETCore.App.Ref`, carpeta `ref/<version>/`.
* Solo estan los ensamblados que el juego referencia al compilar un script
  (ver `GameWorld.GetScriptTypes()` decompilado y `ResolverReferencias()` en `Sfd/MotorSfd.cs`).
* Son ~1 MB por version, frente a los ~19 MB que ocuparian los ensamblados reales del runtime.

Si en cambio se apunta `SFD_DLL_DIR` a la carpeta de instalacion del juego, esta carpeta se ignora:
se usan los ensamblados reales del juego, que es la maxima fidelidad posible.
