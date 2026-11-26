; RelaxDesk-owned installer recipe. Inno Setup is an external build tool.
#define ViewerRunKey "Software\Microsoft\Windows\CurrentVersion\App Paths\rdesk.exe"
[Setup]
AppId=RelaxDesk.{#Role}
AppName={#Title}
AppVersion={#AppVersion}
AppPublisher=RelaxDesk
DefaultDirName={localappdata}\Programs\RelaxDesk\{#Role}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupArchitecture=x64
MinVersion=10.0
WizardStyle=modern
ShowLanguageDialog=no
DisableWelcomePage=no
Compression=lzma2/normal
SolidCompression=yes
OutputDir={#OutputDirectory}
OutputBaseFilename=RelaxDesk-{#Role}-Setup-x64
VersionInfoVersion={#FileVersion}
UninstallDisplayName={#Title}
UninstallDisplayIcon={app}\{#Release}\{#Entry}
UninstallLogMode=overwrite
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupMutex=Local\RelaxDesk.Setup.{#Role}

[Languages]
Name: chinesesimplified; MessagesFile: compiler:Languages\ChineseSimplified.isl
Name: english; MessagesFile: compiler:Default.isl

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; Flags: checkedonce

[Files]
Source: "{#Payload}\*"; DestDir: "{app}\{#Release}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Receipt}"; DestDir: "{app}\receipts"; DestName: "{#Release}.hashes"; Flags: ignoreversion
Source: "{#CompilerLicense}"; DestDir: "{app}\receipts\{#Release}"; DestName: "InnoSetup-LICENSE.txt"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\RelaxDesk\Installations\{#Role}"; ValueType: string; ValueName: "Root"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\RelaxDesk\Installations\{#Role}"; ValueType: string; ValueName: "Receipts"; ValueData: "{code:GetReceiptIndex}"; Flags: uninsdeletevalue uninsdeletekeyifempty

#if Role == "Viewer"
Root: HKCU; Subkey: "{#ViewerRunKey}"; ValueType: string; ValueData: "{app}\{#Release}\{#Entry}"; Flags: uninsdeletevalue uninsdeletekeyifempty
#endif

[Icons]
Name: "{userprograms}\RelaxDesk\{#Title}"; Filename: "{app}\{#Release}\{#Entry}"; WorkingDir: "{app}\{#Release}"
Name: "{userdesktop}\{#Title}"; Filename: "{app}\{#Release}\{#Entry}"; WorkingDir: "{app}\{#Release}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#Release}\{#Entry}"; Description: "打开 {#Title}"; WorkingDir: "{app}\{#Release}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const Registration = 'Software\RelaxDesk\Installations\{#Role}';
var ReceiptIndex: String;
    TreeEntries: Integer;

function NativeAttributes(Path: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function NativeLastError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';

function Canonical(Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

function Child(Path, Root: String): Boolean;
begin
  Result := Pos(Uppercase(AddBackslash(Root)), Uppercase(Path)) = 1;
end;

function NoReparse(Path: String): Boolean;
var Parent: String; A, Error: LongWord;
begin
  Result := False;
  if Copy(Path, 2, 1) <> ':' then exit;
  while Length(Path) > 2 do begin
    A := NativeAttributes('\\?\' + Path);
    if A = $FFFFFFFF then begin
      Error := NativeLastError;
      if (Error <> 2) and (Error <> 3) then exit;
    end;
    if (A <> $FFFFFFFF) and ((A and $400) <> 0) then exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
  Result := True;
end;

function CheckTree(Root: String): Boolean;
var F: TFindRec; Path: String;
begin
  Result := False;
  if not NoReparse(Root) then exit;
  if FindFirst(AddBackslash(Root) + '*', F) then begin
    try
      repeat
        if (F.Name <> '.') and (F.Name <> '..') then begin
          TreeEntries := TreeEntries + 1;
          if (TreeEntries > 20000) or ((F.Attributes and $400) <> 0) then exit;
          Path := AddBackslash(Root) + F.Name;
          if ((F.Attributes and $10) <> 0) and not CheckTree(Path) then exit;
        end;
      until not FindNext(F);
    finally FindClose(F); end;
  end else if DirExists(Root) then exit;
  Result := True;
end;

function NonEmpty(Root: String): Boolean;
var F: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + '*', F) then begin
    try repeat
      if (F.Name <> '.') and (F.Name <> '..') then begin Result := True; exit; end;
    until not FindNext(F);
    finally FindClose(F); end;
  end else if DirExists(Root) then Result := True;
end;

function Token(var Text: String; Separator: String): String;
var P: Integer;
begin
  P := Pos(Separator, Text);
  if P = 0 then begin Result := Text; Text := ''; end
  else begin Result := Copy(Text, 1, P - 1); Delete(Text, 1, P); end;
end;

function Hex(Text: String; Size: Integer): Boolean;
var I: Integer;
begin
  Result := False;
  if Length(Text) <> Size then exit;
  for I := 1 to Length(Text) do
    if Pos(Lowercase(Copy(Text, I, 1)), '0123456789abcdef') = 0 then exit;
  Result := True;
end;

function SafeRelative(Path: String): Boolean;
var Part: String;
begin
  Result := False;
  if (Length(Path) < 1) or (Length(Path) > 512) or (Path[1] = '\') or
     (Pos(':', Path) > 0) or (Pos('/', Path) > 0) then exit;
  while Path <> '' do begin
    Part := Token(Path, '\');
    if (Part = '') or (Part = '.') or (Part = '..') or (Trim(Part) <> Part) or
       (Copy(Part, Length(Part), 1) = '.') then exit;
  end;
  Result := True;
end;

function VerifyReceipts(Root, Index, RequiredRelease: String): Boolean;
var Item, Release, Digest, ReceiptPath, Line, Expected, Relative, Path: String;
    Lines: TArrayOfString; I, Count: Integer;
begin
  Result := False; Count := 0;
  if Index = '' then exit;
  while Index <> '' do begin
    Count := Count + 1; if Count > 32 then exit;
    Item := Token(Index, ';'); Release := Token(Item, '='); Digest := Item;
    if (Copy(Release, 1, 4) <> 'app-') or not Hex(Copy(Release, 5, 32), 32) or
       (Length(Release) <> 36) or not Hex(Digest, 64) then exit;
    ReceiptPath := AddBackslash(Root) + 'receipts\' + Release + '.hashes';
    if not NoReparse(ReceiptPath) or not FileExists(ReceiptPath) then exit;
    if Lowercase(GetSHA256OfFile(ReceiptPath)) <> Lowercase(Digest) then exit;
    if not LoadStringsFromFile(ReceiptPath, Lines) then exit;
    if (GetArrayLength(Lines) < 1) or (GetArrayLength(Lines) > 10000) then exit;
    for I := 0 to GetArrayLength(Lines) - 1 do begin
      Line := Lines[I]; Expected := Token(Line, #9); Relative := Line;
      if not Hex(Expected, 64) or not SafeRelative(Relative) then exit;
      if (Pos(Release + '\', Relative) <> 1) and
         (Relative <> 'receipts\' + Release + '\InnoSetup-LICENSE.txt') then exit;
      Path := Canonical(AddBackslash(Root) + Relative);
      if not Child(Path, Root) or not NoReparse(Path) then exit;
      if DirExists(Path) then exit;
      if (Release = RequiredRelease) and not FileExists(Path) then exit;
      if FileExists(Path) and (Lowercase(GetSHA256OfFile(Path)) <> Lowercase(Expected)) then exit;
    end;
  end;
  Result := True;
end;

function Running(Root: String): Boolean;
var Locator, Services, Processes, P: Variant; I: Integer; Path: String;
begin
  Result := True;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('{#ProcessQuery}');
    for I := 0 to Processes.Count - 1 do begin
      P := Processes.ItemIndex(I);
      if VarIsNull(P.ExecutablePath) or VarIsEmpty(P.ExecutablePath) then exit;
      Path := P.ExecutablePath;
      if (Path = '') or Child(Canonical(Path), Root) then exit;
    end;
    Result := False;
  except Log('Cannot establish application process ownership.'); end;
end;

function OwnedShortcuts(Root, Index, KeepRelease: String): Boolean;
var Shell, Link: Variant; Shortcut, Item, Release, Remaining: String; I: Integer; Owned: Boolean;
begin
  Result := False;
  Shell := CreateOleObject('WScript.Shell');
  for I := 0 to 1 do begin
    if I = 0 then Shortcut := ExpandConstant('{userprograms}\RelaxDesk\{#Title}.lnk')
    else Shortcut := ExpandConstant('{userdesktop}\{#Title}.lnk');
    if not NoReparse(Shortcut) then exit;
    if FileExists(Shortcut) then begin
      Link := Shell.CreateShortcut(Shortcut); Remaining := Index; Owned := False;
      while Remaining <> '' do begin
        Item := Token(Remaining, ';'); Release := Token(Item, '=');
        if (CompareText(Link.TargetPath, AddBackslash(Root) + Release + '\{#Entry}') = 0) and
           (Link.Arguments = '') then Owned := True;
      end;
      if not Owned then exit;
      if (KeepRelease <> '') and
         (CompareText(Link.TargetPath, AddBackslash(Root) + KeepRelease + '\{#Entry}') <> 0) then
        if not DeleteFile(Shortcut) then exit;
    end;
  end;
  Result := True;
end;

#if Role == "Viewer"
function OwnedViewerRunAlias(Root, Index: String): Boolean;
var Target, Item, Release: String;
begin
  Result := not RegKeyExists(HKCU, '{#ViewerRunKey}');
  if Result then exit;
  if not RegQueryStringValue(HKCU, '{#ViewerRunKey}', '', Target) then exit;
  while Index <> '' do begin
    Item := Token(Index, ';'); Release := Token(Item, '=');
    if CompareText(Target, AddBackslash(Root) + Release + '\{#Entry}') = 0 then begin
      Result := True; exit;
    end;
  end;
end;
#endif

function GetReceiptIndex(Param: String): String;
begin Result := ReceiptIndex; end;

procedure RemoveEmptyParents(Path, Root: String);
begin
  Path := ExtractFileDir(Path);
  while Child(Path, Root) do begin
    if not NoReparse(Path) or not RemoveDir(Path) then exit;
    Path := ExtractFileDir(Path);
  end;
end;

function PruneReleases(Root, Index, KeepRelease: String; RemoveReceipts: Boolean): Boolean;
var Item, Release, ReceiptPath, Line, Expected, Path: String;
    Lines: TArrayOfString; I: Integer;
begin
  Result := False;
  while Index <> '' do begin
    Item := Token(Index, ';'); Release := Token(Item, '=');
    if CompareText(Release, KeepRelease) <> 0 then begin
      ReceiptPath := AddBackslash(Root) + 'receipts\' + Release + '.hashes';
      if RemoveReceipts then begin
        if FileExists(ReceiptPath) then begin
          if not NoReparse(ReceiptPath) then exit;
          if Lowercase(GetSHA256OfFile(ReceiptPath)) <> Lowercase(Item) then exit;
          if not DeleteFile(ReceiptPath) then Log('Could not remove superseded receipt: ' + ReceiptPath);
        end;
      end else begin
        if not NoReparse(ReceiptPath) or not FileExists(ReceiptPath) then exit;
        if Lowercase(GetSHA256OfFile(ReceiptPath)) <> Lowercase(Item) then exit;
        if not LoadStringsFromFile(ReceiptPath, Lines) then exit;
        for I := 0 to GetArrayLength(Lines) - 1 do begin
          Line := Lines[I]; Expected := Token(Line, #9);
          if not SafeRelative(Line) or ((Pos(Release + '\', Line) <> 1) and
             (Line <> 'receipts\' + Release + '\InnoSetup-LICENSE.txt')) then exit;
          Path := Canonical(AddBackslash(Root) + Line);
          if not Child(Path, Root) or not NoReparse(Path) or DirExists(Path) then exit;
          if FileExists(Path) then begin
            if Lowercase(GetSHA256OfFile(Path)) <> Lowercase(Expected) then exit;
            if not DeleteFile(Path) then exit;
          end;
          RemoveEmptyParents(Path, Root);
        end;
      end;
    end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Root, Previous, Existing: String;
begin
  Result := ''; Root := Canonical(ExpandConstant('{app}'));
  if (Length(Root) < 4) or (Copy(Root, 2, 1) <> ':') then begin Result := '请选择本机非根目录。'; exit; end;
  TreeEntries := 0;
  if not CheckTree(Root) then begin Result := '安装目录含链接或异常路径，请选择普通本机目录。'; exit; end;
  if RegQueryStringValue(HKCU, Registration, 'Root', Previous) then begin
    if CompareText(Canonical(Previous), Root) <> 0 then begin Result := '已安装在其他目录，请先使用应用列表中的卸载入口。'; exit; end;
    if not RegQueryStringValue(HKCU, Registration, 'Receipts', Existing) or not VerifyReceipts(Root, Existing, '') then begin Result := '已有程序文件或安装记录被修改。请先备份并恢复，安装器不会覆盖这些内容。'; exit; end;
  end else if NonEmpty(Root) then begin Result := '此目录包含非本安装器管理的文件，请选择空目录。'; exit; end;
  if Running(Root) then begin Result := '请先关闭此安装目录中的 {#Title}，再继续。安装器不会结束共享会话。'; exit; end;
  if not OwnedShortcuts(Root, Existing, '') then begin Result := '同名快捷方式已存在且不属于此安装，请先检查。'; exit; end;
#if Role == "Viewer"
  if not OwnedViewerRunAlias(Root, Existing) or
     (not RegKeyExists(HKCU, '{#ViewerRunKey}') and
       (RegKeyExists(HKLM64, '{#ViewerRunKey}') or RegKeyExists(HKLM32, '{#ViewerRunKey}'))) then begin
    Result := '运行命令 rdesk 已被其他程序注册。请先检查现有注册项，安装器不会覆盖它。'; exit;
  end;
#endif
  ReceiptIndex := Existing;
  if Pos('{#Release}=', ReceiptIndex) = 0 then begin
    if ReceiptIndex <> '' then ReceiptIndex := ReceiptIndex + ';';
    ReceiptIndex := ReceiptIndex + '{#Release}={#ReceiptHash}';
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var Root, Current: String;
begin
  if Step = ssPostInstall then begin
    Root := Canonical(ExpandConstant('{app}'));
    Current := '{#Release}={#ReceiptHash}';
    if not VerifyReceipts(Root, ReceiptIndex, '{#Release}') then
      RaiseException('安装后校验失败；请保留安装日志，不要启动应用。');
    if (ReceiptIndex <> Current) then begin
      if Running(Root) or not OwnedShortcuts(Root, ReceiptIndex, '{#Release}') or
         not PruneReleases(Root, ReceiptIndex, '{#Release}', False) then
        RaiseException('新版已安装，但暂时无法清理旧版程序。请关闭 RelaxDesk 后重新运行安装包。');
      if not RegWriteStringValue(HKCU, Registration, 'Receipts', Current) then
        RaiseException('新版已安装，但无法更新安装记录。请重新运行安装包。');
      PruneReleases(Root, ReceiptIndex, '{#Release}', True);
      ReceiptIndex := Current;
    end;
  end;
end;

function InitializeUninstall: Boolean;
var Root, Registered, Index: String;
begin
  Result := False; Root := Canonical(ExpandConstant('{app}')); TreeEntries := 0;
  try
    if not CheckTree(Root) or not RegQueryStringValue(HKCU, Registration, 'Root', Registered) or
       (CompareText(Canonical(Registered), Root) <> 0) or
       not RegQueryStringValue(HKCU, Registration, 'Receipts', Index) or
       not VerifyReceipts(Root, Index, '') or not OwnedShortcuts(Root, Index, '') then begin
      SuppressibleMsgBox('文件、快捷方式或安装记录已修改，已停止卸载以保留内容。请备份并恢复后重试。', mbError, MB_OK, IDOK); exit;
    end;
#if Role == "Viewer"
    if not OwnedViewerRunAlias(Root, Index) then begin
      SuppressibleMsgBox('运行命令 rdesk 已被修改，已停止卸载以保留现有注册。请检查后重试。', mbError, MB_OK, IDOK); exit;
    end;
#endif
    if Running(Root) then begin SuppressibleMsgBox('请先关闭此安装目录中的 {#Title}。', mbError, MB_OK, IDOK); exit; end;
    Result := True;
  except SuppressibleMsgBox('无法安全确认卸载范围，未删除文件。请保留日志并检查。', mbError, MB_OK, IDOK); end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var Root, Index: String;
begin
  if Step = usUninstall then begin
    Root := Canonical(ExpandConstant('{app}'));
    if not RegQueryStringValue(HKCU, Registration, 'Receipts', Index) or
       not VerifyReceipts(Root, Index, '') or
       not OwnedShortcuts(Root, Index, '{#Release}') or
       not PruneReleases(Root, Index, '{#Release}', False) then
      RaiseException('无法清理旧版程序，已停止卸载。请关闭 RelaxDesk 后重试。');
    PruneReleases(Root, Index, '{#Release}', True);
  end;
end;
