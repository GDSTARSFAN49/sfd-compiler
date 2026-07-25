#!/bin/bash

# Ruta de los scripts
script_route=dataset/harvested

# Leemos linea a linea los datasets
for line in $(ls $script_route)
do
    # Nombre del script
    script_name=$line
    
    # Buscamos el archivo .cs
    script_cs=$(ls "${script_route}/${script_name}" | grep .*.cs)

    # Sacamos el contenido y lo escribimos
    cat "${script_route}/${script_name}/${script_cs}" > "${script_name}.txt"
done