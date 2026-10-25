// Official Microsoft .NET 8 Desktop Runtime x64. Verified against release-metadata SHA-512
// and Microsoft's Authenticode signature; only downloaded when no suitable runtime exists.
#define DesktopRuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.32/windowsdesktop-runtime-8.0.32-win-x64.exe"
#define DesktopRuntimeSha256 "0f45815f9f032095910af092405c9c6d3844b7fe57252f854d65ed9379b45598"

function HasFramework(const Root, Framework, LibraryName: String; MinimumPatch: Integer): Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + 'shared\' + Framework + '\8.0.*', Rec) then
  begin
    try
      repeat
        if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (Pos('-', Rec.Name) = 0) and
           (StrToIntDef(Copy(Rec.Name, 5, Length(Rec.Name)), -1) >= MinimumPatch) and
           FileExists(AddBackslash(Root) + 'shared\' + Framework + '\' + Rec.Name + '\' + LibraryName) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

function DesktopRuntimeAt(const Root: String): Boolean;
var
  Rec: TFindRec;
  Patch: Integer;
begin
  Result := False;
  if (Root = '') or not FileExists(AddBackslash(Root) + 'dotnet.exe') then Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\8.0.*', Rec) then
  begin
    try
      repeat
        Patch := StrToIntDef(Copy(Rec.Name, 5, Length(Rec.Name)), -1);
        if (Patch >= 0) and ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           FileExists(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\' + Rec.Name + '\PresentationFramework.dll') and
           HasFramework(Root, 'Microsoft.NETCore.App', 'coreclr.dll', Patch) then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

function DesktopRuntimeInstalled: Boolean;
var
  Root: String;
begin
  Result := False;
  if DesktopRuntimeAt(GetEnv('DOTNET_ROOT_X64')) then
  begin
    Result := True;
    Exit;
  end;
  // Explicit x64 registration prevents mistaking an x86 or ARM64 runtime for this app's runtime.
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
    Result := DesktopRuntimeAt(Root);
  if Result then Exit;
  Root := ExpandConstant('{commonpf64}\dotnet');
  if IsArm64 then Root := Root + '\x64';
  Result := DesktopRuntimeAt(Root);
end;

function OnRuntimeDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then
    WizardForm.PreparingLabel.Caption := ExpandConstant('{cm:RuntimeDownloading}') + ' ' +
      IntToStr((Progress * 100) div ProgressMax) + ' %';
  Result := True;
end;

// Called for both interactive installation and silent one-click updates, before app files change.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installer: String;
  ResultCode: Integer;
begin
  Result := '';
  if DesktopRuntimeInstalled then
  begin
    Log('Compatible .NET 8 Desktop Runtime x64 found; no download required.');
    Exit;
  end;
  try
    DownloadTemporaryFile('{#DesktopRuntimeUrl}', 'windowsdesktop-runtime.exe', '{#DesktopRuntimeSha256}', @OnRuntimeDownloadProgress);
  except
    Log(GetExceptionMessage);
    Result := ExpandConstant('{cm:RuntimeFailed}');
    Exit;
  end;
  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  WizardForm.PreparingLabel.Caption := ExpandConstant('{cm:RuntimeInstalling}');
  if not ShellExec('runas', Installer, '/install /passive /norestart', '', SW_SHOW,
                   ewWaitUntilTerminated, ResultCode) then
  begin
    Result := ExpandConstant('{cm:RuntimeFailed}');
    Exit;
  end;
  Log('Desktop Runtime installer exit code: ' + IntToStr(ResultCode));
  if ResultCode = 3010 then NeedsRestart := True;
  // A successful exit code is insufficient: recheck before replacing a working application.
  if ((ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638)) or not DesktopRuntimeInstalled then
    Result := ExpandConstant('{cm:RuntimeFailed}');
end;
