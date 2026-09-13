; Inno Setup 脚本 —— build.ps1 构建完 exe 后调用 ISCC 生成安装包。
; 版本号由命令行传入：ISCC.exe /Obuild /DMyAppVersion=1.1.51 installer.iss
; 注意：本文件需保存为 UTF-8 **带 BOM**，否则 Inno 会按 ANSI 读取导致中文乱码。
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{A7E3C1D2-5B4F-4A6E-9D8C-2F1B3A4C5D6E}
AppName=快截
AppVersion={#MyAppVersion}
AppVerName=快截 v{#MyAppVersion}
AppPublisher=Lvbta
AppPublisherURL=https://github.com/Lvbta
AppSupportURL=https://github.com/Lvbta
AppUpdatesURL=https://github.com/Lvbta
AppContact=https://github.com/Lvbta
SetupIconFile=src\app.ico
DefaultDirName={localappdata}\Programs\SnapCut
DefaultGroupName=快截
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=build
OutputBaseFilename=SnapCut-Setup-v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 单文件绿色程序：装到当前用户目录，不需要管理员权限 / 不弹 UAC
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName=快截 v{#MyAppVersion}
UninstallDisplayIcon={app}\SnapCut.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "build\SnapCut.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\快截"; Filename: "{app}\SnapCut.exe"
Name: "{group}\卸载 快截"; Filename: "{uninstallexe}"
Name: "{autodesktop}\快截"; Filename: "{app}\SnapCut.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SnapCut.exe"; Description: "安装完成后立即运行 快截"; Flags: nowait postinstall skipifsilent
