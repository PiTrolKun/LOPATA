var
  UpdateDirectionPage: TInputOptionWizardPage;
  AutostartPage: TInputOptionWizardPage;
  FirstInstallation: Boolean;

function HasExplicitStandDirection(): Boolean;
var
  Choice: String;
begin
  Choice := ExpandConstant('{param:UPDATECHANNEL|}');
  Result := (ExpandConstant('{param:UPDATESTAND|0}') = '1') and ((Choice = 'stable') or (Choice = 'beta'));
end;

procedure InitializeWizard();
begin
  FirstInstallation := not FileExists(ExpandConstant('{#UserDataRoot}\Updates\installation.json'));
  UpdateDirectionPage := CreateInputOptionPage(wpLicense,
    CustomMessage('UpdateDirectionTitle'), CustomMessage('UpdateDirectionDescription'),
    CustomMessage('UpdateDirectionHelp'), True, False);
  UpdateDirectionPage.Add(CustomMessage('UpdateStable'));
  UpdateDirectionPage.Add(CustomMessage('UpdateBeta'));
  UpdateDirectionPage.SelectedValueIndex := -1;
  { Interactive installation never preselects a branch, including a reinstall. }
  if WizardSilent and HasExplicitStandDirection() then
  begin
    if ExpandConstant('{param:UPDATECHANNEL|}') = 'stable' then UpdateDirectionPage.SelectedValueIndex := 0
    else UpdateDirectionPage.SelectedValueIndex := 1;
  end;
  AutostartPage := CreateInputOptionPage(UpdateDirectionPage.ID,
    CustomMessage('AutostartTitle'), CustomMessage('AutostartDescription'),
    CustomMessage('AutostartHelp'), False, False);
  AutostartPage.Add(CustomMessage('AutostartChoice'));
  AutostartPage.Values[0] := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = AutostartPage.ID) and ((not FirstInstallation) or WizardSilent);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = UpdateDirectionPage.ID) and (UpdateDirectionPage.SelectedValueIndex < 0) then
  begin
    MsgBox(CustomMessage('UpdateChoose'), mbInformation, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
#ifdef NetworkSetup
  if FileExists(ExpandConstant('{#UserDataRoot}\Updates\installation.json')) then
  begin
    Result := CustomMessage('UpdateUseFull');
    Exit;
  end;
#endif
  if UpdateDirectionPage.SelectedValueIndex < 0 then
  begin
    Result := CustomMessage('UpdateChoose');
    Exit;
  end;
  if FileExists(ExpandConstant('{#UpdateHost}')) and
      FileExists(ExpandConstant('{#UserDataRoot}\Updates\installation.json')) then
  begin
    if (not Exec(ExpandConstant('{#UpdateHost}'), '--recover', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
      Result := CustomMessage('UpdateRecoveryFailed');
  end;
end;

procedure CompleteUpdateRegistration();
var
  ExitCode: Integer;
  Arguments, Choice, Target, Folder: String;
begin
  ExtractTemporaryFile('lopata-files.json');
#ifdef NetworkSetup
  Arguments := '--install "';
#else
  Arguments := '--register "';
#endif
  Arguments := Arguments + ExpandConstant('{app}') + '" "' + ExpandConstant('{#LlamaTarget}') + '" "' +
    ExpandConstant('{#ChatLlmTarget}') + '" "' + ExpandConstant('{tmp}\lopata-files.json') + '"';
  if (not Exec(ExpandConstant('{#UpdateHost}'), Arguments, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
    RaiseException(CustomMessage('UpdateRegisterFailed'));
  if UpdateDirectionPage.SelectedValueIndex = 0 then Choice := 'stable' else Choice := 'beta';
  Folder := ExpandConstant('{#UserDataRoot}\Updates');
  ForceDirectories(Folder);
  Target := Folder + '\channel.json';
  if (not SaveStringToFile(Target + '.tmp', AnsiString('{"schemaVersion":1,"channel":"' + Choice + '"}'), False)) or
      (not MoveFileEx(Target + '.tmp', Target, 9)) then RaiseException(CustomMessage('UpdateChannelFailed'));
#ifndef StandDataRoot
  if FirstInstallation then
  begin
    if AutostartPage.Values[0] then Choice := '1' else Choice := '0';
    if (not Exec(ExpandConstant('{#UpdateHost}'), '--configure-autostart ' + Choice, '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
      RaiseException(CustomMessage('AutostartFailed'));
  end;
#endif
end;

function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{#UpdateHost}')) and
      FileExists(ExpandConstant('{#UserDataRoot}\Updates\installation.json')) then
  begin
    Result := Exec(ExpandConstant('{#UpdateHost}'), '--unregister', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
    if not Result then MsgBox(CustomMessage('UpdateRecoveryFailed'), mbError, MB_OK);
  end;
end;
