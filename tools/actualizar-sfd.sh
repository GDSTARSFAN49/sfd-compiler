#!/bin/bash
# Pone el compilador al dia con una version nueva de Superfighters Deluxe.
#
# Uso:
#   ./tools/actualizar-sfd.sh "/ruta/a/steamapps/common/Superfighters Deluxe"
#
# Hace dos cosas:
#   1. Copia las dos DLL del juego (la API y el motor de scripts).
#   2. Detecta sobre que version de .NET corre el juego y, si no la tenemos, descarga
#      los ensamblados de referencia de esa version a referencias/netX.Y/.
#
# Con esto el compilador queda alineado con el juego sin tocar una linea de codigo.

set -euo pipefail

# Comprobamos que nos hayan pasado la carpeta del juego
if [ $# -lt 1 ]; then
  echo "Uso: $0 \"<carpeta de instalacion de Superfighters Deluxe>\"" >&2
  exit 1
fi

CARPETA_JUEGO="$1"
CARPETA_COMPILADOR="$(cd "$(dirname "$0")/.." && pwd)"

# Estas son las dos unicas DLL que el compilador necesita del juego
DLLS=("SFD.GameScriptInterface.dll" "SFD.ScriptEngine.dll")

# Estos son los ensamblados de referencia que el compilador pasa al compilar un script.
# Salen de replicar GameWorld.GetScriptTypes(); si algun dia cambian, se ajusta esta lista
# y la de ResolverReferencias() en Sfd/MotorSfd.cs.
REFERENCIAS=(
  System.Runtime
  System.Linq
  System.Linq.Queryable
  System.Linq.Expressions
  System.Collections
  System.Collections.NonGeneric
  System.Collections.Specialized
  System.Collections.Concurrent
  System.Text.RegularExpressions
)

echo "== 1. DLL del juego =="

# Verificamos que existan antes de tocar nada
for dll in "${DLLS[@]}"; do
  if [ ! -f "$CARPETA_JUEGO/$dll" ]; then
    echo "[X] No encuentro '$dll' en '$CARPETA_JUEGO'" >&2
    exit 1
  fi
done

# Copiamos mostrando el cambio de huella para dejar constancia de la version
for dll in "${DLLS[@]}"; do
  ANTES="ausente"
  [ -f "$CARPETA_COMPILADOR/$dll" ] && ANTES="$(sha256sum "$CARPETA_COMPILADOR/$dll" | cut -c1-12)"
  cp "$CARPETA_JUEGO/$dll" "$CARPETA_COMPILADOR/$dll"
  DESPUES="$(sha256sum "$CARPETA_COMPILADOR/$dll" | cut -c1-12)"
  echo "[OK] $dll  $ANTES -> $DESPUES"
done

echo
echo "== 2. Version de .NET del juego =="

# El motor declara su version objetivo dentro del propio binario, asi que la leemos de ahi.
# No hace falta ni .NET ni decompilar: la cadena esta en texto plano en los metadatos.
FRAMEWORK="$(strings -a "$CARPETA_COMPILADOR/SFD.ScriptEngine.dll" | grep -oE '\.NETCoreApp,Version=v[0-9]+\.[0-9]+' | head -1 || true)"

# Sin esa cadena no podemos deducir que ensamblados de referencia hacen falta
if [ -z "$FRAMEWORK" ]; then
  echo "[!] No he podido leer la version de .NET del motor."
  echo "    Miralo en el arranque ('Motor cargado (compilado para ...)') y crea la carpeta a mano."
  exit 0
fi

# Traducimos ".NETCoreApp,Version=v8.0" al nombre corto "net8.0"
VERSION="${FRAMEWORK#.NETCoreApp,Version=v}"
MONIKER="net$VERSION"
DESTINO="$CARPETA_COMPILADOR/referencias/$MONIKER"
echo "[OK] El juego corre sobre $MONIKER"

# Si ya tenemos esa carpeta no hay nada que descargar
if [ -f "$DESTINO/System.Runtime.dll" ]; then
  echo "[OK] Ya existe referencias/$MONIKER, no hay nada que hacer"
  echo
  echo "Listo. Reconstruye con 'dotnet build' y comprueba el arranque:"
  echo "  dotnet run  y luego  curl http://localhost:8080/"
  exit 0
fi

echo
echo "== 3. Descargando ensamblados de referencia de $MONIKER =="

# Preguntamos a NuGet cual es el ultimo parche publicado de esa version mayor
PAQUETE="microsoft.netcore.app.ref"
ULTIMA="$(curl -fsS "https://api.nuget.org/v3-flatcontainer/$PAQUETE/index.json" \
  | grep -oE "\"$VERSION\.[0-9]+\"" | tr -d '"' | sort -V | tail -1 || true)"

# Si NuGet no tiene esa version, avisamos en vez de dejar el compilador a medias
if [ -z "$ULTIMA" ]; then
  echo "[X] NuGet no publica ningun $PAQUETE $VERSION.x todavia." >&2
  echo "    Alternativa: apunta SFD_DLL_DIR a la carpeta del juego y usara sus propios ensamblados." >&2
  exit 1
fi

echo "[OK] Usando $PAQUETE $ULTIMA"

# Descargamos el paquete a un temporal que se limpia solo al salir
TEMPORAL="$(mktemp -d)"
trap 'rm -rf "$TEMPORAL"' EXIT
curl -fsS -o "$TEMPORAL/ref.zip" \
  "https://api.nuget.org/v3-flatcontainer/$PAQUETE/$ULTIMA/$PAQUETE.$ULTIMA.nupkg"

# Extraemos unicamente los ensamblados que el compilador necesita
mkdir -p "$DESTINO"
for ref in "${REFERENCIAS[@]}"; do
  unzip -o -j "$TEMPORAL/ref.zip" "ref/$MONIKER/$ref.dll" -d "$DESTINO" > /dev/null
  echo "[OK] $ref.dll"
done

echo
echo "Listo. Reconstruye con 'dotnet build' y comprueba el arranque:"
echo "  dotnet run  y luego  curl http://localhost:8080/"
echo
echo "Deberia aparecer:"
echo "  [OK ] Referencias de compilacion: N (ensamblados de referencia de $MONIKER)"
