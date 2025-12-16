using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms; // FolderBrowserDialog (WinForms)


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

        // =========================
        //  Core: export / compare
        // =========================
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

                // Overload compatible con DacFx antiguo
                services.Extract(outputDacpacPath, dbName, appName, new Version(version));

                Log($"[{tag}] DACPAC generado: {outputDacpacPath}");
            });
        }

        private Task CompareAndGenerateOutputsAsync(string origenDacpac, string destinoDacpac, string destinoConnectionString)
        {
            return Task.Run(() =>
            {
                var origenSource = new SchemaCompareDacpacEndpoint(origenDacpac);
                var destinoSource = new SchemaCompareDacpacEndpoint(destinoDacpac);

                var comparison = new SchemaComparison(origenSource, destinoSource);
                comparison.Options.IgnoreWhitespace = true;

                var result = comparison.Compare();

                // 1) Lista de objetos diferentes
                var sb = new StringBuilder();
                sb.AppendLine($"Diferencias detectadas: {result.Differences.Count()}");
                sb.AppendLine($"Origen DACPAC: {origenDacpac}");
                sb.AppendLine($"Destino DACPAC: {destinoDacpac}");
                sb.AppendLine(new string('-', 80));

                foreach (var diff in result.Differences)
                    sb.AppendLine($"{diff.Name} | {diff.DifferenceType}");

                var diffPath = Path.Combine(_salidasDir, "objetos_diferentes.txt");
                File.WriteAllText(diffPath, sb.ToString(), Encoding.UTF8);
                Log($"Diferencias guardadas: {diffPath}");

                // 2) Script para igualar DESTINO a ORIGEN (DACPAC(origen) -> DB(destino))
                var dacpac = DacPackage.Load(origenDacpac);

                var deployOptions = new DacDeployOptions
                {
                    CreateNewDatabase = false
                };

                var dacServicesDestino = new DacServices(destinoConnectionString);
                dacServicesDestino.Message += (s, e) => Log($"[DEPLOY] {e.Message}");

                string targetDbName = GetDatabaseNameFromConnectionString(destinoConnectionString);
                if (string.IsNullOrWhiteSpace(targetDbName))
                    throw new Exception("No pude leer el nombre de la BBDD destino. Asegura 'Database=...'. ");

                var script = dacServicesDestino.GenerateDeployScript(dacpac, targetDbName, deployOptions);

                var scriptPath = Path.Combine(_salidasDir, "script_igualar_destino_a_origen.sql");
                File.WriteAllText(scriptPath, script, Encoding.UTF8);
                Log($"Script de igualación guardado: {scriptPath}");
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
        private void ExportCreateTableScripts(string dacpacPath, string outputFolder, string tag)
        {
            Directory.CreateDirectory(outputFolder);

            using var model = new TSqlModel(dacpacPath);

            // Tablas (incluye dbo, IS, etc.)
            var tables = model.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass);

            foreach (var t in tables)
            {
                var schema = t.Name.Parts.Count > 1 ? t.Name.Parts[0] : "dbo";
                var name = t.Name.Parts.Count > 1 ? t.Name.Parts[1] : t.Name.Parts[0];

                // Genera el script del objeto
                var script = t.GetScript();

                // Limpieza básica
                script = script.Replace("\r\n", "\n");

                var fileName = $"{schema}.{name}.sql";
                var filePath = Path.Combine(outputFolder, fileName);

                File.WriteAllText(filePath, script, Encoding.UTF8);
            }

            Log($"[{tag}] CREATE TABLE scripts generados en: {outputFolder}");
        }

    }

}
