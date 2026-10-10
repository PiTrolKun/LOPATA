{ Keep themed check/radio glyphs inside their owner-drawn list at high DPI.
  Reflow the explanatory text before assigning the remaining list height. }
function WrapSentences(Text: String): String;
begin
  Result := Text;
  StringChangeEx(Result, '. ', '.'#13#10, True);
  StringChangeEx(Result, '; ', ';'#13#10, True);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then begin
    WizardForm.SelectTasksLabel.Caption := CustomMessage('LayoutTasksHelp');
    WizardForm.AdjustLabelHeight(WizardForm.SelectTasksLabel);
  end;
  if CurPageID = wpReady then begin
    WizardForm.ReadyLabel.Caption := CustomMessage('LayoutReadyHelp');
    WizardForm.AdjustLabelHeight(WizardForm.ReadyLabel);
  end;
end;

function WrappedBetaCaption(): String;
begin
  Result := WrapSentences(CustomMessage('UpdateBeta'));
end;

procedure FitOptionPage(Page: TInputOptionWizardPage);
begin
  { Explicit sentence boundaries also avoid Cyrillic words split by GDI wrapping. }
  Page.SubCaptionLabel.Caption := WrapSentences(Page.SubCaptionLabel.Caption);
  Page.SubCaptionLabel.WordWrap := True;
  Page.SubCaptionLabel.Width := Page.SurfaceWidth;
  WizardForm.AdjustLabelHeight(Page.SubCaptionLabel);
  Page.CheckListBox.Top := Page.SubCaptionLabel.Top + Page.SubCaptionLabel.Height + ScaleY(12);
  Page.CheckListBox.Height := Page.SurfaceHeight - Page.CheckListBox.Top;
  Page.CheckListBox.Offset := ScaleX(12);
  Page.CheckListBox.MinItemHeight := ScaleY(24);
end;

procedure FitWizardButtons();
var Width, Gap, Right: Integer;
begin
  Width := WizardForm.CalculateButtonWidth([WizardForm.BackButton.Caption,
    WizardForm.NextButton.Caption, WizardForm.CancelButton.Caption]);
  if Width < ScaleX(80) then Width := ScaleX(80);
  Gap := ScaleX(10);
  Right := WizardForm.CancelButton.Left + WizardForm.CancelButton.Width;
  WizardForm.CancelButton.Width := Width;
  WizardForm.CancelButton.Left := Right - Width;
  WizardForm.NextButton.Width := Width;
  WizardForm.NextButton.Left := WizardForm.CancelButton.Left - Gap - Width;
  WizardForm.BackButton.Width := Width;
  WizardForm.BackButton.Left := WizardForm.NextButton.Left - Gap - Width;
  WizardForm.TasksList.Offset := ScaleX(12);
  WizardForm.TasksList.MinItemHeight := ScaleY(24);
  WizardForm.RunList.Offset := ScaleX(12);
  WizardForm.RunList.MinItemHeight := ScaleY(24);
  WizardForm.SelectTasksLabel.Caption := CustomMessage('LayoutTasksHelp');
  WizardForm.ReadyLabel.Caption := CustomMessage('LayoutReadyHelp');
end;
