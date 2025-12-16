using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms; // FolderBrowserDialog (WinForms)
using System.Windows.Input;

namespace DCA
{
    public partial class MainWindow : Window
    {
        // YA NO readonly: ahora se puede cambiar la carpeta de trabajo
        private string _baseDir = "";
        private string _origenDir = "";
        private string _destinoDir = "";
        private string _salidasDir = "";
        private string _logsDir = "";

        private string OrigenDacpacPath => Path.Combine(_origenDir, "schema.dacpac");
        private string DestinoDacpacPath => Path.Combine(_destinoDir, "schema.dacpac");

        public MainWindow()
        {
            InitializeComponent();

            // Por defecto: carpeta del exe
            SetWorkingDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        // =========================
        //  UI: Elegir carpeta trabajo
        // =========================
        private void PickWorkDirButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Selecciona la carpeta donde se guardarán origen, destino, salidas y logs",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            var result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK &&
                !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                SetWorkingDirectory(dialog.SelectedPath);
            }
        }

        private void SetWorkingDirectory(string baseDir)
        {
            _baseDir = baseDir;

            _origenDir = Path.Combine(_baseDir, "origen");
            _destinoDir = Path.Combine(_baseDir, "destino");
            _salidasDir = Path.Combine(_baseDir, "salidas");
            _logsDir = Path.Combine(_baseDir, "logs");

            Directory.CreateDirectory(_origenDir);
            Directory.CreateDirectory(_destinoDir);
            Directory.CreateDirectory(_salidasDir);
            Directory.CreateDirectory(_logsDir);

            if (WorkDirTextBox != null)
                WorkDirTextBox.Text = _baseDir;

            Log($"Carpeta de trabajo: {_baseDir}");
            Log("Carpetas preparadas: origen, destino, salidas, logs");
        }

        // =========================
        //  Botones
        // =========================
        private async void ExportOrigenButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSafe(async () =>
            {
                string cs = NormalizeConnectionString(OrigenConnectionStringTextBox.Text);
                if (string.IsNullOrWhiteSpace(cs)) throw new Exception("Cadena de conexión ORIGEN vacía.");

                Estado("Exportando ORIGEN...");
                await ExportDacpacAsync(cs, OrigenDacpacPath, "ORIGEN");
                Estado("OK ORIGEN exportado.");
            });
        }

        private async void ExportDestinoButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSafe(async () =>
            {
                string cs = NormalizeConnectionString(DestinoConnectionStringTextBox.Text);
                if (string.IsNullOrWhiteSpace(cs)) throw new Exception("Cadena de conexión DESTINO vacía.");

                Estado("Exportando DESTINO...");
                await ExportDacpacAsync(cs, DestinoDacpacPath, "DESTINO");
                Estado("OK DESTINO exportado.");
            });
        }

        private async void CompararButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSafe(async () =>
            {
                if (!File.Exists(OrigenDacpacPath))
                    throw new Exception($"No existe el dacpac de ORIGEN: {OrigenDacpacPath}");

                if (!File.Exists(DestinoDacpacPath))
                    throw new Exception($"No existe el dacpac de DESTINO: {DestinoDacpacPath}");

                var destinoCs = NormalizeConnectionString(DestinoConnectionStringTextBox.Text);
                if (string.IsNullOrWhiteSpace(destinoCs))
                    throw new Exception("Cadena de conexión DESTINO vacía (necesaria para generar script).");

                Estado("Comparando esquemas...");
                await CompareAndGenerateOutputsAsync(OrigenDacpacPath, DestinoDacpacPath, destinoCs);
                Estado("Comparación finalizada. Revisa salidas\\");
            });
        }
        private void ExportExtendedPropertiesSql(string connectionString, string outputSqlPath, string tag)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- Extended properties extraídas desde la BBDD");
            sb.AppendLine("-- Generado por DCA");
            sb.AppendLine();

            using var cn = new SqlConnection(connectionString);
            cn.Open();

            // Tabla + Columna
            var cmd = cn.CreateCommand();
            cmd.CommandText = @"
            SELECT
                s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName,
                ep.name AS PropertyName,
                CAST(ep.value AS NVARCHAR(MAX)) AS PropertyValue
            FROM sys.extended_properties ep
            JOIN sys.tables t ON ep.major_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.columns c ON c.object_id = t.object_id AND c.column_id = ep.minor_id
            WHERE ep.class = 1
            ORDER BY s.name, t.name, c.column_id, ep.name;
            ";
            using var rd = cmd.ExecuteReader();

            string? lastKey = null;

            while (rd.Read())
            {
                var schema = rd.GetString(0);
                var table = rd.GetString(1);
                var col = rd.IsDBNull(2) ? null : rd.GetString(2);
                var propName = rd.GetString(3);
                var propValue = rd.IsDBNull(4) ? "" : rd.GetString(4);

                var key = $"{schema}.{table}";
                if (key != lastKey)
                {
                    sb.AppendLine();
                    sb.AppendLine($"-- [{schema}].[{table}]");
                    lastKey = key;
                }

                if (string.IsNullOrWhiteSpace(col))
                {
                    sb.AppendLine($@"
        EXEC sys.sp_addextendedproperty
          @name = N'{EscapeSql(propName)}',
          @value = N'{EscapeSql(propValue)}',
          @level0type = N'SCHEMA', @level0name = N'{EscapeSql(schema)}',
          @level1type = N'TABLE',  @level1name = N'{EscapeSql(table)}';
        GO");
                }
                else
                {
                    sb.AppendLine($@"
        EXEC sys.sp_addextendedproperty
          @name = N'{EscapeSql(propName)}',
          @value = N'{EscapeSql(propValue)}',
          @level0type = N'SCHEMA', @level0name = N'{EscapeSql(schema)}',
          @level1type = N'TABLE',  @level1name = N'{EscapeSql(table)}',
          @level2type = N'COLUMN', @level2name = N'{EscapeSql(col)}';
        GO");
                }
            }

            File.WriteAllText(outputSqlPath, sb.ToString(), Encoding.UTF8);
            Log($"[{tag}] Extended properties guardadas en: {outputSqlPath}");
        }

        // =========================
        //  Core: export / compare
        // =========================
        private void ExportCreateTableScripts(string dacpacPath, string outputFolder, string tag)
        {
            Directory.CreateDirectory(outputFolder);

            using var model = new TSqlModel(dacpacPath);
            var tables = model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass).ToList();

            foreach (var t in tables)
            {
                var schema = t.Name.Parts.Count > 1 ? t.Name.Parts[0] : "dbo";
                var tableName = t.Name.Parts.Count > 1 ? t.Name.Parts[1] : t.Name.Parts[0];

                var sb = new StringBuilder();
                sb.AppendLine(t.GetScript());
                sb.AppendLine();
                sb.AppendLine("GO");
                sb.AppendLine();

                var fileName = $"{schema}.{tableName}.sql";
                File.WriteAllText(Path.Combine(outputFolder, fileName), sb.ToString(), Encoding.UTF8);
            }

            Log($"[{tag}] CREATE TABLE scripts generados en: {outputFolder}");
        }


        private static string EscapeSql(string s) => (s ?? "").Replace("'", "''");

        private static string SqlLiteral(object? value)
        {
            if (value == null) return "NULL";

            // lo más habitual en extended props es string
            if (value is string str) return $"N'{EscapeSql(str)}'";

            // bool / números (por si acaso)
            if (value is bool b) return b ? "1" : "0";
            if (value is int or long or short or byte) return value.ToString()!;
            if (value is decimal or double or float) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

            // fallback: a string
            return $"N'{EscapeSql(value.ToString() ?? "")}'";
        }
        private Task CompareAndGenerateOutputsAsync(string origenDacpac, string destinoDacpac, string destinoConnectionString)
        {
            return Task.Run(() =>
            {
                // RUTAS
                var origenTablasDir = Path.Combine(_origenDir, "tablas");
                var destinoTablasDir = Path.Combine(_destinoDir, "tablas");
                var origenEpPath = Path.Combine(_origenDir, "extended_properties.sql");
                var destinoEpPath = Path.Combine(_destinoDir, "extended_properties.sql");

                Directory.CreateDirectory(_salidasDir);

                // =========================
                //  A) DDL (CREATE TABLE) DIFF
                // =========================
                var ddlDiffTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var oFiles = Directory.Exists(origenTablasDir)
                    ? Directory.GetFiles(origenTablasDir, "*.sql", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();

                var dFiles = Directory.Exists(destinoTablasDir)
                    ? Directory.GetFiles(destinoTablasDir, "*.sql", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();

                var oMap = oFiles.ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
                var dMap = dFiles.ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

                var allFileNames = new HashSet<string>(oMap.Keys, StringComparer.OrdinalIgnoreCase);
                allFileNames.UnionWith(dMap.Keys);

                foreach (var fn in allFileNames)
                {
                    var inO = oMap.TryGetValue(fn, out var of);
                    var inD = dMap.TryGetValue(fn, out var df);

                    var tableKey = NormalizeTableKeyFromFileName(fn); // "schema.tabla"

                    // Si falta en uno, es diferente
                    if (!inO || !inD)
                    {
                        ddlDiffTables.Add(tableKey);
                        continue;
                    }

                    var oTxt = NormalizeSqlForCompare(File.ReadAllText(of!, Encoding.UTF8));
                    var dTxt = NormalizeSqlForCompare(File.ReadAllText(df!, Encoding.UTF8));

                    if (!string.Equals(Sha256(oTxt), Sha256(dTxt), StringComparison.OrdinalIgnoreCase))
                        ddlDiffTables.Add(tableKey);
                }

                Log($"[COMPARE] Tablas con DDL distinto: {ddlDiffTables.Count}");

                // =========================
                //  B) EXTENDED PROPS DIFF
                // =========================
                var epDiffTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var oEp = ParseExtendedPropertiesFile(origenEpPath);
                var dEp = ParseExtendedPropertiesFile(destinoEpPath);

                var allEpKeys = new HashSet<EpKey>(oEp.Keys);
                allEpKeys.UnionWith(dEp.Keys);

                foreach (var k in allEpKeys)
                {
                    var inO = oEp.TryGetValue(k, out var ov);
                    var inD = dEp.TryGetValue(k, out var dv);

                    if (!inO || !inD || !string.Equals(ov, dv, StringComparison.Ordinal))
                    {
                        epDiffTables.Add($"{k.Schema}.{k.Table}");
                    }
                }

                Log($"[COMPARE] Tablas con EP distinto: {epDiffTables.Count}");

                // =========================
                //  C) LISTA FINAL (lo que tú quieres)
                // =========================
                var allDiff = new HashSet<string>(ddlDiffTables, StringComparer.OrdinalIgnoreCase);
                allDiff.UnionWith(epDiffTables);

                var report = new StringBuilder();
                report.AppendLine("== TABLAS DIFERENTES ==");
                report.AppendLine($"DDL distintos: {ddlDiffTables.Count}");
                report.AppendLine($"EP distintos:  {epDiffTables.Count}");
                report.AppendLine($"TOTAL:        {allDiff.Count}");
                report.AppendLine(new string('-', 80));

                foreach (var t in allDiff.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var hasDdl = ddlDiffTables.Contains(t);
                    var hasEp = epDiffTables.Contains(t);

                    var tag = hasDdl && hasEp ? "[DDL+EP]" :
                              hasDdl ? "[DDL]" :
                              "[EP]";

                    report.AppendLine($"{tag} {t}");
                }

                var outPath = Path.Combine(_salidasDir, "tablas_diferentes.txt");
                File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);
                Log($"[COMPARE] Lista final guardada: {outPath}");

                // (Opcional) Mantener tu comparación DACPAC y deploy script
                // Si lo quieres, lo dejamos como extra:
                try
                {
                    var origenSource = new SchemaCompareDacpacEndpoint(origenDacpac);
                    var destinoSource = new SchemaCompareDacpacEndpoint(destinoDacpac);
                    var comparison = new SchemaComparison(origenSource, destinoSource);
                    comparison.Options.IgnoreWhitespace = true;

                    var result = comparison.Compare();

                    var sb = new StringBuilder();
                    sb.AppendLine($"Diferencias detectadas (DACPAC compare): {result.Differences.Count()}");
                    foreach (var diff in result.Differences)
                        sb.AppendLine($"{diff.Name} | {diff.DifferenceType}");

                    File.WriteAllText(Path.Combine(_salidasDir, "objetos_diferentes_dacpac.txt"), sb.ToString(), Encoding.UTF8);

                    var dacpac = DacPackage.Load(origenDacpac);
                    var deployOptions = new DacDeployOptions { CreateNewDatabase = false };
                    var dacServicesDestino = new DacServices(destinoConnectionString);

                    var targetDbName = GetDatabaseNameFromConnectionString(destinoConnectionString);
                    if (!string.IsNullOrWhiteSpace(targetDbName))
                    {
                        var script = dacServicesDestino.GenerateDeployScript(dacpac, targetDbName, deployOptions);
                        File.WriteAllText(Path.Combine(_salidasDir, "script_igualar_destino_a_origen.sql"), script, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Log("[COMPARE] Aviso: deploy script/dacpac compare falló: " + ex.Message);
                }
            });
        }

        // =========================
        //  Helpers
        // =========================
        private static string GetDatabaseNameFromConnectionString(string cs)
        {
            var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) continue;

                var key = kv[0].Trim().ToLowerInvariant();
                var val = kv[1].Trim();

                if (key == "database" || key == "initial catalog")
                    return val;
            }
            return "";
        }
        private Task ExportDacpacAsync(string connectionString, string outputDacpacPath, string tag)
        {
            return Task.Run(() =>
            {
                var services = new DacServices(connectionString);
                services.Message += (s, e) => Log($"[{tag}] {e.Message}");

                if (File.Exists(outputDacpacPath))
                    File.Delete(outputDacpacPath);

                string appName = "DCA";
                string dbName = GetDatabaseNameFromConnectionString(connectionString);
                if (string.IsNullOrWhiteSpace(dbName))
                    throw new Exception("No pude leer el nombre de la BBDD desde la cadena (Database=...).");

                string version = "1.0.0.0";

                // 1) DACPAC
                services.Extract(outputDacpacPath, dbName, appName, new Version(version));
                Log($"[{tag}] DACPAC generado: {outputDacpacPath}");

                // 2) Scripts CREATE TABLE (desde DACPAC)
                var tablasDir = Path.Combine(Path.GetDirectoryName(outputDacpacPath)!, "tablas");
                ExportCreateTableScripts(outputDacpacPath, tablasDir, tag);

                // 3) Extended properties (desde DB) en un archivo global
                var epPath = Path.Combine(Path.GetDirectoryName(outputDacpacPath)!, "extended_properties.sql");
                ExportExtendedPropertiesSql(connectionString, epPath, tag);
            });
        }

        private static string NormalizeConnectionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            // Convierte saltos de línea a ';'
            var s = raw.Replace("\r\n", ";").Replace("\n", ";").Replace("\r", ";");

            // Divide por ';', limpia espacios, quita vacíos
            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0);

            // Normaliza "Key = Value" -> "Key=Value"
            parts = parts.Select(p =>
            {
                var kv = p.Split('=', 2);
                if (kv.Length != 2) return p;
                return $"{kv[0].Trim()}={kv[1].Trim()}";
            });

            return string.Join(";", parts) + ";";
        }

        // =========================
        //  UI + logging
        // =========================
        private async Task RunSafe(Func<Task> action)
        {
            try
            {
                SetButtons(false);
                await action();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                System.Windows.MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Estado("Error (mira logs).");
            }
            finally
            {
                SetButtons(true);
            }
        }

        private void SetButtons(bool enabled)
        {
            ExportOrigenButton.IsEnabled = enabled;
            ExportDestinoButton.IsEnabled = enabled;
            CompararButton.IsEnabled = enabled;
        }

        private void Estado(string text)
        {
            Dispatcher.Invoke(() => EstadoTextBlock.Text = text);
            Log(text);
        }

        private void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(line + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });

            try
            {
                if (!string.IsNullOrWhiteSpace(_logsDir))
                    File.AppendAllText(Path.Combine(_logsDir, "app.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* no matar la app por logging */ }
        }

    private static string NormalizeSqlForCompare(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return "";
            var s = sql.Replace("\r\n", "\n").Replace("\r", "\n");

            var lines = s.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Where(l => !string.Equals(l, "GO", StringComparison.OrdinalIgnoreCase));

            return string.Join("\n", lines);
        }

        private static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private static string NormalizeTableKeyFromFileName(string fileName)
        {
            // "IS.Liquidacion.sql" -> "IS.Liquidacion"
            var n = Path.GetFileNameWithoutExtension(fileName);
            return n.Trim();
        }

        private record EpKey(string Schema, string Table, string? Column, string PropName);

        private Dictionary<EpKey, string> ParseExtendedPropertiesFile(string filePath)
        {
            var dict = new Dictionary<EpKey, string>();
            if (!File.Exists(filePath)) return dict;

            var text = File.ReadAllText(filePath, Encoding.UTF8);

            var blocks = Regex.Matches(text, @"EXEC\s+sys\.sp_addextendedproperty\s+(?<body>[\s\S]*?);",
                RegexOptions.IgnoreCase);

            foreach (Match m in blocks)
            {
                var body = m.Groups["body"].Value;

                string Get(string param)
                {
                    var mm = Regex.Match(body, @"@" + param + @"\s*=\s*(?<v>[^,\r\n]+)", RegexOptions.IgnoreCase);
                    if (!mm.Success) return "";
                    var v = mm.Groups["v"].Value.Trim();

                    // Limpia N'..' o '..'
                    if (v.StartsWith("N'") && v.EndsWith("'")) v = v[2..^1];
                    else if (v.StartsWith("'") && v.EndsWith("'")) v = v[1..^1];

                    return v.Replace("''", "'");
                }

                var propName = Get("name");
                var propValue = Get("value");

                var schema = Get("level0name");
                var table = Get("level1name");
                var column = Get("level2name");
                if (string.IsNullOrWhiteSpace(column)) column = null;

                if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(propName))
                    continue;

                dict[new EpKey(schema, table, column, propName)] = propValue;
            }

            return dict;
        }


    }

}
