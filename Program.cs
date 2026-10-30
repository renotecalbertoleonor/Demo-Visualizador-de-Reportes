using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community; // Comprobar elegibilidad de esta licencia para su uso.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<ReportRepository>();
builder.Services.AddScoped<AuthStore>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
// Setup only when RRV_BOOTSTRAP_PASSWORD is provided. SQL script must already be applied.
using (var scope=app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AuthStore>().Bootstrap(CancellationToken.None);
// Sin login: las consultas y exportaciones son de solo lectura.
// Administracion de usuarios queda limitada al propio equipo para no exponer
// creacion de administradores por la red al quitar la autenticacion.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/users"))
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Administracion de usuarios: acceso local solamente" });
            return;
        }
        // Requerido por el endpoint existente de edicion; no autentica usuarios.
        context.Items["identity"] = new UserIdentity(0, "Administrador local", "local", "Administrador", new List<string> { "users.manage" });
    }
    await next();
});
app.MapGet("/api/access/local", (HttpContext ctx) =>
    Results.Ok(new { userManagement = ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) }));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", store = "SQUARE ONE", views = ReportRepository.AllowedViews }));
// Dashboard: sumar por vista directamente en SQL, sin límite TOP(50000).
// IMPORTANTE: las tres vistas se contabilizan de manera aditiva; si comparten
// registros, esos registros aparecerán más de una vez en el total consolidado.
app.MapGet("/api/dashboard/summary", async (ReportRepository repo, CancellationToken ct) =>
{
    try { return Results.Ok(await repo.GetDashboardSummaryAsync(ct)); }
    catch (Exception e) { app.Logger.LogError(e, "Dashboard summary failed"); return Results.Problem("No fue posible consultar las tres vistas. Revise los logs.", statusCode: 500); }
});
app.MapGet("/api/report", async (HttpContext context, ReportRepository repo, CancellationToken ct) =>
{
    try { return Results.Ok(await repo.GetReportAsync(ReportFilter.ParseQuery(context.Request.Query), ct)); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (Exception e) { app.Logger.LogError(e, "Report failed"); return Results.Problem("No se pudo consultar el reporte. Revise conexión, vistas y logs.", statusCode: 500); }
});
app.MapGet("/api/export/{format}", async (string format, HttpContext context, ReportRepository repo, CancellationToken ct) =>
{
    try
    {
        var report = await repo.GetReportAsync(ReportFilter.ParseQuery(context.Request.Query), ct);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        if (format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Cuadres");
            var cols = new[] { "ID empleado", "Empleado", "Cuenta", "Débito", "Crédito", "Saldo", "Registros" };
            for (int c = 0; c < cols.Length; c++) ws.Cell(1, c + 1).Value = cols[c];
            int r = 2;
            foreach (var x in report.Rows)
            {
                ws.Cell(r, 1).Value = x.EmployeeId; ws.Cell(r, 2).Value = x.Employee;
                ws.Cell(r, 3).Value = x.Account;
                ws.Cell(r, 4).Value = x.Debit; ws.Cell(r, 5).Value = x.Credit;
                ws.Cell(r, 6).Value = x.Balance; ws.Cell(r, 7).Value = x.Count;
                r++;
            }
            ws.Range(1, 1, 1, 7).Style.Font.Bold = true;
            ws.Range(1, 1, 1, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#263C80");
            ws.Range(1, 1, 1, 7).Style.Font.FontColor = XLColor.White;
            if (r > 2) ws.Range(2, 4, r - 1, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Columns().AdjustToContents();
            using var ms = new MemoryStream(); wb.SaveAs(ms);
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"SquareOne_{stamp}.xlsx");
        }
        if (format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape()); page.Margin(24);
                page.Header().Text("SQUARE ONE | Reporte de cuadres").Bold().FontSize(18).FontColor("#263C80");
                page.Content().PaddingTop(12).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1); });
                    t.Header(h => { foreach (var label in new[] { "ID", "Empleado", "Cuenta", "Débito", "Crédito", "Saldo" }) h.Cell().Background("#263C80").Padding(5).Text(label).FontColor(Colors.White).Bold().FontSize(9); });
                    foreach (var x in report.Rows)
                    {
                        foreach (var v in new[] { x.EmployeeId, x.Employee, x.Account, x.Debit.ToString("N2"), x.Credit.ToString("N2"), x.Balance.ToString("N2") })
                            t.Cell().BorderBottom(0.3f).BorderColor("#DDDDDD").Padding(4).Text(v ?? "").FontSize(8);
                    }
                });
                page.Footer().AlignRight().Text($"Totales | Débito: {report.Totals.Debit:N2} | Crédito: {report.Totals.Credit:N2} | Saldo: {report.Totals.Balance:N2}").FontSize(9);
            })).GeneratePdf();
            return Results.File(bytes, "application/pdf", $"SquareOne_{stamp}.pdf");
        }
        return Results.BadRequest(new { error = "Formato permitido: xlsx o pdf" });
    }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (Exception e) { app.Logger.LogError(e, "Export failed"); return Results.Problem("No se pudo exportar. Revise los logs.", statusCode: 500); }
});
app.Run();

record ReportFilter(string View, DateTime? From, DateTime? To, string? Employee, string? Account, string GroupBy)
{
    public static ReportFilter ParseQuery(Microsoft.AspNetCore.Http.IQueryCollection q)
    {
        string Get(string key, string fallback = "") => q.TryGetValue(key, out var v) ? v.ToString() : fallback;
        DateTime? ParseDate(string v) => string.IsNullOrWhiteSpace(v) ? null : DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new ArgumentException("Fecha inválida: use yyyy-MM-dd");
        var f = ParseDate(Get("from")); var t = ParseDate(Get("to"));
        if (f > t) throw new ArgumentException("Fecha inicial posterior a fecha final");
        var group = Get("groupBy", "account").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        if (!new[] { "employee", "account", "detail", "employeeaccount" }.Contains(group))
            throw new ArgumentException("Agrupación inválida. Valores: detail, account, employee, employeeAccount.");
        return new ReportFilter(Get("view", "View_Cuadre_0.1"), f, t, Get("employee"), Get("account"), group);
    }
}
record ReportRow(string EmployeeId, string Employee, string Account, decimal Debit, decimal Credit, decimal Balance, int Count);
record ReportTotals(decimal Debit, decimal Credit, decimal Balance, int Count);
record ReportResult(string View, string GroupBy, string? DateColumn, int SourceRows, int ReturnedRows, ReportTotals Totals, List<ReportRow> Rows, string[] Warnings);

sealed class ReportRepository(IConfiguration config)
{
    public static readonly string[] AllowedViews = ["View_Cuadre_0.1", "View_Cuadre_0.1_SC", "View_Cuadre_0.1_SG"];
    // Consolida las tres vistas sin descargar filas individuales al Web.
    // Devuelve también los subtotales por vista para auditar los importes.
    public async Task<object> GetDashboardSummaryAsync(CancellationToken ct)
    {
        var connection = config.GetConnectionString("SquareOne");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Falta ConnectionStrings:SquareOne");
        await using var db = new SqlConnection(connection);
        await db.OpenAsync(ct);
        var byView = new List<object>();
        var accounts = new Dictionary<string, (decimal Debit, decimal Credit)>(StringComparer.OrdinalIgnoreCase);
        long totalRows = 0;
        decimal totalDebit = 0, totalCredit = 0;
        foreach (var view in AllowedViews)
        {
            var safeView = "[dbo].[" + view.Replace("]", "]]", StringComparison.Ordinal) + "]";
            var schema = new DataTable();
            await using (var check = new SqlCommand($"SELECT TOP (0) * FROM {safeView}", db))
            await using (var rd = await check.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct)) schema.Load(rd);
            var names = schema.Columns.Cast<DataColumn>().Select(x => x.ColumnName).ToArray();
            string? Match(params string[] options) => names.FirstOrDefault(n => options.Any(o => string.Equals(n, o, StringComparison.OrdinalIgnoreCase)));
            var accountCol = Match("Cuenta", "NombreCuenta", "Account", "DescripcionCuenta");
            var debitCol = Match("Debito", "Débito", "Debit");
            var creditCol = Match("Credito", "Crédito", "Credit");
            var balanceCol = Match("Saldo", "Balance");
            if (debitCol is null && creditCol is null && balanceCol is null)
                throw new InvalidOperationException($"La vista {view} no tiene columnas de montos reconocidas.");
            string Col(string col) => "[" + col.Replace("]", "]]", StringComparison.Ordinal) + "]";
            string Amount(string? col) => col is null ? "CAST(0 AS decimal(38,4))" : $"COALESCE(TRY_CONVERT(decimal(28,4), {Col(col)}), 0)";
            // Si solo existe Saldo, representarlo como diferencia entre crédito y débito.
            var debitExpr = debitCol is not null ? Amount(debitCol) : balanceCol is not null && creditCol is null ? $"CASE WHEN {Amount(balanceCol)} < 0 THEN -{Amount(balanceCol)} ELSE 0 END" : "CAST(0 AS decimal(38,4))";
            var creditExpr = creditCol is not null ? Amount(creditCol) : balanceCol is not null && debitCol is null ? $"CASE WHEN {Amount(balanceCol)} > 0 THEN {Amount(balanceCol)} ELSE 0 END" : "CAST(0 AS decimal(38,4))";
            var accountExpr = accountCol is null ? "N'Sin cuenta'" : $"COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(300), {Col(accountCol)}))), N''), N'Sin cuenta')";
            var sql = $"SELECT {accountExpr} AS Cuenta, COUNT_BIG(*) AS Registros, SUM({debitExpr}) AS Debito, SUM({creditExpr}) AS Credito FROM {safeView} GROUP BY {accountExpr}";
            long rows = 0; decimal debit = 0, credit = 0;
            await using (var cmd = new SqlCommand(sql, db) { CommandTimeout = 180 })
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    var name = rd.GetString(0);
                    var count = rd.GetInt64(1);
                    var d = rd.IsDBNull(2) ? 0 : rd.GetDecimal(2);
                    var c = rd.IsDBNull(3) ? 0 : rd.GetDecimal(3);
                    rows += count; debit += d; credit += c;
                    accounts.TryGetValue(name, out var previous);
                    accounts[name] = (previous.Debit + d, previous.Credit + c);
                }
            }
            totalRows += rows; totalDebit += debit; totalCredit += credit;
            byView.Add(new { view, sourceRows = rows, debit, credit, balance = credit - debit });
        }
        return new {
            sourceRows = totalRows,
            totals = new { debit = totalDebit, credit = totalCredit, balance = totalCredit - totalDebit },
            views = byView,
            rows = accounts.Select(x => new { account = x.Key, debit = x.Value.Debit, credit = x.Value.Credit })
                .OrderBy(x => x.account).ToArray(),
            aggregation = "suma_por_vista",
            note = "Consolidación aditiva de tres vistas; comprobar si existen registros compartidos entre ellas."
        };
    }

    public async Task<ReportResult> GetReportAsync(ReportFilter filter, CancellationToken ct)
    {
        if (!AllowedViews.Contains(filter.View, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Vista no permitida");
        var connection = config.GetConnectionString("SquareOne");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Falta ConnectionStrings:SquareOne");
        await using var db = new SqlConnection(connection);
        await db.OpenAsync(ct);
        var viewSql = $"[dbo].[{filter.View.Replace("]", "]]", StringComparison.Ordinal)}]";
        var schema = new DataTable();
        await using (var cmd = new SqlCommand($"SELECT TOP (0) * FROM {viewSql}", db))
        await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct)) { schema.Load(reader); }
        var names = schema.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        string? Match(params string[] opts) => names.FirstOrDefault(n => opts.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)));
        var idCol = Match("IDEmpleado", "IdEmpleado", "EmployeeID", "Id_Empleado");
        var empCol = Match("NombreEmpleado", "Empleado", "EmployeeName", "Nombre_Empleado");
        var accCol = Match("Cuenta", "NombreCuenta", "Account", "DescripcionCuenta");
        var debitCol = Match("Debito", "Débito", "Debit");
        var creditCol = Match("Credito", "Crédito", "Credit");
        var balanceCol = Match("Saldo", "Balance");
        var dateCol = Match("Fecha", "FechaNegocio", "FechaVenta", "BusinessDate", "Date", "FechaTransaccion", "FechaOperacion");
        if (debitCol is null && creditCol is null && balanceCol is null) throw new ArgumentException("La vista no contiene columnas de Débito, Crédito o Saldo. Revise estructura SQL.");
        if ((filter.From.HasValue || filter.To.HasValue) && dateCol is null) throw new ArgumentException("No se detectó columna de fecha en esta vista. Confirme el nombre de la columna.");
        string Col(string c) => "[" + c.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var sql = $"SELECT TOP (50000) * FROM {viewSql} WHERE 1=1";
        if (filter.From.HasValue) sql += $" AND {Col(dateCol!)} >= @from";
        if (filter.To.HasValue) sql += $" AND {Col(dateCol!)} < @to";
        if (!string.IsNullOrWhiteSpace(filter.Employee) && (idCol is not null || empCol is not null))
            sql += " AND (" + string.Join(" OR ", new[] { idCol, empCol }.Where(x => x is not null).Select(x => $"CONVERT(nvarchar(200), {Col(x!)}) LIKE @employee")) + ")";
        if (!string.IsNullOrWhiteSpace(filter.Account) && accCol is not null) sql += $" AND CONVERT(nvarchar(200), {Col(accCol)}) LIKE @account";
        using var command = new SqlCommand(sql, db) { CommandTimeout = 120 };
        if (filter.From.HasValue) command.Parameters.Add("@from", SqlDbType.DateTime2).Value = filter.From.Value;
        if (filter.To.HasValue) command.Parameters.Add("@to", SqlDbType.DateTime2).Value = filter.To.Value.AddDays(1);
        if (!string.IsNullOrWhiteSpace(filter.Employee)) command.Parameters.Add("@employee", SqlDbType.NVarChar, 256).Value = $"%{filter.Employee.Trim()}%";
        if (!string.IsNullOrWhiteSpace(filter.Account)) command.Parameters.Add("@account", SqlDbType.NVarChar, 256).Value = $"%{filter.Account.Trim()}%";
        var items = new List<ReportRow>();
        await using var rd = await command.ExecuteReaderAsync(ct);
        int Index(string? name) => name is null ? -1 : rd.GetOrdinal(name);
        var iid = Index(idCol); var ie = Index(empCol); var ia = Index(accCol); var ideb = Index(debitCol); var icr = Index(creditCol); var ibal = Index(balanceCol);
        string S(int i) => i < 0 || rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        decimal N(int i)
        {
            if (i < 0 || rd.IsDBNull(i)) return 0;
            var val = rd.GetValue(i);
            try { return Convert.ToDecimal(val, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        while (await rd.ReadAsync(ct)) items.Add(new ReportRow(S(iid), S(ie), S(ia), N(ideb), N(icr), N(ibal), 1));
        // Agrupa únicamente la información consultada; no modifica datos de SQL Server.
        // El valor Count conserva la cantidad de registros originales de cada grupo.
        List<ReportRow> result = filter.GroupBy switch
        {
            "detail" => items,
            "account" => items.GroupBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ReportRow("", "", g.Key, g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Sum(x => x.Balance), g.Count())).ToList(),
            "employeeaccount" => items.GroupBy(x => new { x.EmployeeId, x.Employee, x.Account })
                .Select(g => new ReportRow(g.Key.EmployeeId, g.Key.Employee, g.Key.Account,
                    g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Sum(x => x.Balance), g.Count())).ToList(),
            "employee" => items.GroupBy(x => (x.EmployeeId, x.Employee))
                .Select(g => new ReportRow(g.Key.EmployeeId, g.Key.Employee, "Todas las cuentas",
                    g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Sum(x => x.Balance), g.Count())).ToList(),
            _ => throw new ArgumentException("Agrupación no soportada")
        };
        result = result.OrderBy(x => x.Employee).ThenBy(x => x.Account).ToList();
        var totals = new ReportTotals(items.Sum(x => x.Debit), items.Sum(x => x.Credit), items.Sum(x => x.Balance), items.Count);
        var warnings = new List<string>();
        if (items.Count == 50000) warnings.Add("Se alcanzó el límite de 50,000 registros. Reduzca el intervalo de fechas.");
        if (idCol is null && empCol is null) warnings.Add("No se detectaron columnas de empleado; revise la vista.");
        if (accCol is null && (filter.GroupBy == "account" || filter.GroupBy == "employeeaccount"))
            warnings.Add("No se detectó la columna Cuenta; las cuentas aparecerán en blanco.");
        return new ReportResult(filter.View, filter.GroupBy, dateCol, items.Count, result.Count, totals, result, warnings.ToArray());
    }
}
