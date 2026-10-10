#ifndef BootstrapDir
#error BootstrapDir is required.
#endif
#ifndef OutputDir
#error OutputDir is required.
#endif
#ifndef SetupIconFile
#error SetupIconFile is required.
#endif
#ifdef StandDataRoot
#define UserDataRoot StandDataRoot
#else
#define UserDataRoot "{localappdata}\AI_HUB"
#endif
#define UpdateHost UserDataRoot + "\UpdateHost\LOPATA.Updater.exe"
#define LlamaTarget UserDataRoot + "\Runtime\Backends\llama.cpp\b9442\win-cuda-12.4-x64"
#define ChatLlmTarget UserDataRoot + "\Runtime\Backends\chatllm.cpp\v24\win-x64"

[Setup]
#ifdef StandDataRoot
AppId=LOPATA_Mini_Stand
DefaultDirName={#StandDataRoot}\App
CreateUninstallRegKey=no
#else
AppId={{85E9F5C5-2B18-43B1-84E2-A99B25E9B9E8}
DefaultDirName={localappdata}\Programs\LOPATA
#endif
AppName=LOPATA
AppVersion={code:SelectedVersion}
VersionInfoVersion=1.0.0.0
VersionInfoTextVersion=1
VersionInfoProductTextVersion=Universal bootstrap protocol 1
AppPublisher=LOPATA
AppPublisherURL=https://github.com/PiTrolKun/LOPATA
DefaultGroupName=LOPATA
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=LOPATA_Setup
SetupIconFile={#SetupIconFile}
UninstallDisplayIcon={app}\AIHub.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
#include "UpdateMessages.iss"

[CustomMessages]
russian.MiniTitle=Универсальная установка ЛОПАТЫ
english.MiniTitle=Universal LOPATA setup
russian.MiniDownload=Загрузка проверенного установочного движка
english.MiniDownload=Downloading the verified installation engine
russian.MiniLicense=Лицензии выбранного выпуска
english.MiniLicense=Selected release licenses
russian.MiniAccept=Я принимаю условия лицензий этого выпуска
english.MiniAccept=I accept the licenses of this release
russian.MiniConnections=Параллельные соединения
english.MiniConnections=Parallel connections
russian.MiniConnectionsHelp=Выберите число соединений для загрузки движка и программы. Автоматически: 4 или 8 в зависимости от размера.
english.MiniConnectionsHelp=Choose the connection limit for engine and application downloads. Automatic: 4 or 8 depending on size.
russian.MiniAutomatic=Автоматически
english.MiniAutomatic=Automatic
russian.MiniFailed=Установка не завершена. Скачанные части сохранены для повторной попытки.
english.MiniFailed=Setup did not complete. Downloaded parts are retained for retry.
russian.MiniExit=Выйти из программы установки?
english.MiniExit=Exit setup?
russian.MiniExitHelp=Установка ещё не завершена. Вы сможете продолжить её, запустив установщик снова. Скачанные части сохранятся.
english.MiniExitHelp=Installation is not complete. Run setup again to continue. Downloaded parts will be retained.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
[Files]
Source: "{#BootstrapDir}\LOPATA.Bootstrap.exe"; Flags: dontcopy
[Icons]
#ifndef StandDataRoot
Name: "{autoprograms}\ЛОПАТА"; Filename: "{#UpdateHost}"; Parameters: "--launch"; WorkingDir: "{app}"; IconFilename: "{app}\AIHub.exe"
Name: "{autodesktop}\ЛОПАТА"; Filename: "{#UpdateHost}"; Parameters: "--launch"; WorkingDir: "{app}"; IconFilename: "{app}\AIHub.exe"; Tasks: desktopicon
#endif
[Run]
Filename: "{#UpdateHost}"; Parameters: "--launch"; Description: "{cm:LaunchLopata}"; Flags: nowait postinstall skipifsilent
[UninstallDelete]
Type: files; Name: "{#UpdateHost}"

[Code]
#include "WizardLayout.iss"
type
  TLicenseSystemTime = record
    Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds: Word;
  end;
var
  ConnectionPage, DirectionPage, StartupPage: TInputOptionWizardPage;
  LicensePage: TWizardPage;
  LicenseMemo: TNewMemo;
  AcceptLicense: TNewCheckBox;
  ProgressPage: TOutputMarqueeProgressWizardPage;
  TargetVersion, Session, CancelFile, Engine, EngineHash, Manifest, Receipt, LastTransferError: String;
  LlamaDirectory, ChatLlmDirectory: String;
  Busy, EngineReady, Installed, FirstInstallation: Boolean;

function MoveFileEx(Existing, NewName: String; Flags: Integer): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';
procedure GetSystemTime(var Value: TLicenseSystemTime);
  external 'GetSystemTime@kernel32.dll stdcall';

function SelectedVersion(Param: String): String;
begin
  if TargetVersion = '' then Result := '0.0.0' else Result := TargetVersion;
end;

function Connections(): String;
begin
  case ConnectionPage.SelectedValueIndex of
    1: Result := '1'; 2: Result := '2'; 3: Result := '4'; 4: Result := '8';
  else Result := '0'; end;
end;

procedure TransferLog(const S: String; Error, FirstLine: Boolean);
begin
  Log(S);
  if Pos('[error]', S) > 0 then LastTransferError := S;
  ProgressPage.SetText(CustomMessage('MiniDownload'), S);
end;

procedure PrepareEngine();
var
  Code: Integer;
  Ini, LicensePath, Arguments, RegisteredApp: String;
begin
  if EngineReady then Exit;
  ExtractTemporaryFile('LOPATA.Bootstrap.exe');
  Session := ExpandConstant('{tmp}\mini-session');
  ForceDirectories(Session);
  CancelFile := Session + '\cancel';
  DeleteFile(CancelFile);
  Arguments := '"' + Session + '" "' + ExpandConstant('{#UserDataRoot}\Updates\bootstrap') + '" ' + Connections() + ' "' + CancelFile + '"';
  Busy := True;
  ProgressPage.SetText(CustomMessage('MiniDownload'), 'GitHub Releases');
  ProgressPage.Show;
  try
    if (not ExecAndLogOutput(ExpandConstant('{tmp}\LOPATA.Bootstrap.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code, @TransferLog)) or (Code <> 0) then
      RaiseException(CustomMessage('MiniFailed') + #13#10 + LastTransferError);
    Ini := Session + '\setup.ini';
    TargetVersion := GetIniString('setup', 'version', '', Ini);
    Engine := GetIniString('setup', 'engine', '', Ini);
    EngineHash := GetIniString('setup', 'engineHash', '', Ini);
    Manifest := GetIniString('setup', 'manifest', '', Ini);
    Receipt := GetIniString('setup', 'receipt', '', Ini);
    LicensePath := GetIniString('setup', 'license', '', Ini);
    RegisteredApp := GetIniString('setup', 'registeredApp', '', Ini);
    if (RegisteredApp <> '') and (ExpandConstant('{param:DIR|}') = '') then WizardForm.DirEdit.Text := RegisteredApp;
    if RegisteredApp <> '' then begin
      LlamaDirectory := GetIniString('setup', 'registeredLlama', '', Ini);
      ChatLlmDirectory := GetIniString('setup', 'registeredChatLlm', '', Ini);
    end;
    if (TargetVersion = '') or (not FileExists(Engine)) or (not FileExists(Manifest)) or
      (CompareText(GetSHA256OfFile(Engine), EngineHash) <> 0) then RaiseException(CustomMessage('MiniFailed'));
    LicenseMemo.Lines.LoadFromFile(LicensePath);
    LicensePage.Description := 'LOPATA ' + TargetVersion;
    EngineReady := True;
  finally
    Busy := False;
    ProgressPage.Hide;
  end;
end;

function InitializeSetup(): Boolean;
begin
#ifdef StandDataRoot
  Result := (not WizardSilent) or ((ExpandConstant('{param:UPDATESTAND|0}') = '1') and
    (ExpandConstant('{param:ACCEPTLICENSES|0}') = '1') and (ExpandConstant('{param:UPDATECHANNEL|}') = 'beta'));
#else
  Result := not WizardSilent;
#endif
end;

procedure InitializeWizard();
begin
  FirstInstallation := not FileExists(ExpandConstant('{#UserDataRoot}\Updates\installation.json'));
  LlamaDirectory := ExpandConstant('{#LlamaTarget}');
  ChatLlmDirectory := ExpandConstant('{#ChatLlmTarget}');
  ConnectionPage := CreateInputOptionPage(wpWelcome, CustomMessage('MiniConnections'), CustomMessage('MiniTitle'), CustomMessage('MiniConnectionsHelp'), True, False);
  ConnectionPage.Add(CustomMessage('MiniAutomatic'));
  ConnectionPage.Add('1'); ConnectionPage.Add('2'); ConnectionPage.Add('4'); ConnectionPage.Add('8');
  ConnectionPage.SelectedValueIndex := 0;
  FitOptionPage(ConnectionPage);
  LicensePage := CreateCustomPage(ConnectionPage.ID, CustomMessage('MiniLicense'), CustomMessage('MiniTitle'));
  LicenseMemo := TNewMemo.Create(WizardForm);
  LicenseMemo.Parent := LicensePage.Surface;
  LicenseMemo.SetBounds(0, 0, LicensePage.SurfaceWidth, LicensePage.SurfaceHeight - ScaleY(40));
  LicenseMemo.ReadOnly := True; LicenseMemo.ScrollBars := ssVertical; LicenseMemo.WordWrap := True;
  AcceptLicense := TNewCheckBox.Create(WizardForm);
  AcceptLicense.Parent := LicensePage.Surface;
  AcceptLicense.SetBounds(ScaleX(4), LicensePage.SurfaceHeight - ScaleY(32), LicensePage.SurfaceWidth - ScaleX(8), ScaleY(30));
  AcceptLicense.Caption := CustomMessage('MiniAccept');
  DirectionPage := CreateInputOptionPage(LicensePage.ID, CustomMessage('UpdateDirectionTitle'), CustomMessage('UpdateDirectionDescription'), CustomMessage('UpdateDirectionHelp'), True, False);
  DirectionPage.Add(CustomMessage('UpdateStable')); DirectionPage.Add(WrappedBetaCaption());
  DirectionPage.SelectedValueIndex := -1;
  FitOptionPage(DirectionPage);
  StartupPage := CreateInputOptionPage(DirectionPage.ID, CustomMessage('AutostartTitle'), CustomMessage('AutostartDescription'), CustomMessage('AutostartHelp'), False, False);
  StartupPage.Add(CustomMessage('AutostartChoice')); StartupPage.Values[0] := False;
  FitOptionPage(StartupPage);
  FitWizardButtons();
  ProgressPage := CreateOutputMarqueeProgressPage(CustomMessage('MiniTitle'), CustomMessage('MiniDownload'));
#ifdef StandDataRoot
  if WizardSilent then begin AcceptLicense.Checked := True; DirectionPage.SelectedValueIndex := 1; PrepareEngine(); end;
#endif
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = StartupPage.ID) and (not FirstInstallation);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ConnectionPage.ID then
    try PrepareEngine(); except TaskDialogMsgBox(CustomMessage('MiniFailed'), GetExceptionMessage, mbError, MB_OK, [], 0); Result := False; end;
  if CurPageID = LicensePage.ID then Result := AcceptLicense.Checked;
  if (CurPageID = DirectionPage.ID) and (DirectionPage.SelectedValueIndex < 0) then begin
    TaskDialogMsgBox(CustomMessage('UpdateChoose'), '', mbInformation, MB_OK, [], 0); Result := False;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if Busy then begin
    SaveStringToFile(CancelFile, 'cancel', False);
    Cancel := False; Confirm := False;
  end else if CurPageID <> wpFinished then begin
    Confirm := False;
    Cancel := TaskDialogMsgBox(CustomMessage('MiniExit'), CustomMessage('MiniExitHelp'), mbConfirmation, MB_YESNO, [], 0) = IDYES;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Arguments: String;
begin
  Result := '';
  if Installed then Exit;
  if (not EngineReady) or (not AcceptLicense.Checked) or (DirectionPage.SelectedValueIndex < 0) then begin
    Result := CustomMessage('UpdateChoose'); Exit;
  end;
  if CompareText(GetSHA256OfFile(Engine), EngineHash) <> 0 then begin Result := CustomMessage('MiniFailed'); Exit; end;
  Arguments := '--setup "' + ExpandConstant('{app}') + '" "' + LlamaDirectory + '" "' +
    ChatLlmDirectory + '" "' + Manifest + '" ' + Connections();
  { The verified engine has its own responsive progress and cancellation window. }
  if (not Exec(Engine, Arguments, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code)) or (Code <> 0) then
    Result := CustomMessage('MiniFailed')
  else Installed := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Value: AnsiString;
  Choice, Folder, ReceiptText: String;
  #ifndef StandDataRoot
  Code: Integer;
  #endif
  Time: TLicenseSystemTime;
begin
  if CurStep <> ssPostInstall then Exit;
  if CompareText(GetSHA256OfFile(Engine), EngineHash) <> 0 then RaiseException(CustomMessage('UpdateRegisterFailed'));
  Folder := ExpandConstant('{#UserDataRoot}\UpdateHost'); ForceDirectories(Folder);
  if not CopyFile(Engine, Folder + '\LOPATA.Updater.exe.tmp', False) then RaiseException(CustomMessage('UpdateRegisterFailed'));
  if not MoveFileEx(Folder + '\LOPATA.Updater.exe.tmp', Folder + '\LOPATA.Updater.exe', 9) then RaiseException(CustomMessage('UpdateRegisterFailed'));
  Folder := ExpandConstant('{#UserDataRoot}\Updates'); ForceDirectories(Folder);
  if DirectionPage.SelectedValueIndex = 0 then Choice := 'stable' else Choice := 'beta';
  if (not SaveStringToFile(Folder + '\channel.json.tmp', AnsiString('{"schemaVersion":1,"channel":"' + Choice + '"}'), False)) or
    (not MoveFileEx(Folder + '\channel.json.tmp', Folder + '\channel.json', 9)) then RaiseException(CustomMessage('UpdateChannelFailed'));
  if not LoadStringFromFile(Receipt, Value) then RaiseException(CustomMessage('UpdateRegisterFailed'));
  ReceiptText := String(Value);
  StringChangeEx(ReceiptText, '__APP_VERSION__', TargetVersion, True);
  GetSystemTime(Time);
  StringChangeEx(ReceiptText, '__ACCEPTED_AT__', Format('%.4d-%.2d-%.2dT%.2d:%.2d:%.2dZ', [Time.Year, Time.Month, Time.Day, Time.Hour, Time.Minute, Time.Second]), True);
  Folder := ExpandConstant('{#UserDataRoot}\Licenses'); ForceDirectories(Folder);
  if (not SaveStringToFile(Folder + '\installer-receipts.json.tmp', AnsiString(ReceiptText), False)) or
    (not MoveFileEx(Folder + '\installer-receipts.json.tmp', Folder + '\installer-receipts.json', 9)) then RaiseException(CustomMessage('UpdateRegisterFailed'));
#ifndef StandDataRoot
  if FirstInstallation then begin
    if StartupPage.Values[0] then Choice := '1' else Choice := '0';
    if (not Exec(ExpandConstant('{#UpdateHost}'), '--configure-autostart ' + Choice, '', SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then RaiseException(CustomMessage('AutostartFailed'));
  end;
#endif
end;

function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{#UpdateHost}'), '--unregister', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
#ifndef StandDataRoot
var Marker, Executable, MenuKey: String;
#endif
begin
#ifndef StandDataRoot
  if CurUninstallStep = usPostUninstall then begin
    MenuKey := 'Software\Classes\SystemFileAssociations\image\shell\LOPATA';
    if RegQueryStringValue(HKCU, MenuKey, 'LOPATA.Owner', Marker) and (Marker = 'AIHub.ImageShellIntegration.v1') and
      RegQueryStringValue(HKCU, MenuKey, 'LOPATA.Executable', Executable) and
      (CompareText(Executable, ExpandConstant('{app}\AIHub.exe')) = 0) then RegDeleteKeyIncludingSubkeys(HKCU, MenuKey);
  end;
#endif
end;
