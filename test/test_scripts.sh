FALLOS=0
echo "=============================="
echo "Iniciando análisis de scripts..."
echo "=============================="

# Recorre todos los archivos dentro de la carpeta test/
for archivo in ./test/*; do
  if [ -f "$archivo" ]; then
    echo -n "Probando: $(basename "$archivo") ... "
    
    # Ejecuta tu compilador pasándole el archivo como argumento.
    # (Asume que tu Program.cs acepta la ruta del archivo como argumento)
    if dotnet run --no-build -- "$archivo"; then
      echo " [ OK ]"
    else
      echo " [ ERROR ]"
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
