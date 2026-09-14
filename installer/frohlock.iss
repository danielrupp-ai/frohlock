; FrohLock – Installer (Inno Setup 6). Erzeugt EINE herunterladbare Setup-.exe.
; Wird von der CI mit iscc kompiliert. Payload liegt unter installer\payload\.

#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif

#define AppName "FrohLock"
#define ServiceName "FrohLockService"
#define AgentTask "FrohLockAgent"
#define Publisher "Fröhlich Dienste"

[Setup]
AppId={{53775357-FC9E-4D03-8797-572611E3E148}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=FrohLockSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
UninstallDisplayName={#AppName} (Bildschirmzeit-Schutz)
; FrohLock NICHT in "Apps & Features" listen -> keine System-Deinstallation.
; Entfernen nur über die PIN-gesicherte Verknüpfung "FrohLock deinstallieren".
CreateUninstallRegKey=no
SetupIconFile=

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "payload\service\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\agent\*";   DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\setup\*";   DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\{#AppName}"

[Icons]
Name: "{group}\FrohLock einrichten"; Filename: "{app}\FrohLockSetup.exe"
Name: "{group}\FrohLock Übersicht"; Filename: "{app}\FrohLockAgent.exe"; Parameters: "--status"
Name: "{group}\FrohLock deinstallieren"; Filename: "{uninstallexe}"
; Gut sichtbare Verknüpfungen.
Name: "{autodesktop}\FrohLock einrichten"; Filename: "{app}\FrohLockSetup.exe"
Name: "{autodesktop}\FrohLock Übersicht"; Filename: "{app}\FrohLockAgent.exe"; Parameters: "--status"

[Run]
; 1) ProgramData-Verzeichnis abriegeln: nur SYSTEM + Administratoren, keine Standardnutzer.
Filename: "{sys}\icacls.exe"; \
  Parameters: """{commonappdata}\{#AppName}"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F"; \
  Flags: runhidden waituntilterminated

; 2) Dienst anlegen (Autostart), beschreiben, Recovery = immer neu starten.
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\FrohLockService.exe\"""" start= auto DisplayName= ""FrohLock Schutzdienst"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Bildschirmzeit-Schutz fuer Kinder (FrohLock)."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failureflag {#ServiceName} 1"; Flags: runhidden waituntilterminated
; Dienst auch im abgesicherten Modus starten (schließt die Safe-Mode-Umgehung).
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{#ServiceName}"" /ve /t REG_SZ /d Service /f"; Flags: runhidden waituntilterminated
Filename: "{sys}\reg.exe"; Parameters: "add ""HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{#ServiceName}"" /ve /t REG_SZ /d Service /f"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated

; 3) Overlay-Agent bei JEDEM Logon (im Nutzerkontext) starten.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#AgentTask}"" /TR ""\""{app}\FrohLockAgent.exe\"""" /SC ONLOGON /RL LIMITED /F"; Flags: runhidden waituntilterminated

; 4) Direkt die Eltern-Einrichtung öffnen.
Filename: "{app}\FrohLockSetup.exe"; Description: "FrohLock jetzt einrichten"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Reihenfolge: Task weg, Dienst stoppen + entfernen (vor dem Löschen der Dateien).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AgentTask}"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im FrohLockAgent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DelSvc"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{#ServiceName}"" /f"; Flags: runhidden; RunOnceId: "DelSafeMin"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{#ServiceName}"" /f"; Flags: runhidden; RunOnceId: "DelSafeNet"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\{#AppName}"

[Code]
{ Vor der Installation: laufenden Dienst + Agent stoppen und Dienst entfernen,
  damit ein Upgrade die (sonst gesperrten) Programmdateien wirklich ersetzt. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im FrohLockAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
  Result := '';
end;

{ Deinstallation nur mit Eltern-PIN – oder wenn der Server einen Admin-Wipe autorisiert hat. }
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Marker: string;
begin
  Marker := ExpandConstant('{commonappdata}\{#AppName}\wipe.authorized');
  if FileExists(Marker) then
  begin
    Result := True;
    exit;
  end;

  if not Exec(ExpandConstant('{app}\FrohLockSetup.exe'), '--verify-pin', '',
              SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Nur Eltern koennen FrohLock mit dem PIN-Code entfernen. Deinstallation abgebrochen.', mbError, MB_OK);
    Result := False;
    exit;
  end;

  if ResultCode = 0 then
    Result := True
  else
  begin
    MsgBox('Nur Eltern koennen FrohLock mit dem PIN-Code entfernen. Ohne richtigen PIN ist keine Deinstallation moeglich.', mbError, MB_OK);
    Result := False;
  end;
end;
