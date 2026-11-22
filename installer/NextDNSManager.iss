#define MyAppName "NextDNS Manager"
#define MyAppVersion "1.1.1"
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
OutputDir=..\artifacts
OutputBaseFilename=NextDNS-Manager-1.1.1-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
VersionInfoVersion=1.1.1.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Instalador de NextDNS Manager
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish-1.1.1-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir {#MyAppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

[Code]
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

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
