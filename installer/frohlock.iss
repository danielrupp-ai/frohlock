; FrohLock – Installer (Inno Setup 6). Erzeugt EINE herunterladbare Setup-.exe.
; Wird von der CI mit iscc kompiliert. Payload liegt unter installer\payload\.

#ifndef AppVersion
  #define AppVersion "0.6.0"
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
Name: "{group}\FrohLock deinstallieren"; Filename: "{uninstallexe}"
; Gut sichtbare Verknüpfung, falls das Einrichtungs-Fenster geschlossen wurde.
Name: "{autodesktop}\FrohLock einrichten"; Filename: "{app}\FrohLockSetup.exe"

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
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated

; 3) Overlay-Agent bei JEDEM Logon (im Nutzerkontext) starten.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#AgentTask}"" /TR ""\""{app}\FrohLockAgent.exe\"""" /SC ONLOGON /RL LIMITED /F"; Flags: runhidden waituntilterminated

; 4) Direkt die Eltern-Einrichtung öffnen.
Filename: "{app}\FrohLockSetup.exe"; Description: "FrohLock jetzt einrichten"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Reihenfolge: Task weg, Dienst stoppen + entfernen (vor dem Löschen der Dateien).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AgentTask}"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DelSvc"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\{#AppName}"

[Code]
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
    MsgBox('PIN-Pruefung konnte nicht gestartet werden. Deinstallation abgebrochen.', mbError, MB_OK);
    Result := False;
    exit;
  end;

  if ResultCode = 0 then
    Result := True
  else
  begin
    MsgBox('Falscher oder abgebrochener PIN. Deinstallation abgebrochen.', mbError, MB_OK);
    Result := False;
  end;
end;
