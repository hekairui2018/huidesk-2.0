; 灰Desk 2.0 安装脚本 —— Inno Setup 6.5
; 装到用户目录，不需要管理员权限；与一代（Programs\GrayDesk）并列不冲突
#define AppVer "2.4.1"
#define SrcDir "E:/开发项目/GrayDesk2.0/dist/步E-2.4.1-20261110"

[Setup]
AppId={{7A3C9B21-5D4E-4F8A-9C31-2B6E8D4A5F70}
AppName=灰Desk 2.0
AppVersion={#AppVer}
AppVerName=灰Desk 2.0 {#AppVer}
AppPublisher=灰Desk
AppPublisherURL=https://localhost/
DefaultDirName={localappdata}\Programs\GrayDesk20
DefaultGroupName=灰Desk 2.0
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=E:/开发项目/GrayDesk2.0/dist
OutputBaseFilename=灰Desk2.0-{#AppVer}-安装包
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\GrayDesk2.0.exe
UninstallDisplayName=灰Desk 2.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\灰Desk 2.0"; Filename: "{app}\GrayDesk2.0.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\灰Desk 2.0"; Filename: "{app}\GrayDesk2.0.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GrayDesk2.0.exe"; Description: "立即启动灰Desk 2.0"; Flags: nowait postinstall skipifsilent
