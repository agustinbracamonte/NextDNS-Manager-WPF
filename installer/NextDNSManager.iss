#define MyAppName "NextDNS Manager"
#define MyAppVersion "2.3.1"
#define MyAppPublisher "Agustín Bracamonte"
#define MyAppExeName "NextDNSModernWPF.exe"

[Setup]
AppId={{9D19127A-218A-4A9E-9342-716AC4F70A66}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/agustinbracamonte/NextDNS-Manager-WPF
AppSupportURL=https://github.com/agustinbracamonte/NextDNS-Manager-WPF/issues
AppUpdatesURL=https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases
DefaultDirName={autopf}\NextDNS Manager
DefaultGroupName={#MyAppName}
DisableDirPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\NextDNSModernWPF\escudo_dns.ico
WizardImageFile=branding\wizard.png
WizardSmallImageFile=..\NextDNSModernWPF\brand.png
WizardImageBackColor=#081221
WizardSmallImageBackColor=#081221
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=120,120
PrivilegesRequired=admin
MinVersion=10.0.14393
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
AppMutex=NextDNSManagerWPF-Running
RestartApplications=no
OutputDir=..\artifacts
OutputBaseFilename=NextDNS-Manager-{#MyAppVersion}-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
VersionInfoVersion=2.3.1.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=NextDNS Manager Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.PathInspectionFailed=Could not check the path for %1.
en.ReparsePointNotAllowed=Links and junctions are not allowed in %1.
en.PreviousInstallationDescription=the previous installation
en.PreviousInstallReparsePointNotAllowed=Links and junctions are not allowed inside the previous installation.
en.BackupDirectoryPreparationFailed=Could not prepare a backup of the previous installation.
en.BackupFileFailed=Could not back up a file from the previous installation: %1
en.CustomInstallPathNotSupported=The helper service must be installed in Program Files\NextDNS Manager. A custom folder is not supported.
en.CloseAppBeforeInstall=Close NextDNS Manager from its tray icon (Exit) before installing or repairing. An instance is open, so it is not safe to replace its files.
en.CouldNotCheckAppBeforeInstall=Could not check whether NextDNS Manager is still open. Installation was canceled to protect its files. Close the app and try again.
en.OldCliServiceMustBeRemoved=An existing NextDNS CLI service is installed without the new controller. Disable it from its previous app before upgrading; its DNS configuration was preserved.
en.BrokerExecutableMissing=The helper service exists, but its executable is missing. The upgrade was canceled to preserve the files.
en.CouldNotStopBroker=Could not stop the helper service. Its files were preserved.
en.BrokerCloseNotConfirmed=The helper service did not confirm that it closed. The upgrade was canceled to protect its files.
en.RollbackFolderAlreadyExists=A rollback temporary folder already exists. The upgrade was canceled to protect the previous installation.
en.InstallManifestMissing=The installation manifest is missing. Run the installer again to repair.
en.InstallManifestInvalid=The installation manifest is invalid.
en.InstalledFileMissing=An installed file is missing: %1. Run the installer again to repair.
en.InstalledFileMismatch=The installed file does not match the original: %1. Run the installer again to repair.
en.CouldNotRegisterBroker=Could not register the helper service. Run the installer again to repair.
en.BrokerStartNotConfirmed=The helper service did not confirm startup. Run the installer again to repair.
en.CloseAppBeforeUninstall=Close NextDNS Manager from its tray icon (Exit) before uninstalling. Its files will be kept while the app is open.
en.CouldNotCheckAppBeforeUninstall=Could not check whether NextDNS Manager is still open. Uninstallation was canceled to protect its files.
en.CouldNotRemoveBroker=Could not remove the helper service. Uninstallation was canceled to preserve its files.
en.BrokerRemovalOrDnsRestoreNotConfirmed=Could not confirm removal of the helper service or restoration of DNS. The files were preserved.
en.RollbackBrokerExecutableMissing=The broker could not be stopped during rollback because its executable is missing.
en.RollbackBrokerStopFailed=The broker could not be stopped during rollback. The current files were kept to avoid replacing an active process.
en.RollbackNewBrokerRemoveFailed=The broker created during the failed installation could not be removed.
en.RollbackStillRegistered=The new broker is still registered. Restoration was canceled to avoid mixing files.
en.PreviousBrokerRestartFailed=The previous broker could not be restarted. The installation needs repair.
en.PreviousInstallationRestoreIncomplete=The previous installation could not be fully restored. The installation needs repair.

es.PathInspectionFailed=No se pudo comprobar la ruta de %1.
es.ReparsePointNotAllowed=No se permiten enlaces ni junctions en %1.
es.PreviousInstallationDescription=la instalación anterior
es.PreviousInstallReparsePointNotAllowed=No se permiten enlaces ni junctions dentro de la instalación anterior.
es.BackupDirectoryPreparationFailed=No se pudo preparar la copia de seguridad de la instalación anterior.
es.BackupFileFailed=No se pudo respaldar un archivo de la instalación anterior: %1
es.CustomInstallPathNotSupported=El servicio auxiliar debe instalarse en Program Files\NextDNS Manager. No se admite una carpeta personalizada.
es.CloseAppBeforeInstall=Cierra NextDNS Manager desde su icono de la bandeja (Salir) antes de instalar o reparar. Hay una instancia abierta y no es seguro reemplazar sus archivos.
es.CouldNotCheckAppBeforeInstall=No se pudo comprobar si NextDNS Manager sigue abierto. La instalación se canceló para proteger sus archivos. Cierra la aplicación y vuelve a intentarlo.
es.OldCliServiceMustBeRemoved=Hay un servicio NextDNS CLI instalado sin el nuevo controlador. Desactívalo desde su aplicación anterior antes de actualizar; se conservó su configuración DNS.
es.BrokerExecutableMissing=El servicio auxiliar existe, pero falta su ejecutable. Se canceló la actualización para conservar los archivos.
es.CouldNotStopBroker=No se pudo detener el servicio auxiliar. Se conservaron sus archivos.
es.BrokerCloseNotConfirmed=El servicio auxiliar no confirmó su cierre. Se canceló la actualización para proteger sus archivos.
es.RollbackFolderAlreadyExists=Ya existe una carpeta temporal de rollback. Se canceló la actualización para proteger la instalación anterior.
es.InstallManifestMissing=Falta el manifiesto de instalación. Vuelve a ejecutar el instalador para reparar.
es.InstallManifestInvalid=El manifiesto de instalación no es válido.
es.InstalledFileMissing=Falta un archivo instalado: %1. Vuelve a ejecutar el instalador para reparar.
es.InstalledFileMismatch=El archivo instalado no coincide con el original: %1. Vuelve a ejecutar el instalador para reparar.
es.CouldNotRegisterBroker=No se pudo registrar el servicio auxiliar. Vuelve a ejecutar el instalador para reparar.
es.BrokerStartNotConfirmed=El servicio auxiliar no confirmó su inicio. Vuelve a ejecutar el instalador para reparar.
es.CloseAppBeforeUninstall=Cierra NextDNS Manager desde su icono de la bandeja (Salir) antes de desinstalar. Se conservarán sus archivos mientras la aplicación esté abierta.
es.CouldNotCheckAppBeforeUninstall=No se pudo comprobar si NextDNS Manager sigue abierto. Se canceló la desinstalación para proteger sus archivos.
es.CouldNotRemoveBroker=No se pudo quitar el servicio auxiliar. Se canceló la desinstalación para conservar sus archivos.
es.BrokerRemovalOrDnsRestoreNotConfirmed=No se pudo confirmar la eliminación del servicio auxiliar o la restauración del DNS. Se conservaron los archivos.
es.RollbackBrokerExecutableMissing=No se pudo detener el broker durante el rollback porque falta su ejecutable.
es.RollbackBrokerStopFailed=No se pudo detener el broker durante el rollback. Se conservaron los archivos actuales para no reemplazar un proceso activo.
es.RollbackNewBrokerRemoveFailed=No se pudo eliminar el broker creado durante la instalación fallida.
es.RollbackStillRegistered=El broker nuevo sigue registrado. Se canceló la restauración para evitar archivos mezclados.
es.PreviousBrokerRestartFailed=No se pudo iniciar el broker anterior. Se requiere reparar la instalación.
es.PreviousInstallationRestoreIncomplete=No se pudo restaurar por completo la instalación anterior. Se requiere reparar la instalación.

fr.PathInspectionFailed=Impossible de vérifier le chemin de %1.
fr.ReparsePointNotAllowed=Les liens symboliques et les jonctions ne sont pas autorisés dans %1.
fr.PreviousInstallationDescription=l'installation précédente
fr.PreviousInstallReparsePointNotAllowed=Les liens symboliques et les jonctions ne sont pas autorisés dans l'installation précédente.
fr.BackupDirectoryPreparationFailed=Impossible de préparer la sauvegarde de l'installation précédente.
fr.BackupFileFailed=Impossible de sauvegarder un fichier de l'installation précédente : %1
fr.CustomInstallPathNotSupported=Le service auxiliaire doit être installé dans Program Files\NextDNS Manager. Les dossiers personnalisés ne sont pas pris en charge.
fr.CloseAppBeforeInstall=Fermez NextDNS Manager depuis son icône de la zone de notification (Quitter) avant de l'installer ou de le réparer. Une instance est ouverte et le remplacement de ses fichiers n'est pas sûr.
fr.CouldNotCheckAppBeforeInstall=Impossible de vérifier si NextDNS Manager est toujours ouvert. L'installation a été annulée pour protéger ses fichiers. Fermez l'application et réessayez.
fr.OldCliServiceMustBeRemoved=Un service NextDNS CLI est installé sans le nouveau contrôleur. Désactivez-le depuis l'ancienne application avant la mise à jour ; sa configuration DNS a été conservée.
fr.BrokerExecutableMissing=Le service auxiliaire existe, mais son exécutable est introuvable. La mise à jour a été annulée pour préserver les fichiers.
fr.CouldNotStopBroker=Impossible d'arrêter le service auxiliaire. Ses fichiers ont été conservés.
fr.BrokerCloseNotConfirmed=Le service auxiliaire n'a pas confirmé son arrêt. La mise à jour a été annulée pour protéger ses fichiers.
fr.RollbackFolderAlreadyExists=Un dossier temporaire de restauration existe déjà. La mise à jour a été annulée pour protéger l'installation précédente.
fr.InstallManifestMissing=Le manifeste d'installation est introuvable. Relancez le programme d'installation pour réparer.
fr.InstallManifestInvalid=Le manifeste d'installation n'est pas valide.
fr.InstalledFileMissing=Un fichier installé est introuvable : %1. Relancez le programme d'installation pour réparer.
fr.InstalledFileMismatch=Le fichier installé ne correspond pas à l'original : %1. Relancez le programme d'installation pour réparer.
fr.CouldNotRegisterBroker=Impossible d'enregistrer le service auxiliaire. Relancez le programme d'installation pour réparer.
fr.BrokerStartNotConfirmed=Le service auxiliaire n'a pas confirmé son démarrage. Relancez le programme d'installation pour réparer.
fr.CloseAppBeforeUninstall=Fermez NextDNS Manager depuis son icône de la zone de notification (Quitter) avant de le désinstaller. Ses fichiers seront conservés tant que l'application est ouverte.
fr.CouldNotCheckAppBeforeUninstall=Impossible de vérifier si NextDNS Manager est toujours ouvert. La désinstallation a été annulée pour protéger ses fichiers.
fr.CouldNotRemoveBroker=Impossible de supprimer le service auxiliaire. La désinstallation a été annulée pour préserver ses fichiers.
fr.BrokerRemovalOrDnsRestoreNotConfirmed=Impossible de confirmer la suppression du service auxiliaire ou la restauration du DNS. Les fichiers ont été conservés.
fr.RollbackBrokerExecutableMissing=Impossible d'arrêter le broker pendant la restauration, car son exécutable est introuvable.
fr.RollbackBrokerStopFailed=Impossible d'arrêter le broker pendant la restauration. Les fichiers actuels ont été conservés pour éviter de remplacer un processus actif.
fr.RollbackNewBrokerRemoveFailed=Impossible de supprimer le broker créé lors de l'échec de l'installation.
fr.RollbackStillRegistered=Le nouveau broker est toujours enregistré. La restauration a été annulée pour éviter de mélanger les fichiers.
fr.PreviousBrokerRestartFailed=Impossible de redémarrer le broker précédent. Une réparation de l'installation est nécessaire.
fr.PreviousInstallationRestoreIncomplete=La restauration de l'installation précédente est incomplète. Une réparation est nécessaire.

pt.PathInspectionFailed=Não foi possível verificar o caminho de %1.
pt.ReparsePointNotAllowed=Links simbólicos e junctions não são permitidos em %1.
pt.PreviousInstallationDescription=a instalação anterior
pt.PreviousInstallReparsePointNotAllowed=Links simbólicos e junctions não são permitidos na instalação anterior.
pt.BackupDirectoryPreparationFailed=Não foi possível preparar a cópia de segurança da instalação anterior.
pt.BackupFileFailed=Não foi possível guardar uma cópia de segurança de um ficheiro da instalação anterior: %1
pt.CustomInstallPathNotSupported=O serviço auxiliar tem de ser instalado em Program Files\NextDNS Manager. Não é suportada a instalação numa pasta personalizada.
pt.CloseAppBeforeInstall=Feche o NextDNS Manager através do ícone da área de notificação (Sair) antes de instalar ou reparar. Está aberta uma instância e não é seguro substituir os ficheiros.
pt.CouldNotCheckAppBeforeInstall=Não foi possível verificar se o NextDNS Manager ainda está aberto. A instalação foi cancelada para proteger os ficheiros. Feche a aplicação e tente novamente.
pt.OldCliServiceMustBeRemoved=Existe um serviço NextDNS CLI instalado sem o novo controlador. Desative-o na aplicação anterior antes de atualizar; a configuração DNS foi preservada.
pt.BrokerExecutableMissing=O serviço auxiliar existe, mas o ficheiro executável está em falta. A atualização foi cancelada para preservar os ficheiros.
pt.CouldNotStopBroker=Não foi possível parar o serviço auxiliar. Os ficheiros foram preservados.
pt.BrokerCloseNotConfirmed=O serviço auxiliar não confirmou que foi parado. A atualização foi cancelada para proteger os ficheiros.
pt.RollbackFolderAlreadyExists=Já existe uma pasta temporária de reversão. A atualização foi cancelada para proteger a instalação anterior.
pt.InstallManifestMissing=O manifesto de instalação está em falta. Execute novamente o programa de instalação para reparar.
pt.InstallManifestInvalid=O manifesto de instalação é inválido.
pt.InstalledFileMissing=Falta um ficheiro instalado: %1. Execute novamente o programa de instalação para reparar.
pt.InstalledFileMismatch=O ficheiro instalado não corresponde ao original: %1. Execute novamente o programa de instalação para reparar.
pt.CouldNotRegisterBroker=Não foi possível registar o serviço auxiliar. Execute novamente o programa de instalação para reparar.
pt.BrokerStartNotConfirmed=O serviço auxiliar não confirmou o arranque. Execute novamente o programa de instalação para reparar.
pt.CloseAppBeforeUninstall=Feche o NextDNS Manager através do ícone da área de notificação (Sair) antes de desinstalar. Os ficheiros serão mantidos enquanto a aplicação estiver aberta.
pt.CouldNotCheckAppBeforeUninstall=Não foi possível verificar se o NextDNS Manager ainda está aberto. A desinstalação foi cancelada para proteger os ficheiros.
pt.CouldNotRemoveBroker=Não foi possível remover o serviço auxiliar. A desinstalação foi cancelada para preservar os ficheiros.
pt.BrokerRemovalOrDnsRestoreNotConfirmed=Não foi possível confirmar a remoção do serviço auxiliar ou o restauro do DNS. Os ficheiros foram preservados.
pt.RollbackBrokerExecutableMissing=Não foi possível parar o broker durante a reversão porque o ficheiro executável está em falta.
pt.RollbackBrokerStopFailed=Não foi possível parar o broker durante a reversão. Os ficheiros atuais foram mantidos para evitar substituir um processo ativo.
pt.RollbackNewBrokerRemoveFailed=Não foi possível remover o broker criado durante a instalação falhada.
pt.RollbackStillRegistered=O novo broker continua registado. A restauração foi cancelada para evitar misturar ficheiros.
pt.PreviousBrokerRestartFailed=Não foi possível reiniciar o broker anterior. É necessário reparar a instalação.
pt.PreviousInstallationRestoreIncomplete=Não foi possível restaurar completamente a instalação anterior. É necessário reparar a instalação.

de.PathInspectionFailed=Der Pfad für %1 konnte nicht überprüft werden.
de.ReparsePointNotAllowed=Links und Junctions sind in %1 nicht zulässig.
de.PreviousInstallationDescription=die vorherige Installation
de.PreviousInstallReparsePointNotAllowed=Links und Junctions sind innerhalb der vorherigen Installation nicht zulässig.
de.BackupDirectoryPreparationFailed=Die Sicherung der vorherigen Installation konnte nicht vorbereitet werden.
de.BackupFileFailed=Eine Datei der vorherigen Installation konnte nicht gesichert werden: %1
de.CustomInstallPathNotSupported=Der Hilfsdienst muss unter Program Files\NextDNS Manager installiert werden. Ein benutzerdefinierter Ordner wird nicht unterstützt.
de.CloseAppBeforeInstall=Schließen Sie NextDNS Manager über das Symbol im Infobereich (Beenden), bevor Sie das Programm installieren oder reparieren. Eine Instanz ist geöffnet; ein Ersetzen der Dateien ist daher nicht sicher.
de.CouldNotCheckAppBeforeInstall=Es konnte nicht überprüft werden, ob NextDNS Manager noch geöffnet ist. Die Installation wurde zum Schutz der Dateien abgebrochen. Schließen Sie die Anwendung und versuchen Sie es erneut.
de.OldCliServiceMustBeRemoved=Ein NextDNS-CLI-Dienst ist ohne den neuen Controller installiert. Deaktivieren Sie ihn vor dem Upgrade in der vorherigen Anwendung; die DNS-Konfiguration wurde beibehalten.
de.BrokerExecutableMissing=Der Hilfsdienst ist vorhanden, aber seine ausführbare Datei fehlt. Das Upgrade wurde abgebrochen, um die Dateien zu erhalten.
de.CouldNotStopBroker=Der Hilfsdienst konnte nicht angehalten werden. Seine Dateien wurden beibehalten.
de.BrokerCloseNotConfirmed=Der Hilfsdienst hat das Beenden nicht bestätigt. Das Upgrade wurde zum Schutz der Dateien abgebrochen.
de.RollbackFolderAlreadyExists=Ein temporärer Rollback-Ordner ist bereits vorhanden. Das Upgrade wurde zum Schutz der vorherigen Installation abgebrochen.
de.InstallManifestMissing=Das Installationsmanifest fehlt. Führen Sie das Installationsprogramm erneut aus, um die Installation zu reparieren.
de.InstallManifestInvalid=Das Installationsmanifest ist ungültig.
de.InstalledFileMissing=Eine installierte Datei fehlt: %1. Führen Sie das Installationsprogramm erneut aus, um die Installation zu reparieren.
de.InstalledFileMismatch=Die installierte Datei stimmt nicht mit dem Original überein: %1. Führen Sie das Installationsprogramm erneut aus, um die Installation zu reparieren.
de.CouldNotRegisterBroker=Der Hilfsdienst konnte nicht registriert werden. Führen Sie das Installationsprogramm erneut aus, um die Installation zu reparieren.
de.BrokerStartNotConfirmed=Der Hilfsdienst hat den Start nicht bestätigt. Führen Sie das Installationsprogramm erneut aus, um die Installation zu reparieren.
de.CloseAppBeforeUninstall=Schließen Sie NextDNS Manager über das Symbol im Infobereich (Beenden), bevor Sie das Programm deinstallieren. Solange die Anwendung geöffnet ist, bleiben ihre Dateien erhalten.
de.CouldNotCheckAppBeforeUninstall=Es konnte nicht überprüft werden, ob NextDNS Manager noch geöffnet ist. Die Deinstallation wurde zum Schutz der Dateien abgebrochen.
de.CouldNotRemoveBroker=Der Hilfsdienst konnte nicht entfernt werden. Die Deinstallation wurde abgebrochen, um die Dateien zu erhalten.
de.BrokerRemovalOrDnsRestoreNotConfirmed=Das Entfernen des Hilfsdienstes oder die Wiederherstellung der DNS-Konfiguration konnte nicht bestätigt werden. Die Dateien wurden beibehalten.
de.RollbackBrokerExecutableMissing=Der Broker konnte während des Rollbacks nicht angehalten werden, da seine ausführbare Datei fehlt.
de.RollbackBrokerStopFailed=Der Broker konnte während des Rollbacks nicht angehalten werden. Die aktuellen Dateien wurden beibehalten, um keinen aktiven Prozess zu ersetzen.
de.RollbackNewBrokerRemoveFailed=Der während der fehlgeschlagenen Installation erstellte Broker konnte nicht entfernt werden.
de.RollbackStillRegistered=Der neue Broker ist weiterhin registriert. Die Wiederherstellung wurde abgebrochen, um eine Vermischung von Dateien zu vermeiden.
de.PreviousBrokerRestartFailed=Der vorherige Broker konnte nicht neu gestartet werden. Die Installation muss repariert werden.
de.PreviousInstallationRestoreIncomplete=Die vorherige Installation konnte nicht vollständig wiederhergestellt werden. Die Installation muss repariert werden.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish-{#MyAppVersion}-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\NextDNSManager.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\NextDNSManager.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
var
  PreviousApplicationExisted: Boolean;
  ApplicationBackupAvailable: Boolean;
  PreviousBrokerExisted: Boolean;
  PreviousBrokerStopped: Boolean;
  InstallPhaseStarted: Boolean;
  BrokerCommitted: Boolean;
  ApplicationBackupDirectory: String;

function LocalizedMessageWithParameter(const Name, Parameter: String): String;
begin
  Result := FmtMessage(CustomMessage(Name), [Parameter]);
end;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure RequireNoReparsePoint(Path, Description: String);
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  if Attributes = $FFFFFFFF then
    RaiseException(LocalizedMessageWithParameter('PathInspectionFailed', Description));
  if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
    RaiseException(LocalizedMessageWithParameter('ReparsePointNotAllowed', Description));
end;

function IsInnoUninstallerFile(Name: String): Boolean;
var
  LowerName, Extension: String;
begin
  LowerName := Lowercase(Name);
  Extension := Lowercase(ExtractFileExt(Name));
  Result := (Copy(LowerName, 1, 5) = 'unins') and ((Extension = '.exe') or (Extension = '.dat'));
end;

procedure CheckTreeForReparse(Directory: String; Root: Boolean);
var
  Entry: TFindRec;
  EntryPath: String;
begin
  RequireNoReparsePoint(Directory, CustomMessage('PreviousInstallationDescription'));
  if FindFirst(AddBackslash(Directory) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          EntryPath := AddBackslash(Directory) + Entry.Name;
          if (Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
            RaiseException(CustomMessage('PreviousInstallReparsePointNotAllowed'));
          if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CheckTreeForReparse(EntryPath, False);
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure CopyApplicationTree(Source, Destination: String; Root: Boolean);
var
  Entry: TFindRec;
  SourcePath, DestinationPath: String;
begin
  if not ForceDirectories(Destination) then RaiseException(CustomMessage('BackupDirectoryPreparationFailed'));
  if FindFirst(AddBackslash(Source) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          if not (Root and IsInnoUninstallerFile(Entry.Name)) then
          begin
            if (Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
              RaiseException(CustomMessage('PreviousInstallReparsePointNotAllowed'));
            SourcePath := AddBackslash(Source) + Entry.Name;
            DestinationPath := AddBackslash(Destination) + Entry.Name;
            if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
              CopyApplicationTree(SourcePath, DestinationPath, False)
            else if not FileCopy(SourcePath, DestinationPath, False) then
              RaiseException(LocalizedMessageWithParameter('BackupFileFailed', Entry.Name));
          end;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function StopBrokerForRollback(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then Exit;
  if not FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
  begin
    Result := False;
    Log(CustomMessage('RollbackBrokerExecutableMissing'));
    Exit;
  end;
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Result := False;
    Log(CustomMessage('RollbackBrokerStopFailed'));
  end;
end;

procedure RestoreApplicationBackup;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
  begin
    if not StopBrokerForRollback() then Exit;
    if not PreviousBrokerExisted then
    begin
      if not Exec(ExpandConstant('{sys}\sc.exe'), 'delete NextDNSManagerBroker', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      begin
        Log(CustomMessage('RollbackNewBrokerRemoveFailed'));
        Exit;
      end;
      Attempts := 0;
      while RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') and (Attempts < 67) do
      begin
        Sleep(150);
        Attempts := Attempts + 1;
      end;
      if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
      begin
        Log(CustomMessage('RollbackStillRegistered'));
        Exit;
      end;
    end;
  end;

  if not InstallPhaseStarted then
  begin
    if PreviousBrokerExisted and PreviousBrokerStopped and FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
    begin
      if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
        Log(CustomMessage('PreviousBrokerRestartFailed'));
    end;
    Exit;
  end;

  if ApplicationBackupAvailable then
  begin
    if DirExists(ExpandConstant('{app}')) then
      CheckTreeForReparse(ExpandConstant('{app}'), True);
    CopyApplicationTree(ApplicationBackupDirectory, ExpandConstant('{app}'), True);
  end;

  if PreviousBrokerExisted and FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
  begin
    if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      Log(CustomMessage('PreviousBrokerRestartFailed'));
  end;
end;

function ManagerIsRunning(): Boolean;
var
  Locator, Services, Processes, Process: Variant;
  ProcessIndex: Integer;
  ProcessPath, InstalledManagerPath: String;
begin
  { Include older versions which did not create AppMutex. Check every session,
    but ignore the update helper running from its private LocalAppData copy. }
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('.', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''NextDNSModernWPF.exe''');
  InstalledManagerPath := ExpandConstant('{app}\NextDNSModernWPF.exe');
  Result := False;
  for ProcessIndex := 0 to Processes.Count - 1 do
  begin
    Process := Processes.ItemIndex(ProcessIndex);
    if VarIsNull(Process.ExecutablePath) then
    begin
      { Fail closed if Windows cannot identify the image path of a matching process. }
      Result := True;
      Exit;
    end;
    ProcessPath := Process.ExecutablePath;
    if CompareText(ProcessPath, InstalledManagerPath) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\NextDNS Manager')) <> 0 then
  begin
    Result := CustomMessage('CustomInstallPathNotSupported');
    Exit;
  end;
  try
    if ManagerIsRunning() then
      Result := CustomMessage('CloseAppBeforeInstall');
  except
    Result := CustomMessage('CouldNotCheckAppBeforeInstall');
  end;
  if Result <> '' then Exit;
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\nextdns') and
     not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
  begin
    Result := CustomMessage('OldCliServiceMustBeRemoved');
    Exit;
  end;

  PreviousApplicationExisted := DirExists(ExpandConstant('{app}'));
  PreviousBrokerExisted := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker');
  if PreviousBrokerExisted then
  begin
    if not FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
    begin
      Result := CustomMessage('BrokerExecutableMissing');
      Exit;
    end;
    if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := CustomMessage('CouldNotStopBroker')
    else if ResultCode <> 0 then
      Result := CustomMessage('BrokerCloseNotConfirmed');
    if Result <> '' then Exit;
    PreviousBrokerStopped := True;
  end;

  if PreviousApplicationExisted then
  begin
    ApplicationBackupDirectory := ExpandConstant('{tmp}\NextDNSManagerRollback');
    if DirExists(ApplicationBackupDirectory) then
    begin
      Result := CustomMessage('RollbackFolderAlreadyExists');
      Exit;
    end;
    CheckTreeForReparse(ExpandConstant('{app}'), True);
    if not ForceDirectories(ApplicationBackupDirectory) then
    begin
      Result := CustomMessage('BackupDirectoryPreparationFailed');
      Exit;
    end;
    CopyApplicationTree(ExpandConstant('{app}'), ApplicationBackupDirectory, True);
    ApplicationBackupAvailable := True;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
  Index: Integer;
  ExpectedHash, RelativePath, InstalledPath: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    InstallPhaseStarted := True;
    Exit;
  end;
  if CurStep <> ssPostInstall then Exit;
  if not LoadStringsFromFile(ExpandConstant('{app}\install-files.sha256'), Lines) then
    RaiseException(CustomMessage('InstallManifestMissing'));
  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    ExpectedHash := Copy(Lines[Index], 1, 64);
    RelativePath := Copy(Lines[Index], 67, MaxInt);
    InstalledPath := ExpandConstant('{app}\') + RelativePath;
    if (Length(ExpectedHash) <> 64) or (RelativePath = '') or (Pos('..', RelativePath) > 0) then
      RaiseException(CustomMessage('InstallManifestInvalid'));
    if not FileExists(InstalledPath) then
      RaiseException(LocalizedMessageWithParameter('InstalledFileMissing', RelativePath));
    if CompareText(GetSHA256OfFile(InstalledPath), ExpectedHash) <> 0 then
      RaiseException(LocalizedMessageWithParameter('InstalledFileMismatch', RelativePath));
  end;
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException(CustomMessage('CouldNotRegisterBroker'));
  if ResultCode <> 0 then
    RaiseException(CustomMessage('BrokerStartNotConfirmed'));
  BrokerCommitted := True;
end;

procedure DeinitializeSetup();
begin
  if BrokerCommitted or (not InstallPhaseStarted and not PreviousBrokerStopped) then Exit;
  try
    RestoreApplicationBackup();
  except
    Log(CustomMessage('PreviousInstallationRestoreIncomplete'));
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  ControllerSid: String;
begin
  Result := True;

  try
    if ManagerIsRunning() then
    begin
      if not UninstallSilent then
        MsgBox(CustomMessage('CloseAppBeforeUninstall'), mbInformation, MB_OK);
      Result := False;
      Exit;
    end;
  except
    if not UninstallSilent then
      MsgBox(CustomMessage('CouldNotCheckAppBeforeUninstall'), mbError, MB_OK);
    Result := False;
    Exit;
  end;

  { The broker validates ownership and restores the saved DNS configuration.
    Never use CLI uninstall: it resets static DNS to DHCP on Windows. }
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--remove', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if not UninstallSilent then MsgBox(CustomMessage('CouldNotRemoveBroker'), mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (ResultCode <> 0) or RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
  begin
    if not UninstallSilent then MsgBox(CustomMessage('BrokerRemovalOrDnsRestoreNotConfirmed'), mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if RegQueryStringValue(HKLM, 'SOFTWARE\NextDNSManager', 'ControllerSid', ControllerSid) then
  begin
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "NextDNS Manager Resume ' + ControllerSid + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKU, ControllerSid + '\Software\Microsoft\Windows\CurrentVersion\Run', 'NextDNS Manager');
    RegDeleteValue(HKU, ControllerSid + '\Software\NextDNSManager', 'StartupConfigured');
  end;
end;
