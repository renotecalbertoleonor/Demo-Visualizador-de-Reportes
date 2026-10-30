# Renotec Data Integrator — SQUARE ONE
Reconstrucción visual y funcional basada en la captura del piloto del 21/10/2026. No es recuperación byte a byte del ZIP original.

## Iniciar
```powershell
cd .\SquareOneWeb
dotnet restore
dotnet run
```
Abrir http://127.0.0.1:8095

Configurar ConnectionStrings:SquareOne en appsettings.json.
Solo consulta vistas SQL Server. No realiza INSERT/UPDATE/DELETE.
Vistas: View_Cuadre_0.1, View_Cuadre_0.1_SC, View_Cuadre_0.1_SG.
Filtros y agrupaciones dependen de los nombres de columnas identificados en las vistas.
La columna Fecha / período en el detalle indica el intervalo solicitado, no la fecha individual del registro.
El campo Nombre empleado se utiliza como alternativa a IDEmpleado cuando este está vacío.
Dashboard, Tiendas, Exportaciones y Configuración son paneles informativos en este piloto.
