#define MyAppName "NextDNS Manager"
#define MyAppVersion "1.1.2"
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
OutputBaseFilename=NextDNS-Manager-1.1.2-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
VersionInfoVersion=1.1.2.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Instalador de NextDNS Manager
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish-1.1.2-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir {#MyAppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
function ManagerIsRunning(): Boolean;
var
  Locator, Services, Processes: Variant;
begin
  { Include older versions which did not create AppMutex. Check every session. }
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('.', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''NextDNSModernWPF.exe''');
  Result := Processes.Count > 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  try
    if ManagerIsRunning() then
      Result := 'Cierra NextDNS Manager desde su icono de la bandeja (Salir) antes de instalar o reparar. Hay una instancia abierta y no es seguro reemplazar sus archivos.';
  except
    Result := 'No se pudo comprobar si NextDNS Manager sigue abierto. La instalación se canceló para proteger sus archivos. Cierra la aplicación y vuelve a intentarlo.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
  Index: Integer;
  ExpectedHash, RelativePath, InstalledPath: String;
begin
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
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
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

  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\nextdns') then
    Exit;

  { Never remove a registered NextDNS CLI service unattended. }
  if UninstallSilent then
  begin
    Result := False;
    Exit;
  end;

  if MsgBox(
       'Se detectó un servicio "nextdns" de línea de comandos. Para evitar dejarlo apuntando a archivos que se van a borrar, elija Sí para quitar ese servicio y continuar. Si el servicio pertenece a otra instalación de NextDNS, elija No: la desinstalación se cancelará.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
  begin
    Result := False;
    Exit;
  end;

  if not Exec(ExpandConstant('{app}\nextdns.exe'), 'uninstall', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('No se pudo ejecutar nextdns.exe uninstall. La desinstalación se canceló para conservar los archivos del servicio.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (ResultCode <> 0) or RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\nextdns') then
  begin
    MsgBox('No se pudo confirmar la eliminación del servicio "nextdns". La desinstalación se canceló para evitar dejar un servicio incompleto.', mbError, MB_OK);
    Result := False;
  end;
end;
