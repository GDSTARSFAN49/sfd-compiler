# Scripts que la 1.6.0 se ha cargado

Estos scripts funcionaban antes y **ya no funcionan dentro del juego** desde la 1.6.0.
No son fallos del compilador: se han comprobado uno a uno contra los datos que vienen
embebidos en `SFD.ScriptEngine.dll`, sin que intervenga nada de la maquina donde corre la API
(`SFD_ESCANEO_VECINOS=0`, o sea usando solo la lista que trae el propio juego dentro).

Estan aqui, en una subcarpeta, para que `test_scripts.sh` los ignore (el bucle solo mira ficheros
sueltos de `test/`) pero sin perderlos de vista.

| Script | Que usa | Por que ya no vale |
| --- | --- | --- |
| `Revive_mod.sfde` | `new System.Timers.Timer(...)` | `Timer` y `System.Timers.Timer` estan en la lista negra de tipos del motor. |
| `Deadpool.sfde` | `Activator.CreateInstance` | El analizador de seguridad la bloquea por nombre, ademas de tener `Activator` en la lista negra. |
| `FunxLive.sfde` | `Activator.CreateInstance` | Lo mismo que el anterior. |
| `SFB.sfde` | `Image` | Esta en la lista de tipos vetados por vivir en un namespace prohibido. |
| `Gemi2.sfde` | `GameScriptInterface.Game` | Cambio de API: en 1.5 `GameScriptInterface` tenia `public static IGame Game`, en 1.6.0 esa propiedad ya no existe. Ahora hay que usar `Game` a secas. |

Encaja con lo que avisaron en el changelog de la 1.6.0:

> Added a new script engine as part of the move to .NET 8 with improved stability and security.
> NOTE: This means some custom scripts may no longer work if they use .NET types that are no
> longer considered safe.

## Como arreglarlos

* **Timer**: usar `Events.UpdateCallback` con un acumulador de tiempo en vez de un temporizador del sistema.
* **Activator.CreateInstance**: instanciar las clases directamente con `new`.
* **Image**: usar los tipos de la API del juego (`IObject`, `IObjectText`...) en vez de tipos de dibujo de .NET.
* **GameScriptInterface.Game**: sustituir por `Game`, que la plantilla del juego ya deja disponible.

Cuando alguno se arregle, se saca de aqui y vuelve a `test/`.

Nota: habia un `a.sfde` que era copia byte a byte de `Revive_mod.sfde`, se ha quitado por duplicado.
