; Warden kurulum betiği (Inno Setup 6)
; Yerelde derlemek için önce:
;   dotnet publish Warden.csproj -c Release -r win-x64 --self-contained true -o publish
; sonra:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Warden.iss
; CI sürümü /DAppVersion ve /DAppNumericVersion ile verir.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "1.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{AA396D37-4461-49CD-931F-D9C166A6F104}
AppName=Warden
AppVersion={#AppVersion}
AppVerName=Warden {#AppVersion}
AppPublisher=AlperenNyKs
AppPublisherURL=https://github.com/AlperenNyKs/Warden
; Program Files: yalnızca yöneticiler yazabilir. Warden açılışta yönetici yetkisiyle başladığı için
; (Görev Zamanlayıcı /rl highest) uygulama kendi klasörünün güvenli olmasını şart koşar.
DefaultDirName={autopf}\Warden
DisableDirPage=yes
DefaultGroupName=Warden
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=Warden-Setup-{#AppVersion}
SetupIconFile=..\Assets\icon.ico
UninstallDisplayIcon={app}\Warden.exe
UninstallDisplayName=Warden
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductVersion={#AppNumericVersion}
; Çalışan Warden PrepareToInstall'da kapatılır (tepside, yönetici olarak çalıştığı için Restart Manager'a bırakılmaz)
CloseApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.DriversGroup=Sürücüler:
english.DriversGroup=Drivers:
turkish.InstallPawnIO=PawnIO sürücüsünü kur (CPU sıcaklık/saat/güç sensörleri için gerekli, winget ile)
english.InstallPawnIO=Install the PawnIO driver (needed for CPU temperature/clock/power sensors, via winget)
turkish.InstallingPawnIO=PawnIO sürücüsü kuruluyor...
english.InstallingPawnIO=Installing the PawnIO driver...
turkish.PawnIOFailed=PawnIO otomatik kurulamadı (winget bulunamadı veya hata verdi).%n%nCPU sensörleri için https://pawnio.eu adresinden elle kurabilirsin.
english.PawnIOFailed=PawnIO could not be installed automatically (winget missing or failed).%n%nYou can install it manually from https://pawnio.eu for CPU sensors.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pawnio"; Description: "{cm:InstallPawnIO}"; GroupDescription: "{cm:DriversGroup}"; Check: not IsPawnIOInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Lisans yükümlülükleri: Warden + dağıtılan üçüncü taraf kütüphanelerin lisans metinleri
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Warden"; Filename: "{app}\Warden.exe"
Name: "{autodesktop}\Warden"; Filename: "{app}\Warden.exe"; Tasks: desktopicon

[Run]
; Warden requireAdministrator manifestli: kurulumun yönetici token'ıyla başlatılır (ikinci UAC sorusu çıkmaz)
Filename: "{app}\Warden.exe"; Description: "{cm:LaunchProgram,Warden}"; Flags: nowait postinstall skipifsilent runascurrentuser
; Uygulama içinden güncelleme (/SILENT): kurulum bitince Warden kendiliğinden yeniden açılır
Filename: "{app}\Warden.exe"; Flags: nowait runascurrentuser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Warden.exe /F"; Flags: runhidden; RunOnceId: "KillWarden"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""Warden"" /f"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[Code]
const
  PawnIOUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';

function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIOUninstallKey) or RegKeyExists(HKLM32, PawnIOUninstallKey);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Tepside çalışan Warden'ı kapat (dosyalar kilitli kalmasın)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Warden.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Eski kurulumun (ör. D:\Warden\bin\...) açılış görevi yeni kuruluma yönlendirilene kadar silinir;
  // Warden ilk açılışta, "Windows ile başlat" açıksa görevi Program Files yoluyla yeniden oluşturur.
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "Warden" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('pawnio') then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('InstallingPawnIO');
    if not Exec('winget.exe',
                'install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements',
                '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       or (not IsPawnIOInstalled) then
      SuppressibleMsgBox(CustomMessage('PawnIOFailed'), mbInformation, MB_OK, IDOK);
  end;
end;
