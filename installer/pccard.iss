; ════════════════════════════════════════════════════════════════════
;  PcCard（开机卡控制台）安装脚本（Inno Setup 6）
;
;  设计要点：
;    · 无包标识的普通 Win32 应用（不是 MSIX）—— 用户明确要求 .exe 安装器
;    · 完全自包含，目标机器无需 .NET 或 Windows App Runtime（CRT 已 app-local 随包）
;    · 托盘自启 = 计划任务 PcCard\Tray（onlogon + /RL HIGHEST + /IT，免密码静默创建）
;      —— 让 GUI 一启动就是提权态，界面里建/改计划任务不再弹 UAC
;
;  ★ 两条从 FlowGet 踩出来的硬教训（改这个文件前务必先读）：
;
;  1) 注册表/系统集成写入全部放在 [Code] 里，逐条 try/except。
;     [Registry] 段里任何一条失败都会中止整个安装并回滚。
;
;  2) 卸载器是独立进程，对安装时做过什么一无所知（[Code] 变量全空）。
;     安装时把「做了什么」写进 HKLM\Software\PcCard\Install 标记，
;     卸载时按标记回滚。
;
;  另：卸载定时任务清单不解析 schtasks /Query 输出（任务名可含中文，
;      Inno 的 Pos/Copy 是字节偏移会在中文上翻车），而是读应用维护的
;      %ProgramData%\PcCard\tasks-index.txt（纯 ASCII）。
;
;  构建走 tools\make-installer.ps1，不要直接调 ISCC。
; ════════════════════════════════════════════════════════════════════

#define AppName        "开机卡控制台"
#define AppNameEn      "PcCard"
#define AppPublisher   "Wang Hongtao"
#define AppExeName     "PcCardApp.exe"
#define CliExeName     "pccard.exe"
#define AppId          "{{7C4A9E12-8D5F-4B6A-A3E9-5F1D2C8B6A47}"
#define StateKey       "Software\PcCard\Install"
#define TrayTaskName   "PcCard\Tray"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "D:\pccard-build\stage\Release"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}（{#AppNameEn}）{#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppNameEn} Setup

DefaultDirName={autopf}\PcCard
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\build\installer
OutputBaseFilename=PcCard-Setup-{#AppVersion}
SetupIconFile=..\assets\appicon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinese.CreateDesktopIcon=创建桌面快捷方式(&D)
english.CreateDesktopIcon=Create a &desktop shortcut
chinese.UninstallProgram=卸载 %1
english.UninstallProgram=Uninstall %1

chinese.TaskGroupStartup=启动
english.TaskGroupStartup=Startup
chinese.TaskAutostart=登录后自动运行（托盘常驻，随时可用）
english.TaskAutostart=Start at sign-in (stays in the notification area)

chinese.ConfirmRemoveUserData=是否同时删除开机卡控制台的数据？%n%n包括：云凭据（DPAPI 加密）、设备清单、定时任务定义、操作日志。%n位置：%1%n%n选择「否」将保留（重装后免去重新导入凭据）。
english.ConfirmRemoveUserData=Also delete PcCard data?%n%nThis includes cloud credentials (DPAPI-protected), device list, scheduled tasks and logs.%nLocation: %1%n%nChoose No to keep them (still there after reinstalling).

chinese.TrayTaskFailed=托盘自启任务注册失败（%1）。%n%n安装已完成，但登录后不会自动运行 —— 可重新以管理员身份运行安装程序，或手动运行 %2。
english.TrayTaskFailed=Failed to register the tray autostart task (%1).%n%nInstallation completed, but the app won't autostart at sign-in. Re-run setup as administrator, or launch %2 manually.

[Tasks]
; ★ checkedonce：默认勾选（静默安装也生效 —— 阿里云部署不加 /TASKS 参数就能拿到托盘自启）；
;   用户手动取消过则尊重选择。
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "autostart";   Description: "{cm:TaskAutostart}";     GroupDescription: "{cm:TaskGroupStartup}"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";                       Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 装完把托盘拉起来。★ 不直接 Run PcCardApp.exe —— 安装器在提权上下文里启动它，会继承
; 安装器的完整性级别；schtasks /run 让它落到交互会话里，且 Tray 任务自带 /RL HIGHEST。
Filename: "schtasks.exe"; Parameters: "/run /tn ""{#TrayTaskName}"""; \
    Flags: nowait postinstall skipifsilent runhidden; Tasks: autostart

[Code]
{ ════════════════════════════════════════════════════════════════════
  安装期：注册托盘自启任务 + 写安装标记
  卸载期：读标记删除任务/自启项/数据（询问）
  ════════════════════════════════════════════════════════════════════ }

var
  TrayTaskError: String;
  TrayTaskOk: Boolean;

{ ── 底层读写（全吃掉异常并记录，绝不拖垮主流程）── }
function WriteStr(RootKey: Integer; const SubKey, ValueName, ValueData: String): Boolean;
begin
  Result := False;
  try
    RegWriteStringValue(RootKey, SubKey, ValueName, ValueData);
    Result := True;
  except
    Log('WriteStr 失败: ' + SubKey + ' \ ' + ValueName + ' : ' + GetExceptionMessage);
  end;
end;

function DeleteVal(RootKey: Integer; const SubKey, ValueName: String): Boolean;
begin
  Result := False;
  try
    RegDeleteValue(RootKey, SubKey, ValueName);
    Result := True;
  except
    Log('DeleteVal 忽略异常: ' + SubKey + ' \ ' + ValueName);
  end;
end;

function ReadVal(RootKey: Integer; const SubKey, ValueName: String): String;
var
  S: String;
begin
  S := '';
  try
    if RegQueryStringValue(RootKey, SubKey, ValueName, S) then Result := S else Result := '';
  except
    Result := '';
  end;
end;

procedure DeleteKeyIfEmpty(RootKey: Integer; const SubKey: String);
begin
  try
    RegDeleteKeyIfEmpty(RootKey, SubKey);
  except
  end;
end;

{ ── 托盘自启任务 ──
  /SC ONLOGON /RU <用户> /RL HIGHEST /IT /F —— 实测免密码可静默创建；
  /IT 与 /NP 不能同用（schtasks 明确报错）。/DELAY 等会话稳定。 }
procedure CreateTrayTask;
var
  Params: String;
  ResultCode: Integer;
  Ok: Boolean;
begin
  Params := '/Create /TN "' + '{#TrayTaskName}' + '"'
          + ' /TR "\"' + ExpandConstant('{app}\{#AppExeName}') + '\" --tray"'
          + ' /SC ONLOGON /DELAY 0000:15 /RU ' + GetUserNameString() + ' /RL HIGHEST /IT /F';
  Log('CreateTrayTask: schtasks ' + Params);
  Ok := False;
  try
    Ok := Exec('schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    if not Ok then
      TrayTaskError := 'schtasks 退出码 ' + IntToStr(ResultCode);
  except
    TrayTaskError := GetExceptionMessage;
  end;
  TrayTaskOk := Ok;
  Log('CreateTrayTask 结果: ' + IntToStr(Ord(Ok)) + ' ' + TrayTaskError);
end;

procedure DeleteTrayTask;
var
  ResultCode: Integer;
begin
  try
    Exec('schtasks.exe', '/Delete /TN "' + '{#TrayTaskName}' + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('DeleteTrayTask 退出码 ' + IntToStr(ResultCode));
  except
    Log('DeleteTrayTask 忽略异常: ' + GetExceptionMessage);
  end;
end;

{ ── 删除应用注册过的全部定时任务：读纯 ASCII 索引文件，
     不解析 schtasks 输出（中文任务名 + Inno 字节偏移 = 翻车）── }
procedure DeleteAppTasks;
var
  IndexFile: String;
  Lines: TArrayOfString;
  I: Integer;
  ResultCode: Integer;
  Name: String;
begin
  IndexFile := ExpandConstant('{commonappdata}\PcCard\tasks-index.txt');
  if not FileExists(IndexFile) then
  begin
    Log('无任务索引文件，跳过：' + IndexFile);
    Exit;
  end;
  if LoadStringsFromFile(IndexFile, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Name := Trim(Lines[I]);
      if Name = '' then continue;
      try
        Exec('schtasks.exe', '/Delete /TN "' + Name + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Log('删除任务 ' + Name + ' 退出码 ' + IntToStr(ResultCode));
      except
        Log('删除任务忽略异常 ' + Name);
      end;
    end;
  end;
end;

{ ── 安装标记：卸载器唯一的线索来源 ── }
procedure SaveInstallState;
begin
  WriteStr(HKLM, '{#StateKey}', 'InstallDir', ExpandConstant('{app}'));
  WriteStr(HKLM, '{#StateKey}', 'TrayTaskCreated', IntToStr(Ord(TrayTaskOk)));
  WriteStr(HKLM, '{#StateKey}', 'AutoStartRunKey', IntToStr(Ord(WizardIsTaskSelected('autostart'))));
end;

procedure ClearInstallState;
begin
  DeleteVal(HKLM, '{#StateKey}', 'InstallDir');
  DeleteVal(HKLM, '{#StateKey}', 'TrayTaskCreated');
  DeleteVal(HKLM, '{#StateKey}', 'AutoStartRunKey');
end;

{ ═══════════════ 安装流程 ═══════════════ }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    Log('--- 安装后系统集成 ---');

    if WizardIsTaskSelected('autostart') then
    begin
      CreateTrayTask;
      { HKCU Run 兜底（Tray 任务失败时仍能起；两者同时存在也无害 ——
        schtasks 任务先到，Run 项启动的第二实例会转发后退出） }
      WriteStr(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
               'PcCard', '"' + ExpandConstant('{app}\{#AppExeName}') + '" --tray');
    end;

    SaveInstallState;

    if WizardIsTaskSelected('autostart') and (not TrayTaskOk) then
      { ★ 数组字面量的 '[' 绝不能出现在行首（含缩进）—— Inno 预处理器会把它
        当作节标签并报 "Invalid section tag"。保持 '[TrayTaskError' 跟在同一行。 }
      SuppressibleMsgBox(FmtMessage(CustomMessage('TrayTaskFailed'), [TrayTaskError,
                         ExpandConstant('{app}\{#AppExeName}')]),
                         mbInformation, MB_OK, IDOK);
  end;
end;

{ ═══════════════ 卸载流程 ═══════════════ }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Marked: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Log('--- 卸载前清理系统集成 ---');

    { ★ 卸载器读不到安装期的变量，一切以持久化的标记为准 }
    Marked := ReadVal(HKLM, '{#StateKey}', 'InstallDir');
    Log('标记里的 InstallDir = ' + Marked);

    DeleteTrayTask;
    DeleteAppTasks;

    DeleteVal(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PcCard');

    ClearInstallState;
    DeleteKeyIfEmpty(HKLM, '{#StateKey}');
    DeleteKeyIfEmpty(HKLM, 'Software\PcCard');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\PcCard');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(FmtMessage(CustomMessage('ConfirmRemoveUserData'), [DataDir]),
                            mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
