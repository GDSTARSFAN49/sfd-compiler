ruta_guardar="test"

for script in $(ls ${ruta_guardar})
do
    base=$(basename "$script")
    nombre_script="${base/.txt/.sfde}"
    mv "${ruta_guardar}/${script}" "${ruta_guardar}/${nombre_script}"
done