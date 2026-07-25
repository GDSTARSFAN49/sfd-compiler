# Descargamos la imagen de Microsoft que tiene el SDK de donet
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiamos el archivo del proyecto y restauramos las dependencias
COPY ["SFD-COMPILER.csproj", "./"]
RUN dotnet restore "SFD-COMPILER.csproj"

# Copiamos el resto de tus archivos (Program.cs, la DLL de SFD, etc.)
COPY . .

# Compilamos tu proyecto en modo "Release" (optimizado para velocidad) y lo metemos en la carpeta /app/publish
RUN dotnet publish "SFD-COMPILER.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Descargamos la imagen ligera de Microsoft que tiene solo el Runtime (ASP.NET)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Copiamos el resultado de la ETAPA 1 a esta nueva máquina
COPY --from=build /app/publish .

# Nos aseguramos de copiar la DLL del juego al directorio final,
COPY ["SFD.GameScriptInterface.dll", "."]

# Exponemos el puerto estándar 8080 (Render usa esto por detrás)
EXPOSE 8080

# Comando que ejecuta el servidor cuando la máquina se enciende
ENTRYPOINT ["dotnet", "SFD-COMPILER.dll"]