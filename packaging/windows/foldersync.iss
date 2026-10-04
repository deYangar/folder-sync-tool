; FolderSync Inno Setup 脚本（方案 §7.1：win-x86/x64/arm64 → Setup.exe；6.3+ 才编 arm64）
; 命令行：ISCC /DAppVersion=<ver> /DArch=<x64|x86|arm64> /DSourcePath=<publish输出> /O<输出目录> 本文件
; arch 由调用方传入（win-x86 → x86，win-x64 → x64，win-arm64 → arm64）

#ifndef AppVersion
#error 需要定义 AppVersion（/DAppVersion=2.0.0）
#endif
#ifndef Arch
#error 需要定义 Arch（/DArch=x64 或 x86 或 arm64）
#endif
#ifndef SourcePath
#error 需要定义 SourcePath（publish 输出目录）
#endif

#if Arch == "x64"
  #define ArchTitle "x64"
  #define ArchWin "Win64"
#elif Arch == "x86"
  #define ArchTitle "x86"
  #define ArchWin ""
#else
  #define ArchTitle "ARM64"
  #define ArchWin "ARM64"
#endif

[Setup]
AppId={{8B7C1E2A-64D3-4F5B-9A81-0F2E4A6C9D10}
AppName=FolderSync
AppVersion={#AppVersion}
AppPublisher=FolderSync
DefaultDirName={autopf}\FolderSync
DefaultGroupName=FolderSync
UninstallDisplayIcon={app}\FolderSync.exe
OutputBaseFilename=FolderSync_{#AppVersion}_win-{#Arch}_Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed={#ArchWin}
; Windows 10 1607+（.NET 8 最低要求，方案风险表）
MinVersion=10.0.14393
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=.
#if Arch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
#elif Arch == "x64"
ArchitecturesInstallIn64BitMode=x64
#endif

[Files]
Source: "{#SourcePath}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\FolderSync"; Filename: "{app}\FolderSync.exe"
Name: "{autodesktop}\FolderSync"; Filename: "{app}\FolderSync.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Run]
Filename: "{app}\FolderSync.exe"; Description: "立即启动 FolderSync"; Flags: postinstall nowait skipifsilent unchecked

[UninstallDelete]
; 数据在 %LOCALAPPDATA%\FolderSync（不随卸载删除——用户数据安全优先，卸载器不动数据目录）
Type: files; Name: "{app}\crash.log"
