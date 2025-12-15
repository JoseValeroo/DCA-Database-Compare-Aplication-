using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace DCA
{
    public partial class MainWindow : Window
    {
        private readonly string _baseDir;
        private readonly string _origenDir;
        private readonly string _destinoDir;
        private readonly string _salidasDir;
        private readonly string _logsDir;

        private string OrigenDacpacPath => Path.Combine(_origenDir, "schema.dacpac");
        private string DestinoDacpacPath => Path.Combine(_destinoDir, "schema.dacpac");

        public MainWindow()
        {
            InitializeComponent();

            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _origenDir = Path.Combine(_baseDir, "origen");
            _destinoDir = Path.Combine(_baseDir, "destino");
            _salidasDir = Path.Combine(_baseDir, "salidas");
            _logsDir = Path.Combine(_baseDir, "logs");

            Directory.CreateDirectory(_origenDir);
            Directory.CreateDirectory(_destinoDir);
            Directory.CreateDirectory(_salidasDir);
            Directory.CreateDirectory(_logsDir);

            Log($"BaseDir: {_baseDir}");
            Log("Carpetas preparadas: origen, destino, salidas, logs");
        }

        private async void ExportOrigenButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSafe(async () =>
            {
                string cs = OrigenConnectionStringTextBox.Text.Trim();
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
                string cs = DestinoConnectionStringTextBox.Text.Trim();
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

                Estado("Comparando esquemas...");
                await CompareAndGenerateOutputsAsync(OrigenDacpacPath, DestinoDacpacPath);
                Estado("Comparación finalizada. Revisa salidas\\");
            });
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
                string dbName = $"schema_{tag}";
                string version = "1.0.0.0";

                // Overload compatible con DacFx antiguo
                services.Extract(outputDacpacPath, dbName, appName, new Version(version));

                Log($"[{tag}] DACPAC generado: {outputDacpacPath}");
            });
        }


        private Task CompareAndGenerateOutputsAsync(string origenDacpac, string destinoDacpac)
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
                sb.AppendLine($"Diferencias detectadas: {result.Differences.Count}");
                sb.AppendLine($"Origen: {origenDacpac}");
                sb.AppendLine($"Destino: {destinoDacpac}");
                sb.AppendLine(new string('-', 80));

                foreach (var diff in result.Differences)
                    sb.AppendLine($"{diff.Name} | {diff.DifferenceType}");

                var diffPath = Path.Combine(_salidasDir, "objetos_diferentes.txt");
                File.WriteAllText(diffPath, sb.ToString(), Encoding.UTF8);
                Log($"Diferencias guardadas: {diffPath}");

                // 2) Script para igualar DESTINO a ORIGEN (Deploy script: DACPAC -> DB)
                var destinoConnectionString = Dispatcher.Invoke(() => DestinoConnectionStringTextBox.Text.Trim());
                if (string.IsNullOrWhiteSpace(destinoConnectionString))
                    throw new Exception("Cadena de conexión DESTINO vacía (necesaria para generar script de igualación).");

                var dacpac = DacPackage.Load(origenDacpac);

                var deployOptions = new DacDeployOptions
                {
                    // Esto hace que el script sea incremental (no te hace drop&create completo)
                    CreateNewDatabase = false
                };

                var dacServicesDestino = new DacServices(destinoConnectionString);
                dacServicesDestino.Message += (s, e) => Log($"[DEPLOY] {e.Message}");

                // Nombre de DB destino: lo podemos sacar del connection string “a mano”
                // pero DacFx lo necesita; lo más simple: que venga en la cadena (Database=...)
                string targetDbName = GetDatabaseNameFromConnectionString(destinoConnectionString);
                if (string.IsNullOrWhiteSpace(targetDbName))
                    throw new Exception("No pude leer el nombre de la BBDD destino desde la cadena. Asegura 'Database=...'. ");

                var script = dacServicesDestino.GenerateDeployScript(
                dacpac,
                targetDbName,
                deployOptions
                );


                var scriptPath = Path.Combine(_salidasDir, "script_igualar_destino_a_origen.sql");
                File.WriteAllText(scriptPath, script, Encoding.UTF8);
                Log($"Script de igualación guardado: {scriptPath}");
            });
        }
        private static string GetDatabaseNameFromConnectionString(string cs)
        {
            // parse sencillo para "Database=xxx" o "Initial Catalog=xxx"
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
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                File.AppendAllText(Path.Combine(_logsDir, "app.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* no matar la app por logging */ }
        }
    }
}
