#!/bin/bash

# Comprobamos y eliminamos de forma forzosa cualquier proceso zombie que este ocupando el puerto 8080
fuser -k 8080/tcp 2>/dev/null

# Pausa breve de seguridad para garantizar que el sistema libere completamente los recursos de red
sleep 1

# Imprimimos un mensaje informativo en consola indicando el inicio del arranque de la API
echo "Iniciando servidor API para pruebas..."

# 1. Iniciamos la aplicacion web en segundo plano redirigiendo toda la salida a nulo
dotnet run >/dev/null 2>&1 &

# Capturamos el identificador unico (PID) del proceso del servidor web lanzado
SERVER_PID=$!

# Bucle de espera activa (Health Check): Esperamos dinamicamente hasta que la API responda
echo "Esperando a que el servidor este listo..."
until curl -s http://localhost:8080/ > /dev/null; do
  sleep 1
done

# Inicializamos el contador numerico para registrar el total de fallos detectados
FALLOS=0

# Imprimimos el separador visual e indicamos el comienzo del proceso de analisis de los scripts
echo "=============================="
echo "Iniciando analisis de scripts..."
echo "=============================="

# 2. Iteramos a traves de cada elemento contenido dentro del directorio de pruebas designado
for archivo in ./test/*; do
  
  # Verificamos fisicamente que el elemento sea un archivo regular y que no corresponda a este mismo script de shell
  if [ -f "$archivo" ]; then
    
    # Imprimimos en pantalla el nombre del script actual que esta siendo evaluado
    echo -n "Probando: $(basename "$archivo") ... "

    # Construimos el cuerpo JSON de la peticion utilizando jq con rawfile y lo enviamos mediante curl usando la entrada estandar
    RESPUESTA=$(jq -n --rawfile code "$archivo" '{Code: $code}' | curl -s -X POST http://localhost:8080/validate \
      -H "Content-Type: application/json" \
      --data-binary @-)

    # Evaluamos si la respuesta devuelta por el servidor contiene la propiedad de exito establecida en verdadero
    if echo "$RESPUESTA" | grep -q '"success":true'; then
      
      # Informamos por consola que la validacion sintactica del script ha sido exitosa
      echo " [ OK ]"
    else
      
      # Informamos sobre el fallo encontrado y mostramos los detalles tecnicos devueltos por la API
      echo " [ ERROR ]"
      echo "   Detalles: $RESPUESTA"
      
      # Incrementamos en una unidad el contador global de fallos
      FALLOS=$((FALLOS + 1))
    fi
  fi
done

# 3. Finalizamos la ejecucion del servidor web utilizando su PID almacenado para asegurar una limpieza correcta de procesos
kill $SERVER_PID 2>/dev/null
wait $SERVER_PID 2>/dev/null

# Imprimimos el separador final de resultados globales
echo "=============================="

# Evaluamos si se registraron fallos durante la ejecucion de todas las pruebas automatizadas
if [ $FALLOS -gt 0 ]; then
  
  # Mostramos un mensaje critico informando la cantidad de scripts erroneos y finalizamos con codigo de error
  echo "[X] Se encontraron errores en $FALLOS script(s)."
  exit 1
else
  
  # Mostramos un mensaje positivo indicando que la totalidad de los scripts superaron los tests con exito
  echo "[OK] ¡Todos los scripts pasaron el test correctamente!"
  exit 0
fi