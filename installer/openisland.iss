; Open Island · Inno Setup script
;
; 通过 CI 调用：APP_VERSION 和 STAGING_DIR 环境变量必须先设。
;   APP_VERSION  e.g. 0.1.0
;   STAGING_DIR  指向已经准备好的 self-contained 目录（含 OpenIsland.exe + hooks + setup + deps）
;
; 编译命令：
;   & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' /Qp installer\openisland.iss

#define MyAppName "Open Island"
#define MyAppVersion GetEnv("APP_VERSION")
#define MyAppPublisher "ludiwangfpga"
#define MyAppURL "https://github.com/ludiwangfpga/open-island-windows"
#define MyAppExeName "OpenIsland.exe"
#define StagingDir GetEnv("STAGING_DIR")

#if MyAppVersion == ""
  #define MyAppVersion "0.0.0-dev"
#endif

[Setup]
; 固定 GUID —— 升级时 Inno 凭它找到旧版做替换升级，**不要**改。
AppId={{B7E3F4D1-9A2C-4E5F-8B6D-A1B2C3D4E5F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; 装到 %LOCALAPPDATA%\OpenIsland —— 不需要管理员权限
DefaultDirName={localappdata}\OpenIsland
DefaultGroupName=Open Island
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\installer-output
OutputBaseFilename=OpenIsland-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Open Island
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
CloseApplications=force
RestartApplications=yes

[Languages]
; 仅英文 —— GitHub Actions 的 windows-latest 上 Inno Setup 默认安装不带 ChineseSimplified.isl。
; 想要中文界面需要把 ChineseSimplified.isl vendored 到 installer/ 下并改 MessagesFile 路径。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start Open Island automatically when I log in"; GroupDescription: "Startup options:"; Flags: unchecked

[Files]
; 整个 staging dir（已经有完整运行时 + hooks + setup + 文档）—— 复制到安装目录
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（HKCU\Run）—— 用户在 Tasks 勾了才写；卸载时清掉
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OpenIsland"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startupicon; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 安装目录里 .NET 运行时生成的少量临时文件，卸载时一并清理
Type: filesandordirs; Name: "{app}\Locales"
