using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NextDNSManager.Shared;

namespace NextDNSModernWPF;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        ApplyLanguage();
        RefreshLog();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        string managerLog = DiagnosticLog.ReadManager();
        string serviceLog = DiagnosticLog.ReadServiceEvents();
        txtDiagnostics.Text =
            "=== NextDNS Manager ===\r\n" + (string.IsNullOrWhiteSpace(managerLog) ? L("Sin registros disponibles.", "No logs available.", "Aucun journal disponible.", "Nenhum registro disponível.", "Keine Protokolle verfügbar.") + "\r\n" : managerLog) +
            "\r\n=== " + L("Servicio NextDNS Manager · eventos recientes de Aplicación", "NextDNS Manager service · recent Application events", "Service NextDNS Manager · événements récents de l’application", "Serviço NextDNS Manager · eventos recentes do Aplicativo", "NextDNS Manager-Dienst · aktuelle Anwendungsereignisse") + " ===\r\n" +
            (string.IsNullOrWhiteSpace(serviceLog) ? L("Sin entradas recientes disponibles. El servicio puede no haberse iniciado o Windows puede haber rotado el registro.", "No recent entries available. The service may not have started or Windows may have rotated the log.", "Aucune entrée récente. Le service n’a peut-être pas démarré ou Windows a remplacé le journal.", "Nenhuma entrada recente. O serviço pode não ter iniciado ou o Windows pode ter substituído o registro.", "Keine aktuellen Einträge. Der Dienst wurde möglicherweise nicht gestartet oder Windows hat das Protokoll ersetzt.") + "\r\n" : serviceLog);
        txtDiagnostics.CaretIndex = txtDiagnostics.Text.Length;
        txtDiagnostics.ScrollToEnd();
    }

    private void OpenManagerFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(DiagnosticLog.ManagerDirectory, L("No se pudo abrir la carpeta de registros de la aplicación.", "Could not open the application log folder.", "Impossible d’ouvrir le dossier des journaux.", "Não foi possível abrir a pasta de registros.", "Der Protokollordner konnte nicht geöffnet werden."));

    private static string L(string es, string en, string fr, string pt, string de) => LocalizationManager.CurrentLanguage switch
    {
        "en" => en, "fr" => fr, "pt" => pt, "de" => de, _ => es
    };

    private void ApplyLanguage()
    {
        Title = L("Registros de diagnóstico", "Diagnostic logs", "Journaux de diagnostic", "Registros de diagnóstico", "Diagnoseprotokolle");
        diagHeading.Text = L("Actividad reciente", "Recent activity", "Activité récente", "Atividade recente", "Letzte Aktivitäten");
        diagDescription.Text = L("La app conserva registros durante 30 días. Los eventos del servicio se filtran por ese período desde el registro Aplicación de Windows. No incluyen datos de perfiles ni consultas.", "The app keeps logs for 30 days. Service events are filtered to that period from the Windows Application log. They do not include profiles or queries.", "L’application conserve les journaux pendant 30 jours. Les événements du service sont filtrés sur cette période dans le journal Application de Windows. Ils ne contiennent ni profils ni requêtes.", "O aplicativo mantém registros por 30 dias. Os eventos do serviço são filtrados nesse período no log de Aplicativo do Windows. Não incluem perfis nem consultas.", "Die App bewahrt Protokolle 30 Tage lang auf. Dienstereignisse werden für diesen Zeitraum aus dem Windows-Anwendungsprotokoll gefiltert. Sie enthalten keine Profile oder Anfragen.");
        diagDays.Text = L("30 DÍAS", "30 DAYS", "30 JOURS", "30 DIAS", "30 TAGE");
        diagOutput.Text = L("Salida de diagnóstico", "Diagnostic output", "Sortie de diagnostic", "Saída de diagnóstico", "Diagnoseausgabe");
        diagReadOnly.Text = L("Solo lectura", "Read only", "Lecture seule", "Somente leitura", "Schreibgeschützt");
        diagRefresh.Content = L("Actualizar", "Refresh", "Actualiser", "Atualizar", "Aktualisieren");
        diagOpen.Content = L("Abrir registros de la app", "Open app logs", "Ouvrir les journaux", "Abrir registros", "App-Protokolle öffnen");
        diagClose.Content = L("Cerrar", "Close", "Fermer", "Fechar", "Schließen");
    }

    private void OpenFolder(string directory, string errorMessage)
    {
        try
        {
            if (!Directory.Exists(directory) || !IsRegularDirectory(directory))
                throw new IOException("La carpeta no está disponible.");
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteManager(DiagnosticEvent.DiagnosticsViewFailure, exception);
            System.Windows.MessageBox.Show(this, errorMessage, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
