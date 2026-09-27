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
#ifndef FileManifest
#error FileManifest is required.
#endif
#ifdef StandDataRoot
#define UserDataRoot StandDataRoot
#else
#define UserDataRoot "{localappdata}\AI_HUB"
#endif
#define LlamaTarget UserDataRoot + "\Runtime\Backends\llama.cpp\b9442\win-cuda-12.4-x64"
#define ChatLlmTarget UserDataRoot + "\Runtime\Backends\chatllm.cpp\v24\win-x64"
#define UpdateHost UserDataRoot + "\UpdateHost\LOPATA.Updater.exe"

[Setup]
#ifdef StandDataRoot
AppId=LOPATA_Update_Stand
#else
AppId={{85E9F5C5-2B18-43B1-84E2-A99B25E9B9E8}
#endif
AppName={#AppName}
AppVersion={#AppVersion}
#ifdef NumericVersion
VersionInfoVersion={#NumericVersion}
#endif
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/PiTrolKun/LOPATA
AppSupportURL=https://github.com/PiTrolKun/LOPATA
AppUpdatesURL=https://github.com/PiTrolKun/LOPATA
#ifdef StandDataRoot
DefaultDirName={#StandDataRoot}\App
CreateUninstallRegKey=no
#else
DefaultDirName={localappdata}\Programs\LOPATA
#endif
DefaultGroupName=LOPATA
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
#ifdef NetworkSetup
OutputBaseFilename=LOPATA_Online_Setup_{#AppVersion}
#else
OutputBaseFilename=LOPATA_Setup_{#AppVersion}
#endif
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
Name: "english"; MessagesFile: "compiler:Default.isl"

#include "UpdateMessages.iss"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Licenses\installer-receipt.json"; Flags: dontcopy
Source: "{#FileManifest}"; DestName: "lopata-files.json"; Flags: dontcopy
#ifndef NetworkSetup
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "{#PublishDir}\Updater\LOPATA.Updater.exe"; DestDir: "{#UserDataRoot}\UpdateHost"; Flags: ignoreversion
#ifndef NetworkSetup
Source: "{#BackendDir}\*"; DestDir: "{#LlamaTarget}"; Excludes: "*.log"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ChatLlmBackendDir}\*"; DestDir: "{#ChatLlmTarget}"; Excludes: "*.log"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
#ifndef StandDataRoot
Name: "{autoprograms}\ЛОПАТА"; Filename: "{#UpdateHost}"; Parameters: "--launch"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\ЛОПАТА"; Filename: "{#UpdateHost}"; Parameters: "--launch"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon
#endif

[Run]
Filename: "{#UpdateHost}"; Parameters: "--launch"; Description: "{cm:LaunchLopata}"; Flags: nowait postinstall skipifsilent

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

#include "UpdateFlow.iss"

function InitializeSetup(): Boolean;
var
  ProcessId: Integer;
  ProcessHandle: THandle;
begin
  Result := (not WizardSilent) or ((ExpandConstant('{param:ACCEPTLICENSES|0}') = '1') and HasExplicitStandDirection());
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
        MsgBox(CustomMessage('CloseLopata'), mbError, MB_OK);
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
    ReceiptDir := ExpandConstant('{#UserDataRoot}\Licenses');
    ForceDirectories(ReceiptDir);
    Target := ReceiptDir + '\installer-receipts.json';
    if not SaveStringToFile(Target + '.tmp', AnsiString(Value), False) then
      RaiseException('Не удалось сохранить подтверждение лицензий.');
    if not MoveFileEx(Target + '.tmp', Target, 9) then
      RaiseException('Не удалось сохранить подтверждение лицензий.');
    CompleteUpdateRegistration();
  end;
end;
