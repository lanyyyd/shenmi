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

; 中文语言包目录（Inno 官方不内置中文，需自带 ChineseSimplified.isl；CI 通过 /DLanguagesDir 传入）
#ifndef LanguagesDir
  #define LanguagesDir "languages"
#endif

[Setup]
; 单个闭括号：{{ 转义成字面 {，末尾单 } 收尾，得到 {GUID}。AppId 一旦随首个安装包发布便不可再改。
AppId={{9BB137D4-3413-4F62-8C75-471DCEBAF2E5}
AppName=茶π·次元口袋
AppVersion={#AppVersion}
AppPublisher=lanyyyd
DefaultDirName={localappdata}\SteamEYA
DefaultGroupName=茶π
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=TeaTT-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 应用最低要求 Windows 10 1809（net10.0-windows / Windows App SDK），否则会装到跑不起来的旧系统。
MinVersion=10.0.17763
PrivilegesRequired=lowest
WizardStyle=modern
; 按系统界面语言自动选安装向导语言（中文系统→中文向导）
LanguageDetectionMethod=uilanguage
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\SteamEyaWinUI.exe
SetupIconFile={#PublishDir}\Assets\AppIcon.ico

[Languages]
; 中文放最前：作为默认语言（系统语言识别不到时回退中文）
Name: "chinesesimplified"; MessagesFile: "{#LanguagesDir}\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 快捷方式显示为「茶π」；开始菜单分组与安装向导标题用全名「茶π·次元口袋」。
Name: "{autoprograms}\茶π"; Filename: "{app}\SteamEyaWinUI.exe"
Name: "{autodesktop}\茶π"; Filename: "{app}\SteamEyaWinUI.exe"; Tasks: desktopicon

[InstallDelete]
; 旧版叫 SteamEYA：升级时清掉遗留入口，避免开始菜单/桌面出现两个图标。
Type: files; Name: "{autoprograms}\SteamEYA"
Type: files; Name: "{autodesktop}\SteamEYA"
Type: dirifempty; Name: "{autoprograms}\SteamEYA"
; 旧版内置的是自研极域组件，现已改为内置原版 JiYuTrainer（jiyu\JiYuTrainer.exe）；
; 升级时清掉这两个遗留文件，避免与新方案混用（旧 Hook 若仍被极域加载，删除失败不影响安装）。
Type: files; Name: "{app}\jiyu\JiYuHost.exe"
Type: files; Name: "{app}\jiyu\SteamEya.JiYuHook.dll"

[Run]
Filename: "{app}\SteamEyaWinUI.exe"; Description: "{cm:LaunchProgram,SteamEYA}"; Flags: nowait postinstall skipifsilent
