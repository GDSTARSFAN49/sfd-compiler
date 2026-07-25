#!/bin/bash

FALLOS=0
echo "=============================="
echo "Iniciando análisis de scripts..."
echo "=============================="

# 1. Compilar el proyecto una sola vez al inicio para no repetir el proceso
dotnet build --quiet

# 2. Buscar automáticamente la DLL compilada en la carpeta bin
DLL_PATH=$(find bin -name "SFD-COMPILER.dll" | head -n 1)

if [ -z "$DLL_PATH" ]; then
  echo "[X] No se encontró la DLL del compilador."
  exit 1
fi

# 3. Recorrer todos los archivos dentro de la carpeta test/
for archivo in ./test/*; do
  if [ -f "$archivo" ]; then
    echo -n "Probando: $(basename "$archivo") ... "
    
    # Ejecutamos la DLL directamente con dotnet (mucho más rápido y no se cuelga)
    if dotnet "$DLL_PATH" "$archivo" > /dev/null 2>&1; then
      echo " [OK]"
    else
      echo " [X]"
      # Si falla, ejecutamos de nuevo sin silenciar para que el log muestre el error exacto
      dotnet "$DLL_PATH" "$archivo"
      FALLOS=$((FALLOS + 1))
    fi
  fi
done

echo "=============================="
if [ $FALLOS -gt 0 ]; then
  echo "[X] Se encontraron errores en $FALLOS script(s)."
  exit 1
else
  echo "[OK] ¡Todos los scripts pasaron el test correctamente!"
  exit 0
fi