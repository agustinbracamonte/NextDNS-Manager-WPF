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
        RefreshLog();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        string managerLog = DiagnosticLog.ReadManager();
        string serviceLog = DiagnosticLog.ReadServiceEvents();
        txtDiagnostics.Text =
            "=== NextDNS Manager ===\r\n" + (string.IsNullOrWhiteSpace(managerLog) ? "Sin registros disponibles.\r\n" : managerLog) +
            "\r\n=== Servicio NextDNS Manager · eventos recientes de Aplicación ===\r\n" +
            (string.IsNullOrWhiteSpace(serviceLog) ? "Sin entradas recientes disponibles. El servicio puede no haberse iniciado o Windows puede haber rotado el registro.\r\n" : serviceLog);
        txtDiagnostics.CaretIndex = txtDiagnostics.Text.Length;
        txtDiagnostics.ScrollToEnd();
    }

    private void OpenManagerFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(DiagnosticLog.ManagerDirectory, "No se pudo abrir la carpeta de registros de la aplicación.");

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
            System.Windows.MessageBox.Show(this, errorMessage, "Registros de diagnóstico", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
