#define MyAppName "NextDNS Manager"
#define MyAppVersion "2.1.0"
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
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
AppMutex=NextDNSManagerWPF-Running
RestartApplications=no
OutputDir=..\artifacts
OutputBaseFilename=NextDNS-Manager-2.1.0-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
VersionInfoVersion=2.1.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Instalador de NextDNS Manager
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish-2.1.0-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\NextDNSManager.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\NextDNSManager.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir {#MyAppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
var
  PreviousApplicationExisted: Boolean;
  ApplicationBackupAvailable: Boolean;
  PreviousBrokerExisted: Boolean;
  PreviousBrokerStopped: Boolean;
  InstallPhaseStarted: Boolean;
  BrokerCommitted: Boolean;
  ApplicationBackupDirectory: String;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure RequireNoReparsePoint(Path, Description: String);
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  if Attributes = $FFFFFFFF then
    RaiseException('No se pudo comprobar la ruta de ' + Description + '.');
  if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
    RaiseException('No se permiten enlaces o junctions en ' + Description + '.');
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
  RequireNoReparsePoint(Directory, 'la instalación anterior');
  if FindFirst(AddBackslash(Directory) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          EntryPath := AddBackslash(Directory) + Entry.Name;
          if (Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
            RaiseException('No se permiten enlaces o junctions dentro de la instalación anterior.');
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
  if not ForceDirectories(Destination) then RaiseException('No se pudo preparar la copia de seguridad de la instalación.');
  if FindFirst(AddBackslash(Source) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          if not (Root and IsInnoUninstallerFile(Entry.Name)) then
          begin
            if (Entry.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
              RaiseException('No se permiten enlaces o junctions dentro de la instalación anterior.');
            SourcePath := AddBackslash(Source) + Entry.Name;
            DestinationPath := AddBackslash(Destination) + Entry.Name;
            if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
              CopyApplicationTree(SourcePath, DestinationPath, False)
            else if not FileCopy(SourcePath, DestinationPath, False) then
              RaiseException('No se pudo respaldar un archivo de la instalación anterior: ' + Entry.Name);
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
    Log('No se pudo detener el broker durante rollback porque falta su ejecutable.');
    Exit;
  end;
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Result := False;
    Log('No se pudo detener el broker durante rollback; se conservaron los archivos actuales para no reemplazar un proceso activo.');
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
        Log('No se pudo eliminar el broker creado durante la instalación fallida.');
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
        Log('El broker nuevo sigue registrado; se canceló la restauración para evitar archivos mezclados.');
        Exit;
      end;
    end;
  end;

  if not InstallPhaseStarted then
  begin
    if PreviousBrokerExisted and PreviousBrokerStopped and FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
    begin
      if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
        Log('No se pudo volver a iniciar el broker anterior. Se requiere reparar la instalación.');
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
      Log('No se pudo iniciar el broker anterior tras restaurar los archivos. Se requiere reparar la instalación.');
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
    Result := 'El servicio auxiliar debe instalarse en Program Files\NextDNS Manager. No se admite una carpeta personalizada.';
    Exit;
  end;
  try
    if ManagerIsRunning() then
      Result := 'Cierra NextDNS Manager desde su icono de la bandeja (Salir) antes de instalar o reparar. Hay una instancia abierta y no es seguro reemplazar sus archivos.';
  except
    Result := 'No se pudo comprobar si NextDNS Manager sigue abierto. La instalación se canceló para proteger sus archivos. Cierra la aplicación y vuelve a intentarlo.';
  end;
  if Result <> '' then Exit;
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\nextdns') and
     not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
  begin
    Result := 'Hay un servicio NextDNS CLI instalado sin el nuevo controlador. Desactívalo desde su aplicación anterior antes de actualizar; se conservó su configuración DNS.';
    Exit;
  end;

  PreviousApplicationExisted := DirExists(ExpandConstant('{app}'));
  PreviousBrokerExisted := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker');
  if PreviousBrokerExisted then
  begin
    if not FileExists(ExpandConstant('{app}\service\NextDNSManager.Service.exe')) then
    begin
      Result := 'El servicio auxiliar existe, pero falta su ejecutable. Se canceló la actualización para conservar los archivos.';
      Exit;
    end;
    if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := 'No se pudo detener el servicio auxiliar. Se conservaron sus archivos.'
    else if ResultCode <> 0 then
      Result := 'El servicio auxiliar no confirmó su cierre. Se canceló la actualización para proteger sus archivos.';
    if Result <> '' then Exit;
    PreviousBrokerStopped := True;
  end;

  if PreviousApplicationExisted then
  begin
    ApplicationBackupDirectory := ExpandConstant('{tmp}\NextDNSManagerRollback');
    if DirExists(ApplicationBackupDirectory) then
    begin
      Result := 'Ya existe una carpeta temporal de rollback. Se canceló la actualización para proteger la instalación anterior.';
      Exit;
    end;
    CheckTreeForReparse(ExpandConstant('{app}'), True);
    if not ForceDirectories(ApplicationBackupDirectory) then
    begin
      Result := 'No se pudo preparar la copia de seguridad de la instalación anterior.';
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
    RaiseException('Falta el manifiesto de instalación. Vuelve a ejecutar el instalador para reparar.');
  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    ExpectedHash := Copy(Lines[Index], 1, 64);
    RelativePath := Copy(Lines[Index], 67, MaxInt);
    InstalledPath := ExpandConstant('{app}\') + RelativePath;
    if (Length(ExpectedHash) <> 64) or (RelativePath = '') or (Pos('..', RelativePath) > 0) then
      RaiseException('El manifiesto de instalación no es válido.');
    if not FileExists(InstalledPath) then
      RaiseException('Falta un archivo instalado: ' + RelativePath + '. Vuelve a ejecutar el instalador para reparar.');
    if CompareText(GetSHA256OfFile(InstalledPath), ExpectedHash) <> 0 then
      RaiseException('El archivo instalado no coincide con el original: ' + RelativePath + '. Vuelve a ejecutar el instalador para reparar.');
  end;
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('No se pudo registrar el servicio auxiliar. Vuelve a ejecutar el instalador para reparar.');
  if ResultCode <> 0 then
    RaiseException('El servicio auxiliar no confirmó su inicio. Vuelve a ejecutar el instalador para reparar.');
  BrokerCommitted := True;
end;

procedure DeinitializeSetup();
begin
  if BrokerCommitted or (not InstallPhaseStarted and not PreviousBrokerStopped) then Exit;
  try
    RestoreApplicationBackup();
  except
    Log('No se pudo restaurar por completo la instalación anterior. Se requiere reparar la instalación.');
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
        MsgBox('Cierra NextDNS Manager desde su icono de la bandeja (Salir) antes de desinstalar. Sus archivos se conservarán mientras esté abierto.', mbInformation, MB_OK);
      Result := False;
      Exit;
    end;
  except
    if not UninstallSilent then
      MsgBox('No se pudo comprobar si NextDNS Manager sigue abierto. Se canceló la desinstalación para proteger sus archivos.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  { The broker validates ownership and restores the saved DNS configuration.
    Never use CLI uninstall: it resets static DNS to DHCP on Windows. }
  if not Exec(ExpandConstant('{app}\service\NextDNSManager.Service.exe'), '--remove', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if not UninstallSilent then MsgBox('No se pudo quitar el servicio auxiliar. La desinstalación se canceló para conservar sus archivos.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (ResultCode <> 0) or RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\NextDNSManagerBroker') then
  begin
    if not UninstallSilent then MsgBox('No se pudo confirmar la eliminación del servicio auxiliar o restaurar el DNS. Se conservaron los archivos.', mbError, MB_OK);
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
