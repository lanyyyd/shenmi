; SteamEYA 单文件安装包脚本（Inno Setup 6 / 7）。
; 由 ISCC.exe 编译：把 Native AOT 自包含发布目录（publish/）整体收进一个 setup.exe，
; 用户双击安装即可，无需另装 .NET 或 Windows App Runtime。
;
; 用法：
;   ISCC.exe /DAppVersion=0.1.5 /DPublishDir=<发布目录> /DOutputDir=<输出目录> build\installer\SteamEYA.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error "PublishDir is not defined."
#endif

#ifndef OutputDir
  #define OutputDir SourcePath
#endif

[Setup]
; 单个闭括号：{{ 转义成字面 {，末尾单 } 收尾，得到 {GUID}。AppId 一旦随首个安装包发布便不可再改。
AppId={{9BB137D4-3413-4F62-8C75-471DCEBAF2E5}
AppName=SteamEYA
AppVersion={#AppVersion}
AppPublisher=lanyyyd
DefaultDirName={localappdata}\SteamEYA
DefaultGroupName=SteamEYA
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=SteamEYA-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 应用最低要求 Windows 10 1809（net10.0-windows / Windows App SDK），否则会装到跑不起来的旧系统。
MinVersion=10.0.17763
PrivilegesRequired=lowest
WizardStyle=modern
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\SteamEyaWinUI.exe
SetupIconFile={#PublishDir}\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SteamEYA"; Filename: "{app}\SteamEyaWinUI.exe"
Name: "{autodesktop}\SteamEYA"; Filename: "{app}\SteamEyaWinUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SteamEyaWinUI.exe"; Description: "{cm:LaunchProgram,SteamEYA}"; Flags: nowait postinstall skipifsilent
