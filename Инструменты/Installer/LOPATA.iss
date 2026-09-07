#ifndef AppVersion
#error AppVersion is required. Pass /DAppVersion=...
#endif

#ifndef PublishDir
#error PublishDir is required. Pass /DPublishDir=...
#endif

#ifndef OutputDir
#error OutputDir is required. Pass /DOutputDir=...
#endif

#ifndef BackendDir
#error BackendDir is required. Pass /DBackendDir=...
#endif

#ifndef ChatLlmBackendDir
#error ChatLlmBackendDir is required. Pass /DChatLlmBackendDir=...
#endif

#ifndef SetupIconFile
#error SetupIconFile is required. Pass /DSetupIconFile=...
#endif

#define AppName "LOPATA"
#define AppPublisher "LOPATA"
#define AppExeName "AIHub.exe"

[Setup]
AppId={{85E9F5C5-2B18-43B1-84E2-A99B25E9B9E8}
AppName={#AppName}
AppVersion={#AppVersion}
#ifdef NumericVersion
VersionInfoVersion={#NumericVersion}
#endif
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/PiTrolKun/LOPATA
AppSupportURL=https://github.com/PiTrolKun/LOPATA
AppUpdatesURL=https://github.com/PiTrolKun/LOPATA
DefaultDirName={localappdata}\Programs\LOPATA
DefaultGroupName=LOPATA
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=LOPATA_Setup_{#AppVersion}
SetupIconFile={#SetupIconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#PublishDir}\Licenses\installer.txt
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Licenses\installer-receipt.json"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BackendDir}\*"; DestDir: "{localappdata}\AI_HUB\Runtime\Backends\llama.cpp\b9442\win-cuda-12.4-x64"; Excludes: "*.log"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ChatLlmBackendDir}\*"; DestDir: "{localappdata}\AI_HUB\Runtime\Backends\chatllm.cpp\v24\win-x64"; Excludes: "*.log"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ЛОПАТА"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\ЛОПАТА"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить ЛОПАТА"; Flags: nowait postinstall skipifsilent

[Code]
type
  TLicenseSystemTime = record
    Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds: Word;
  end;
procedure GetSystemTime(var Value: TLicenseSystemTime);
  external 'GetSystemTime@kernel32.dll stdcall';
function MoveFileEx(Existing, NewName: String; Flags: Integer): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';
function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
var
  ProcessId: Integer;
  ProcessHandle: THandle;
begin
  Result := (not WizardSilent) or (ExpandConstant('{param:ACCEPTLICENSES|0}') = '1');
  if not Result then Exit;
  ProcessId := StrToIntDef(ExpandConstant('{param:WAITFORPID|0}'), 0);
  if ProcessId > 0 then
  begin
    ProcessHandle := OpenProcess($00100000, False, ProcessId);
    if ProcessHandle <> 0 then
    begin
      Result := WaitForSingleObject(ProcessHandle, 120000) = 0;
      CloseHandle(ProcessHandle);
      if not Result then
        MsgBox('Программа ещё работает. Закройте ЛОПАТУ и повторите запуск установщика.', mbError, MB_OK);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Receipt: AnsiString;
  Value, ReceiptDir, Target: String;
  Time: TLicenseSystemTime;
begin
  if CurStep = ssPostInstall then
  begin
    ExtractTemporaryFile('installer-receipt.json');
    if not LoadStringFromFile(ExpandConstant('{tmp}\installer-receipt.json'), Receipt) then
      RaiseException('Не удалось прочитать сведения о лицензиях.');
    Value := String(Receipt);
    GetSystemTime(Time);
    StringChangeEx(Value, '__ACCEPTED_AT__', Format('%.4d-%.2d-%.2dT%.2d:%.2d:%.2dZ', [Time.Year, Time.Month, Time.Day, Time.Hour, Time.Minute, Time.Second]), True);
    StringChangeEx(Value, '__APP_VERSION__', '{#AppVersion}', True);
    ReceiptDir := ExpandConstant('{localappdata}\AI_HUB\Licenses');
    ForceDirectories(ReceiptDir);
    Target := ReceiptDir + '\installer-receipts.json';
    if not SaveStringToFile(Target + '.tmp', AnsiString(Value), False) then
      RaiseException('Не удалось сохранить подтверждение лицензий.');
    if not MoveFileEx(Target + '.tmp', Target, 9) then
      RaiseException('Не удалось сохранить подтверждение лицензий.');
  end;
end;
