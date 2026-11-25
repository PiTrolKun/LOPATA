procedure RemoveVideoConverterShell(InstalledExe: String);
var
  Extensions: TArrayOfString;
  MenuKey, Marker, Executable: String;
  I: Integer;
begin
  SetArrayLength(Extensions, 6);
  Extensions[0] := '.mp4';
  Extensions[1] := '.m4v';
  Extensions[2] := '.mov';
  Extensions[3] := '.mkv';
  Extensions[4] := '.webm';
  Extensions[5] := '.avi';
  for I := 0 to GetArrayLength(Extensions) - 1 do begin
    MenuKey := 'Software\Classes\SystemFileAssociations\' + Extensions[I] + '\shell\LOPATA.VideoConverter';
    if RegQueryStringValue(HKCU, MenuKey, 'LOPATA.Owner', Marker) and (Marker = 'AIHub.VideoConverterShell.v1') and
      RegQueryStringValue(HKCU, MenuKey, 'LOPATA.Executable', Executable) and (CompareText(Executable, InstalledExe) = 0) then
      RegDeleteKeyIncludingSubkeys(HKCU, MenuKey);
  end;
end;
